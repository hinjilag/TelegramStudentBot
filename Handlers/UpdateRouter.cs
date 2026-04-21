using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class UpdateRouter
{
    private readonly CommandHandler _commands;
    private readonly TextHandler _text;
    private readonly CallbackHandler _callbacks;
    private readonly TimerService _timers;
    private readonly SessionService _sessions;
    private readonly ChatSyncService _chatSync;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        CommandHandler commands,
        TextHandler text,
        CallbackHandler callbacks,
        TimerService timers,
        SessionService sessions,
        ChatSyncService chatSync,
        ILogger<UpdateRouter> logger)
    {
        _commands = commands;
        _text = text;
        _callbacks = callbacks;
        _timers = timers;
        _sessions = sessions;
        _chatSync = chatSync;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
            {
                await _callbacks.HandleAsync(update.CallbackQuery, ct);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message is not null)
            {
                await HandleMessageAsync(bot, update.Message, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка при обработке обновления {UpdateType}", update.Type);
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        if (msg.From is null)
        {
            _logger.LogDebug("Сообщение без отправителя пропущено. ChatId: {ChatId}", msg.Chat.Id);
            return;
        }

        var session = _sessions.GetOrCreate(msg.From.Id, msg.From.FirstName);
        if (session.LastChatId != msg.Chat.Id)
        {
            session.LastChatId = msg.Chat.Id;
            _sessions.Save();
        }

        if (msg.WebAppData is not null)
        {
            await HandleWebAppDataAsync(bot, msg, ct);
            return;
        }

        if (msg.Text is not null)
        {
            var text = msg.Text.Trim();
            var commandPart = text
                .Split(' ')
                .Select(p => p.Split('@')[0].ToLowerInvariant())
                .FirstOrDefault(p => p.StartsWith('/'));

            if (commandPart is not null)
            {
                await RouteCommandAsync(msg, commandPart, ct);
            }
            else
            {
                await _text.HandleAsync(msg, ct);
            }
        }
    }

    private async Task HandleWebAppDataAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        var data = msg.WebAppData?.Data;
        if (string.IsNullOrWhiteSpace(data))
            return;

        JsonElement root;
        string? action = null;
        try
        {
            using var payload = JsonDocument.Parse(data);
            root = payload.RootElement.Clone();
            if (payload.RootElement.TryGetProperty("action", out var actionElement))
            {
                action = actionElement.GetString();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Некорректные данные из Mini App: {Data}", data);
            return;
        }

        switch (action)
        {
            case "stop_timer":
                await HandleMiniAppStopTimerAsync(bot, msg, ct);
                break;

            case "start_timer":
                await HandleMiniAppStartTimerAsync(bot, msg, root, ct);
                break;

            case "add_task":
                await HandleMiniAppAddTaskAsync(bot, msg, root, ct);
                break;

            case "toggle_task":
                await HandleMiniAppToggleTaskAsync(msg, root, ct);
                break;

            case "delete_task":
                await HandleMiniAppDeleteTaskAsync(msg, root, ct);
                break;

            case "save_schedule":
                await HandleMiniAppSaveScheduleAsync(msg, root, ct);
                break;

            case "clear_schedule":
            {
                var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
                session.Schedule.Clear();
                session.SchedulePhotoDataUrl = null;
                _sessions.Save();
                await _chatSync.TrySendScheduleClearedAsync(msg.Chat.Id, ct);
                break;
            }
        }
    }

    private async Task HandleMiniAppStopTimerAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        var stopped = _timers.StopTimer(msg.From!.Id);
        var text = stopped
            ? "⏹ Таймер остановлен из Mini App."
            : "ℹ️ Нет активного таймера.";

        await bot.SendMessage(msg.Chat.Id, text, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleMiniAppStartTimerAsync(ITelegramBotClient bot, Message msg, JsonElement root, CancellationToken ct)
    {
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "work";
        var minutes = root.TryGetProperty("minutes", out var minutesElement) ? minutesElement.GetInt32() : 25;

        if (minutes is < 1 or > 300)
        {
            await bot.SendMessage(msg.Chat.Id, "⚠️ Время таймера должно быть от 1 до 300 минут.", cancellationToken: ct);
            return;
        }

        if (string.Equals(type, "rest", StringComparison.OrdinalIgnoreCase))
            await _timers.StartRestTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
        else
            await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }

    private async Task HandleMiniAppAddTaskAsync(ITelegramBotClient bot, Message msg, JsonElement root, CancellationToken ct)
    {
        var idText = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
        var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString()?.Trim() : null;
        var deadlineText = root.TryGetProperty("deadline", out var deadlineElement) ? deadlineElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(subject))
            return;

        DateTime? deadline = DateTime.TryParse(deadlineText, out var parsedDeadline)
            ? parsedDeadline.Date
            : null;

        if (deadline.HasValue && TaskDeadlineRules.IsInPast(deadline.Value))
        {
            await bot.SendMessage(
                chatId: msg.Chat.Id,
                text: $"⚠️ Дедлайн не может быть раньше сегодняшней даты. Укажи дату от <b>{TaskDeadlineRules.TodayForUser}</b> и позже.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var task = new StudyTask
        {
            Id = Guid.TryParse(idText, out var id) ? id : Guid.NewGuid(),
            Title = title,
            Subject = subject,
            Deadline = deadline
        };
        session.Tasks.Add(task);
        _sessions.Save();

        await _chatSync.TrySendTaskAddedAsync(msg.Chat.Id, task, ct);
    }

    private async Task HandleMiniAppToggleTaskAsync(Message msg, JsonElement root, CancellationToken ct)
    {
        var taskId = root.TryGetProperty("taskId", out var taskIdElement) ? taskIdElement.GetString() : null;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() : null;
        var isCompleted = root.TryGetProperty("isCompleted", out var completedElement) &&
                          completedElement.ValueKind == JsonValueKind.True;

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var task = FindTask(session, taskId, title, subject);
        if (task is null)
            return;

        task.IsCompleted = isCompleted;
        _sessions.Save();
        await _chatSync.TrySendTaskStatusChangedAsync(msg.Chat.Id, task, ct);
    }

    private async Task HandleMiniAppDeleteTaskAsync(Message msg, JsonElement root, CancellationToken ct)
    {
        var taskId = root.TryGetProperty("taskId", out var taskIdElement) ? taskIdElement.GetString() : null;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() : null;

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var task = FindTask(session, taskId, title, subject);
        if (task is null)
            return;

        session.Tasks.Remove(task);
        _sessions.Save();
        await _chatSync.TrySendTaskDeletedAsync(msg.Chat.Id, task, ct);
    }

    private async Task HandleMiniAppSaveScheduleAsync(Message msg, JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("entries", out var entriesElement) ||
            entriesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        session.Schedule.Clear();

        foreach (var item in entriesElement.EnumerateArray())
        {
            var day = GetString(item, "day");
            var time = GetString(item, "time");
            var subject = GetString(item, "subject");
            var weekType = NormalizeWeekType(GetString(item, "weekType"));
            var isPriority = item.TryGetProperty("isPriority", out var priorityElement) &&
                             priorityElement.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(day) &&
                string.IsNullOrWhiteSpace(time) &&
                string.IsNullOrWhiteSpace(subject))
            {
                continue;
            }

            session.Schedule.Add(new ScheduleEntry
            {
                Id = Guid.TryParse(GetString(item, "id"), out var id) ? id : Guid.NewGuid(),
                Day = day,
                Time = time,
                Subject = subject,
                WeekType = weekType,
                IsPriority = isPriority
            });
        }
        _sessions.Save();

        await _chatSync.TrySendScheduleSavedAsync(
            msg.Chat.Id,
            session.Schedule,
            session.SchedulePhotoDataUrl is not null,
            ct);
    }

    private static StudyTask? FindTask(UserSession session, string? taskId, string? title, string? subject)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var task = session.Tasks.FirstOrDefault(t =>
                t.Id.ToString("N").Equals(taskId, StringComparison.OrdinalIgnoreCase) ||
                t.ShortId.Equals(taskId, StringComparison.OrdinalIgnoreCase));

            if (task is not null)
                return task;
        }

        return session.Tasks.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(title) &&
            !string.IsNullOrWhiteSpace(subject) &&
            t.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase) &&
            t.Subject.Equals(subject.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) ? element.GetString()?.Trim() ?? "" : "";

    private static string NormalizeWeekType(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "even" => "even",
            "odd" => "odd",
            _ => "every"
        };

    private async Task RouteCommandAsync(Message msg, string command, CancellationToken ct)
    {
        _logger.LogDebug("Команда {Command} от пользователя {UserId}", command, msg.From?.Id);

        switch (command)
        {
            case "/start":
                await _commands.HandleStartAsync(msg, ct);
                break;

            case "/help":
                await _commands.HandleHelpAsync(msg, ct);
                break;

            case "/app":
                await _commands.HandleAppAsync(msg, ct);
                break;

            case "/timer":
                await _commands.HandleTimerAsync(msg, ct);
                break;

            case "/rest":
                await _commands.HandleRestAsync(msg, ct);
                break;

            case "/stop":
                await _commands.HandleStopAsync(msg, ct);
                break;

            case "/fatigue":
                await _commands.HandleFatigueAsync(msg, ct);
                break;

            case "/status":
                await _commands.HandleStatusAsync(msg, ct);
                break;

            case "/plan":
                await _commands.HandlePlanAsync(msg, ct);
                break;

            case "/schedule":
                await _commands.HandleScheduleAsync(msg, ct);
                break;

            default:
                await _text.HandleAsync(msg, ct);
                break;
        }
    }
}
