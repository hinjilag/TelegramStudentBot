using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик нажатий на инлайн-кнопки (CallbackQuery).
///
/// Формат callback_data:
///   timer_25/30/45/60   — рабочий таймер
///   timer_custom        — ввести своё время
///   timer_stop          — стоп
///   rest_5/15/30        — таймер отдыха
///   plan_add            — добавить задачу
///   plan_list           — список задач
///   task_done_{id}      — выполнить задачу
///   task_del_{id}       — удалить задачу
///   sched_confirm       — подтвердить распознанное расписание
///   sched_edit          — войти в режим исправления
///   week_1              — нечётная неделя (после подтверждения)
///   week_2              — чётная неделя
/// </summary>
public class CallbackHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService     _sessions;
    private readonly TimerService       _timers;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService     sessions,
        TimerService       timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    public async Task HandleAsync(CallbackQuery query, CancellationToken ct)
    {
        var chatId  = query.Message!.Chat.Id;
        var userId  = query.From.Id;
        var data    = query.Data ?? string.Empty;

        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        var session = _sessions.GetOrCreate(userId, query.From.FirstName);

        if (data.StartsWith("timer_"))  { await HandleTimerAsync(chatId, userId, session, data, ct); return; }
        if (data.StartsWith("rest_"))   { await HandleRestAsync(chatId, userId, data, ct);           return; }
        if (data.StartsWith("plan_"))   { await HandlePlanAsync(chatId, session, data, ct);          return; }
        if (data.StartsWith("task_"))   { await HandleTaskAsync(chatId, session, data, ct);          return; }
        if (data.StartsWith("sched_"))  { await HandleScheduleAsync(chatId, session, data, ct);      return; }
        if (data.StartsWith("week_"))   { await HandleWeekChoiceAsync(chatId, session, data, ct);    return; }
    }

    // ══════════════════════════════════════════════════════════
    //  Таймеры
    // ══════════════════════════════════════════════════════════

    private async Task HandleTimerAsync(
        long chatId, long userId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "timer_25": case "timer_30": case "timer_45": case "timer_60":
                await _timers.StartWorkTimerAsync(chatId, userId, int.Parse(data.Split('_')[1]));
                break;

            case "timer_custom":
                session.State = UserState.WaitingForTimerMinutes;
                await _bot.SendMessage(chatId, "✏️ Введи количество минут (1–300):", cancellationToken: ct);
                break;

            case "timer_stop":
                var stopped = _timers.StopTimer(userId);
                await _bot.SendMessage(chatId,
                    stopped ? "⏹ Таймер остановлен." : "ℹ️ Нет активного таймера.",
                    cancellationToken: ct);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Отдых
    // ══════════════════════════════════════════════════════════

    private async Task HandleRestAsync(long chatId, long userId, string data, CancellationToken ct)
    {
        if (int.TryParse(data.Split('_')[1], out int minutes))
            await _timers.StartRestTimerAsync(chatId, userId, minutes);
    }

    // ══════════════════════════════════════════════════════════
    //  Планирование задач
    // ══════════════════════════════════════════════════════════

    private async Task HandlePlanAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "plan_add":
                session.State     = UserState.WaitingForTaskTitle;
                session.DraftTask = null;
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      "📝 <b>Добавление задачи</b>\n\nВведи <b>название</b>:",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;

            case "plan_list":
                await SendTaskListAsync(chatId, session, ct);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Управление конкретными задачами
    // ══════════════════════════════════════════════════════════

    private async Task HandleTaskAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        var parts = data.Split('_', 3);
        if (parts.Length < 3) return;

        var task = session.Tasks.FirstOrDefault(t => t.ShortId == parts[2]);
        if (task is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Задача не найдена.", cancellationToken: ct);
            return;
        }

        switch (parts[1])
        {
            case "done":
                task.IsCompleted = true;
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      $"✅ Задача <b>«{task.Title}»</b> выполнена! 🎉",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;

            case "del":
                session.Tasks.Remove(task);
                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      $"🗑 Задача <b>«{task.Title}»</b> удалена.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Подтверждение / исправление расписания
    // ══════════════════════════════════════════════════════════

    private async Task HandleScheduleAsync(
        long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "sched_confirm":
            {
                if (session.PendingSchedule is null || session.PendingSchedule.Count == 0)
                {
                    await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания.", cancellationToken: ct);
                    return;
                }

                var hasWeekSplit = session.PendingSchedule.Any(e => e.WeekType.HasValue);

                if (hasWeekSplit)
                {
                    // Сначала уточняем неделю — расписание пока остаётся в PendingSchedule
                    session.State = UserState.WaitingForWeekChoice;
                    var splitCount = session.PendingSchedule.Count(e => e.WeekType.HasValue);

                    await _bot.SendMessage(
                        chatId:      chatId,
                        text:        $"❓ <b>Какая сейчас неделя?</b>\n" +
                                     $"Обнаружено <b>{splitCount}</b> пар с разбивкой по неделям.\n" +
                                     "Это нужно для корректных напоминаний.",
                        parseMode:   ParseMode.Html,
                        replyMarkup: ScheduleKeyboards.WeekChoice,
                        cancellationToken: ct);
                }
                else
                {
                    // Двойных пар нет — сохраняем сразу
                    session.Schedule        = session.PendingSchedule;
                    session.PendingSchedule = null;
                    session.State           = UserState.Idle;

                    await _bot.SendMessage(
                        chatId:    chatId,
                        text:      $"✅ Расписание сохранено! ({session.Schedule.Count} пар)",
                        cancellationToken: ct);
                }
                break;
            }

            case "sched_edit":
            {
                if (session.State != UserState.WaitingForScheduleConfirmation)
                {
                    await _bot.SendMessage(chatId, "ℹ️ Сначала загрузи расписание через /add_schedule.", cancellationToken: ct);
                    return;
                }

                session.State = UserState.WaitingForScheduleCorrection;

                await _bot.SendMessage(
                    chatId:    chatId,
                    text:      "✏️ <b>Опиши исправление</b>, например:\n\n" +
                               "<i>«первой парой в среду у меня не мат анализ, а линейная алгебра»</i>\n" +
                               "<i>«замени физику на химию в пятницу»</i>\n\n" +
                               "Можно описать несколько исправлений по одному за раз.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Выбор типа недели (после подтверждения расписания)
    // ══════════════════════════════════════════════════════════

    private async Task HandleWeekChoiceAsync(
        long chatId, UserSession session, string data, CancellationToken ct)
    {
        if (session.State != UserState.WaitingForWeekChoice || session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания.", cancellationToken: ct);
            return;
        }

        if (!int.TryParse(data.Split('_')[1], out var weekType) || weekType is not (1 or 2))
        {
            await _bot.SendMessage(chatId, "⚠️ Неизвестный тип недели.", cancellationToken: ct);
            return;
        }

        session.CurrentWeekType = weekType;
        session.Schedule        = session.PendingSchedule;
        session.PendingSchedule = null;
        session.State           = UserState.Idle;

        var weekLabel = weekType == 1 ? "нечётная (1-я)" : "чётная (2-я)";
        var summary   = ScheduleService.FormatSchedule(session.Schedule, session.CurrentWeekType);

        await _bot.SendMessage(
            chatId:    chatId,
            text:      $"✅ <b>Расписание сохранено!</b>\n" +
                       $"Текущая неделя: <b>{weekLabel}</b>\n" +
                       $"Всего пар: <b>{session.Schedule.Count}</b>\n\n" +
                       $"{summary}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Список задач
    // ══════════════════════════════════════════════════════════

    private async Task SendTaskListAsync(long chatId, UserSession session, CancellationToken ct)
    {
        var active    = session.Tasks.Where(t => !t.IsCompleted).ToList();
        var completed = session.Tasks.Where(t => t.IsCompleted).ToList();

        if (session.Tasks.Count == 0)
        {
            await _bot.SendMessage(
                chatId:    chatId,
                text:      "📋 <b>Список задач пуст.</b>\nДобавь первую через /plan → «Добавить задачу».",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId:    chatId,
            text:      $"📋 <b>Твои задачи</b> | Активных: {active.Count} | Выполнено: {completed.Count}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        foreach (var task in active.Take(10))
        {
            var dl = task.Deadline.HasValue ? $"\n📅 {task.Deadline.Value:dd.MM.yyyy}" : string.Empty;

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

            var kb = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task_done_{task.ShortId}"),
                    InlineKeyboardButton.WithCallbackData("🗑 Удалить",   $"task_del_{task.ShortId}")
                }
            });

            await _bot.SendMessage(
                chatId:      chatId,
                text:        $"📌 <b>{task.Title}</b>{urgency}\n📚 {task.Subject}{dl}",
                parseMode:   ParseMode.Html,
                replyMarkup: kb,
                cancellationToken: ct);
        }

        if (active.Count > 10)
            await _bot.SendMessage(chatId,
                $"... и ещё {active.Count - 10} задач(и).", cancellationToken: ct);

        if (completed.Count > 0)
        {
            var txt = string.Join("\n", completed.Take(5).Select(t => $"✅ {t.Title} ({t.Subject})"));
            await _bot.SendMessage(
                chatId:    chatId,
                text:      $"<b>Выполнено:</b>\n{txt}" +
                           (completed.Count > 5 ? $"\n... и ещё {completed.Count - 5}" : ""),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
    }
}
