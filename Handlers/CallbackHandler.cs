using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class CallbackHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ScheduleService _schedule;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleService schedule)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _schedule = schedule;
    }

    public async Task HandleAsync(CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message!.Chat.Id;
        var userId = query.From.Id;
        var data = query.Data ?? string.Empty;

        var session = _sessions.GetOrCreate(userId, query.From.FirstName);

        if (await TryHandleSubGroupCallbackAsync(query, session, data, ct))
            return;

        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data.StartsWith("timer_")) { await HandleTimerAsync(chatId, userId, session, data, ct); return; }
        if (data.StartsWith("rest_")) { await HandleRestAsync(chatId, userId, data, ct); return; }
        if (data.StartsWith("plan_")) { await HandlePlanAsync(chatId, session, data, ct); return; }
        if (data.StartsWith("task_")) { await HandleTaskAsync(chatId, session, data, ct); return; }
        if (data.StartsWith("sched_")) { await HandleScheduleAsync(chatId, session, data, ct); return; }
        if (data.StartsWith("review_")) { await HandleReviewActionAsync(chatId, session, data, ct); return; }
        if (data.StartsWith("week_")) { await HandleWeekChoiceAsync(chatId, session, data, ct); return; }
    }

    private async Task<bool> TryHandleSubGroupCallbackAsync(
        CallbackQuery query, UserSession session, string data, CancellationToken ct)
    {
        if (!data.StartsWith("subgroup_"))
            return false;

        if (session.State == UserState.WaitingForSubGroupParsing)
        {
            await AnswerCallbackPopupAsync(query.Id, "Расписание считывается...", ct);
            return true;
        }

        if (session.State is UserState.WaitingForScheduleConfirmation
            or UserState.WaitingForWeekChoice
            or UserState.WaitingForScheduleReview
            or UserState.WaitingForReviewSlotCorrection)
        {
            await AnswerCallbackPopupAsync(query.Id, "Подгруппа уже выбрана.", ct);
            return true;
        }

        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        await HandleSubGroupChoiceAsync(query.Message!.Chat.Id, session, data, ct);
        return true;
    }

    private Task AnswerCallbackPopupAsync(string callbackQueryId, string text, CancellationToken ct)
        => _bot.AnswerCallbackQuery(
            callbackQueryId: callbackQueryId,
            text: text,
            showAlert: true,
            cancellationToken: ct);

    private async Task HandleTimerAsync(
        long chatId, long userId, UserSession session, string data, CancellationToken ct)
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
                await _bot.SendMessage(chatId, "✏️ Введи количество минут (1-300):", cancellationToken: ct);
                break;

            case "timer_stop":
                var stopped = _timers.StopTimer(userId);
                await _bot.SendMessage(
                    chatId,
                    stopped ? "⏹ Таймер остановлен." : "ℹ️ Нет активного таймера.",
                    cancellationToken: ct);
                break;
        }
    }

    private async Task HandleRestAsync(long chatId, long userId, string data, CancellationToken ct)
    {
        if (int.TryParse(data.Split('_')[1], out var minutes))
            await _timers.StartRestTimerAsync(chatId, userId, minutes);
    }

    private async Task HandlePlanAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "plan_add":
                session.State = UserState.WaitingForTaskTitle;
                session.DraftTask = null;
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "📝 <b>Добавление задачи</b>\n\nВведи <b>название</b>:",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;

            case "plan_list":
                await SendTaskListAsync(chatId, session, ct);
                break;
        }
    }

    private async Task HandleTaskAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        var parts = data.Split('_', 3);
        if (parts.Length < 3)
            return;

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
                    chatId: chatId,
                    text: $"✅ Задача <b>«{task.Title}»</b> выполнена!",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;

            case "del":
                session.Tasks.Remove(task);
                await _bot.SendMessage(
                    chatId: chatId,
                    text: $"🗑 Задача <b>«{task.Title}»</b> удалена.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    private async Task HandleScheduleAsync(
        long chatId, UserSession session, string data, CancellationToken ct)
    {
        switch (data)
        {
            case "sched_confirm":
                await ConfirmScheduleAsync(chatId, session, ct);
                break;

            case "sched_review":
                await StartScheduleReviewAsync(chatId, session, ct);
                break;

            case "sched_edit":
                if (session.State != UserState.WaitingForScheduleConfirmation)
                {
                    await _bot.SendMessage(
                        chatId,
                        "ℹ️ Сначала загрузи расписание через /add_schedule.",
                        cancellationToken: ct);
                    return;
                }

                session.State = UserState.WaitingForScheduleCorrection;
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "✏️ <b>Напиши, что исправить</b>, например:\n\n" +
                          "<i>«В среду второй парой не линейная алгебра, а мат анализ»</i>\n" +
                          "<i>«Замени историю России на дискретную математику в пятницу 2 парой»</i>\n" +
                          "<i>«Убери 4 пару в среду»</i>\n\n" +
                          "После правки я снова покажу, как прочитал расписание.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    private async Task StartScheduleReviewAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания для проверки.", cancellationToken: ct);
            return;
        }

        session.ReviewSlotIndex = 0;
        session.State = UserState.WaitingForScheduleReview;

        await _bot.SendMessage(
            chatId,
            "🔎 <b>Строгая проверка по парам</b>\nЯ покажу все 24 слота по очереди. Так мы можем довести расписание до 0 ошибок перед сохранением.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        await SendCurrentReviewSlotAsync(chatId, session, ct);
    }

    private async Task HandleReviewActionAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания для проверки.", cancellationToken: ct);
            return;
        }

        if (session.State is not UserState.WaitingForScheduleReview and not UserState.WaitingForReviewSlotCorrection)
        {
            await _bot.SendMessage(chatId, "ℹ️ Пошаговая проверка сейчас не запущена.", cancellationToken: ct);
            return;
        }

        switch (data)
        {
            case "review_ok":
                session.ReviewSlotIndex++;
                session.State = UserState.WaitingForScheduleReview;
                await SendCurrentReviewSlotAsync(chatId, session, ct);
                break;

            case "review_edit":
                session.State = UserState.WaitingForReviewSlotCorrection;
                var (day, lesson) = GetReviewSlot(session.ReviewSlotIndex);
                await _bot.SendMessage(
                    chatId,
                    $"✏️ <b>{GetDayName(day)}, {lesson} пара</b>\n" +
                    "Напиши точно так:\n\n" +
                    "<i>первая неделя: ...\nвторая неделя: ...</i>\n\n" +
                    "или:\n" +
                    "<i>обе недели: ...</i>\n\n" +
                    "или просто:\n" +
                    "<i>пары нет</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    private async Task ConfirmScheduleAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null || session.PendingSchedule.Count == 0)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания.", cancellationToken: ct);
            return;
        }

        var hasWeekSplit = session.PendingSchedule.Any(e => e.WeekType.HasValue);
        if (hasWeekSplit)
        {
            session.State = UserState.WaitingForWeekChoice;
            var splitCount = session.PendingSchedule.Count(e => e.WeekType.HasValue);

            await _bot.SendMessage(
                chatId: chatId,
                text: $"❓ <b>Какая сейчас неделя?</b>\n" +
                      $"Обнаружено <b>{splitCount}</b> пар с разбивкой по неделям.\n" +
                      "Это нужно для корректного сохранения расписания.",
                parseMode: ParseMode.Html,
                replyMarkup: ScheduleKeyboards.WeekChoice,
                cancellationToken: ct);
            return;
        }

        session.Schedule = session.PendingSchedule;
        session.CurrentWeekType = null;
        ClearPendingScheduleDraft(session);
        session.State = UserState.Idle;

        await SendSavedScheduleAsync(chatId, session, includeWeek: false, ct);
    }

    private async Task HandleSubGroupChoiceAsync(
        long chatId, UserSession session, string data, CancellationToken ct)
    {
        if (session.State != UserState.WaitingForSubGroupChoice || session.PendingScheduleImage is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания.", cancellationToken: ct);
            return;
        }

        if (!int.TryParse(data.Split('_')[1], out var subGroup) ||
            session.AvailableSubGroups.Count == 0 ||
            !session.AvailableSubGroups.Contains(subGroup))
        {
            await _bot.SendMessage(chatId, "⚠️ Неизвестная подгруппа.", cancellationToken: ct);
            return;
        }

        session.CurrentSubGroup = subGroup;
        session.CurrentWeekType = null;
        session.PendingSchedule = null;
        session.State = UserState.WaitingForSubGroupParsing;

        var imageBytes = session.PendingScheduleImage;
        _ = ProcessSubGroupChoiceAsync(chatId, session, imageBytes, subGroup);
    }

    private async Task ProcessSubGroupChoiceAsync(
        long chatId, UserSession session, byte[]? imageBytes, int subGroup)
    {
        if (imageBytes is null)
        {
            await FailPendingScheduleAsync(
                chatId,
                session,
                "ℹ️ Нет ожидающего расписания.",
                CancellationToken.None);
            return;
        }

        try
        {
            using var subgroupParseCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

            session.PendingSchedule = await _schedule.ParseScheduleForSubGroupAsync(
                imageBytes,
                subGroup,
                session.AvailableSubGroups,
                subgroupParseCts.Token);
        }
        catch (OperationCanceledException)
        {
            await FailPendingScheduleAsync(
                chatId,
                session,
                "⏳ Анализ занял слишком много времени и был остановлен. Сейчас на чтение подгруппы даётся до 4 минут. Попробуй ещё раз, лучше отправить фото как документ или более чёткое изображение.",
                CancellationToken.None);
            return;
        }
        catch (InvalidOperationException ex)
        {
            await FailPendingScheduleAsync(
                chatId,
                session,
                $"❌ <b>Ошибка:</b> {ex.Message}",
                CancellationToken.None,
                ParseMode.Html);
            return;
        }
        catch (Exception ex)
        {
            await FailPendingScheduleAsync(
                chatId,
                session,
                $"❌ Не удалось прочитать расписание для подгруппы: {ex.Message}",
                CancellationToken.None);
            return;
        }

        if (session.PendingSchedule is null || session.PendingSchedule.Count == 0)
        {
            await FailPendingScheduleAsync(
                chatId,
                session,
                "😕 Не удалось уверенно прочитать расписание для выбранной подгруппы. Попробуй более чёткое фото или изображение как документ.",
                CancellationToken.None);
            return;
        }

        session.State = UserState.WaitingForScheduleConfirmation;

        await _bot.SendMessage(
            chatId: chatId,
            text: $"✅ Подгруппа выбрана: <b>{subGroup}</b>. Ниже показываю, как я прочитал расписание только для неё.",
            parseMode: ParseMode.Html,
            cancellationToken: CancellationToken.None);

        await _bot.SendMessage(
            chatId: chatId,
            text: ScheduleService.FormatSchedule(session.PendingSchedule),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.Confirmation,
            cancellationToken: CancellationToken.None);
    }

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
        session.Schedule = FilterScheduleForUser(session.PendingSchedule, session.CurrentSubGroup, weekType);
        ClearPendingScheduleDraft(session);
        session.State = UserState.Idle;

        await SendSavedScheduleAsync(chatId, session, includeWeek: true, ct);
    }

    private async Task FailPendingScheduleAsync(
        long chatId,
        UserSession session,
        string text,
        CancellationToken ct,
        ParseMode? parseMode = null)
    {
        session.State = UserState.Idle;
        ClearPendingScheduleDraft(session);

        if (parseMode.HasValue)
        {
            await _bot.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task SendSavedScheduleAsync(
        long chatId,
        UserSession session,
        bool includeWeek,
        CancellationToken ct)
    {
        var summary = ScheduleService.FormatSchedule(session.Schedule, session.CurrentWeekType);
        var text = $"✅ <b>Расписание сохранено!</b>\n" +
                   $"Твоя подгруппа: <b>{session.CurrentSubGroup}</b>\n";

        if (includeWeek)
        {
            var weekLabel = session.CurrentWeekType == 1 ? "нечётная (1-я)" : "чётная (2-я)";
            text += $"Текущая неделя: <b>{weekLabel}</b>\n";
        }

        text += $"Всего пар: <b>{session.Schedule.Count}</b>\n\n{summary}";

        await _bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private static void ClearPendingScheduleDraft(UserSession session)
    {
        session.PendingSchedule = null;
        session.PendingScheduleImage = null;
        session.AvailableSubGroups.Clear();
    }

    private static List<ScheduleEntry> FilterScheduleForUser(
        List<ScheduleEntry> entries, int? subGroup, int? weekType)
    {
        return entries
            .Where(e => !subGroup.HasValue || e.SubGroup is null || e.SubGroup == subGroup)
            .Where(e => !weekType.HasValue || e.WeekType is null || e.WeekType == weekType)
            .Select(e => new ScheduleEntry
            {
                DayOfWeek = e.DayOfWeek,
                LessonNumber = e.LessonNumber,
                Subject = e.Subject,
                SubGroup = subGroup.HasValue ? null : e.SubGroup,
                WeekType = e.WeekType
            })
            .ToList();
    }

    internal async Task SendCurrentReviewSlotAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания для проверки.", cancellationToken: ct);
            return;
        }

        if (session.ReviewSlotIndex >= 24)
        {
            session.State = UserState.WaitingForScheduleConfirmation;
            await _bot.SendMessage(
                chatId,
                "✅ Пошаговая проверка завершена. Ниже итоговое расписание, теперь можно сохранять.",
                cancellationToken: ct);

            await _bot.SendMessage(
                chatId,
                ScheduleService.FormatSchedule(session.PendingSchedule),
                parseMode: ParseMode.Html,
                replyMarkup: ScheduleKeyboards.Confirmation,
                cancellationToken: ct);
            return;
        }

        var (day, lesson) = GetReviewSlot(session.ReviewSlotIndex);
        var slotEntries = session.PendingSchedule
            .Where(e => e.DayOfWeek == day && e.LessonNumber == lesson)
            .OrderBy(e => e.WeekType ?? 0)
            .ToList();

        var firstWeek = slotEntries.Where(e => e.WeekType is null or 1).ToList();
        var secondWeek = slotEntries.Where(e => e.WeekType is null or 2).ToList();

        var text =
            $"🔎 <b>Проверка {session.ReviewSlotIndex + 1}/24</b>\n" +
            $"<b>{GetDayName(day)}, {lesson} пара</b>\n\n" +
            $"Первая неделя: {FormatReviewWeek(firstWeek)}\n" +
            $"Вторая неделя: {FormatReviewWeek(secondWeek)}\n\n" +
            "Это верно?";

        await _bot.SendMessage(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ReviewSlotChoice,
            cancellationToken: ct);
    }

    private static (int Day, int Lesson) GetReviewSlot(int slotIndex)
        => (slotIndex / 4 + 1, slotIndex % 4 + 1);

    private static string GetDayName(int day) => day switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        _ => $"День {day}"
    };

    private static string FormatReviewWeek(List<ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return "пары нет";

        return string.Join("; ", entries.Select(e => e.Subject).Distinct());
    }

    private async Task SendTaskListAsync(long chatId, UserSession session, CancellationToken ct)
    {
        var active = session.Tasks.Where(t => !t.IsCompleted).ToList();
        var completed = session.Tasks.Where(t => t.IsCompleted).ToList();

        if (session.Tasks.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "📋 <b>Список задач пуст.</b>\nДобавь первую через /plan → «Добавить задачу».",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId: chatId,
            text: $"📋 <b>Твои задачи</b> | Активных: {active.Count} | Выполнено: {completed.Count}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        foreach (var task in active.Take(10))
        {
            var deadlineText = task.Deadline.HasValue ? $"\n📅 {task.Deadline.Value:dd.MM.yyyy}" : string.Empty;

            string urgency = string.Empty;
            if (task.Deadline.HasValue)
            {
                var days = (task.Deadline.Value.Date - DateTime.Today).Days;
                urgency = days switch
                {
                    < 0 => " 🔴 <b>Просрочено!</b>",
                    0 => " 🟡 <b>Сдать сегодня!</b>",
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
                text: $"📌 <b>{task.Title}</b>{urgency}\n📚 {task.Subject}{deadlineText}",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);
        }

        if (active.Count > 10)
            await _bot.SendMessage(chatId, $"... и ещё {active.Count - 10} задач(и).", cancellationToken: ct);

        if (completed.Count == 0)
            return;

        var completedText = string.Join("\n", completed.Take(5).Select(t => $"✅ {t.Title} ({t.Subject})"));
        await _bot.SendMessage(
            chatId: chatId,
            text: $"<b>Выполнено:</b>\n{completedText}" +
                  (completed.Count > 5 ? $"\n... и ещё {completed.Count - 5}" : string.Empty),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }
}
