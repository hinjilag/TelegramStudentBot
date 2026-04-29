using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;
using System.Net;

namespace TelegramStudentBot.Handlers;

public class CallbackHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ReminderSettingsService _reminders;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ReminderSettingsService reminders,
        GroupReminderSettingsService groupReminders,
        HomeworkSubjectPreferencesService homeworkSubjects)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _reminders = reminders;
        _groupReminders = groupReminders;
        _homeworkSubjects = homeworkSubjects;
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
        if (data.StartsWith("plan_")) { await HandlePlanAsync(query, session, data, ct); return; }
        if (data.StartsWith("hw_")) { await HandleHomeworkAsync(query, userId, session, data, ct); return; }
        if (data.StartsWith("rem_")) { await HandleReminderFlowAsync(query, session, data, ct); return; }
        if (data.StartsWith("task_")) { await HandleTaskAsync(query, session, data, ct); return; }
        if (data.StartsWith("sched_")) { await HandleScheduleAsync(query, userId, session, data, ct); return; }
        if (data.StartsWith("review_")) { await HandleReviewActionAsync(chatId, session, data, ct); return; }
        if (data.StartsWith("week_")) { await HandleWeekChoiceAsync(chatId, session, data, ct); return; }
    }

    private async Task<bool> TryHandleSubGroupCallbackAsync(
        CallbackQuery query, UserSession session, string data, CancellationToken ct)
    {
        if (!data.StartsWith("subgroup_"))
            return false;

        session.State = UserState.Idle;
        await AnswerCallbackPopupAsync(query.Id, "Распознавание расписания из фото удалено.", ct);
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

    private async Task HandlePlanAsync(CallbackQuery query, UserSession session, string data, CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        switch (data)
        {
            case "plan_add":
                session.State = UserState.WaitingForTaskTitle;
                session.DraftTask = null;
                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: "📝 <b>Добавление дела</b>\n\nНапиши, что нужно сделать:",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;

            case "plan_list":
                await EditPlanListMessageAsync(message, session, ct);
                return;

            case "plan_back":
                await EditPlanListMessageAsync(message, session, ct);
                return;

            case "plan_completed":
                await EditCompletedPlanMessageAsync(message, session, ct);
                return;

            case "plan_choose_done":
            case "plan_choose_del":
                await EditPlanTaskChoiceMessageAsync(message, session, data == "plan_choose_del" ? "del" : "done", ct);
                return;
        }

        if (data.StartsWith("plan_due_"))
        {
            await HandlePlanQuickDeadlineDateAsync(message, session, data, ct);
            return;
        }

        var parts = data.Split('_', 3);
        if (parts.Length < 3)
            return;

        var task = session.Tasks.FirstOrDefault(t =>
            t.ShortId == parts[2] &&
            TaskSubjects.IsPersonal(t.Subject));

        if (task is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Дело не найдено.", cancellationToken: ct);
            return;
        }

        switch (parts[1])
        {
            case "done":
                task.IsCompleted = true;
                _sessions.SaveTasks(session);
                await EditPlanListMessageAsync(message, session, ct);
                break;

            case "del":
                var confirmation = PlanListView.BuildDeleteConfirmation(task);
                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: confirmation.Text,
                    parseMode: ParseMode.Html,
                    replyMarkup: confirmation.Keyboard,
                    cancellationToken: ct);
                break;

            case "confirmdel":
                session.Tasks.Remove(task);
                _sessions.SaveTasks(session);
                await EditPlanListMessageAsync(message, session, ct);
                break;
        }
    }

    private async Task HandlePlanQuickDeadlineDateAsync(
        Message message,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var chatId = message.Chat.Id;

        if (session.State != UserState.WaitingForTaskDeadline || session.DraftTask is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Сейчас я не жду дедлайн. Начни добавление дела через /plan.", cancellationToken: ct);
            return;
        }

        var offsetDays = data switch
        {
            "plan_due_today" => 0,
            "plan_due_tomorrow" => 1,
            "plan_due_after_tomorrow" => 2,
            _ => 0
        };

        var date = DateTime.Today.AddDays(offsetDays);
        session.PendingTaskDeadlineDate = date;
        session.State = UserState.WaitingForTaskDeadlineTime;

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: $"📅 Дата: <b>{date:dd.MM.yyyy}</b>\n\n" +
                  "Теперь напиши время дедлайна в формате <b>ЧЧ:ММ</b>, например <b>18:00</b>.\n" +
                  "Если дедлайн не нужен, напиши <b>нет</b>.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task EditPlanListMessageAsync(Message message, UserSession session, CancellationToken ct)
    {
        var view = PlanListView.Build(session);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task EditCompletedPlanMessageAsync(Message message, UserSession session, CancellationToken ct)
    {
        var view = PlanListView.BuildCompleted(session);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task EditPlanTaskChoiceMessageAsync(
        Message message,
        UserSession session,
        string action,
        CancellationToken ct)
    {
        var view = PlanListView.BuildTaskChoice(session, action);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task HandleHomeworkAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = IsGroupChat(message.Chat.Type);

        if (data == "hw_cancel")
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.PendingGroupHomeworkChatId = null;
            session.PendingGroupHomeworkChatTitle = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: "Добавление ДЗ отменено.",
                cancellationToken: ct);
            return;
        }

        if (data == "hw_show_all")
        {
            if (isGroup)
            {
                await _bot.SendMessage(chatId, "В группе доступны только предметы из общего расписания.", cancellationToken: ct);
                return;
            }

            await EditHomeworkSubjectChoiceAsync(query, userId, session, showAll: true, ct);
            return;
        }

        if (data == "hw_config")
        {
            if (isGroup)
            {
                await _bot.SendMessage(chatId, "В группе нет личной настройки предметов. Здесь используется общее расписание.", cancellationToken: ct);
                return;
            }

            await EditHomeworkSubjectSettingsAsync(query, userId, session, ct);
            return;
        }

        if (data == "hw_done")
        {
            if (isGroup)
            {
                await _bot.SendMessage(chatId, "В группе нет личной настройки предметов. Просто выбери предмет из списка.", cancellationToken: ct);
                return;
            }

            await EditHomeworkSubjectChoiceAsync(query, userId, session, showAll: false, ct);
            return;
        }

        if (data.StartsWith("hw_fav_"))
        {
            if (isGroup)
            {
                await _bot.SendMessage(chatId, "В группе нет личной настройки предметов.", cancellationToken: ct);
                return;
            }

            await ToggleHomeworkFavoriteSubjectAsync(query, userId, session, data, ct);
            return;
        }

        if (data.StartsWith("hw_subject_"))
        {
            await HandleHomeworkSubjectChoiceAsync(query, userId, session, data, ct);
            return;
        }

        if (data.StartsWith("hw_type_"))
        {
            await HandleHomeworkLessonTypeChoiceAsync(query, userId, session, data, ct);
            return;
        }
    }

    private async Task HandleReminderAsync(
        CallbackQuery query,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        switch (data)
        {
            case "rem_set":
                session.State = UserState.WaitingForReminderTime;
                session.ReminderTargetChatId = chatId;
                session.ReminderTargetChatTitle = message.Chat.Title;
                session.ReminderTargetIsGroup = isGroup;

                if (!isGroup)
                    _reminders.MarkPromptAnswered(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "⏰ Во сколько писать в этот чат про общие дедлайны на завтра?\n\n" +
                          "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                          "Время по МСК."
                        : "⏰ Во сколько напоминать о дедлайнах на завтра?\n\n" +
                          "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                          "Время по МСК.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;

            case "rem_later":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;

                if (!isGroup)
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "Хорошо, напоминания для этой группы можно включить позже через /reminders."
                        : "Хорошо, не буду напоминать. Настроить можно в любой момент через /reminders.\n\n" +
                          BuildBasicCommandsText(),
                    cancellationToken: ct);
                break;

            case "rem_off":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;

                if (isGroup)
                    _groupReminders.Disable(chatId, message.Chat.Title);
                else
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "⏰ Групповые напоминания выключены. Включить снова можно через /reminders."
                        : "⏰ Напоминания выключены. Включить снова можно через /reminders.",
                    cancellationToken: ct);
                break;
        }
    }

    private async Task HandleReminderFlowAsync(
        CallbackQuery query,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        switch (data)
        {
            case "rem_set":
                session.ReminderTargetChatId = chatId;
                session.ReminderTargetChatTitle = message.Chat.Title;
                session.ReminderTargetIsGroup = isGroup;

                if (isGroup)
                {
                    session.State = UserState.Idle;
                    session.PendingGroupReminderFrequency = null;

                    await _bot.EditMessageText(
                        chatId: chatId,
                        messageId: message.MessageId,
                        text: "⏰ <b>Как часто присылать напоминания?</b>\n\n" +
                              "Сначала выбери удобную частоту, потом я спрошу время.",
                        parseMode: ParseMode.Html,
                        replyMarkup: BuildGroupReminderFrequencyKeyboard(),
                        cancellationToken: ct);
                    return;
                }

                session.State = UserState.WaitingForReminderTime;
                _reminders.MarkPromptAnswered(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: "⏰ Во сколько напоминать о дедлайнах на завтра?\n\n" +
                          "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                          "Время по МСК.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;

            case "rem_freq_daily":
            case "rem_freq_weekdays":
                session.State = UserState.WaitingForReminderTime;
                session.ReminderTargetChatId = chatId;
                session.ReminderTargetChatTitle = message.Chat.Title;
                session.ReminderTargetIsGroup = true;
                session.PendingGroupReminderFrequency = data == "rem_freq_weekdays"
                    ? Models.GroupReminderFrequency.Weekdays
                    : Models.GroupReminderFrequency.Daily;

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: $"⏰ <b>Во сколько удобно присылать напоминания {FormatGroupFrequencyText(session.PendingGroupReminderFrequency.Value)}?</b>\n\n" +
                          "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                          "Я пришлю сообщение в этот чат и отмечу участников, которых уже видел в группе.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;

            case "rem_later":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;
                session.PendingGroupReminderFrequency = null;

                if (!isGroup)
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "Хорошо, настроить напоминания для этой группы можно позже через /reminders."
                        : "Хорошо, не буду напоминать. Настроить можно в любой момент через /reminders.\n\n" +
                          BuildBasicCommandsText(),
                    cancellationToken: ct);
                return;

            case "rem_off":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;
                session.PendingGroupReminderFrequency = null;

                if (isGroup)
                    _groupReminders.Disable(chatId, message.Chat.Title);
                else
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "⏰ Групповые напоминания выключены. Включить снова можно через /reminders."
                        : "⏰ Напоминания выключены. Включить снова можно через /reminders.",
                    cancellationToken: ct);
                return;
        }
    }

    private static InlineKeyboardMarkup BuildGroupReminderFrequencyKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Каждый день", "rem_freq_daily"),
                InlineKeyboardButton.WithCallbackData("По будням", "rem_freq_weekdays")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Не сейчас", "rem_later")
            }
        });
    }

    private static string FormatGroupFrequencyText(Models.GroupReminderFrequency frequency)
        => frequency == Models.GroupReminderFrequency.Weekdays ? "по будням" : "каждый день";

    private async Task EditHomeworkSubjectChoiceAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        bool showAll,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (!TryGetAllScheduleEntries(userId, out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Сначала выбери своё расписание через /schedule, потом я смогу добавить ДЗ.",
                cancellationToken: ct);
            return;
        }

        var allSubjects = GetHomeworkSubjects(entries);
        var preferences = _homeworkSubjects.Get(userId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => allSubjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured && !showAll
            ? favoriteSubjects
            : allSubjects;

        session.State = UserState.Idle;
        session.DraftTask = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (!showAll && preferences.IsConfigured)
            buttons.Add(("👀 Показать все", "hw_show_all"));

        buttons.Add(("⚙️ Настроить", "hw_config"));
        buttons.Add(("🔴 Отмена", "hw_cancel"));

        var text = visibleSubjects.Count == 0
            ? "📚 <b>В списке ДЗ пока нет выбранных предметов.</b>\nНажми «Настроить» и отметь нужные."
            : preferences.IsConfigured || showAll
                ? "📚 <b>Выбери предмет, по которому задали ДЗ:</b>"
                : "📚 <b>Выбери предмет, по которому задали ДЗ:</b>\n\n" +
                  "Если тут есть лишние предметы, нажми «⚙️ Настроить» и оставь только нужные.\n\n" +
                  "Предметы будут идти в том порядке, в котором ты их отметишь.";

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task EditHomeworkSubjectSettingsAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (!TryGetAllScheduleEntries(userId, out _, out _, out var entries))
        {
            await _bot.SendMessage(
                chatId,
                "Сначала выбери своё расписание через /schedule, потом я смогу настроить предметы.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        var preferences = _homeworkSubjects.Get(userId);

        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var buttons = subjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                var priority = preferences.FavoriteSubjects.FindIndex(favorite =>
                    string.Equals(favorite, subject, StringComparison.OrdinalIgnoreCase));
                var label = priority >= 0
                    ? $"✅ {priority + 1}. {subject}"
                    : $"⬜ {subject}";

                return (label, $"hw_fav_{key}");
            })
            .Append(("Готово", "hw_done"));

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: "⚙️ <b>Предметы для ДЗ</b>\n" +
                  "Отмечай предметы в нужном порядке: первый выбранный будет выше всех в /add_homework.",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task ToggleHomeworkFavoriteSubjectAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var key = data["hw_fav_".Length..];
        if (!session.HomeworkSubjectChoices.TryGetValue(key, out var subject))
        {
            await _bot.SendMessage(
                query.Message!.Chat.Id,
                "Настройка предметов устарела. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        _homeworkSubjects.ToggleFavoriteSubject(userId, subject);
        await EditHomeworkSubjectSettingsAsync(query, userId, session, ct);
    }

    private async Task HandleHomeworkSubjectChoiceAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = IsGroupChat(message.Chat.Type);

        var key = data["hw_subject_".Length..];
        if (!session.HomeworkSubjectChoices.TryGetValue(key, out var subjectTitle))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Выбор предмета устарел. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        if (!TryGetAllScheduleEntries(GetScheduleSelectionKey(message.Chat, userId), out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Сначала выбери своё расписание через /schedule, потом я смогу добавить ДЗ.",
                cancellationToken: ct);
            return;
        }

        var typedSubjects = entries
            .Select(e => e.Subject)
            .Where(s => string.Equals(
                ScheduleCatalogService.GetHomeworkSubjectTitle(s),
                subjectTitle,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScheduleCatalogService.GetHomeworkLessonTypeLabel)
            .ToList();

        if (typedSubjects.Count == 0)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Не нашёл типы занятий для этого предмета. Попробуй открыть список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        if (typedSubjects.Count == 1)
        {
            await StartHomeworkTextInputAsync(message, session, entries, typedSubjects[0], isGroup, ct);
            return;
        }

        session.HomeworkLessonTypeChoices.Clear();
        var buttons = typedSubjects
            .Select((subject, index) =>
            {
                var typeKey = index.ToString();
                session.HomeworkLessonTypeChoices[typeKey] = subject;
                return (ScheduleCatalogService.GetHomeworkLessonTypeLabel(subject), $"hw_type_{typeKey}");
            })
            .Append(("🔴 Отмена", "hw_cancel"));

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: $"📚 <b>{Escape(subjectTitle)}</b>\nВыбери тип занятия:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task HandleHomeworkLessonTypeChoiceAsync(
        CallbackQuery query,
        long userId,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = IsGroupChat(message.Chat.Type);

        var key = data["hw_type_".Length..];
        if (!session.HomeworkLessonTypeChoices.TryGetValue(key, out var subject))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Выбор типа занятия устарел. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        if (!TryGetAllScheduleEntries(GetScheduleSelectionKey(message.Chat, userId), out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Сначала выбери своё расписание через /schedule, потом я смогу добавить ДЗ.",
                cancellationToken: ct);
            return;
        }

        await StartHomeworkTextInputAsync(message, session, entries, subject, isGroup, ct);
    }

    private async Task StartHomeworkTextInputAsync(
        Message message,
        UserSession session,
        List<ScheduleEntry> entries,
        string subject,
        bool isGroup,
        CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var deadline = _scheduleCatalog.FindNextLessonDate(entries, subject);
        if (!deadline.HasValue)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId,
                "Не смог найти следующую пару по этому предмету. Проверь расписание через /schedule.",
                cancellationToken: ct);
            return;
        }

        session.DraftTask = new StudyTask
        {
            Subject = subject,
            Deadline = deadline.Value
        };
        session.PendingGroupHomeworkChatId = isGroup ? chatId : null;
        session.PendingGroupHomeworkChatTitle = isGroup ? message.Chat.Title : null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();
        session.State = UserState.WaitingForHomeworkText;

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: $"📚 <b>{Escape(subject)}</b>\n" +
                  $"📅 Дедлайн: <b>{deadline.Value:dd.MM.yyyy}</b>\n\n" +
                  (isGroup ? "Напиши общее ДЗ для группы:" : "Напиши, что задали:"),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskAsync(CallbackQuery query, UserSession session, string data, CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (data == "task_back")
        {
            await RefreshTaskListMessageAsync(message, session, ct);
            return;
        }

        if (data == "task_completed")
        {
            var view = HomeworkListView.BuildCompleted(session);
            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: view.Text,
                parseMode: ParseMode.Html,
                replyMarkup: view.Keyboard,
                cancellationToken: ct);
            return;
        }

        if (data == "task_choose_done" || data == "task_choose_del")
        {
            var action = data == "task_choose_del" ? "del" : "done";
            var view = HomeworkListView.BuildTaskChoice(session, action);
            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: view.Text,
                parseMode: ParseMode.Html,
                replyMarkup: view.Keyboard,
                cancellationToken: ct);
            return;
        }

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
                _sessions.SaveTasks(session);
                await RefreshTaskListMessageAsync(message, session, ct);
                break;

            case "del":
                var confirmation = HomeworkListView.BuildDeleteConfirmation(task);
                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: confirmation.Text,
                    parseMode: ParseMode.Html,
                    replyMarkup: confirmation.Keyboard,
                    cancellationToken: ct);
                break;

            case "confirmdel":
                session.Tasks.Remove(task);
                _sessions.SaveTasks(session);
                await RefreshTaskListMessageAsync(message, session, ct);
                break;
        }
    }

    private async Task RefreshTaskListMessageAsync(Message message, UserSession session, CancellationToken ct)
    {
        var view = HomeworkListView.Build(session);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task HandleScheduleAsync(
        CallbackQuery query, long userId, UserSession session, string data, CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var messageId = message.MessageId;
        var selectionKey = GetScheduleSelectionKey(message.Chat, userId);
        var isGroup = IsGroupChat(message.Chat.Type);

        if (data.StartsWith("sched_dir_"))
        {
            var directionCode = data["sched_dir_".Length..];
            await SendCourseChoiceAsync(chatId, messageId, directionCode, ct);
            return;
        }

        if (data.StartsWith("sched_course_"))
        {
            var parts = data.Split('_', 4);
            if (parts.Length == 4 && int.TryParse(parts[3], out var course))
                await SendSubGroupChoiceOrSaveAsync(chatId, messageId, selectionKey, session, parts[2], course, ct);

            return;
        }

        if (data.StartsWith("sched_pick_"))
        {
            var parts = data.Split('_', 4);
            if (parts.Length == 4)
            {
                var subGroup = parts[3] == "none" ? (int?)null : int.Parse(parts[3]);
                await SaveScheduleSelectionAsync(chatId, messageId, selectionKey, session, parts[2], subGroup, ct);
            }

            return;
        }

        switch (data)
        {
            case "sched_today":
                await SendScheduleAsync(chatId, messageId, selectionKey, session, true, ct);
                break;

            case "sched_week":
                await SendScheduleAsync(chatId, messageId, selectionKey, session, false, ct);
                break;

            case "sched_change":
                await SendDirectionChoiceAsync(chatId, messageId, ct);
                break;

            case "sched_delete":
                await EditScheduleMessageAsync(
                    chatId: chatId,
                    messageId: messageId,
                    text: isGroup ? "Удалить сохранённое расписание группы?" : "Удалить сохранённое расписание?",
                    replyMarkup: ScheduleKeyboards.DeleteConfirmation,
                    cancellationToken: ct);
                break;

            case "sched_delete_yes":
                _scheduleSelections.Delete(selectionKey);
                session.Schedule.Clear();
                session.CurrentSubGroup = null;
                session.CurrentWeekType = null;
                session.PendingSchedule = null;
                session.State = UserState.Idle;

                await SendDirectionChoiceAsync(
                    chatId,
                    messageId,
                    ct,
                    isGroup
                        ? "Расписание группы удалено. Чтобы выбрать новое, используй /schedule."
                        : "Расписание удалено. Чтобы выбрать новое, используй /schedule.",
                    cancellationToken: ct);
                break;

            case "sched_delete_no":
                await SendSelectedScheduleMenuAsync(chatId, messageId, selectionKey, session, ct);
                break;

            case "sched_confirm":
                await ConfirmScheduleAsync(chatId, session, ct);
                break;

            case "sched_review":
                await StartScheduleReviewAsync(chatId, session, ct);
                break;

            case "sched_edit":
                await _bot.SendMessage(
                    chatId,
                    "Редактирование фото-расписания больше не используется. Выбери группу через /schedule.",
                    cancellationToken: ct);
                break;
        }
    }

    private async Task SendDirectionChoiceAsync(long chatId, CancellationToken ct)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} — {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: "Шаг 1/3. Выбери направление:",
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private bool TryGetAllScheduleEntries(
        long selectionKey,
        out ScheduleGroup? group,
        out int? subGroup,
        out List<ScheduleEntry> entries)
    {
        group = null;
        subGroup = null;
        entries = new List<ScheduleEntry>();

        var selection = _scheduleSelections.Get(selectionKey);
        if (selection is null)
            return false;

        group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
            return false;

        subGroup = selection.SubGroup;
        entries = _scheduleCatalog.GetAllEntriesForSelection(group, subGroup);
        return true;
    }

    private static List<string> GetHomeworkSubjects(List<ScheduleEntry> entries)
    {
        return entries
            .Select(e => ScheduleCatalogService.GetHomeworkSubjectTitle(e.Subject))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScheduleCatalogService.GetHomeworkSubjectSortGroup)
            .ThenBy(s => s)
            .ToList();
    }

    private async Task SendCourseChoiceAsync(long chatId, string directionCode, CancellationToken ct)
    {
        var groups = _scheduleCatalog.GetGroupsByDirection(directionCode);
        if (groups.Count == 0)
        {
            await _bot.SendMessage(chatId, "Не нашёл курсы для этого направления.", cancellationToken: ct);
            return;
        }

        var directionName = groups[0].DirectionName;
        var buttons = groups.Select(g => ($"{g.Course} курс", $"sched_course_{g.DirectionCode}_{g.Course}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: $"Шаг 2/3. Направление: <b>{Escape(directionName)}</b>\nВыбери курс:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task SendSubGroupChoiceOrSaveAsync(
        long chatId,
        long userId,
        UserSession session,
        string directionCode,
        int course,
        CancellationToken ct)
    {
        var group = _scheduleCatalog.GetGroup(directionCode, course);
        if (group is null)
        {
            await _bot.SendMessage(chatId, "Не нашёл расписание для этого курса.", cancellationToken: ct);
            return;
        }

        if (group.SubGroups.Count == 0)
        {
            await SaveScheduleSelectionAsync(chatId, userId, session, group.Id, null, ct);
            return;
        }

        var buttons = group.SubGroups
            .OrderBy(x => x)
            .Select(x => ($"Подгруппа {x}", $"sched_pick_{group.Id}_{x}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: $"Шаг 3/3. Курс: <b>{Escape(group.Title)}</b>\nВыбери подгруппу:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task SaveScheduleSelectionAsync(
        long chatId,
        long userId,
        UserSession session,
        string scheduleId,
        int? subGroup,
        CancellationToken ct)
    {
        var group = _scheduleCatalog.GetGroup(scheduleId);
        if (group is null)
        {
            await _bot.SendMessage(chatId, "Не нашёл выбранное расписание.", cancellationToken: ct);
            return;
        }

        _scheduleSelections.Save(userId, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = subGroup
        });

        ApplySelectionToSession(session, group, subGroup);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"{(userId == chatId ? "✅ <b>Готово! Расписание закреплено за тобой.</b>" : "✅ <b>Готово! Расписание сохранено для этой группы.</b>")}\n\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n\n" +
                  "Теперь ты можешь:\n" +
                  "• смотреть пары на сегодня и неделю через /schedule\n" +
                  "• добавлять домашку через /add_homework\n" +
                  "• смотреть список ДЗ через /homework\n\n" +
                  "Советую начать с /add_homework: выбери предмет, напиши задание, а дедлайн я поставлю по следующей паре.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        await SendSelectedScheduleMenuAsync(chatId, group, subGroup, ct);
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        long userId,
        UserSession session,
        CancellationToken ct)
    {
        var selection = _scheduleSelections.Get(userId);
        if (selection is null)
        {
            await SendDirectionChoiceAsync(chatId, ct);
            return;
        }

        var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
        {
            _scheduleSelections.Delete(userId);
            await SendDirectionChoiceAsync(chatId, ct);
            return;
        }

        ApplySelectionToSession(session, group, selection.SubGroup);
        await SendSelectedScheduleMenuAsync(chatId, group, selection.SubGroup, ct);
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        ScheduleGroup group,
        int? subGroup,
        CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId: chatId,
            text: $"{(chatId < 0 ? "📅 <b>Расписание группы</b>" : "📅 <b>Твоё расписание</b>")}\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n" +
                  $"Текущая неделя: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\n" +
                  "Что показать?",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private async Task SendScheduleAsync(
        long chatId,
        long userId,
        UserSession session,
        bool onlyToday,
        CancellationToken ct)
    {
        var selection = _scheduleSelections.Get(userId);
        if (selection is null)
        {
            await SendDirectionChoiceAsync(chatId, ct);
            return;
        }

        var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
        {
            _scheduleSelections.Delete(userId);
            await SendDirectionChoiceAsync(chatId, ct);
            return;
        }

        ApplySelectionToSession(session, group, selection.SubGroup);

        var entries = session.Schedule;
        var title = "Расписание на неделю";
        if (onlyToday)
        {
            var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
            entries = entries.Where(e => e.DayOfWeek == today).ToList();
            title = $"Расписание на сегодня, {ScheduleService.GetDayName(today).ToLowerInvariant()}";
        }

        var summary = entries.Count == 0
            ? "Пар нет."
            : ScheduleService.FormatSchedule(entries, session.CurrentWeekType);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"<b>{title}</b>\n" +
                  $"{Escape(FormatGroupTitle(group, selection.SubGroup))} | <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\n" +
                  summary,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private async Task SendDirectionChoiceAsync(
        long chatId,
        int messageId,
        CancellationToken ct,
        string? prefix = null)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} — {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        var text = string.IsNullOrWhiteSpace(prefix)
            ? "Шаг 1/3. Выбери направление:"
            : $"{prefix}\n\nШаг 1/3. Выбери направление:";

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: text,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private Task SendDirectionChoiceAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} — {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        return _bot.SendMessage(
            chatId: chatId,
            text: $"{text}\n\nШаг 1/3. Выбери направление:",
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: cancellationToken);
    }

    private Task SendDirectionChoiceAsync(
        long chatId,
        int messageId,
        CancellationToken ct,
        string text,
        CancellationToken cancellationToken)
        => SendDirectionChoiceAsync(chatId, messageId, ct, text);

    private async Task SendCourseChoiceAsync(long chatId, int messageId, string directionCode, CancellationToken ct)
    {
        var groups = _scheduleCatalog.GetGroupsByDirection(directionCode);
        if (groups.Count == 0)
        {
            await SendDirectionChoiceAsync(chatId, messageId, ct, "Не нашел курсы для этого направления.");
            return;
        }

        var directionName = groups[0].DirectionName;
        var buttons = groups.Select(g => ($"{g.Course} курс", $"sched_course_{g.DirectionCode}_{g.Course}"));

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: $"Шаг 2/3. Направление: <b>{Escape(directionName)}</b>\nВыбери курс:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task SendSubGroupChoiceOrSaveAsync(
        long chatId,
        int messageId,
        long selectionKey,
        UserSession session,
        string directionCode,
        int course,
        CancellationToken ct)
    {
        var group = _scheduleCatalog.GetGroup(directionCode, course);
        if (group is null)
        {
            await SendDirectionChoiceAsync(chatId, messageId, ct, "Не нашел расписание для этого курса.");
            return;
        }

        if (group.SubGroups.Count == 0)
        {
            await SaveScheduleSelectionAsync(chatId, messageId, selectionKey, session, group.Id, null, ct);
            return;
        }

        var buttons = group.SubGroups
            .OrderBy(x => x)
            .Select(x => ($"Подгруппа {x}", $"sched_pick_{group.Id}_{x}"));

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: $"Шаг 3/3. Курс: <b>{Escape(group.Title)}</b>\nВыбери подгруппу:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task SaveScheduleSelectionAsync(
        long chatId,
        int messageId,
        long selectionKey,
        UserSession session,
        string scheduleId,
        int? subGroup,
        CancellationToken ct)
    {
        var group = _scheduleCatalog.GetGroup(scheduleId);
        if (group is null)
        {
            await SendDirectionChoiceAsync(chatId, messageId, ct, "Не нашел выбранное расписание.");
            return;
        }

        _scheduleSelections.Save(selectionKey, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = subGroup
        });

        ApplySelectionToSession(session, group, subGroup);
        await SendSelectedScheduleMenuAsync(
            chatId,
            messageId,
            group,
            subGroup,
            ct,
            selectionKey == chatId ? "✅ Готово! Расписание сохранено для этой группы." : "✅ Готово! Расписание сохранено.");
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        int messageId,
        long selectionKey,
        UserSession session,
        CancellationToken ct)
    {
        var selection = _scheduleSelections.Get(selectionKey);
        if (selection is null)
        {
            await SendDirectionChoiceAsync(chatId, messageId, ct);
            return;
        }

        var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
        {
            _scheduleSelections.Delete(selectionKey);
            await SendDirectionChoiceAsync(chatId, messageId, ct);
            return;
        }

        ApplySelectionToSession(session, group, selection.SubGroup);
        await SendSelectedScheduleMenuAsync(chatId, messageId, group, selection.SubGroup, ct, null, selectionKey == chatId);
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        int messageId,
        ScheduleGroup group,
        int? subGroup,
        CancellationToken ct,
        string? prefix = null,
        bool isGroup = false)
    {
        var selectionKey = isGroup ? chatId : 0L;
        var text = string.IsNullOrWhiteSpace(prefix)
            ? $"{(selectionKey == chatId ? "📅 <b>Расписание группы</b>" : "📅 <b>Твоё расписание</b>")}\n{Escape(FormatGroupTitle(group, subGroup))}\nТекущая неделя: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\nЧто показать?"
            : $"{prefix}\n\n{(selectionKey == chatId ? "📅 <b>Расписание группы</b>" : "📅 <b>Твоё расписание</b>")}\n{Escape(FormatGroupTitle(group, subGroup))}\nТекущая неделя: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\nЧто показать?";

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private async Task SendScheduleAsync(
        long chatId,
        int messageId,
        long selectionKey,
        UserSession session,
        bool onlyToday,
        CancellationToken ct)
    {
        var selection = _scheduleSelections.Get(selectionKey);
        if (selection is null)
        {
            await SendDirectionChoiceAsync(chatId, messageId, ct);
            return;
        }

        var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
        {
            _scheduleSelections.Delete(selectionKey);
            await SendDirectionChoiceAsync(chatId, messageId, ct);
            return;
        }

        ApplySelectionToSession(session, group, selection.SubGroup);

        var entries = session.Schedule;
        var title = "Расписание на неделю";
        if (onlyToday)
        {
            var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
            entries = entries.Where(e => e.DayOfWeek == today).ToList();
            title = $"Расписание на сегодня, {ScheduleService.GetDayName(today).ToLowerInvariant()}";
        }

        var summary = entries.Count == 0
            ? "Пар нет."
            : ScheduleService.FormatSchedule(entries, session.CurrentWeekType);

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: $"<b>{title}</b>\n{Escape(FormatGroupTitle(group, selection.SubGroup))} | <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\n{summary}",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private Task EditScheduleMessageAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null)
    {
        if (parseMode.HasValue)
        {
            return _bot.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: parseMode.Value,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }

        return _bot.EditMessageText(
            chatId: chatId,
            messageId: messageId,
            text: text,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);
    }

    private void ApplySelectionToSession(UserSession session, ScheduleGroup group, int? subGroup)
    {
        var weekType = _scheduleCatalog.GetCurrentWeekType();
        session.CurrentWeekType = weekType;
        session.CurrentSubGroup = subGroup;
        session.Schedule = _scheduleCatalog.GetEntriesForSelection(group, subGroup, weekType);
        session.PendingSchedule = null;
        session.State = UserState.Idle;
    }

    private static long GetScheduleSelectionKey(Chat chat, long userId)
        => IsGroupChat(chat.Type) ? chat.Id : userId;

    private static bool IsGroupChat(ChatType chatType)
        => chatType is ChatType.Group or ChatType.Supergroup;

    private static string FormatGroupTitle(ScheduleGroup group, int? subGroup)
        => subGroup.HasValue ? $"{group.Title}, подгруппа {subGroup.Value}" : group.Title;

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static string BuildBasicCommandsText()
        => "Базовая настройка готова.\n\n" +
           "Основные команды:\n" +
           "/schedule — расписание\n" +
           "/add_homework — добавить ДЗ\n" +
           "/homework — список заданий\n" +
           "/timer — таймер для учёбы\n" +
           "/help — все команды";

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
                Time = e.Time,
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
        var view = PlanListView.Build(session);
        await _bot.SendMessage(
            chatId: chatId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }
}
