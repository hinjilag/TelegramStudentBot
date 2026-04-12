using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramStudentBot.Services;

/// <summary>
/// Встроенный HTTP-сервер, раздающий Mini App (timer.html).
///
/// Для работы с Telegram Mini Apps URL должен быть HTTPS.
/// Для разработки используй ngrok:
///   ngrok http 8080
/// Получишь URL вида https://xxxx.ngrok.io — вставь в WebAppUrl в appsettings.json.
///
/// Для продакшна разверни на сервере с SSL (например, через nginx + Let's Encrypt).
/// </summary>
public class WebAppService : BackgroundService
{
    private readonly HttpListener _listener = new();
    private readonly string _wwwrootPath;
    private readonly string _htmlPath;
    private readonly int _port;
    private readonly TimerService _timers;
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<WebAppService> _logger;

    public WebAppService(
        IConfiguration config,
        TimerService timers,
        ITelegramBotClient bot,
        ILogger<WebAppService> logger)
    {
        _port     = config.GetValue("WebAppPort", 8080);
        _timers   = timers;
        _bot      = bot;
        _logger   = logger;
        _wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _htmlPath = Path.Combine(_wwwrootPath, "timer.html");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!File.Exists(_htmlPath))
        {
            _logger.LogWarning("timer.html не найден по пути {Path} — Mini App недоступен.", _htmlPath);
            return;
        }

        try
        {
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _logger.LogInformation("Mini App HTTP сервер запущен: http://localhost:{Port}/timer", _port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Не удалось запустить HTTP сервер на порту {Port}. " +
                "Проверь, что порт не занят другим процессом.",
                _port);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleAsync(context, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Ошибка в HTTP цикле WebAppService");
            }
        }

        _listener.Stop();
        _logger.LogInformation("Mini App HTTP сервер остановлен.");
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path == "/timer")
            {
                var html = await File.ReadAllBytesAsync(_htmlPath, ct);
                ctx.Response.StatusCode  = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                // Разрешаем открытие в iframe Telegram
                ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL";
                SetNoCacheHeaders(ctx);
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html, ct);
            }
            else if (path == "/timer/stop")
            {
                await HandleStopTimerAsync(ctx, ct);
            }
            else if (path == "/timer/status")
            {
                await HandleTimerStatusAsync(ctx, ct);
            }
            else if (await TryServeStaticFileAsync(ctx, path, ct))
            {
                return;
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке HTTP запроса");
            try { ctx.Response.StatusCode = 500; } catch { /* игнор */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* игнор */ }
        }
    }

    private async Task HandleStopTimerAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.QueryString;

        if (!long.TryParse(query["userId"], out var userId) ||
            !long.TryParse(query["chatId"], out var chatId))
        {
            await WriteJsonAsync(ctx, 400, """{"ok":false,"message":"bad_request"}""", ct);
            return;
        }

        var stopped = Guid.TryParse(query["timerId"], out var timerId)
            ? _timers.StopTimer(userId, timerId)
            : _timers.StopTimer(userId);

        if (stopped)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "⏹ Таймер остановлен из Mini App.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        var body = stopped
            ? """{"ok":true,"message":"stopped"}"""
            : """{"ok":false,"message":"not_active"}""";

        await WriteJsonAsync(ctx, 200, body, ct);
    }

    private async Task HandleTimerStatusAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.QueryString;

        if (!long.TryParse(query["userId"], out var userId) ||
            !Guid.TryParse(query["timerId"], out var timerId))
        {
            await WriteJsonAsync(ctx, 400, """{"ok":false,"active":false,"message":"bad_request"}""", ct);
            return;
        }

        var active = _timers.IsTimerActive(userId, timerId);
        var body = active
            ? """{"ok":true,"active":true}"""
            : """{"ok":true,"active":false}""";

        await WriteJsonAsync(ctx, 200, body, ct);
    }

    private async Task<bool> TryServeStaticFileAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        var relativePath = Uri.UnescapeDataString(path.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains(".."))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(_wwwrootPath, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_wwwrootPath), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            return false;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = GetContentType(fullPath);
        ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL";
        SetNoCacheHeaders(ctx);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        return true;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".mp3"  => "audio/mpeg",
            ".ogg"  => "audio/ogg",
            ".wav"  => "audio/wav",
            ".m4a"  => "audio/mp4",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "text/javascript; charset=utf-8",
            _       => "application/octet-stream"
        };

    private static void SetNoCacheHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int statusCode, string body, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
    }

    public override void Dispose()
    {
        _listener.Close();
        base.Dispose();
    }
}
