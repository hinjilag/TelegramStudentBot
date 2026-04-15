using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Встроенный HTTP-сервер для Telegram Mini App.
///
/// Для Telegram Mini Apps URL должен быть HTTPS.
/// Для разработки можно запустить внешний туннель:
///   ngrok http 8080
/// Полученный HTTPS URL укажи в WebAppUrl.
/// </summary>
public class WebAppService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpListener _listener = new();
    private readonly string _wwwrootPath;
    private readonly string _htmlPath;
    private readonly int _port;
    private readonly string? _webAppHost;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<WebAppService> _logger;

    public WebAppService(
        IConfiguration config,
        SessionService sessions,
        TimerService timers,
        ITelegramBotClient bot,
        ILogger<WebAppService> logger)
    {
        _port     = config.GetValue("WebAppPort", 8080);
        _webAppHost = TryGetHost(config["WebAppUrl"]);
        _sessions = sessions;
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
            _logger.LogWarning("timer.html не найден по пути {Path} - Mini App недоступен.", _htmlPath);
            return;
        }

        try
        {
            if (!TryStartListener())
            {
                _logger.LogError("Не удалось запустить HTTP сервер на порту {Port}.", _port);
                return;
            }
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
            SetCorsHeaders(ctx);

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path == "/" || path == "/app" || path == "/timer")
            {
                await ServeMiniAppAsync(ctx, ct);
            }
            else if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(ctx, path, ct);
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
            try { await WriteJsonAsync(ctx, 500, new { ok = false, message = "server_error" }, ct); }
            catch { /* игнор */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* игнор */ }
        }
    }

    private bool TryStartListener()
    {
        var attempts = new List<string[]>
        {
            new[] { $"http://localhost:{_port}/", $"http://127.0.0.1:{_port}/" }
        };

        if (!string.IsNullOrWhiteSpace(_webAppHost))
        {
            attempts.Add(new[]
            {
                $"http://{_webAppHost}:{_port}/",
                $"http://localhost:{_port}/",
                $"http://127.0.0.1:{_port}/"
            });
        }

        attempts.Add(new[] { $"http://*:{_port}/" });

        foreach (var prefixes in attempts)
        {
            try
            {
                _listener.Prefixes.Clear();

                foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _listener.Prefixes.Add(prefix);
                }

                _listener.Start();
                _logger.LogInformation(
                    "Mini App HTTP сервер запущен: http://localhost:{Port}/app. Prefixes: {Prefixes}",
                    _port,
                    string.Join(", ", prefixes));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось запустить HTTP listener с prefixes: {Prefixes}", string.Join(", ", prefixes));
            }
        }

        return false;
    }

    private async Task ServeMiniAppAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var html = await File.ReadAllBytesAsync(_htmlPath, ct);
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL";
        SetNoCacheHeaders(ctx);
        ctx.Response.ContentLength64 = html.Length;
        await ctx.Response.OutputStream.WriteAsync(html, ct);
    }

    private async Task HandleApiAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        switch (path)
        {
            case "/api/state":
                await HandleStateAsync(ctx, ct);
                break;

            case "/api/timer/start":
                await HandleStartTimerFromApiAsync(ctx, ct);
                break;

            case "/api/timer/stop":
                await HandleStopTimerFromApiAsync(ctx, ct);
                break;

            case "/api/tasks/add":
                await HandleAddTaskAsync(ctx, ct);
                break;

            case "/api/tasks/toggle":
                await HandleToggleTaskAsync(ctx, ct);
                break;

            case "/api/tasks/delete":
                await HandleDeleteTaskAsync(ctx, ct);
                break;

            case "/api/schedule/save":
                await HandleSaveScheduleAsync(ctx, ct);
                break;

            case "/api/schedule/clear":
                await HandleClearScheduleAsync(ctx, ct);
                break;

            default:
                await WriteJsonAsync(ctx, 404, new { ok = false, message = "not_found" }, ct);
                break;
        }
    }

    private async Task HandleStateAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!TryGetUserId(ctx, out var userId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_user_id" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(userId);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleStartTimerFromApiAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<StartTimerRequest>(ctx, ct);
        if (request is null || request.UserId <= 0 || request.Minutes is < 1 or > 300)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var chatId = request.ChatId != 0 ? request.ChatId : request.UserId;
        if (string.Equals(request.Type, "rest", StringComparison.OrdinalIgnoreCase))
        {
            await _timers.StartRestTimerAsync(chatId, request.UserId, request.Minutes);
        }
        else
        {
            await _timers.StartWorkTimerAsync(chatId, request.UserId, request.Minutes);
        }

        await WriteStateAsync(ctx, _sessions.GetOrCreate(request.UserId), ct);
    }

    private async Task HandleStopTimerFromApiAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<StopTimerRequest>(ctx, ct);
        if (request is null || request.UserId <= 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var stopped = Guid.TryParse(request.TimerId, out var timerId)
            ? _timers.StopTimer(request.UserId, timerId)
            : _timers.StopTimer(request.UserId);

        if (stopped && request.ChatId != 0)
        {
            await _bot.SendMessage(
                chatId: request.ChatId,
                text: "⏹ Таймер остановлен из Mini App.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        await WriteStateAsync(ctx, _sessions.GetOrCreate(request.UserId), ct, new { stopped });
    }

    private async Task HandleAddTaskAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskRequest>(ctx, ct);
        if (request is null ||
            request.UserId <= 0 ||
            string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Subject))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(request.UserId);
        session.Tasks.Add(new StudyTask
        {
            Title = request.Title.Trim(),
            Subject = request.Subject.Trim(),
            Deadline = request.Deadline?.Date
        });

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleToggleTaskAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskToggleRequest>(ctx, ct);
        if (request is null || request.UserId <= 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(request.UserId);
        var task = FindTask(session, request.TaskId);
        if (task is null)
        {
            await WriteJsonAsync(ctx, 404, new { ok = false, message = "task_not_found" }, ct);
            return;
        }

        task.IsCompleted = request.IsCompleted;
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleDeleteTaskAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskDeleteRequest>(ctx, ct);
        if (request is null || request.UserId <= 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(request.UserId);
        var task = FindTask(session, request.TaskId);
        if (task is not null)
        {
            session.Tasks.Remove(task);
        }

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleSaveScheduleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<ScheduleSaveRequest>(ctx, ct);
        if (request is null || request.UserId <= 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(request.UserId);
        session.Schedule.Clear();

        foreach (var row in (request.Entries ?? []).Where(IsValidScheduleRow))
        {
            session.Schedule.Add(new ScheduleEntry
            {
                Id = Guid.TryParse(row.Id, out var id) ? id : Guid.NewGuid(),
                Day = Normalize(row.Day),
                Time = Normalize(row.Time),
                Subject = Normalize(row.Subject),
                WeekType = NormalizeWeekType(row.WeekType),
                IsPriority = row.IsPriority
            });
        }

        session.SchedulePhotoDataUrl = string.IsNullOrWhiteSpace(request.PhotoDataUrl)
            ? null
            : request.PhotoDataUrl;

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleClearScheduleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<UserRequest>(ctx, ct);
        if (request is null || request.UserId <= 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(request.UserId);
        session.Schedule.Clear();
        session.SchedulePhotoDataUrl = null;
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleStopTimerAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.QueryString;

        if (!long.TryParse(query["userId"], out var userId) ||
            !long.TryParse(query["chatId"], out var chatId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
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

        await WriteJsonAsync(ctx, 200, stopped
            ? new { ok = true, message = "stopped" }
            : new { ok = false, message = "not_active" }, ct);
    }

    private async Task HandleTimerStatusAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.QueryString;

        if (!long.TryParse(query["userId"], out var userId) ||
            !Guid.TryParse(query["timerId"], out var timerId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, active = false, message = "bad_request" }, ct);
            return;
        }

        var active = _timers.IsTimerActive(userId, timerId);
        await WriteJsonAsync(ctx, 200, new { ok = true, active }, ct);
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

    private async Task WriteStateAsync(
        HttpListenerContext ctx,
        UserSession session,
        CancellationToken ct,
        object? extra = null)
    {
        var activeTimer = session.ActiveTimer;
        object timer = activeTimer is null
            ? new { active = false }
            : new
            {
                active = true,
                id = activeTimer.Id.ToString("N"),
                type = activeTimer.Type == TimerType.Rest ? "rest" : "work",
                durationMinutes = activeTimer.DurationMinutes,
                startedAt = activeTimer.StartedAt,
                startedAtUnixMs = new DateTimeOffset(activeTimer.StartedAt).ToUnixTimeMilliseconds(),
                endsAt = activeTimer.EndsAt,
                remainingSeconds = Math.Max(0, (int)Math.Ceiling(activeTimer.Remaining.TotalSeconds))
            };

        await WriteJsonAsync(ctx, 200, new
        {
            ok = true,
            extra,
            profile = new
            {
                userId = session.UserId,
                firstName = session.FirstName,
                fatigueLevel = session.FatigueLevel,
                fatigueDescription = session.FatigueDescription,
                needsRest = session.NeedsRest,
                workSessionsWithoutRest = session.WorkSessionsWithoutRest
            },
            timer,
            tasks = session.Tasks
                .OrderBy(t => t.IsCompleted)
                .ThenBy(t => t.Deadline ?? DateTime.MaxValue)
                .Select(t => new
                {
                    id = t.Id.ToString("N"),
                    shortId = t.ShortId,
                    title = t.Title,
                    subject = t.Subject,
                    deadline = t.Deadline?.ToString("yyyy-MM-dd"),
                    isCompleted = t.IsCompleted,
                    createdAt = t.CreatedAt
                }),
            schedulePhotoDataUrl = session.SchedulePhotoDataUrl,
            schedule = session.Schedule
                .OrderBy(e => DayOrder(e.Day))
                .ThenBy(e => e.Time)
                .Select(e => new
                {
                    id = e.Id.ToString("N"),
                    shortId = e.ShortId,
                    day = e.Day,
                    time = e.Time,
                    subject = e.Subject,
                    weekType = e.WeekType,
                    isPriority = e.IsPriority
                })
        }, ct);
    }

    private static bool TryGetUserId(HttpListenerContext ctx, out long userId) =>
        long.TryParse(ctx.Request.QueryString["userId"], out userId) && userId > 0;

    private static StudyTask? FindTask(UserSession session, string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return null;

        return session.Tasks.FirstOrDefault(t =>
            t.Id.ToString("N").Equals(taskId, StringComparison.OrdinalIgnoreCase) ||
            t.ShortId.Equals(taskId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidScheduleRow(ScheduleRowRequest row) =>
        !string.IsNullOrWhiteSpace(row.Day) ||
        !string.IsNullOrWhiteSpace(row.Time) ||
        !string.IsNullOrWhiteSpace(row.Subject);

    private static int DayOrder(string day) => day switch
    {
        "Понедельник" => 1,
        "Вторник" => 2,
        "Среда" => 3,
        "Четверг" => 4,
        "Пятница" => 5,
        "Суббота" => 6,
        "Воскресенье" => 7,
        _ => 99
    };

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeWeekType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "even" => "even",
            "odd" => "odd",
            _ => "every"
        };

    private static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasEntityBody)
            return default;

        return await JsonSerializer.DeserializeAsync<T>(ctx.Request.InputStream, JsonOptions, ct);
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
            ".json" => "application/json; charset=utf-8",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _       => "application/octet-stream"
        };

    private static void SetNoCacheHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
    }

    private static void SetCorsHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext ctx,
        int statusCode,
        object body,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        SetCorsHeaders(ctx);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
    }

    public override void Dispose()
    {
        _listener.Close();
        base.Dispose();
    }

    private sealed record UserRequest(long UserId);
    private sealed record StartTimerRequest(long UserId, long ChatId, string? Type, int Minutes);
    private sealed record StopTimerRequest(long UserId, long ChatId, string? TimerId);
    private sealed record TaskRequest(long UserId, string Title, string Subject, DateTime? Deadline);
    private sealed record TaskToggleRequest(long UserId, string? TaskId, bool IsCompleted);
    private sealed record TaskDeleteRequest(long UserId, string? TaskId);
    private sealed record ScheduleSaveRequest(long UserId, string? PhotoDataUrl, List<ScheduleRowRequest> Entries);
    private sealed record ScheduleRowRequest(
        string? Id,
        string? Day,
        string? Time,
        string? Subject,
        string? WeekType,
        bool IsPriority);
}
