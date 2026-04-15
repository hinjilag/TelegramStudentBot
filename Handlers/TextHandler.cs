using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик обычных текстовых сообщений.
/// Работает как машина состояний: реакция зависит от текущего состояния сессии.
/// </summary>
public class TextHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;

    public TextHandler(ITelegramBotClient bot, SessionService sessions, TimerService timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    public async Task HandleAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var text    = msg.Text?.Trim() ?? string.Empty;

        switch (session.State)
        {
            // ── Создание задачи ───────────────────────────────
            case UserState.WaitingForTaskTitle:
                await HandleTaskTitleAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskSubject:
                await HandleTaskSubjectAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskDeadline:
                await HandleTaskDeadlineAsync(msg, session, text, ct);
                break;

            // ── Таймер ────────────────────────────────────────
            case UserState.WaitingForTimerMinutes:
                await HandleCustomTimerAsync(msg, session, text, ct);
                break;

            // ── Добавление расписания ─────────────────────────
            case UserState.WaitingForScheduleDay:
                await HandleScheduleDayAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForScheduleSubject:
                await HandleScheduleSubjectAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForScheduleTime:
                await HandleScheduleTimeAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForScheduleWeekType:
                await HandleScheduleWeekTypeAsync(msg, session, text, ct);
                break;

            default:
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "ℹ️ Используй /help для просмотра команд.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Создание задачи
    // ══════════════════════════════════════════════════════════

    private async Task HandleTaskTitleAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Название не может быть пустым:", cancellationToken: ct);
            return;
        }

        session.DraftTask = new StudyTask { Title = text };
        session.State     = UserState.WaitingForTaskSubject;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Название: <b>{TelegramHtml.Escape(text)}</b>\n\nВведи <b>предмет</b> (например: Математика):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskSubjectAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Предмет не может быть пустым:", cancellationToken: ct);
            return;
        }

        session.DraftTask!.Subject = text;
        session.State              = UserState.WaitingForTaskDeadline;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Предмет: <b>{TelegramHtml.Escape(text)}</b>\n\n" +
                       $"Введи <b>дедлайн</b> в формате ДД.ММ.ГГГГ\n" +
                       $"(или напиши <b>нет</b>, чтобы пропустить):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskDeadlineAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (text.Equals("нет", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no",  StringComparison.OrdinalIgnoreCase) ||
            text == "-")
        {
            session.DraftTask!.Deadline = null;
        }
        else
        {
            if (!DateTime.TryParseExact(text, new[] { "dd.MM.yyyy", "dd/MM/yyyy", "d.M.yyyy" },
                    null, System.Globalization.DateTimeStyles.None, out var deadline))
            {
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "⚠️ Неверный формат. Используй ДД.ММ.ГГГГ (например: 25.05.2025)\n" +
                               "Или напиши <b>нет</b> чтобы пропустить.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }
            session.DraftTask!.Deadline = deadline;
        }

        var task = session.DraftTask!;
        session.Tasks.Add(task);
        session.DraftTask = null;
        session.State     = UserState.Idle;

        var deadlineText = task.Deadline.HasValue
            ? task.Deadline.Value.ToString("dd.MM.yyyy")
            : "не задан";

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"🎉 <b>Задача добавлена!</b>\n\n" +
                       $"📌 <b>{TelegramHtml.Escape(task.Title)}</b>\n" +
                       $"📚 Предмет: {TelegramHtml.Escape(task.Subject)}\n" +
                       $"📅 Дедлайн: {deadlineText}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Таймер
    // ══════════════════════════════════════════════════════════

    private async Task HandleCustomTimerAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (!int.TryParse(text, out int minutes) || minutes < 1 || minutes > 300)
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text:   "⚠️ Введи число минут от 1 до 300:",
                cancellationToken: ct);
            return;
        }

        session.State = UserState.Idle;
        await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }

    // ══════════════════════════════════════════════════════════
    //  Добавление расписания (4 шага)
    // ══════════════════════════════════════════════════════════

    private static readonly HashSet<string> ValidDays = new(StringComparer.OrdinalIgnoreCase)
    {
        "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"
    };

    /// <summary>Шаг 1: день недели</summary>
    private async Task HandleScheduleDayAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(
                msg.Chat.Id,
                "⚠️ Введи один из дней:\n" +
                "Понедельник / Вторник / Среда / Четверг / Пятница / Суббота / Воскресенье",
                cancellationToken: ct);
            return;
        }

        // Нормализуем первую букву
        var day = char.ToUpper(text[0]) + text[1..].ToLower();

        if (!ValidDays.Contains(day))
        {
            await _bot.SendMessage(
                msg.Chat.Id,
                "⚠️ Введи один из дней:\n" +
                "Понедельник / Вторник / Среда / Четверг / Пятница / Суббота / Воскресенье",
                cancellationToken: ct);
            return;
        }

        session.DraftSchedule!.Day = day;
        session.State = UserState.WaitingForScheduleSubject;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ День: <b>{day}</b>\n\nВведи <b>название предмета</b>:",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>Шаг 2: предмет</summary>
    private async Task HandleScheduleSubjectAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Название предмета не может быть пустым:", cancellationToken: ct);
            return;
        }

        session.DraftSchedule!.Subject = text;
        session.State = UserState.WaitingForScheduleTime;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Предмет: <b>{TelegramHtml.Escape(text)}</b>\n\n" +
                       $"Введи <b>время занятия</b>\n(например: <code>09:00-10:30</code> или <code>9:00</code>):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>Шаг 3: время</summary>
    private async Task HandleScheduleTimeAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Введи время (например: 09:00-10:30):", cancellationToken: ct);
            return;
        }

        session.DraftSchedule!.Time = text;
        session.State = UserState.WaitingForScheduleWeekType;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Время: <b>{TelegramHtml.Escape(text)}</b>\n\n" +
                       $"Укажи, когда проходит пара:\n" +
                       $"<b>каждую</b> / <b>четная</b> / <b>нечетная</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>Шаг 4: тип недели</summary>
    private async Task HandleScheduleWeekTypeAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        session.DraftSchedule!.WeekType = NormalizeWeekType(text);

        var entry = session.DraftSchedule!;
        session.Schedule.Add(entry);
        session.DraftSchedule = null;
        session.State         = UserState.Idle;

        var weekText = entry.WeekType switch
        {
            "even" => "чётная неделя",
            "odd" => "нечётная неделя",
            _ => "каждую неделю"
        };

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ <b>Занятие добавлено!</b>\n\n" +
                       $"🗓 <b>{TelegramHtml.Escape(entry.Day)}</b>, {TelegramHtml.Escape(entry.Time)}\n" +
                       $"📚 {TelegramHtml.Escape(entry.Subject)}\n" +
                       $"🔁 {weekText}\n\n" +
                       $"Добавь ещё через /schedule.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private static string NormalizeWeekType(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace('ё', 'е');
        return normalized switch
        {
            "четная" or "чётная" or "чет" or "even" => "even",
            "нечетная" or "нечётная" or "нечет" or "odd" => "odd",
            _ => "every"
        };
    }
}
