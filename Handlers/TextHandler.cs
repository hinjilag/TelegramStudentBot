using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class TextHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ReminderSettingsService _reminders;

    public TextHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ReminderSettingsService reminders)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _reminders = reminders;
    }

    public async Task HandleAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var text = msg.Text?.Trim() ?? string.Empty;

        switch (session.State)
        {
            case UserState.WaitingForTaskTitle:
                await HandleTaskTitleAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskSubject:
                await HandleTaskSubjectAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskDeadline:
                await HandleTaskDeadlineAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTimerMinutes:
                await HandleCustomTimerAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForReminderTime:
                await HandleReminderTimeAsync(msg, session, text, ct);
                break;

            default:
                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "Используй /help, чтобы посмотреть команды.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    private async Task HandleTaskTitleAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "Название не может быть пустым.", cancellationToken: ct);
            return;
        }

        session.DraftTask = new StudyTask { Title = text };
        session.State = UserState.WaitingForTaskSubject;

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"Название: <b>{TelegramHtml.Escape(text)}</b>\n\nТеперь введи <b>предмет</b>.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskSubjectAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "Предмет не может быть пустым.", cancellationToken: ct);
            return;
        }

        session.DraftTask!.Subject = text;
        session.State = UserState.WaitingForTaskDeadline;

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"Предмет: <b>{TelegramHtml.Escape(text)}</b>\n\n" +
                  "Теперь введи <b>дедлайн</b> в формате <code>ДД.ММ.ГГГГ</code>\n" +
                  "или напиши <b>нет</b>, если дедлайна нет.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskDeadlineAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (text.Equals("нет", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            text == "-")
        {
            session.DraftTask!.Deadline = null;
        }
        else
        {
            if (!DateTime.TryParseExact(
                    text,
                    new[] { "dd.MM.yyyy", "dd/MM/yyyy", "d.M.yyyy" },
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out var deadline))
            {
                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "Неверный формат. Используй <code>ДД.ММ.ГГГГ</code>, например <code>25.05.2026</code>, или напиши <b>нет</b>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }

            if (TaskDeadlineRules.IsInPast(deadline))
            {
                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: $"Дедлайн не может быть раньше сегодняшней даты. Укажи дату от <b>{TaskDeadlineRules.TodayForUser}</b> и позже.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }

            session.DraftTask!.Deadline = deadline.Date;
        }

        var task = session.DraftTask!;
        session.Tasks.Add(task);
        session.DraftTask = null;
        session.State = UserState.Idle;
        _sessions.Save();

        var deadlineText = task.Deadline.HasValue
            ? task.Deadline.Value.ToString("dd.MM.yyyy")
            : "не задан";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"🎉 <b>Задача добавлена</b>\n\n" +
                  $"📌 <b>{TelegramHtml.Escape(task.Title)}</b>\n" +
                  $"📚 Предмет: {TelegramHtml.Escape(task.Subject)}\n" +
                  $"📅 Дедлайн: {deadlineText}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleCustomTimerAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (!int.TryParse(text, out var minutes) || minutes < 1 || minutes > 300)
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Введи число минут от 1 до 300.",
                cancellationToken: ct);
            return;
        }

        session.State = UserState.Idle;
        await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }

    private async Task HandleReminderTimeAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (!TimeOnly.TryParseExact(text, "HH:mm", out var time) &&
            !TimeOnly.TryParseExact(text, "H:mm", out time))
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Нужен формат <b>HH:mm</b>, например <code>19:30</code>.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        _reminders.Enable(msg.From!.Id, msg.Chat.Id, time.Hour, time.Minute);
        session.State = UserState.Idle;
        var timeText = time.ToString("HH':'mm");

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"⏰ Напоминания включены на <b>{timeText}</b>. Раз в день я буду присылать дедлайны на сегодня и на завтра.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }
}
