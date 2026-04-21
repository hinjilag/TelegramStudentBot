using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class CallbackHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ReminderSettingsService _reminders;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ReminderSettingsService reminders,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _reminders = reminders;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
    }

    public async Task HandleAsync(CallbackQuery query, CancellationToken ct)
    {
        var userId = query.From.Id;
        var data = query.Data ?? string.Empty;

        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (query.Message is null)
            return;

        var chatId = query.Message.Chat.Id;
        var session = _sessions.GetOrCreate(userId, query.From.FirstName);
        if (session.LastChatId != chatId)
        {
            session.LastChatId = chatId;
            _sessions.Save();
        }

        if (data.StartsWith("timer_", StringComparison.Ordinal))
        {
            await HandleTimerCallbackAsync(chatId, userId, session, data, ct);
            return;
        }

        if (data.StartsWith("rest_", StringComparison.Ordinal))
        {
            await HandleRestCallbackAsync(chatId, userId, data, ct);
            return;
        }

        if (data.StartsWith("plan_", StringComparison.Ordinal))
        {
            await HandlePlanCallbackAsync(chatId, session, data, ct);
            return;
        }

        if (data.StartsWith("task_", StringComparison.Ordinal))
        {
            await HandleTaskCallbackAsync(chatId, session, data, ct);
            return;
        }

        if (data.StartsWith("schedule_", StringComparison.Ordinal))
        {
            await HandleScheduleCallbackAsync(chatId, userId, session, data, ct);
            return;
        }

        if (data.StartsWith("reminder_", StringComparison.Ordinal))
            await HandleReminderCallbackAsync(chatId, userId, session, data, ct);
    }

    private async Task HandleTimerCallbackAsync(long chatId, long userId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "timer_25":
            case "timer_30":
            case "timer_45":
            case "timer_60":
                await _timers.StartWorkTimerAsync(chatId, userId, int.Parse(data.Split('_')[1]));
                break;

            case "timer_custom":
                session.State = UserState.WaitingForTimerMinutes;
                await _bot.SendMessage(chatId, "Введи количество минут от 1 до 300.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;

            case "timer_stop":
                var stopped = _timers.StopTimer(userId);
                await _bot.SendMessage(chatId, stopped ? "Таймер остановлен." : "Активного таймера нет.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;
        }
    }

    private async Task HandleRestCallbackAsync(long chatId, long userId, string data, CancellationToken ct)
    {
        if (int.TryParse(data.Split('_')[1], out var minutes))
            await _timers.StartRestTimerAsync(chatId, userId, minutes);
    }

    private async Task HandlePlanCallbackAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "plan_add":
                session.State = UserState.WaitingForTaskTitle;
                session.DraftTask = null;
                await _bot.SendMessage(chatId, "📝 <b>Добавление задачи</b>\n\nВведи название задачи.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;

            case "plan_list":
                await SendTaskListAsync(chatId, session, ct);
                break;
        }
    }

    private async Task HandleTaskCallbackAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        var parts = data.Split('_', 3);
        if (parts.Length < 3)
            return;

        var action = parts[1];
        var shortId = parts[2];
        var task = session.Tasks.FirstOrDefault(t => t.ShortId == shortId);

        if (task is null)
        {
            await _bot.SendMessage(chatId, "Задача не найдена.", cancellationToken: ct);
            return;
        }

        switch (action)
        {
            case "done":
                task.IsCompleted = true;
                _sessions.Save();
                await _bot.SendMessage(chatId, $"✅ Задача <b>«{TelegramHtml.Escape(task.Title)}»</b> отмечена как выполненная.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;

            case "del":
                session.Tasks.Remove(task);
                _sessions.Save();
                await _bot.SendMessage(chatId, $"🗑 Задача <b>«{TelegramHtml.Escape(task.Title)}»</b> удалена.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;
        }
    }

    private async Task HandleScheduleCallbackAsync(long chatId, long userId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "schedule_select":
                await SendScheduleGroupChooserAsync(chatId, ct);
                return;

            case "schedule_today":
                await SendTodayScheduleAsync(chatId, session, ct);
                return;

            case "schedule_clear":
                _scheduleSelections.Delete(userId);
                session.Schedule.Clear();
                session.SchedulePhotoDataUrl = null;
                _sessions.Save();
                await _bot.SendMessage(chatId, "Выбор группы сброшен. Расписание очищено.", cancellationToken: ct);
                return;

            case "schedule_week":
                await SendWeekScheduleAsync(chatId, session, ct);
                return;
        }

        if (data.StartsWith("schedule_group_", StringComparison.Ordinal))
        {
            var scheduleId = data["schedule_group_".Length..];
            await SelectScheduleGroupAsync(chatId, userId, session, scheduleId, subGroup: null, ct);
            return;
        }

        if (data.StartsWith("schedule_sub_", StringComparison.Ordinal))
        {
            var payload = data["schedule_sub_".Length..];
            var separatorIndex = payload.LastIndexOf('_');
            if (separatorIndex <= 0)
                return;

            var scheduleId = payload[..separatorIndex];
            if (!int.TryParse(payload[(separatorIndex + 1)..], out var subGroup))
                return;

            await SelectScheduleGroupAsync(chatId, userId, session, scheduleId, subGroup, ct);
        }
    }

    private async Task SendScheduleGroupChooserAsync(long chatId, CancellationToken ct)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .SelectMany(direction => _scheduleCatalog.GetGroupsByDirection(direction.DirectionCode))
            .Select(group => (group.Title, $"schedule_group_{group.Id}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: "<b>Выбери группу</b>\n\nЕсли у группы есть подгруппы, я затем попрошу выбрать и подгруппу.",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task SelectScheduleGroupAsync(long chatId, long userId, UserSession session, string scheduleId, int? subGroup, CancellationToken ct)
    {
        var group = _scheduleCatalog.GetGroup(scheduleId);
        if (group is null)
        {
            await _bot.SendMessage(chatId, "Не нашёл такую группу в каталоге.", cancellationToken: ct);
            return;
        }

        if (!subGroup.HasValue && group.SubGroups.Count > 0)
        {
            var buttons = group.SubGroups.Select(number => ($"Подгруппа {number}", $"schedule_sub_{group.Id}_{number}"));
            await _bot.SendMessage(
                chatId: chatId,
                text: $"<b>{TelegramHtml.Escape(group.Title)}</b>\n\nТеперь выбери подгруппу:",
                parseMode: ParseMode.Html,
                replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
                cancellationToken: ct);
            return;
        }

        _scheduleSelections.Save(userId, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = subGroup
        });

        session.Schedule = _scheduleCatalog.GetAllEntriesForSelection(group, subGroup);
        session.SchedulePhotoDataUrl = null;
        _sessions.Save();

        var subgroupText = subGroup.HasValue ? $"\nПодгруппа: <b>{subGroup.Value}</b>" : string.Empty;
        await _bot.SendMessage(
            chatId: chatId,
            text: $"<b>Расписание подключено</b>\n\nГруппа: <b>{TelegramHtml.Escape(group.Title)}</b>{subgroupText}\nЗаписей в расписании: <b>{session.Schedule.Count}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task SendTodayScheduleAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.Schedule.Count == 0)
        {
            await _bot.SendMessage(chatId, "Сначала выбери группу через /schedule.", cancellationToken: ct);
            return;
        }

        var dayNumber = ScheduleCatalogService.GetDayNumber(DateTime.Today);
        var currentWeekType = _scheduleCatalog.GetCurrentWeekType();
        var todayEntries = session.Schedule
            .Where(entry => entry.DayOfWeek == dayNumber && (!entry.WeekTypeCode.HasValue || entry.WeekTypeCode.Value == currentWeekType))
            .OrderBy(entry => entry.LessonNumber)
            .ThenBy(entry => entry.Time)
            .ToList();

        if (todayEntries.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"<b>На сегодня пар нет</b>\nТекущая неделя: <b>{TelegramHtml.Escape(_scheduleCatalog.GetCurrentWeekLabel())}</b>.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var text = "<b>Расписание на сегодня</b>\n" +
                   $"Неделя: <b>{TelegramHtml.Escape(_scheduleCatalog.GetCurrentWeekLabel())}</b>\n\n" +
                   ScheduleService.FormatSchedule(todayEntries, currentWeekType);

        await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task SendWeekScheduleAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.Schedule.Count == 0)
        {
            await _bot.SendMessage(chatId, "Сначала выбери группу через /schedule.", cancellationToken: ct);
            return;
        }

        var currentWeekType = _scheduleCatalog.GetCurrentWeekType();
        var text = "<b>Расписание на неделю</b>\n" +
                   $"Текущая неделя: <b>{TelegramHtml.Escape(_scheduleCatalog.GetCurrentWeekLabel())}</b>\n\n" +
                   ScheduleService.FormatSchedule(session.Schedule, currentWeekType);

        await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleReminderCallbackAsync(long chatId, long userId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "reminder_enable_default":
                _reminders.Enable(userId, chatId, 20, 0);
                await _bot.SendMessage(chatId, "Напоминания включены. Каждый день в 20:00 я пришлю список задач на завтра.", cancellationToken: ct);
                break;

            case "reminder_disable":
                _reminders.Disable(userId, chatId);
                await _bot.SendMessage(chatId, "Напоминания выключены.", cancellationToken: ct);
                break;

            case "reminder_change_time":
                session.State = UserState.WaitingForReminderTime;
                await _bot.SendMessage(chatId, "Введи время напоминаний в формате <b>HH:mm</b>, например <code>19:30</code>.", parseMode: ParseMode.Html, cancellationToken: ct);
                break;
        }
    }

    private async Task SendTaskListAsync(long chatId, UserSession session, CancellationToken ct)
    {
        var active = session.Tasks.Where(task => !task.IsCompleted).ToList();
        var completed = session.Tasks.Where(task => task.IsCompleted).ToList();

        if (session.Tasks.Count == 0)
        {
            await _bot.SendMessage(chatId, "📋 <b>Список задач пуст.</b>\nДобавь первую задачу через /plan.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId: chatId,
            text: $"📋 <b>Твои задачи</b>\nАктивных: {active.Count} | Выполненных: {completed.Count}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        foreach (var task in active.Take(10))
        {
            var deadlineText = task.Deadline.HasValue ? $"\n📅 Дедлайн: {task.Deadline.Value:dd.MM.yyyy}" : string.Empty;
            var urgency = string.Empty;
            if (task.Deadline.HasValue)
            {
                var days = (task.Deadline.Value.Date - DateTime.Today).Days;
                urgency = days switch
                {
                    < 0 => " 🔴 <b>Просрочено</b>",
                    0 => " 🟡 <b>Сдать сегодня</b>",
                    1 => " 🟡 Завтра",
                    <= 3 => $" 🟠 Через {days} дня",
                    _ => $" ✅ Через {days} дней"
                };
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task_done_{task.ShortId}"),
                    InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"task_del_{task.ShortId}")
                }
            });

            await _bot.SendMessage(
                chatId: chatId,
                text: $"📌 <b>{TelegramHtml.Escape(task.Title)}</b>{urgency}\n📚 {TelegramHtml.Escape(task.Subject)}{deadlineText}",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);
        }
    }
}
