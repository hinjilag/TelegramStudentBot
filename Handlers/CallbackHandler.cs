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

        var session = _sessions.GetOrCreate(userId, query.From.FirstName);

        if (await TryHandleSubGroupCallbackAsync(query, session, data, ct))
            return;

        if (query.Message?.ReplyMarkup is InlineKeyboardMarkup &&
            _inlineCleanup.IsExpired(query.Message.Chat.Id, query.Message.MessageId, query.Message.Date))
        {
            await AnswerCallbackPopupAsync(query.Id, "Р­С‚Рѕ РјРµРЅСЋ СѓСЃС‚Р°СЂРµР»Рѕ. РћС‚РєСЂРѕР№ РєРѕРјР°РЅРґСѓ Р·Р°РЅРѕРІРѕ.", ct);
            await _inlineCleanup.TryDeleteAsync(query.Message.Chat.Id, query.Message.MessageId, ct);
            return;
        }

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
        await AnswerCallbackPopupAsync(query.Id, "Р Р°СЃРїРѕР·РЅР°РІР°РЅРёРµ СЂР°СЃРїРёСЃР°РЅРёСЏ РёР· С„РѕС‚Рѕ СѓРґР°Р»РµРЅРѕ.", ct);
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
                await _bot.SendMessage(chatId, "вњЏпёЏ Р’РІРµРґРё РєРѕР»РёС‡РµСЃС‚РІРѕ РјРёРЅСѓС‚ (1-300):", cancellationToken: ct);
                break;

            case "timer_stop":
                var stopped = _timers.StopTimer(userId);
                await _bot.SendMessage(
                    chatId,
                    stopped ? "вЏ№ РўР°Р№РјРµСЂ РѕСЃС‚Р°РЅРѕРІР»РµРЅ." : "в„№пёЏ РќРµС‚ Р°РєС‚РёРІРЅРѕРіРѕ С‚Р°Р№РјРµСЂР°.",
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
                    text: "рџ“ќ <b>Р”РѕР±Р°РІР»РµРЅРёРµ РґРµР»Р°</b>\n\nРќР°РїРёС€Рё, С‡С‚Рѕ РЅСѓР¶РЅРѕ СЃРґРµР»Р°С‚СЊ:",
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
            await _bot.SendMessage(chatId, "вљ пёЏ Р”РµР»Рѕ РЅРµ РЅР°Р№РґРµРЅРѕ.", cancellationToken: ct);
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
            await _bot.SendMessage(chatId, "вљ пёЏ РЎРµР№С‡Р°СЃ СЏ РЅРµ Р¶РґСѓ РґРµРґР»Р°Р№РЅ. РќР°С‡РЅРё РґРѕР±Р°РІР»РµРЅРёРµ РґРµР»Р° С‡РµСЂРµР· /plan.", cancellationToken: ct);
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
            text: $"рџ“… Р”Р°С‚Р°: <b>{date:dd.MM.yyyy}</b>\n\n" +
                  "РўРµРїРµСЂСЊ РЅР°РїРёС€Рё РІСЂРµРјСЏ РґРµРґР»Р°Р№РЅР° РІ С„РѕСЂРјР°С‚Рµ <b>Р§Р§:РњРњ</b>, РЅР°РїСЂРёРјРµСЂ <b>18:00</b>.\n" +
                  "Р•СЃР»Рё РґРµРґР»Р°Р№РЅ РЅРµ РЅСѓР¶РµРЅ, РЅР°РїРёС€Рё <b>РЅРµС‚</b>.",
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
                        ? "вЏ° Р’Рѕ СЃРєРѕР»СЊРєРѕ РїРёСЃР°С‚СЊ РІ СЌС‚РѕС‚ С‡Р°С‚ РїСЂРѕ РѕР±С‰РёРµ РґРµРґР»Р°Р№РЅС‹ РЅР° Р·Р°РІС‚СЂР°?\n\n" +
                          "РќР°РїРёС€Рё РІСЂРµРјСЏ РІ С„РѕСЂРјР°С‚Рµ <b>Р§Р§:РњРњ</b>, РЅР°РїСЂРёРјРµСЂ <b>20:00</b>.\n" +
                          "Р’СЂРµРјСЏ РїРѕ РњРЎРљ."
                        : "вЏ° Р’Рѕ СЃРєРѕР»СЊРєРѕ РЅР°РїРѕРјРёРЅР°С‚СЊ Рѕ РґРµРґР»Р°Р№РЅР°С… РЅР° Р·Р°РІС‚СЂР°?\n\n" +
                          "РќР°РїРёС€Рё РІСЂРµРјСЏ РІ С„РѕСЂРјР°С‚Рµ <b>Р§Р§:РњРњ</b>, РЅР°РїСЂРёРјРµСЂ <b>20:00</b>.\n" +
                          "Р’СЂРµРјСЏ РїРѕ РњРЎРљ.",
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
                        ? "РҐРѕСЂРѕС€Рѕ, РЅР°РїРѕРјРёРЅР°РЅРёСЏ РґР»СЏ СЌС‚РѕР№ РіСЂСѓРїРїС‹ РјРѕР¶РЅРѕ РІРєР»СЋС‡РёС‚СЊ РїРѕР·Р¶Рµ С‡РµСЂРµР· /reminders."
                        : "РҐРѕСЂРѕС€Рѕ, РЅРµ Р±СѓРґСѓ РЅР°РїРѕРјРёРЅР°С‚СЊ. РќР°СЃС‚СЂРѕРёС‚СЊ РјРѕР¶РЅРѕ РІ Р»СЋР±РѕР№ РјРѕРјРµРЅС‚ С‡РµСЂРµР· /reminders.\n\n" +
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
                        ? "вЏ° Р“СЂСѓРїРїРѕРІС‹Рµ РЅР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹. Р’РєР»СЋС‡РёС‚СЊ СЃРЅРѕРІР° РјРѕР¶РЅРѕ С‡РµСЂРµР· /reminders."
                        : "вЏ° РќР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹. Р’РєР»СЋС‡РёС‚СЊ СЃРЅРѕРІР° РјРѕР¶РЅРѕ С‡РµСЂРµР· /reminders.",
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
                        text: "вЏ° <b>РќР°СЃС‚СЂРѕРёРј РЅР°РїРѕРјРёРЅР°РЅРёСЏ РґР»СЏ РіСЂСѓРїРїС‹</b>\n\n" +
                              "РљР°Рє С‡Р°СЃС‚Рѕ РёС… РїСЂРёСЃС‹Р»Р°С‚СЊ?",
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
                    text: "вЏ° Р’Рѕ СЃРєРѕР»СЊРєРѕ РЅР°РїРѕРјРёРЅР°С‚СЊ Рѕ РґРµРґР»Р°Р№РЅР°С… РЅР° Р·Р°РІС‚СЂР°?\n\n" +
                          "РќР°РїРёС€Рё РІСЂРµРјСЏ РІ С„РѕСЂРјР°С‚Рµ <b>Р§Р§:РњРњ</b>, РЅР°РїСЂРёРјРµСЂ <b>20:00</b>.\n" +
                          "Р’СЂРµРјСЏ РїРѕ РњРЎРљ.",
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
                    text: $"вЏ° <b>Р’Рѕ СЃРєРѕР»СЊРєРѕ РїСЂРёСЃС‹Р»Р°С‚СЊ РЅР°РїРѕРјРёРЅР°РЅРёСЏ {FormatGroupFrequencyText(session.PendingGroupReminderFrequency.Value)}?</b>\n\n" +
                          "РќР°РїРёС€Рё РІСЂРµРјСЏ РІ С„РѕСЂРјР°С‚Рµ <b>Р§Р§:РњРњ</b>, РЅР°РїСЂРёРјРµСЂ <b>20:00</b>.\n" +
                          "РЇ РїСЂРёС€Р»СЋ СЃРѕРѕР±С‰РµРЅРёРµ РІ СЌС‚РѕС‚ С‡Р°С‚ Рё РѕС‚РјРµС‡Сѓ СѓС‡Р°СЃС‚РЅРёРєРѕРІ, РєРѕС‚РѕСЂС‹С… СѓР¶Рµ РІРёРґРµР» РІ РіСЂСѓРїРїРµ.",
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
                        ? "РҐРѕСЂРѕС€Рѕ. РќР°СЃС‚СЂРѕРёС‚СЊ РЅР°РїРѕРјРёРЅР°РЅРёСЏ РґР»СЏ СЌС‚РѕР№ РіСЂСѓРїРїС‹ РјРѕР¶РЅРѕ РїРѕР·Р¶Рµ С‡РµСЂРµР· /reminders."
                        : "РҐРѕСЂРѕС€Рѕ, РЅРµ Р±СѓРґСѓ РЅР°РїРѕРјРёРЅР°С‚СЊ. РќР°СЃС‚СЂРѕРёС‚СЊ РјРѕР¶РЅРѕ РІ Р»СЋР±РѕР№ РјРѕРјРµРЅС‚ С‡РµСЂРµР· /reminders.\n\n" +
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
                        ? "вЏ° РќР°РїРѕРјРёРЅР°РЅРёСЏ РґР»СЏ СЌС‚РѕР№ РіСЂСѓРїРїС‹ РІС‹РєР»СЋС‡РµРЅС‹. Р’РєР»СЋС‡РёС‚СЊ СЃРЅРѕРІР° РјРѕР¶РЅРѕ С‡РµСЂРµР· /reminders."
                        : "вЏ° РќР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹. Р’РєР»СЋС‡РёС‚СЊ СЃРЅРѕРІР° РјРѕР¶РЅРѕ С‡РµСЂРµР· /reminders.",
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
                InlineKeyboardButton.WithCallbackData("РљР°Р¶РґС‹Р№ РґРµРЅСЊ", "rem_freq_daily"),
                InlineKeyboardButton.WithCallbackData("РџРѕ Р±СѓРґРЅСЏРј", "rem_freq_weekdays")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("РќРµ СЃРµР№С‡Р°СЃ", "rem_later")
            }
        });
    }

    private static string FormatGroupFrequencyText(Models.GroupReminderFrequency frequency)
        => frequency == Models.GroupReminderFrequency.Weekdays ? "РїРѕ Р±СѓРґРЅСЏРј" : "РєР°Р¶РґС‹Р№ РґРµРЅСЊ";

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

            await _bot.SendMessage(
                chatId,
                "Р’С‹Р±РѕСЂ РїСЂРµРґРјРµС‚Р° СѓСЃС‚Р°СЂРµР». РћС‚РєСЂРѕР№ СЃРїРёСЃРѕРє Р·Р°РЅРѕРІРѕ С‡РµСЂРµР· /add_homework.",
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
                "РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СЃРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ С‡РµСЂРµР· /schedule, РїРѕС‚РѕРј СЏ СЃРјРѕРіСѓ РґРѕР±Р°РІРёС‚СЊ Р”Р—.",
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
                "РќРµ РЅР°С€С‘Р» С‚РёРїС‹ Р·Р°РЅСЏС‚РёР№ РґР»СЏ СЌС‚РѕРіРѕ РїСЂРµРґРјРµС‚Р°. РџРѕРїСЂРѕР±СѓР№ РѕС‚РєСЂС‹С‚СЊ СЃРїРёСЃРѕРє Р·Р°РЅРѕРІРѕ С‡РµСЂРµР· /add_homework.",
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
            .Append(("рџ”ґ РћС‚РјРµРЅР°", "hw_cancel"));

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: message.MessageId,
            text: $"рџ“љ <b>{Escape(subjectTitle)}</b>\nР’С‹Р±РµСЂРё С‚РёРї Р·Р°РЅСЏС‚РёСЏ:",
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
                "Р’С‹Р±РѕСЂ С‚РёРїР° Р·Р°РЅСЏС‚РёСЏ СѓСЃС‚Р°СЂРµР». РћС‚РєСЂРѕР№ СЃРїРёСЃРѕРє Р·Р°РЅРѕРІРѕ С‡РµСЂРµР· /add_homework.",
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
                "РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СЃРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ С‡РµСЂРµР· /schedule, РїРѕС‚РѕРј СЏ СЃРјРѕРіСѓ РґРѕР±Р°РІРёС‚СЊ Р”Р—.",
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
                "РќРµ СЃРјРѕРі РЅР°Р№С‚Рё СЃР»РµРґСѓСЋС‰СѓСЋ РїР°СЂСѓ РїРѕ СЌС‚РѕРјСѓ РїСЂРµРґРјРµС‚Сѓ. РџСЂРѕРІРµСЂСЊ СЂР°СЃРїРёСЃР°РЅРёРµ С‡РµСЂРµР· /schedule.",
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
            text: $"рџ“љ <b>{Escape(subject)}</b>\n" +
                  $"рџ“… Р”РµРґР»Р°Р№РЅ: <b>{deadline.Value:dd.MM.yyyy}</b>\n\n" +
                  (isGroup ? "РќР°РїРёС€Рё РѕР±С‰РµРµ Р”Р— РґР»СЏ РіСЂСѓРїРїС‹:" : "РќР°РїРёС€Рё, С‡С‚Рѕ Р·Р°РґР°Р»Рё:"),
            parseMode: ParseMode.Html,
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
            await _bot.SendMessage(chatId, "вљ пёЏ Р—Р°РґР°С‡Р° РЅРµ РЅР°Р№РґРµРЅР°.", cancellationToken: ct);
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

    private async Task HandleGroupTaskAsync(Message message, string data, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var chatTitle = message.Chat.Title ?? "Group";

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
        var view = HomeworkListView.BuildGroup(message.Chat.Title ?? "Group", _groupTasks.Get(message.Chat.Id));
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
                    text: isGroup ? "РЈРґР°Р»РёС‚СЊ СЃРѕС…СЂР°РЅС‘РЅРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹?" : "РЈРґР°Р»РёС‚СЊ СЃРѕС…СЂР°РЅС‘РЅРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ?",
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
                        ? "Р Р°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹ СѓРґР°Р»РµРЅРѕ. Р§С‚РѕР±С‹ РІС‹Р±СЂР°С‚СЊ РЅРѕРІРѕРµ, РёСЃРїРѕР»СЊР·СѓР№ /schedule."
                        : "Р Р°СЃРїРёСЃР°РЅРёРµ СѓРґР°Р»РµРЅРѕ. Р§С‚РѕР±С‹ РІС‹Р±СЂР°С‚СЊ РЅРѕРІРѕРµ, РёСЃРїРѕР»СЊР·СѓР№ /schedule.",
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
                    "Р РµРґР°РєС‚РёСЂРѕРІР°РЅРёРµ С„РѕС‚Рѕ-СЂР°СЃРїРёСЃР°РЅРёСЏ Р±РѕР»СЊС€Рµ РЅРµ РёСЃРїРѕР»СЊР·СѓРµС‚СЃСЏ. Р’С‹Р±РµСЂРё РіСЂСѓРїРїСѓ С‡РµСЂРµР· /schedule.",
                    cancellationToken: ct);
                break;
        }
    }

    private async Task SendDirectionChoiceAsync(long chatId, CancellationToken ct)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} вЂ” {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: "РЁР°Рі 1/3. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ:",
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
            await _bot.SendMessage(chatId, "РќРµ РЅР°С€С‘Р» РєСѓСЂСЃС‹ РґР»СЏ СЌС‚РѕРіРѕ РЅР°РїСЂР°РІР»РµРЅРёСЏ.", cancellationToken: ct);
            return;
        }

        var directionName = groups[0].DirectionName;
        var buttons = groups.Select(g => ($"{g.Course} РєСѓСЂСЃ", $"sched_course_{g.DirectionCode}_{g.Course}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: $"РЁР°Рі 2/3. РќР°РїСЂР°РІР»РµРЅРёРµ: <b>{Escape(directionName)}</b>\nР’С‹Р±РµСЂРё РєСѓСЂСЃ:",
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
            await _bot.SendMessage(chatId, "РќРµ РЅР°С€С‘Р» СЂР°СЃРїРёСЃР°РЅРёРµ РґР»СЏ СЌС‚РѕРіРѕ РєСѓСЂСЃР°.", cancellationToken: ct);
            return;
        }

        if (group.SubGroups.Count == 0)
        {
            await SaveScheduleSelectionAsync(chatId, userId, session, group.Id, null, ct);
            return;
        }

        var buttons = group.SubGroups
            .OrderBy(x => x)
            .Select(x => ($"РџРѕРґРіСЂСѓРїРїР° {x}", $"sched_pick_{group.Id}_{x}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: $"РЁР°Рі 3/3. РљСѓСЂСЃ: <b>{Escape(group.Title)}</b>\nР’С‹Р±РµСЂРё РїРѕРґРіСЂСѓРїРїСѓ:",
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
            await _bot.SendMessage(chatId, "РќРµ РЅР°С€С‘Р» РІС‹Р±СЂР°РЅРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ.", cancellationToken: ct);
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
            text: $"{(userId == chatId ? "вњ… <b>Р“РѕС‚РѕРІРѕ! Р Р°СЃРїРёСЃР°РЅРёРµ Р·Р°РєСЂРµРїР»РµРЅРѕ Р·Р° С‚РѕР±РѕР№.</b>" : "вњ… <b>Р“РѕС‚РѕРІРѕ! Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРµРЅРѕ РґР»СЏ СЌС‚РѕР№ РіСЂСѓРїРїС‹.</b>")}\n\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n\n" +
                  "РўРµРїРµСЂСЊ С‚С‹ РјРѕР¶РµС€СЊ:\n" +
                  "вЂў СЃРјРѕС‚СЂРµС‚СЊ РїР°СЂС‹ РЅР° СЃРµРіРѕРґРЅСЏ Рё РЅРµРґРµР»СЋ С‡РµСЂРµР· /schedule\n" +
                  "вЂў РґРѕР±Р°РІР»СЏС‚СЊ РґРѕРјР°С€РєСѓ С‡РµСЂРµР· /add_homework\n" +
                  "вЂў СЃРјРѕС‚СЂРµС‚СЊ СЃРїРёСЃРѕРє Р”Р— С‡РµСЂРµР· /homework\n\n" +
                  "РЎРѕРІРµС‚СѓСЋ РЅР°С‡Р°С‚СЊ СЃ /add_homework: РІС‹Р±РµСЂРё РїСЂРµРґРјРµС‚, РЅР°РїРёС€Рё Р·Р°РґР°РЅРёРµ, Р° РґРµРґР»Р°Р№РЅ СЏ РїРѕСЃС‚Р°РІР»СЋ РїРѕ СЃР»РµРґСѓСЋС‰РµР№ РїР°СЂРµ.",
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
            text: $"{(chatId < 0 ? "рџ“… <b>Р Р°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹</b>" : "рџ“… <b>РўРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ</b>")}\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n" +
                  $"РўРµРєСѓС‰Р°СЏ РЅРµРґРµР»СЏ: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\n" +
                  "Р§С‚Рѕ РїРѕРєР°Р·Р°С‚СЊ?",
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
        var title = "Р Р°СЃРїРёСЃР°РЅРёРµ РЅР° РЅРµРґРµР»СЋ";
        if (onlyToday)
        {
            var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
            entries = entries.Where(e => e.DayOfWeek == today).ToList();
            title = $"Р Р°СЃРїРёСЃР°РЅРёРµ РЅР° СЃРµРіРѕРґРЅСЏ, {ScheduleService.GetDayName(today).ToLowerInvariant()}";
        }

        var summary = entries.Count == 0
            ? "РџР°СЂ РЅРµС‚."
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
            .Select(d => ($"{d.ShortTitle} вЂ” {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        var text = string.IsNullOrWhiteSpace(prefix)
            ? "РЁР°Рі 1/3. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ:"
            : $"{prefix}\n\nРЁР°Рі 1/3. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ:";

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
            .Select(d => ($"{d.ShortTitle} вЂ” {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        return _bot.SendMessage(
            chatId: chatId,
            text: $"{text}\n\nРЁР°Рі 1/3. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ:",
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
            await SendDirectionChoiceAsync(chatId, messageId, ct, "РќРµ РЅР°С€РµР» РєСѓСЂСЃС‹ РґР»СЏ СЌС‚РѕРіРѕ РЅР°РїСЂР°РІР»РµРЅРёСЏ.");
            return;
        }

        var directionName = groups[0].DirectionName;
        var buttons = groups.Select(g => ($"{g.Course} РєСѓСЂСЃ", $"sched_course_{g.DirectionCode}_{g.Course}"));

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: $"РЁР°Рі 2/3. РќР°РїСЂР°РІР»РµРЅРёРµ: <b>{Escape(directionName)}</b>\nР’С‹Р±РµСЂРё РєСѓСЂСЃ:",
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
            await SendDirectionChoiceAsync(chatId, messageId, ct, "РќРµ РЅР°С€РµР» СЂР°СЃРїРёСЃР°РЅРёРµ РґР»СЏ СЌС‚РѕРіРѕ РєСѓСЂСЃР°.");
            return;
        }

        if (group.SubGroups.Count == 0)
        {
            await SaveScheduleSelectionAsync(chatId, messageId, selectionKey, session, group.Id, null, ct);
            return;
        }

        var buttons = group.SubGroups
            .OrderBy(x => x)
            .Select(x => ($"РџРѕРґРіСЂСѓРїРїР° {x}", $"sched_pick_{group.Id}_{x}"));

        await EditScheduleMessageAsync(
            chatId: chatId,
            messageId: messageId,
            text: $"РЁР°Рі 3/3. РљСѓСЂСЃ: <b>{Escape(group.Title)}</b>\nР’С‹Р±РµСЂРё РїРѕРґРіСЂСѓРїРїСѓ:",
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
            await SendDirectionChoiceAsync(chatId, messageId, ct, "РќРµ РЅР°С€РµР» РІС‹Р±СЂР°РЅРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ.");
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
            selectionKey == chatId ? "вњ… Р“РѕС‚РѕРІРѕ! Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРµРЅРѕ РґР»СЏ СЌС‚РѕР№ РіСЂСѓРїРїС‹." : "вњ… Р“РѕС‚РѕРІРѕ! Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРµРЅРѕ.");
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
            await _bot.SendMessage(
                chatId,
                "Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРёР»РѕСЃСЊ, РЅРѕ СЏ РЅРµ СЃРјРѕРі СЃСЂР°Р·Сѓ РѕС‚РєСЂС‹С‚СЊ РІС‹Р±РѕСЂ РїСЂРµРґРјРµС‚Р°. РџРѕРїСЂРѕР±СѓР№ РµС‰С‘ СЂР°Р· С‡РµСЂРµР· /add_homework.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        if (subjects.Count == 0)
        {
            await _bot.SendMessage(
                chatId,
                "Р Р°СЃРїРёСЃР°РЅРёРµ РїРѕРґРєР»СЋС‡РµРЅРѕ, РЅРѕ РІ РЅС‘Рј РїРѕРєР° РЅРµ РЅР°С€Р»РѕСЃСЊ РїСЂРµРґРјРµС‚РѕРІ РґР»СЏ Р”Р—. РњРѕР¶РµС€СЊ РёР·РјРµРЅРёС‚СЊ СЂР°СЃРїРёСЃР°РЅРёРµ С‡РµСЂРµР· /schedule.",
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
            await EditScheduleMessageAsync(
                chatId: chatId,
                messageId: messageId,
                text: "Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРёР»РѕСЃСЊ, РЅРѕ СЏ РЅРµ СЃРјРѕРі СЃСЂР°Р·Сѓ РѕС‚РєСЂС‹С‚СЊ РІС‹Р±РѕСЂ РїСЂРµРґРјРµС‚Р°. РџРѕРїСЂРѕР±СѓР№ РµС‰С‘ СЂР°Р· С‡РµСЂРµР· /add_homework.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);
        if (subjects.Count == 0)
        {
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
            ? $"{(selectionKey == chatId ? "рџ“… <b>Р Р°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹</b>" : "рџ“… <b>РўРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ</b>")}\n{Escape(FormatGroupTitle(group, subGroup))}\nРўРµРєСѓС‰Р°СЏ РЅРµРґРµР»СЏ: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\nР§С‚Рѕ РїРѕРєР°Р·Р°С‚СЊ?"
            : $"{prefix}\n\n{(selectionKey == chatId ? "рџ“… <b>Р Р°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹</b>" : "рџ“… <b>РўРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ</b>")}\n{Escape(FormatGroupTitle(group, subGroup))}\nРўРµРєСѓС‰Р°СЏ РЅРµРґРµР»СЏ: <b>{_scheduleCatalog.GetCurrentWeekLabel()}</b>\n\nР§С‚Рѕ РїРѕРєР°Р·Р°С‚СЊ?";

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
        var title = "Р Р°СЃРїРёСЃР°РЅРёРµ РЅР° РЅРµРґРµР»СЋ";
        if (onlyToday)
        {
            var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
            entries = entries.Where(e => e.DayOfWeek == today).ToList();
            title = $"Р Р°СЃРїРёСЃР°РЅРёРµ РЅР° СЃРµРіРѕРґРЅСЏ, {ScheduleService.GetDayName(today).ToLowerInvariant()}";
        }

        var summary = entries.Count == 0
            ? "РџР°СЂ РЅРµС‚."
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
        => subGroup.HasValue ? $"{group.Title}, РїРѕРґРіСЂСѓРїРїР° {subGroup.Value}" : group.Title;

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static string BuildBasicCommandsText()
        => "Р‘Р°Р·РѕРІР°СЏ РЅР°СЃС‚СЂРѕР№РєР° РіРѕС‚РѕРІР°.\n\n" +
           "РћСЃРЅРѕРІРЅС‹Рµ РєРѕРјР°РЅРґС‹:\n" +
           "/schedule вЂ” СЂР°СЃРїРёСЃР°РЅРёРµ\n" +
           "/add_homework вЂ” РґРѕР±Р°РІРёС‚СЊ Р”Р—\n" +
           "/homework вЂ” СЃРїРёСЃРѕРє Р·Р°РґР°РЅРёР№\n" +
           "/timer вЂ” С‚Р°Р№РјРµСЂ РґР»СЏ СѓС‡С‘Р±С‹\n" +
           "/help вЂ” РІСЃРµ РєРѕРјР°РЅРґС‹";

    private async Task StartScheduleReviewAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "в„№пёЏ РќРµС‚ РѕР¶РёРґР°СЋС‰РµРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ РґР»СЏ РїСЂРѕРІРµСЂРєРё.", cancellationToken: ct);
            return;
        }

        session.ReviewSlotIndex = 0;
        session.State = UserState.WaitingForScheduleReview;

        await _bot.SendMessage(
            chatId,
            "рџ”Ћ <b>РЎС‚СЂРѕРіР°СЏ РїСЂРѕРІРµСЂРєР° РїРѕ РїР°СЂР°Рј</b>\nРЇ РїРѕРєР°Р¶Сѓ РІСЃРµ 24 СЃР»РѕС‚Р° РїРѕ РѕС‡РµСЂРµРґРё. РўР°Рє РјС‹ РјРѕР¶РµРј РґРѕРІРµСЃС‚Рё СЂР°СЃРїРёСЃР°РЅРёРµ РґРѕ 0 РѕС€РёР±РѕРє РїРµСЂРµРґ СЃРѕС…СЂР°РЅРµРЅРёРµРј.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        await SendCurrentReviewSlotAsync(chatId, session, ct);
    }

    private async Task HandleReviewActionAsync(long chatId, UserSession session, string data, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "в„№пёЏ РќРµС‚ РѕР¶РёРґР°СЋС‰РµРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ РґР»СЏ РїСЂРѕРІРµСЂРєРё.", cancellationToken: ct);
            return;
        }

        if (session.State is not UserState.WaitingForScheduleReview and not UserState.WaitingForReviewSlotCorrection)
        {
            await _bot.SendMessage(chatId, "в„№пёЏ РџРѕС€Р°РіРѕРІР°СЏ РїСЂРѕРІРµСЂРєР° СЃРµР№С‡Р°СЃ РЅРµ Р·Р°РїСѓС‰РµРЅР°.", cancellationToken: ct);
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
                    $"вњЏпёЏ <b>{GetDayName(day)}, {lesson} РїР°СЂР°</b>\n" +
                    "РќР°РїРёС€Рё С‚РѕС‡РЅРѕ С‚Р°Рє:\n\n" +
                    "<i>РїРµСЂРІР°СЏ РЅРµРґРµР»СЏ: ...\nРІС‚РѕСЂР°СЏ РЅРµРґРµР»СЏ: ...</i>\n\n" +
                    "РёР»Рё:\n" +
                    "<i>РѕР±Рµ РЅРµРґРµР»Рё: ...</i>\n\n" +
                    "РёР»Рё РїСЂРѕСЃС‚Рѕ:\n" +
                    "<i>РїР°СЂС‹ РЅРµС‚</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    private async Task ConfirmScheduleAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null || session.PendingSchedule.Count == 0)
        {
            await _bot.SendMessage(chatId, "в„№пёЏ РќРµС‚ РѕР¶РёРґР°СЋС‰РµРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ.", cancellationToken: ct);
            return;
        }

        var hasWeekSplit = session.PendingSchedule.Any(e => e.WeekType.HasValue);
        if (hasWeekSplit)
        {
            session.State = UserState.WaitingForWeekChoice;
            var splitCount = session.PendingSchedule.Count(e => e.WeekType.HasValue);

            await _bot.SendMessage(
                chatId: chatId,
                text: $"вќ“ <b>РљР°РєР°СЏ СЃРµР№С‡Р°СЃ РЅРµРґРµР»СЏ?</b>\n" +
                      $"РћР±РЅР°СЂСѓР¶РµРЅРѕ <b>{splitCount}</b> РїР°СЂ СЃ СЂР°Р·Р±РёРІРєРѕР№ РїРѕ РЅРµРґРµР»СЏРј.\n" +
                      "Р­С‚Рѕ РЅСѓР¶РЅРѕ РґР»СЏ РєРѕСЂСЂРµРєС‚РЅРѕРіРѕ СЃРѕС…СЂР°РЅРµРЅРёСЏ СЂР°СЃРїРёСЃР°РЅРёСЏ.",
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
            await _bot.SendMessage(chatId, "в„№пёЏ РќРµС‚ РѕР¶РёРґР°СЋС‰РµРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ.", cancellationToken: ct);
            return;
        }

        if (!int.TryParse(data.Split('_')[1], out var weekType) || weekType is not (1 or 2))
        {
            await _bot.SendMessage(chatId, "вљ пёЏ РќРµРёР·РІРµСЃС‚РЅС‹Р№ С‚РёРї РЅРµРґРµР»Рё.", cancellationToken: ct);
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
        var text = $"вњ… <b>Р Р°СЃРїРёСЃР°РЅРёРµ СЃРѕС…СЂР°РЅРµРЅРѕ!</b>\n" +
                   $"РўРІРѕСЏ РїРѕРґРіСЂСѓРїРїР°: <b>{session.CurrentSubGroup}</b>\n";

        if (includeWeek)
        {
            var weekLabel = session.CurrentWeekType == 1 ? "РЅРµС‡С‘С‚РЅР°СЏ (1-СЏ)" : "С‡С‘С‚РЅР°СЏ (2-СЏ)";
            text += $"РўРµРєСѓС‰Р°СЏ РЅРµРґРµР»СЏ: <b>{weekLabel}</b>\n";
        }

        text += $"Р’СЃРµРіРѕ РїР°СЂ: <b>{session.Schedule.Count}</b>\n\n{summary}";

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
            await _bot.SendMessage(chatId, "в„№пёЏ РќРµС‚ РѕР¶РёРґР°СЋС‰РµРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ РґР»СЏ РїСЂРѕРІРµСЂРєРё.", cancellationToken: ct);
            return;
        }

        if (session.ReviewSlotIndex >= 24)
        {
            session.State = UserState.WaitingForScheduleConfirmation;
            await _bot.SendMessage(
                chatId,
                "вњ… РџРѕС€Р°РіРѕРІР°СЏ РїСЂРѕРІРµСЂРєР° Р·Р°РІРµСЂС€РµРЅР°. РќРёР¶Рµ РёС‚РѕРіРѕРІРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ, С‚РµРїРµСЂСЊ РјРѕР¶РЅРѕ СЃРѕС…СЂР°РЅСЏС‚СЊ.",
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
            $"рџ”Ћ <b>РџСЂРѕРІРµСЂРєР° {session.ReviewSlotIndex + 1}/24</b>\n" +
            $"<b>{GetDayName(day)}, {lesson} РїР°СЂР°</b>\n\n" +
            $"РџРµСЂРІР°СЏ РЅРµРґРµР»СЏ: {FormatReviewWeek(firstWeek)}\n" +
            $"Р’С‚РѕСЂР°СЏ РЅРµРґРµР»СЏ: {FormatReviewWeek(secondWeek)}\n\n" +
            "Р­С‚Рѕ РІРµСЂРЅРѕ?";

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
        1 => "РџРѕРЅРµРґРµР»СЊРЅРёРє",
        2 => "Р’С‚РѕСЂРЅРёРє",
        3 => "РЎСЂРµРґР°",
        4 => "Р§РµС‚РІРµСЂРі",
        5 => "РџСЏС‚РЅРёС†Р°",
        6 => "РЎСѓР±Р±РѕС‚Р°",
        _ => $"Р”РµРЅСЊ {day}"
    };

    private static string FormatReviewWeek(List<ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return "РїР°СЂС‹ РЅРµС‚";

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






