using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class WebAppService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _wwwrootPath;
    private readonly string _htmlPath;
    private readonly int _port;
    private readonly string _botToken;
    private readonly bool _skipInitDataValidation;
    private readonly TimeSpan _initDataMaxAge;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ChatSyncService _chatSync;
    private readonly ReminderSettingsService _reminders;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ILogger<WebAppService> _logger;

    public WebAppService(
        IConfiguration config,
        IHostEnvironment hostEnvironment,
        SessionService sessions,
        TimerService timers,
        ChatSyncService chatSync,
        ReminderSettingsService reminders,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ILogger<WebAppService> logger)
    {
        _port = config.GetValue("WebAppPort", 8080);
        _botToken = config["BotToken"] ?? "";
        _skipInitDataValidation = hostEnvironment.IsEnvironment("Local");
        _initDataMaxAge = TimeSpan.FromSeconds(config.GetValue("MiniAppInitDataMaxAgeSeconds", 86400));
        _sessions = sessions;
        _timers = timers;
        _chatSync = chatSync;
        _reminders = reminders;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _logger = logger;
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

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");

        await using var app = builder.Build();
        app.Run(context => HandleAsync(context, ct));

        try
        {
            await app.StartAsync(ct);
            _logger.LogInformation("Mini App HTTP сервер запущен: http://localhost:{Port}/app", _port);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить HTTP сервер на порту {Port}.", _port);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            _logger.LogInformation("Mini App HTTP сервер остановлен.");
        }
    }

    private async Task HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            SetCorsHeaders(ctx);

            if (ctx.Request.Method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            var path = ctx.Request.Path.Value ?? "/";

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
            try
            {
                await WriteJsonAsync(ctx, 500, new { ok = false, message = "server_error" }, ct);
            }
            catch
            {
            }
        }
    }

    private async Task ServeMiniAppAsync(HttpContext ctx, CancellationToken ct)
    {
        var html = await File.ReadAllBytesAsync(_htmlPath, ct);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL";
        SetNoCacheHeaders(ctx);
        ctx.Response.ContentLength = html.Length;
        await ctx.Response.Body.WriteAsync(html, ct);
    }

    private async Task HandleApiAsync(HttpContext ctx, string path, CancellationToken ct)
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
            case "/api/schedule/select":
                await HandleSelectScheduleAsync(ctx, ct);
                break;
            case "/api/reminders/save":
                await HandleSaveRemindersAsync(ctx, ct);
                break;
            default:
                await WriteJsonAsync(ctx, 404, new { ok = false, message = "not_found" }, ct);
                break;
        }
    }

    private async Task HandleStateAsync(HttpContext ctx, CancellationToken ct)
    {
        TryGetUserId(ctx, out var requestedUserId);

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, requestedUserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleStartTimerFromApiAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<StartTimerRequest>(ctx, ct);
        if (request is null || request.Minutes is < 1 or > 300)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        var chatId = ResolveTrustedChatId(session, request.ChatId);
        if (string.Equals(request.Type, "rest", StringComparison.OrdinalIgnoreCase))
            await _timers.StartRestTimerAsync(chatId, userId.Value, request.Minutes);
        else
            await _timers.StartWorkTimerAsync(chatId, userId.Value, request.Minutes);

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleStopTimerFromApiAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<StopTimerRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var stopped = Guid.TryParse(request.TimerId, out var timerId)
            ? _timers.StopTimer(userId.Value, timerId)
            : _timers.StopTimer(userId.Value);

        if (stopped)
        {
            var session = _sessions.GetOrCreate(userId.Value);
            await _chatSync.TrySendTimerStoppedAsync(ResolveTrustedChatId(session, request.ChatId), ct);
        }

        await WriteStateAsync(ctx, _sessions.GetOrCreate(userId.Value), ct, new { stopped });
    }

    private async Task HandleAddTaskAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskRequest>(ctx, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Subject))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        if (request.Deadline.HasValue && TaskDeadlineRules.IsInPast(request.Deadline.Value))
        {
            await WriteJsonAsync(ctx, 400, new
            {
                ok = false,
                message = "deadline_in_past",
                minDeadline = TaskDeadlineRules.TodayForInput
            }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(userId.Value);
        var task = new StudyTask
        {
            Title = request.Title.Trim(),
            Subject = request.Subject.Trim(),
            Deadline = request.Deadline?.Date
        };

        session.Tasks.Add(task);
        _sessions.Save();
        await _chatSync.TrySendTaskAddedAsync(ResolveTrustedChatId(session, request.ChatId), task, ct);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleToggleTaskAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskToggleRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        var task = FindTask(session, request.TaskId);
        if (task is null)
        {
            await WriteJsonAsync(ctx, 404, new { ok = false, message = "task_not_found" }, ct);
            return;
        }

        task.IsCompleted = request.IsCompleted;
        _sessions.Save();
        await _chatSync.TrySendTaskStatusChangedAsync(ResolveTrustedChatId(session, request.ChatId), task, ct);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleDeleteTaskAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<TaskDeleteRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        var task = FindTask(session, request.TaskId);
        if (task is not null)
        {
            session.Tasks.Remove(task);
            _sessions.Save();
            await _chatSync.TrySendTaskDeletedAsync(ResolveTrustedChatId(session, request.ChatId), task, ct);
        }

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleSaveScheduleAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<ScheduleSaveRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
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

        session.SchedulePhotoDataUrl = string.IsNullOrWhiteSpace(request.PhotoDataUrl) ? null : request.PhotoDataUrl;
        _sessions.Save();

        await _chatSync.TrySendScheduleSavedAsync(
            ResolveTrustedChatId(session, request.ChatId),
            session.Schedule,
            session.SchedulePhotoDataUrl is not null,
            ct);

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleClearScheduleAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<UserRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        _scheduleSelections.Delete(userId.Value);
        session.Schedule.Clear();
        session.SchedulePhotoDataUrl = null;
        _sessions.Save();
        await _chatSync.TrySendScheduleClearedAsync(ResolveTrustedChatId(session, request.ChatId), ct);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleSelectScheduleAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<ScheduleSelectRequest>(ctx, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.ScheduleId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var group = _scheduleCatalog.GetGroup(request.ScheduleId);
        if (group is null)
        {
            await WriteJsonAsync(ctx, 404, new { ok = false, message = "schedule_not_found" }, ct);
            return;
        }

        if (request.SubGroup.HasValue && group.SubGroups.Count > 0 && !group.SubGroups.Contains(request.SubGroup.Value))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_subgroup" }, ct);
            return;
        }

        var session = _sessions.GetOrCreate(userId.Value);
        _scheduleSelections.Save(userId.Value, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = request.SubGroup
        });
        session.Schedule = _scheduleCatalog.GetAllEntriesForSelection(group, request.SubGroup);
        session.SchedulePhotoDataUrl = null;
        _sessions.Save();

        await _chatSync.TrySendScheduleSavedAsync(
            ResolveTrustedChatId(session, request.ChatId),
            session.Schedule,
            hasPhoto: false,
            ct);

        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleSaveRemindersAsync(HttpContext ctx, CancellationToken ct)
    {
        var request = await ReadJsonAsync<ReminderSaveRequest>(ctx, ct);
        if (request is null)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var userId = await TryResolveAuthorizedUserIdAsync(ctx, request.UserId, ct);
        if (userId is null)
            return;

        var session = _sessions.GetOrCreate(userId.Value);
        var chatId = ResolveTrustedChatId(session, request.ChatId);

        if (!request.IsEnabled)
        {
            _reminders.Disable(userId.Value, chatId);
            await WriteStateAsync(ctx, session, ct);
            return;
        }

        if (!TryParseReminderTime(request.Time, out var hour, out var minute))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_time" }, ct);
            return;
        }

        _reminders.Enable(userId.Value, chatId, hour, minute);
        await WriteStateAsync(ctx, session, ct);
    }

    private async Task HandleStopTimerAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        if (!long.TryParse(query["userId"], out var userId) || !long.TryParse(query["chatId"], out var chatId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_request" }, ct);
            return;
        }

        var stopped = Guid.TryParse(query["timerId"], out var timerId)
            ? _timers.StopTimer(userId, timerId)
            : _timers.StopTimer(userId);

        if (stopped)
        {
            var session = _sessions.GetOrCreate(userId);
            await _chatSync.TrySendTimerStoppedAsync(ResolveTrustedChatId(session, chatId), ct);
        }

        await WriteJsonAsync(ctx, 200, stopped
            ? new { ok = true, message = "stopped" }
            : new { ok = false, message = "not_active" }, ct);
    }

    private async Task HandleTimerStatusAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        if (!long.TryParse(query["userId"], out var userId) || !Guid.TryParse(query["timerId"], out var timerId))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, active = false, message = "bad_request" }, ct);
            return;
        }

        var active = _timers.IsTimerActive(userId, timerId);
        await WriteJsonAsync(ctx, 200, new { ok = true, active }, ct);
    }

    private async Task<bool> TryServeStaticFileAsync(HttpContext ctx, string path, CancellationToken ct)
    {
        var relativePath = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains(".."))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(_wwwrootPath, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_wwwrootPath), StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return false;

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = GetContentType(fullPath);
        ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL";
        SetNoCacheHeaders(ctx);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct);
        return true;
    }

    private async Task WriteStateAsync(HttpContext ctx, UserSession session, CancellationToken ct, object? extra = null)
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

        var reminderSettings = _reminders.Get(session.UserId);
        var selection = _scheduleSelections.Get(session.UserId);
        var selectedGroup = selection is null ? null : _scheduleCatalog.GetGroup(selection.ScheduleId);

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
            reminders = new
            {
                isEnabled = reminderSettings.IsEnabled,
                time = reminderSettings.TimeText
            },
            scheduleSelection = new
            {
                scheduleId = selection?.ScheduleId,
                subGroup = selection?.SubGroup,
                title = selectedGroup?.Title,
                semester = _scheduleCatalog.Semester,
                currentWeekLabel = _scheduleCatalog.GetCurrentWeekLabel()
            },
            scheduleCatalog = _scheduleCatalog.GetDirections()
                .SelectMany(direction => _scheduleCatalog.GetGroupsByDirection(direction.DirectionCode))
                .Select(group => new
                {
                    id = group.Id,
                    title = group.Title,
                    directionCode = group.DirectionCode,
                    directionName = group.DirectionName,
                    shortTitle = group.ShortTitle,
                    course = group.Course,
                    subGroups = group.SubGroups
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

    private static bool TryGetUserId(HttpContext ctx, out long userId) =>
        long.TryParse(ctx.Request.Query["userId"].ToString(), out userId) && userId > 0;

    private long ResolveTrustedChatId(UserSession session, long requestedChatId)
    {
        if (session.LastChatId != 0)
            return session.LastChatId;

        if (_skipInitDataValidation && requestedChatId != 0)
            return requestedChatId;

        return session.UserId;
    }

    private async Task<long?> TryResolveAuthorizedUserIdAsync(HttpContext ctx, long requestedUserId, CancellationToken ct)
    {
        if (_skipInitDataValidation)
        {
            if (requestedUserId > 0)
                return requestedUserId;

            await WriteJsonAsync(ctx, 400, new { ok = false, message = "bad_user_id" }, ct);
            return null;
        }

        var initData = ctx.Request.Headers["X-Telegram-Init-Data"].ToString();
        if (!TelegramMiniAppInitDataValidator.TryValidate(initData, _botToken, _initDataMaxAge, out var authData, out var errorCode))
        {
            _logger.LogWarning("Mini App init data validation failed: {ErrorCode}", errorCode);
            await WriteJsonAsync(ctx, 401, new { ok = false, message = errorCode }, ct);
            return null;
        }

        return authData.UserId;
    }

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

    private static bool TryParseReminderTime(string? value, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!TimeOnly.TryParseExact(value, "HH:mm", out var time) &&
            !TimeOnly.TryParseExact(value, "H:mm", out time))
        {
            return false;
        }

        hour = time.Hour;
        minute = time.Minute;
        return true;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Request.ContentLength is 0 or null)
            return default;

        return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonOptions, ct);
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static void SetNoCacheHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
    }

    private static void SetCorsHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Telegram-Init-Data";
    }

    private static async Task WriteJsonAsync(HttpContext ctx, int statusCode, object body, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        SetCorsHeaders(ctx);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct);
    }

    private sealed record UserRequest(long UserId, long ChatId);
    private sealed record StartTimerRequest(long UserId, long ChatId, string? Type, int Minutes);
    private sealed record StopTimerRequest(long UserId, long ChatId, string? TimerId);
    private sealed record TaskRequest(long UserId, long ChatId, string Title, string Subject, DateTime? Deadline);
    private sealed record TaskToggleRequest(long UserId, long ChatId, string? TaskId, bool IsCompleted);
    private sealed record TaskDeleteRequest(long UserId, long ChatId, string? TaskId);
    private sealed record ScheduleSaveRequest(long UserId, long ChatId, string? PhotoDataUrl, List<ScheduleRowRequest> Entries);
    private sealed record ScheduleSelectRequest(long UserId, long ChatId, string ScheduleId, int? SubGroup);
    private sealed record ReminderSaveRequest(long UserId, long ChatId, bool IsEnabled, string? Time);
    private sealed record ScheduleRowRequest(
        string? Id,
        string? Day,
        string? Time,
        string? Subject,
        string? WeekType,
        bool IsPriority);
}
