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
    private readonly UserGroupTaskBridgeService _userGroupTasks;
    private readonly GroupInputLockService _groupInputLocks;
    private readonly GroupParticipantStorageService _groupParticipants;
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly GroupHomeworkSubjectPreferencesService _groupHomeworkSubjects;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;
    private readonly InlineMessageCleanupService _inlineCleanup;

    public CallbackHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ReminderSettingsService reminders,
        UserGroupTaskBridgeService userGroupTasks,
        GroupInputLockService groupInputLocks,
        GroupParticipantStorageService groupParticipants,
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        GroupHomeworkSubjectPreferencesService groupHomeworkSubjects,
        HomeworkSubjectPreferencesService homeworkSubjects,
        InlineMessageCleanupService inlineCleanup)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _reminders = reminders;
        _userGroupTasks = userGroupTasks;
        _groupInputLocks = groupInputLocks;
        _groupParticipants = groupParticipants;
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _groupHomeworkSubjects = groupHomeworkSubjects;
        _homeworkSubjects = homeworkSubjects;
        _inlineCleanup = inlineCleanup;
    }

    public async Task HandleAsync(CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message!.Chat.Id;
        var userId = query.From.Id;
        var data = query.Data ?? string.Empty;

        if (IsGroupChat(query.Message.Chat.Type) &&
            ShouldBlockGroupCallback(chatId, userId, data, out var activeLock))
        {
            await _bot.AnswerCallbackQuery(
                callbackQueryId: query.Id,
                text: $"Сейчас я жду ввод от {activeLock!.OwnerDisplayName}.",
                showAlert: false,
                cancellationToken: ct);
            return;
        }

        var session = _sessions.GetOrCreate(userId, query.From.FirstName);

        if (await TryHandleSubGroupCallbackAsync(query, session, data, ct))
            return;

        if (query.Message?.ReplyMarkup is InlineKeyboardMarkup &&
            _inlineCleanup.IsExpired(query.Message.Chat.Id, query.Message.MessageId, query.Message.Date))
        {
            await AnswerCallbackPopupAsync(query.Id, "Это меню устарело. Открой команду заново.", ct);
            await _inlineCleanup.TryDeleteAsync(query.Message.Chat.Id, query.Message.MessageId, ct);
            return;
        }

        await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data.StartsWith("timer_")) { await HandleTimerAsync(chatId, userId, session, data, ct); return; }
        if (data.StartsWith("rest_")) { await HandleRestAsync(chatId, userId, data, ct); return; }
        if (data.StartsWith("plan_")) { await HandlePlanAsync(query, session, data, ct); return; }
        if (data.StartsWith("hw_")) { await HandleHomeworkAsync(query, userId, session, data, ct); return; }
        if (data.StartsWith("rem_")) { await HandleReminderFlowAsync(query, session, data, ct); return; }
        if (data.StartsWith("grp_")) { await HandleGroupParticipantsFlowAsync(query, session, data, ct); return; }
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
            session.ContinueHomeworkAfterScheduleSelection = false;
            session.PendingHomeworkScheduleSelectionKey = null;
            session.PendingGroupHomeworkChatId = null;
            session.PendingGroupHomeworkChatTitle = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

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
                await EditGroupHomeworkSubjectChoiceAsync(query, session, showAll: true, ct);
                return;
            }

            await EditHomeworkSubjectChoiceAsync(query, userId, session, showAll: true, ct);
            return;
        }

        if (data == "hw_config")
        {
            if (isGroup)
            {
                await EditGroupHomeworkSubjectSettingsAsync(query, session, ct);
                return;
            }

            await EditHomeworkSubjectSettingsAsync(query, userId, session, ct);
            return;
        }

        if (data == "hw_done")
        {
            if (isGroup)
            {
                await EditGroupHomeworkSubjectChoiceAsync(query, session, showAll: false, ct);
                return;
            }

            await EditHomeworkSubjectChoiceAsync(query, userId, session, showAll: false, ct);
            return;
        }

        if (data.StartsWith("hw_fav_"))
        {
            if (isGroup)
            {
                await ToggleGroupHomeworkFavoriteSubjectAsync(query, session, data, ct);
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

        if (data.StartsWith("hw_deadline_"))
        {
            await HandleHomeworkDeadlineChoiceAsync(query, session, data, ct);
            return;
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
                if (isGroup &&
                    !_groupInputLocks.TryAcquire(
                        chatId,
                        query.From.Id,
                        TextHandler.BuildAuthorName(query.From),
                        "reminder",
                        out var reminderLock))
                {
                    await AnswerCallbackPopupAsync(query.Id, $"Сейчас я жду ввод от {reminderLock!.OwnerDisplayName}.", ct);
                    return;
                }

                session.ReminderTargetChatId = chatId;
                session.ReminderTargetChatTitle = message.Chat.Title;
                session.ReminderTargetIsGroup = isGroup;

                session.State = UserState.Idle;
                session.PendingReminderMode = null;
                session.PendingReminderSelectedDays.Clear();

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "⏰ <b>Настроим напоминания для группы</b>\n\nКак часто их присылать?"
                        : "⏰ <b>Настроим личные напоминания</b>\n\nОни будут приходить и по ДЗ, и по твоим личным делам. Как часто их присылать?",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildReminderModeKeyboard(),
                    cancellationToken: ct);
                return;

            case "rem_mode_daily":
                await StartReminderTimeInputAsync(query, session, ReminderScheduleMode.Daily, Array.Empty<int>(), ct);
                return;

            case "rem_mode_weekdays":
                await StartReminderTimeInputAsync(query, session, ReminderScheduleMode.Weekdays, Array.Empty<int>(), ct);
                return;

            case "rem_mode_custom":
                session.State = UserState.Idle;
                session.PendingReminderMode = ReminderScheduleMode.CustomDays;
                session.PendingReminderSelectedDays = GetCurrentReminderSelectedDays(session, chatId).ToList();

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: "⏰ <b>Выбери дни для напоминаний</b>\n\n" +
                          "Можно отметить любые дни недели, потом нажми «Продолжить».",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildReminderDaysKeyboard(session.PendingReminderSelectedDays),
                    cancellationToken: ct);
                return;

            case "rem_days_continue":
                if (session.PendingReminderSelectedDays.Count == 0)
                {
                    await AnswerCallbackPopupAsync(query.Id, "Выбери хотя бы один день.", ct);
                    return;
                }

                await StartReminderTimeInputAsync(
                    query,
                    session,
                    ReminderScheduleMode.CustomDays,
                    session.PendingReminderSelectedDays,
                    ct);
                return;

            case "rem_later":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;
                session.PendingReminderMode = null;
                session.PendingReminderSelectedDays.Clear();
                if (isGroup)
                    _groupInputLocks.Release(chatId, query.From.Id);

                if (!isGroup)
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "Хорошо. Настроить напоминания для этой группы можно позже через /reminders."
                        : "Хорошо, не буду напоминать. Настроить можно в любой момент через /reminders.\n\n" +
                          BuildBasicCommandsText(),
                    cancellationToken: ct);
                return;

            case "rem_off":
                session.State = UserState.Idle;
                session.ReminderTargetChatId = 0;
                session.ReminderTargetChatTitle = null;
                session.ReminderTargetIsGroup = false;
                session.PendingReminderMode = null;
                session.PendingReminderSelectedDays.Clear();
                if (isGroup)
                    _groupInputLocks.Release(chatId, query.From.Id);

                if (isGroup)
                    _groupReminders.Disable(chatId, message.Chat.Title);
                else
                    _reminders.Disable(session.UserId, chatId);

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: isGroup
                        ? "⏰ Напоминания для этой группы выключены. Включить снова можно через /reminders."
                        : "⏰ Напоминания выключены. Включить снова можно через /reminders.",
                    cancellationToken: ct);
                return;
        }

        if (data.StartsWith("rem_day_"))
        {
            session.ReminderTargetChatId = chatId;
            session.ReminderTargetChatTitle = message.Chat.Title;
            session.ReminderTargetIsGroup = isGroup;
            session.PendingReminderMode = ReminderScheduleMode.CustomDays;

            if (!int.TryParse(data["rem_day_".Length..], out var day) || day is < 1 or > 7)
            {
                await AnswerCallbackPopupAsync(query.Id, "Не удалось определить день.", ct);
                return;
            }

            ToggleReminderDay(session.PendingReminderSelectedDays, day);

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: "⏰ <b>Выбери дни для напоминаний</b>\n\n" +
                      "Можно отметить любые дни недели, потом нажми «Продолжить».",
                parseMode: ParseMode.Html,
                replyMarkup: BuildReminderDaysKeyboard(session.PendingReminderSelectedDays),
                cancellationToken: ct);
        }
    }

    private async Task HandleGroupParticipantsFlowAsync(
        CallbackQuery query,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (data == "grp_members_set")
        {
            if (!_groupInputLocks.TryAcquire(
                    chatId,
                    query.From.Id,
                    TextHandler.BuildAuthorName(query.From),
                    "participants",
                    out var participantLock))
            {
                await AnswerCallbackPopupAsync(query.Id, $"Сейчас я жду ввод от {participantLock!.OwnerDisplayName}.", ct);
                return;
            }

            session.State = UserState.WaitingForGroupParticipantUsernames;
            session.PendingParticipantChatId = chatId;
            session.PendingParticipantChatTitle = message.Chat.Title;

            var currentUsernames = _groupParticipants.GetManualUsernames(chatId);
            var currentText = currentUsernames.Count == 0
                ? "Пока список пуст."
                : string.Join(", ", currentUsernames);

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: "👥 <b>Участники группы</b>\n\n" +
                      "Пришли список username через пробел, запятую или с новой строки.\n" +
                      "Пример: <code>@anna @ivan @petr</code>\n\n" +
                      "Текущий список:\n" +
                      $"{Escape(currentText)}\n\n" +
                      "По этим username будет работать созыв, а групповые ДЗ смогут попасть в личный бот даже без активности в чате.",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Очистить список", "grp_members_clear"),
                        InlineKeyboardButton.WithCallbackData("Отмена", "grp_members_cancel")
                    }
                }),
                cancellationToken: ct);
            return;
        }

        if (data == "grp_members_clear")
        {
            _groupParticipants.SetManualUsernames(chatId, message.Chat.Title, Array.Empty<string>());
            session.State = UserState.Idle;
            session.PendingParticipantChatId = null;
            session.PendingParticipantChatTitle = null;
            _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: "Список username для группы очищен.",
                cancellationToken: ct);
            return;
        }

        if (data == "grp_members_cancel")
        {
            session.State = UserState.Idle;
            session.PendingParticipantChatId = null;
            session.PendingParticipantChatTitle = null;
            _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: "Настройка участников группы отменена.",
                cancellationToken: ct);
        }
    }

    private async Task StartReminderTimeInputAsync(
        CallbackQuery query,
        UserSession session,
        ReminderScheduleMode mode,
        IReadOnlyCollection<int> selectedDays,
        CancellationToken ct)
    {
        var message = query.Message!;
        var isGroup = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        session.State = UserState.WaitingForReminderTime;
        session.ReminderTargetChatId = message.Chat.Id;
        session.ReminderTargetChatTitle = message.Chat.Title;
        session.ReminderTargetIsGroup = isGroup;
        session.PendingReminderMode = mode;
        session.PendingReminderSelectedDays = selectedDays.OrderBy(day => day).ToList();

        if (!isGroup)
            _reminders.MarkPromptAnswered(session.UserId, message.Chat.Id);

        var modeText = FormatReminderModeText(mode, session.PendingReminderSelectedDays);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: isGroup
                ? $"⏰ <b>Во сколько присылать напоминания {modeText}?</b>\n\n" +
                  "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                  "Я пришлю сообщение в этот чат и отмечу участников, которых уже видел в группе или которые добавлены по username."
                : $"⏰ <b>Во сколько присылать личные напоминания {modeText}?</b>\n\n" +
                  "Напиши время в формате <b>ЧЧ:ММ</b>, например <b>20:00</b>.\n" +
                  "Я буду присылать напоминания и по ДЗ, и по твоим личным делам.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private IReadOnlyList<int> GetCurrentReminderSelectedDays(UserSession session, long chatId)
    {
        if (session.ReminderTargetIsGroup)
            return _groupReminders.Get(chatId).SelectedDays;

        return _reminders.Get(session.UserId).SelectedDays;
    }

    private static InlineKeyboardMarkup BuildReminderModeKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Каждый день", "rem_mode_daily"),
                InlineKeyboardButton.WithCallbackData("По будням", "rem_mode_weekdays")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Свои дни", "rem_mode_custom")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Не сейчас", "rem_later")
            }
        });
    }

    private static InlineKeyboardMarkup BuildReminderDaysKeyboard(IReadOnlyCollection<int> selectedDays)
    {
        static string Label(string title, bool selected) => selected ? $"✓ {title}" : title;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(Label("Пн", selectedDays.Contains(1)), "rem_day_1"),
                InlineKeyboardButton.WithCallbackData(Label("Вт", selectedDays.Contains(2)), "rem_day_2"),
                InlineKeyboardButton.WithCallbackData(Label("Ср", selectedDays.Contains(3)), "rem_day_3"),
                InlineKeyboardButton.WithCallbackData(Label("Чт", selectedDays.Contains(4)), "rem_day_4")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(Label("Пт", selectedDays.Contains(5)), "rem_day_5"),
                InlineKeyboardButton.WithCallbackData(Label("Сб", selectedDays.Contains(6)), "rem_day_6"),
                InlineKeyboardButton.WithCallbackData(Label("Вс", selectedDays.Contains(7)), "rem_day_7")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Продолжить", "rem_days_continue"),
                InlineKeyboardButton.WithCallbackData("Отмена", "rem_later")
            }
        });
    }

    private static void ToggleReminderDay(List<int> selectedDays, int day)
    {
        if (selectedDays.Contains(day))
            selectedDays.Remove(day);
        else
            selectedDays.Add(day);

        selectedDays.Sort();
    }

    private static string FormatReminderModeText(ReminderScheduleMode mode, IReadOnlyCollection<int> selectedDays)
    {
        return mode switch
        {
            ReminderScheduleMode.Weekdays => "по будням",
            ReminderScheduleMode.CustomDays when selectedDays.Count > 0 => "в выбранные дни",
            ReminderScheduleMode.CustomDays => "по выбранным дням",
            _ => "каждый день"
        };
    }

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
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
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
                    ? $"{priority + 1}. {subject}"
                    : $"{subject}";

                return (label, $"hw_fav_{key}");
            })
            .Append(("Готово", "hw_done"));

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: "<b>Предметы для ДЗ</b>\n" +
                  "Отмечай предметы в нужном порядке: первый выбранный будет выше всех в /add_homework.",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
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
                "Настройка предметов устарела. Открой список заново через /homework_settings или /add_homework.",
                cancellationToken: ct);
            return;
        }

        _homeworkSubjects.ToggleFavoriteSubject(userId, subject);
        await EditHomeworkSubjectSettingsAsync(query, userId, session, ct);
    }

    private async Task EditGroupHomeworkSubjectChoiceAsync(
        CallbackQuery query,
        UserSession session,
        bool showAll,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (!TryGetAllScheduleEntries(chatId, out _, out _, out var entries))
        {
            await _bot.SendMessage(
                chatId,
                "Сначала подключи расписание группы через /schedule, потом я смогу добавить общее ДЗ.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        var preferences = _groupHomeworkSubjects.Get(chatId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => subjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured && !showAll
            ? favoriteSubjects
            : subjects;

        session.State = UserState.Idle;
        session.DraftTask = null;
        session.PendingGroupHomeworkChatId = null;
        session.PendingGroupHomeworkChatTitle = null;
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
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var titlePrefix = string.IsNullOrWhiteSpace(message.Chat.Title)
            ? string.Empty
            : $"<b>{Escape(message.Chat.Title)}</b>\n\n";

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: titlePrefix + BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
    }

    private async Task EditGroupHomeworkSubjectSettingsAsync(
        CallbackQuery query,
        UserSession session,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (!TryGetAllScheduleEntries(chatId, out _, out _, out var entries))
        {
            await _bot.SendMessage(
                chatId,
                "Сначала подключи расписание группы через /schedule, потом я смогу настроить предметы для общего ДЗ.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        var preferences = _groupHomeworkSubjects.Get(chatId);

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
                    ? $"{priority + 1}. {subject}"
                    : $"{subject}";

                return (label, $"hw_fav_{key}");
            })
            .Append(("Готово", "hw_done"));

        var titlePrefix = string.IsNullOrWhiteSpace(message.Chat.Title)
            ? string.Empty
            : $"Чат: <b>{Escape(message.Chat.Title)}</b>\n";

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: "<b>Предметы для общего ДЗ</b>\n" +
                  titlePrefix +
                  "Отмечай предметы в нужном порядке: первый выбранный будет выше всех в /add_homework.",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
    }

    private async Task ToggleGroupHomeworkFavoriteSubjectAsync(
        CallbackQuery query,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var key = data["hw_fav_".Length..];
        if (!session.HomeworkSubjectChoices.TryGetValue(key, out var subject))
        {
            await _bot.SendMessage(
                query.Message!.Chat.Id,
                "Настройка предметов устарела. Открой список заново через /homework_settings или /add_homework.",
                cancellationToken: ct);
            return;
        }

        var chat = query.Message!.Chat;
        _groupHomeworkSubjects.ToggleFavoriteSubject(chat.Id, chat.Title, subject);
        await EditGroupHomeworkSubjectSettingsAsync(query, session, ct);
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
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

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
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

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
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.SendMessage(
                chatId,
                "Не нашёл типы занятий для этого предмета. Попробуй открыть список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        if (typedSubjects.Count == 1)
        {
            await StartHomeworkDeadlineChoiceAsync(message, session, entries, typedSubjects[0], isGroup, ct);
            return;
        }

        session.HomeworkLessonTypeChoices.Clear();
        session.HomeworkDeadlineChoices.Clear();
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
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

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
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.SendMessage(
                chatId,
                "Сначала выбери своё расписание через /schedule, потом я смогу добавить ДЗ.",
                cancellationToken: ct);
            return;
        }

        await StartHomeworkDeadlineChoiceAsync(message, session, entries, subject, isGroup, ct);
    }

    private async Task StartHomeworkDeadlineChoiceAsync(
        Message message,
        UserSession session,
        List<ScheduleEntry> entries,
        string subject,
        bool isGroup,
        CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var deadlines = _scheduleCatalog.FindUpcomingHomeworkDates(entries, subject, 5);
        if (deadlines.Count == 0)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, session.UserId);

            await _bot.SendMessage(
                chatId,
                "Не смог найти ближайшие даты для этого предмета. Проверь расписание через /schedule.",
                cancellationToken: ct);
            return;
        }

        session.DraftTask = new StudyTask
        {
            Subject = subject
        };
        session.PendingGroupHomeworkChatId = isGroup ? chatId : null;
        session.PendingGroupHomeworkChatTitle = isGroup ? message.Chat.Title : null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();
        session.HomeworkDeadlineChoices.Clear();
        foreach (var (deadline, index) in deadlines.Select((deadline, index) => (deadline, index)))
            session.HomeworkDeadlineChoices[index.ToString()] = deadline;

        await BeginHomeworkTextInputAsync(message, session, subject, deadlines[0], isGroup, ct);
    }

    private async Task HandleHomeworkDeadlineChoiceAsync(
        CallbackQuery query,
        UserSession session,
        string data,
        CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;
        var isGroup = IsGroupChat(message.Chat.Type);

        if (session.DraftTask is null || string.IsNullOrWhiteSpace(session.DraftTask.Subject))
        {
            session.State = UserState.Idle;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.SendMessage(
                chatId,
                "Выбор дедлайна устарел. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        var key = data["hw_deadline_".Length..];
        if (string.Equals(key, "other", StringComparison.OrdinalIgnoreCase))
        {
            await ShowHomeworkDeadlineOptionsAsync(message, session, ct);
            return;
        }

        if (string.Equals(key, "back", StringComparison.OrdinalIgnoreCase))
        {
            if (!session.DraftTask.Deadline.HasValue)
            {
                await _bot.SendMessage(
                    chatId,
                    "Текущий дедлайн потерялся. Открой список заново через /add_homework.",
                    cancellationToken: ct);
                return;
            }

            await BeginHomeworkTextInputAsync(message, session, session.DraftTask.Subject, session.DraftTask.Deadline.Value, isGroup, ct);
            return;
        }

        if (!session.HomeworkDeadlineChoices.TryGetValue(key, out var deadline))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.SendMessage(
                chatId,
                "Эта дата уже недоступна. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        if (deadline.Date < DateTime.Today)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, query.From.Id);

            await _bot.SendMessage(
                chatId,
                "Этот дедлайн уже прошёл. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        await BeginHomeworkTextInputAsync(message, session, session.DraftTask.Subject, deadline, isGroup, ct);
    }

    private async Task BeginHomeworkTextInputAsync(
        Message message,
        UserSession session,
        string subject,
        DateTime deadline,
        bool isGroup,
        CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        if (deadline.Date < DateTime.Today)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (isGroup)
                _groupInputLocks.Release(chatId, session.UserId);

            await _bot.SendMessage(
                chatId,
                "Этот дедлайн уже прошёл. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        session.DraftTask ??= new StudyTask();
        session.DraftTask.Subject = subject;
        session.DraftTask.Deadline = deadline;
        session.PendingGroupHomeworkChatId = isGroup ? chatId : null;
        session.PendingGroupHomeworkChatTitle = isGroup ? message.Chat.Title : null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();
        session.State = UserState.WaitingForHomeworkText;

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: $"📚 <b>{Escape(subject)}</b>\n" +
                  $"📅 Дедлайн: <b>{deadline:dd.MM.yyyy}</b>\n\n" +
                  "Если нужен другой дедлайн, нажми кнопку ниже.\n\n" +
                  (isGroup ? "Напиши общее ДЗ для группы:" : "Напиши, что задали:"),
            parseMode: ParseMode.Html,
            replyMarkup: BuildHomeworkTextInputKeyboard(session.HomeworkDeadlineChoices.Count > 1),
            cancellationToken: ct);
    }

    private async Task ShowHomeworkDeadlineOptionsAsync(
        Message message,
        UserSession session,
        CancellationToken ct)
    {
        if (session.DraftTask is null || string.IsNullOrWhiteSpace(session.DraftTask.Subject))
        {
            await _bot.SendMessage(
                message.Chat.Id,
                "Выбор дедлайна устарел. Открой список заново через /add_homework.",
                cancellationToken: ct);
            return;
        }

        var buttons = session.HomeworkDeadlineChoices
            .Where(item => item.Value.Date >= DateTime.Today)
            .OrderBy(item => item.Value)
            .Select(item => ($"{item.Value:dd.MM.yyyy}", $"hw_deadline_{item.Key}"))
            .ToList();

        if (buttons.Count == 0)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();
            session.HomeworkDeadlineChoices.Clear();
            if (IsGroupChat(message.Chat.Type))
                _groupInputLocks.Release(message.Chat.Id, session.UserId);

            await _bot.SendMessage(
                message.Chat.Id,
                "Доступных будущих дат больше не осталось. Открой /add_homework заново.",
                cancellationToken: ct);
            return;
        }

        buttons.Add(("Назад", "hw_deadline_back"));
        buttons.Add(("🔴 Отмена", "hw_cancel"));

        var currentDeadline = session.DraftTask.Deadline.HasValue
            ? session.DraftTask.Deadline.Value.ToString("dd.MM.yyyy")
            : "не выбран";

        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: $"📚 <b>{Escape(session.DraftTask.Subject)}</b>\n" +
                  $"Текущий дедлайн: <b>{currentDeadline}</b>\n\n" +
                  "Выбери другую доступную дату:",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task HandleTaskAsync(CallbackQuery query, UserSession session, string data, CancellationToken ct)
    {
        var message = query.Message!;
        var chatId = message.Chat.Id;

        if (IsGroupChat(message.Chat.Type))
        {
            await HandleGroupTaskAsync(message, data, ct);
            return;
        }

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
        var linkedGroups = _userGroupTasks.GetLinkedGroupTaskFeeds(session.UserId);
        var view = HomeworkListView.BuildWithLinkedGroups(session, linkedGroups);
        await _bot.EditMessageText(
            chatId: message.Chat.Id,
            messageId: message.MessageId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task HandleGroupTaskAsync(Message message, string data, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var chatTitle = message.Chat.Title ?? "Группа";

        if (data == "task_group_back")
        {
            await RefreshGroupTaskListMessageAsync(message, ct);
            return;
        }

        if (data == "task_group_choose_del")
        {
            var choiceView = HomeworkListView.BuildGroupDeleteChoice(chatTitle, _groupTasks.Get(chatId));
            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: choiceView.Text,
                parseMode: ParseMode.Html,
                replyMarkup: choiceView.Keyboard,
                cancellationToken: ct);
            return;
        }

        if (data.StartsWith("task_group_del_"))
        {
            var shortId = data["task_group_del_".Length..];
            var task = _groupTasks.Get(chatId).FirstOrDefault(t => t.ShortId == shortId);
            if (task is null)
            {
                await RefreshGroupTaskListMessageAsync(message, ct);
                return;
            }

            var confirmation = HomeworkListView.BuildGroupDeleteConfirmation(task);
            await _bot.EditMessageText(
                chatId: chatId,
                messageId: message.MessageId,
                text: confirmation.Text,
                parseMode: ParseMode.Html,
                replyMarkup: confirmation.Keyboard,
                cancellationToken: ct);
            return;
        }

        if (data.StartsWith("task_group_confirmdel_"))
        {
            var shortId = data["task_group_confirmdel_".Length..];
            var tasks = _groupTasks.Get(chatId);
            var removed = tasks.RemoveAll(t => t.ShortId == shortId) > 0;

            if (removed)
                _groupTasks.Save(chatId, message.Chat.Title, tasks);

            await RefreshGroupTaskListMessageAsync(message, ct);
        }
    }

    private async Task RefreshGroupTaskListMessageAsync(Message message, CancellationToken ct)
    {
        var view = HomeworkListView.BuildGroup(message.Chat.Title ?? "Группа", _groupTasks.Get(message.Chat.Id));
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

    private async Task SendHomeworkSubjectChoiceAsync(
        long chatId,
        long userId,
        UserSession session,
        List<string> allSubjects,
        bool showAll,
        CancellationToken ct)
    {
        session.State = UserState.Idle;
        session.DraftTask = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var preferences = _homeworkSubjects.Get(userId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => allSubjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured && !showAll
            ? favoriteSubjects
            : allSubjects;

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (!showAll && preferences.IsConfigured)
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var message = await _bot.SendMessage(
            chatId: chatId,
            text: BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, message.MessageId, message.ReplyMarkup);
    }

    private async Task EditHomeworkSubjectChoiceAfterScheduleAsync(
        long chatId,
        int messageId,
        long userId,
        UserSession session,
        List<string> allSubjects,
        CancellationToken ct)
    {
        session.State = UserState.Idle;
        session.DraftTask = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var preferences = _homeworkSubjects.Get(userId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => allSubjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured
            ? favoriteSubjects
            : allSubjects;

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (preferences.IsConfigured)
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: messageId,
            text: "Расписание подключено.\n\n" + BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll: false, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
    }

    private async Task SendGroupHomeworkSubjectChoiceAsync(
        long chatId,
        UserSession session,
        List<string> subjects,
        CancellationToken ct)
    {
        session.State = UserState.Idle;
        session.DraftTask = null;
        session.PendingGroupHomeworkChatId = null;
        session.PendingGroupHomeworkChatTitle = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var preferences = _groupHomeworkSubjects.Get(chatId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => subjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured
            ? favoriteSubjects
            : subjects;

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (preferences.IsConfigured)
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var message = await _bot.SendMessage(
            chatId: chatId,
            text: "Расписание группы подключено.\n\n" + BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll: false, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, message.MessageId, message.ReplyMarkup);
    }

    private async Task EditGroupHomeworkSubjectChoiceAsync(
        long chatId,
        int messageId,
        UserSession session,
        List<string> subjects,
        CancellationToken ct)
    {
        session.State = UserState.Idle;
        session.DraftTask = null;
        session.PendingGroupHomeworkChatId = null;
        session.PendingGroupHomeworkChatTitle = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var preferences = _groupHomeworkSubjects.Get(chatId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => subjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured
            ? favoriteSubjects
            : subjects;

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (preferences.IsConfigured)
            buttons.Add(("Показать все", "hw_show_all"));

        buttons.Add(("Настроить", "hw_config"));

        var editedMessage = await _bot.EditMessageText(
            chatId: chatId,
            messageId: messageId,
            text: "Расписание группы подключено.\n\n" + BuildHomeworkSubjectChoiceText(preferences.IsConfigured, showAll: false, visibleSubjects.Count),
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);

        _inlineCleanup.Track(chatId, editedMessage.MessageId, editedMessage.ReplyMarkup);
    }
    private static string BuildHomeworkSubjectChoiceText(bool isConfigured, bool showAll, int visibleCount)
    {
        if (visibleCount == 0)
            return "<b>В списке ДЗ пока нет выбранных предметов.</b>\nНажми «Настроить» и отметь нужные.";

        if (isConfigured || showAll)
            return "<b>Выбери предмет, по которому задали ДЗ:</b>";

        return "<b>Выбери предмет, по которому задали ДЗ:</b>\n\n" +
               "Если тут есть лишние предметы, нажми «Настроить» и оставь только нужные.\n\n" +
               "Предметы будут идти в том порядке, в котором ты их отметишь.";
    }
    private static void ClearHomeworkContinuation(UserSession session)
    {
        session.ContinueHomeworkAfterScheduleSelection = false;
        session.PendingHomeworkScheduleSelectionKey = null;
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

        if (session.ContinueHomeworkAfterScheduleSelection &&
            session.PendingHomeworkScheduleSelectionKey == userId)
        {
            await ContinueHomeworkFlowAfterScheduleSelectionAsync(chatId, userId, session, ct);
            return;
        }

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

        if (session.ContinueHomeworkAfterScheduleSelection &&
            session.PendingHomeworkScheduleSelectionKey == selectionKey)
        {
            await ContinueHomeworkFlowAfterScheduleSelectionAsync(chatId, messageId, selectionKey, session, ct);
            return;
        }

        await SendSelectedScheduleMenuAsync(
            chatId,
            messageId,
            group,
            subGroup,
            ct,
            selectionKey == chatId ? "✅ Готово! Расписание сохранено для этой группы." : "✅ Готово! Расписание сохранено.");
    }

    private async Task ContinueHomeworkFlowAfterScheduleSelectionAsync(
        long chatId,
        long selectionKey,
        UserSession session,
        CancellationToken ct)
    {
        ClearHomeworkContinuation(session);

        if (!TryGetAllScheduleEntries(selectionKey, out _, out _, out var entries))
        {
            if (selectionKey == chatId)
                _groupInputLocks.Release(chatId, session.UserId);

            await _bot.SendMessage(
                chatId,
                "Расписание сохранилось, но я не смог сразу открыть выбор предмета. Попробуй ещё раз через /add_homework.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        if (subjects.Count == 0)
        {
            if (selectionKey == chatId)
                _groupInputLocks.Release(chatId, session.UserId);

            await _bot.SendMessage(
                chatId,
                "Расписание подключено, но в нём пока не нашлось предметов для ДЗ. Можешь изменить расписание через /schedule.",
                cancellationToken: ct);
            return;
        }

        if (selectionKey == chatId)
        {
            await SendGroupHomeworkSubjectChoiceAsync(chatId, session, subjects, ct);
            return;
        }

        await SendHomeworkSubjectChoiceAsync(chatId, selectionKey, session, subjects, showAll: false, ct);
    }

    private async Task ContinueHomeworkFlowAfterScheduleSelectionAsync(
        long chatId,
        int messageId,
        long selectionKey,
        UserSession session,
        CancellationToken ct)
    {
        ClearHomeworkContinuation(session);

        if (!TryGetAllScheduleEntries(selectionKey, out _, out _, out var entries))
        {
            if (selectionKey == chatId)
                _groupInputLocks.Release(chatId, session.UserId);

            await EditScheduleMessageAsync(
                chatId: chatId,
                messageId: messageId,
                text: "Расписание сохранилось, но я не смог сразу открыть выбор предмета. Попробуй ещё раз через /add_homework.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        if (subjects.Count == 0)
        {
            if (selectionKey == chatId)
                _groupInputLocks.Release(chatId, session.UserId);

            await EditScheduleMessageAsync(
                chatId: chatId,
                messageId: messageId,
                text: "Р Р°СЃРїРёСЃР°РЅРёРµ РїРѕРґРєР»СЋС‡РµРЅРѕ, РЅРѕ РІ РЅС‘Рј РїРѕРєР° РЅРµ РЅР°С€Р»РѕСЃСЊ РїСЂРµРґРјРµС‚РѕРІ РґР»СЏ Р”Р—. РР·РјРµРЅРёС‚СЊ СЂР°СЃРїРёСЃР°РЅРёРµ РјРѕР¶РЅРѕ С‡РµСЂРµР· /schedule.",
                cancellationToken: ct);
            return;
        }

        if (selectionKey == chatId)
        {
            await EditGroupHomeworkSubjectChoiceAsync(chatId, messageId, session, subjects, ct);
            return;
        }

        await EditHomeworkSubjectChoiceAfterScheduleAsync(chatId, messageId, selectionKey, session, subjects, ct);
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

    private static InlineKeyboardMarkup BuildHomeworkTextInputKeyboard(bool showOtherDeadline)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (showOtherDeadline)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("Другой дедлайн", "hw_deadline_other")
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🔴 Отмена", "hw_cancel")
        });

        return new InlineKeyboardMarkup(rows);
    }

    private bool ShouldBlockGroupCallback(long chatId, long userId, string data, out GroupInputLock? activeLock)
    {
        activeLock = _groupInputLocks.Get(chatId);
        if (activeLock is null || activeLock.UserId == userId)
            return false;

        return activeLock.Purpose switch
        {
            "homework" => data.StartsWith("hw_", StringComparison.OrdinalIgnoreCase) ||
                          data.StartsWith("sched_", StringComparison.OrdinalIgnoreCase),
            "reminder" => data.StartsWith("rem_", StringComparison.OrdinalIgnoreCase),
            "participants" => data.StartsWith("grp_", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

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




