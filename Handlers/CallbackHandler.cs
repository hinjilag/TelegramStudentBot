using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик нажатий на инлайн-кнопки (CallbackQuery).
///
/// Формат callback_data:
///   timer_25/30/45/60      — запустить рабочий таймер
///   timer_custom           — произвольное время
///   timer_stop             — остановить таймер
///   rest_5/15/30           — таймер отдыха
///   plan_add               — добавить задачу
///   plan_list              — список задач
///   schedule_add           — добавить занятие в расписание
///   schedule_list          — показать расписание
///   schedule_clear         — очистить расписание
///   task_done_{shortId}    — отметить задачу выполненной
///   task_del_{shortId}     — удалить задачу
/// </summary>
public class CallbackHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    /// <summary>Обработать входящий callback query</summary>
    public async Task HandleAsync(CallbackQuery query, CancellationToken ct)
    {
        var userId = query.From.Id;
        var data   = query.Data ?? string.Empty;

        // Подтверждаем получение (убирает часики у кнопки в Telegram)
        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (query.Message is null)
            return;

        var chatId = query.Message.Chat.Id;
        var session = _sessions.GetOrCreate(userId, query.From.FirstName);

        // ── Таймеры ──────────────────────────────────────────
        if (data.StartsWith("timer_"))
        {
            await HandleTimerCallbackAsync(chatId, userId, session, data, ct);
            return;
        }

        // ── Отдых ────────────────────────────────────────────
        if (data.StartsWith("rest_"))
        {
            await HandleRestCallbackAsync(chatId, userId, data, ct);
            return;
        }

        // ── Планирование ─────────────────────────────────────
        if (data.StartsWith("plan_"))
        {
            await HandlePlanCallbackAsync(chatId, session, data, ct);
            return;
        }

        // ── Управление задачами ───────────────────────────────
        if (data.StartsWith("task_"))
        {
            await HandleTaskCallbackAsync(chatId, session, data, ct);
            return;
        }

        // ── Расписание ────────────────────────────────────────
        if (data.StartsWith("schedule_"))
        {
            await HandleScheduleCallbackAsync(chatId, session, data, ct);
            return;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Таймеры
    // ══════════════════════════════════════════════════════════

    private async Task HandleTimerCallbackAsync(long chatId, long userId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "timer_25":
            case "timer_30":
            case "timer_45":
            case "timer_60":
            {
                var minutes = int.Parse(data.Split('_')[1]);
                await _timers.StartWorkTimerAsync(chatId, userId, minutes);
                break;
            }

            case "timer_custom":
            {
                session.State = UserState.WaitingForTimerMinutes;
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      "✏️ Введи количество минут (от 1 до 300):",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }

            case "timer_stop":
            {
                var stopped = _timers.StopTimer(userId);
                var text    = stopped ? "⏹ Таймер <b>остановлен</b>." : "ℹ️ Нет активного таймера.";
                await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Отдых
    // ══════════════════════════════════════════════════════════

    private async Task HandleRestCallbackAsync(long chatId, long userId, string data, CancellationToken ct)
    {
        if (int.TryParse(data.Split('_')[1], out int minutes))
        {
            await _timers.StartRestTimerAsync(chatId, userId, minutes);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Планирование
    // ══════════════════════════════════════════════════════════

    private async Task HandlePlanCallbackAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "plan_add":
            {
                session.State     = UserState.WaitingForTaskTitle;
                session.DraftTask = null;

                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      "📝 <b>Добавление задачи</b>\n\nВведи <b>название</b> задачи:",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }

            case "plan_list":
            {
                await SendTaskListAsync(chatId, session, ct);
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Управление конкретными задачами
    // ══════════════════════════════════════════════════════════

    private async Task HandleTaskCallbackAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        var parts = data.Split('_', 3);
        if (parts.Length < 3) return;

        var action  = parts[1];
        var shortId = parts[2];

        var task = session.Tasks.FirstOrDefault(t => t.ShortId == shortId);

        if (task is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Задача не найдена.", cancellationToken: ct);
            return;
        }

        switch (action)
        {
            case "done":
            {
                task.IsCompleted = true;
                var title = TelegramHtml.Escape(task.Title);
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      $"✅ Задача <b>«{title}»</b> отмечена как выполненная! 🎉",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }

            case "del":
            {
                var title = TelegramHtml.Escape(task.Title);
                session.Tasks.Remove(task);
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      $"🗑 Задача <b>«{title}»</b> удалена.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Расписание
    // ══════════════════════════════════════════════════════════

    private async Task HandleScheduleCallbackAsync(
        long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "schedule_add":
            {
                session.State         = UserState.WaitingForScheduleDay;
                session.DraftSchedule = new ScheduleEntry();
                await _bot.SendMessage(
                    chatId,
                    "🗓 <b>Добавление занятия</b>\n\n" +
                    "Введи <b>день недели</b>:\n" +
                    "Понедельник / Вторник / Среда / Четверг / Пятница / Суббота / Воскресенье",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }

            case "schedule_list":
            {
                await SendScheduleListAsync(chatId, session, ct);
                break;
            }

            case "schedule_clear":
            {
                session.Schedule.Clear();
                await _bot.SendMessage(
                    chatId,
                    "🗑 Расписание очищено.",
                    cancellationToken: ct);
                break;
            }
        }
    }

    private async Task SendScheduleListAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.Schedule.Count == 0)
        {
            await _bot.SendMessage(
                chatId,
                "🗓 Расписание пусто.\nДобавь занятия через /schedule → «Добавить занятие».",
                cancellationToken: ct);
            return;
        }

        var days = new[] { "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье" };
        var sb   = new System.Text.StringBuilder();
        sb.AppendLine("🗓 <b>Расписание занятий:</b>\n");

        foreach (var day in days)
        {
            var entries = session.Schedule.Where(e => e.Day == day).ToList();
            if (entries.Count == 0) continue;

            sb.AppendLine($"<b>{day}:</b>");
            foreach (var e in entries)
            {
                var time = TelegramHtml.Escape(e.Time);
                var subject = TelegramHtml.Escape(e.Subject);
                var room = string.IsNullOrWhiteSpace(e.Room) ? "" : $" ({TelegramHtml.Escape(e.Room)})";
                sb.AppendLine($"  🕐 {time} — {subject}{room}");
            }
            sb.AppendLine();
        }

        await _bot.SendMessage(
            chatId,
            sb.ToString().TrimEnd(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Вспомогательный метод: список задач
    // ══════════════════════════════════════════════════════════

    private async Task SendTaskListAsync(long chatId, UserSession session, CancellationToken ct)
    {
        var active    = session.Tasks.Where(t => !t.IsCompleted).ToList();
        var completed = session.Tasks.Where(t => t.IsCompleted).ToList();

        if (session.Tasks.Count == 0)
        {
            await _bot.SendMessage(
                chatId:    chatId,
                text:      "📋 <b>Список задач пуст.</b>\nДобавь первую задачу через /plan → «Добавить задачу».",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId:    chatId,
            text:      $"📋 <b>Твои задачи</b>\n" +
                       $"Активных: {active.Count} | Выполненных: {completed.Count}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        foreach (var task in active.Take(10))
        {
            var deadlineText = task.Deadline.HasValue
                ? $"\n📅 Дедлайн: {task.Deadline.Value:dd.MM.yyyy}"
                : string.Empty;

            string urgency = string.Empty;
            if (task.Deadline.HasValue)
            {
                var days = (task.Deadline.Value.Date - DateTime.Today).Days;
                urgency = days switch
                {
                    < 0  => " 🔴 <b>Просрочено!</b>",
                    0    => " 🟡 <b>Сдать сегодня!</b>",
                    1    => " 🟡 Завтра",
                    <= 3 => $" 🟠 Через {days} дня",
                    _    => $" ✅ Через {days} дней"
                };
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task_done_{task.ShortId}"),
                    InlineKeyboardButton.WithCallbackData("🗑 Удалить",   $"task_del_{task.ShortId}")
                }
            });

            await _bot.SendMessage(
                chatId:      chatId,
                text:        $"📌 <b>{TelegramHtml.Escape(task.Title)}</b>{urgency}\n" +
                             $"📚 {TelegramHtml.Escape(task.Subject)}{deadlineText}",
                parseMode:   ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);
        }

        if (active.Count > 10)
        {
            await _bot.SendMessage(
                chatId:    chatId,
                text:      $"... и ещё {active.Count - 10} задач(и). Выполни часть, чтобы увидеть остальные.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        if (completed.Count > 0)
        {
            var completedText = string.Join("\n", completed.Take(5).Select(t =>
                $"✅ {TelegramHtml.Escape(t.Title)} ({TelegramHtml.Escape(t.Subject)})"));
            await _bot.SendMessage(
                chatId:    chatId,
                text:      $"<b>Выполнено:</b>\n{completedText}" +
                           (completed.Count > 5 ? $"\n... и ещё {completed.Count - 5}" : ""),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
    }
}
