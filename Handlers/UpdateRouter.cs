using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Маршрутизатор обновлений от Telegram.
/// Получает сырой Update и отправляет его в нужный обработчик:
///   - Команды (/start, /help и т.д.) → CommandHandler
///   - Обычный текст в состоянии диалога → TextHandler
///   - Нажатие инлайн-кнопки → CallbackHandler
/// </summary>
public class UpdateRouter
{
    private readonly CommandHandler _commands;
    private readonly TextHandler    _text;
    private readonly CallbackHandler _callbacks;
    private readonly TimerService _timers;
    private readonly SessionService _sessions;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        CommandHandler commands,
        TextHandler    text,
        CallbackHandler callbacks,
        TimerService timers,
        SessionService sessions,
        ILogger<UpdateRouter> logger)
    {
        _commands  = commands;
        _text      = text;
        _callbacks = callbacks;
        _timers    = timers;
        _sessions  = sessions;
        _logger    = logger;
    }

    /// <summary>
    /// Точка входа для всех обновлений от Telegram.
    /// Вызывается из BotService при каждом входящем сообщении.
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            // Нажатие инлайн-кнопки
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

    /// <summary>
    /// Обработать входящее сообщение.
    /// Маршрутизирует по типу содержимого: текст.
    /// </summary>
    private async Task HandleMessageAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        if (msg.From is null)
        {
            _logger.LogDebug("Сообщение без отправителя пропущено. ChatId: {ChatId}", msg.Chat.Id);
            return;
        }

        if (msg.WebAppData is not null)
        {
            await HandleWebAppDataAsync(bot, msg, ct);
            return;
        }

        // ── Текстовое сообщение ───────────────────────────────
        if (msg.Text is not null)
        {
            var text = msg.Text.Trim();

            // Ищем часть, начинающуюся с '/' — поддерживает кнопки вида "📋 /plan"
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

    /// <summary>Обработать данные, отправленные из Telegram Mini App.</summary>
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

            case "save_schedule":
                await HandleMiniAppSaveScheduleAsync(bot, msg, root, ct);
                break;

            case "clear_schedule":
                _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName).Schedule.Clear();
                await bot.SendMessage(msg.Chat.Id, "🗑 Расписание очищено из Mini App.", cancellationToken: ct);
                break;
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
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
        var subject = root.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString()?.Trim() : null;
        var deadlineText = root.TryGetProperty("deadline", out var deadlineElement) ? deadlineElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(subject))
            return;

        DateTime? deadline = DateTime.TryParse(deadlineText, out var parsedDeadline)
            ? parsedDeadline.Date
            : null;

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        session.Tasks.Add(new StudyTask
        {
            Title = title,
            Subject = subject,
            Deadline = deadline
        });

        await bot.SendMessage(msg.Chat.Id, "📌 ДЗ добавлено из Mini App.", cancellationToken: ct);
    }

    private async Task HandleMiniAppSaveScheduleAsync(ITelegramBotClient bot, Message msg, JsonElement root, CancellationToken ct)
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
                Day = day,
                Time = time,
                Subject = subject,
                WeekType = weekType,
                IsPriority = isPriority
            });
        }

        await bot.SendMessage(msg.Chat.Id, $"🗓 Расписание сохранено из Mini App. Строк: {session.Schedule.Count}.", cancellationToken: ct);
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

    /// <summary>Маршрутизация команд по имени</summary>
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
