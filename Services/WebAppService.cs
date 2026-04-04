using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly string _htmlPath;
    private readonly int _port;
    private readonly ILogger<WebAppService> _logger;

    public WebAppService(IConfiguration config, ILogger<WebAppService> logger)
    {
        _port     = config.GetValue("WebAppPort", 8080);
        _logger   = logger;
        _htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "timer.html");
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
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html, ct);
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

    public override void Dispose()
    {
        _listener.Close();
        base.Dispose();
    }
}
