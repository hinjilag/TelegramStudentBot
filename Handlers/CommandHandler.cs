using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.MiniApp;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;
using System.Net;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик команд (/start, /help, /timer, /rest, /plan, /stop, /schedule).
/// Каждый метод соответствует одной команде.
/// </summary>
public class CommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ReminderSettingsService _reminders;
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;
    private readonly UserFeatureIntroService _featureIntros;
    private readonly BotVisitLogService _visits;
    private readonly BotIdentityService _botIdentity;
    private readonly GroupMiniAppAccessService _groupMiniAppAccess;
    private readonly string? _webAppUrl;
    private readonly string? _groupMiniAppShortName;

    public CommandHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ReminderSettingsService reminders,
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        HomeworkSubjectPreferencesService homeworkSubjects,
        UserFeatureIntroService featureIntros,
        BotVisitLogService visits,
        BotIdentityService botIdentity,
        GroupMiniAppAccessService groupMiniAppAccess,
        IConfiguration configuration)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _reminders = reminders;
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _homeworkSubjects = homeworkSubjects;
        _featureIntros = featureIntros;
        _visits = visits;
        _botIdentity = botIdentity;
        _groupMiniAppAccess = groupMiniAppAccess;
        _webAppUrl = ResolveWebAppUrl(configuration);
        _groupMiniAppShortName = configuration["GroupMiniAppShortName"];
    }

    // ══════════════════════════════════════════════════════════
    //  /start
    // ══════════════════════════════════════════════════════════

    /// <summary>Приветствие при первом запуске или перезапуске</summary>
    public async Task HandleStartAsync(Message msg, CancellationToken ct)
    {
        _visits.RecordVisit(msg.From!);

        if (IsGroupChat(msg.Chat.Type))
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "👋 <b>Привет! В группе я помогаю вести общее расписание и домашние задания.</b>\n\n" +
                      "Доступные команды:\n" +
                      "📅 /schedule — выбрать расписание для этой группы\n" +
                      "➕ /add_homework — добавить общее ДЗ\n" +
                      "📝 /homework — открыть общий список ДЗ\n" +
                      "📱 /miniapp — открыть mini app группы\n" +
                      "⏰ /reminders — настроить напоминания в этот чат\n" +
                      "❓ /help — показать команды\n\n" +
                      "Таймеры и личный планер работают только в личке.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;

        var selection = _scheduleSelections.Get(userId);
        if (selection is not null)
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (group is not null)
            {
                ApplySelectionToSession(session, group, selection.SubGroup);

                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "👋 <b>С возвращением!</b>\n\n" +
                          "Я уже помню твоё расписание:\n" +
                          $"<b>{Escape(FormatGroupTitle(group, selection.SubGroup))}</b>.\n\n" +
                          "Можешь сразу перейти к нужному:\n" +
                          "📅 /schedule — пары на день\n" +
                          "📝 /homework — домашние задания\n" +
                          "➕ /add_homework — добавить новое ДЗ\n" +
                          "📋 /plan — личные дела с дедлайнами\n" +
                          "⏱ /timer — сфокусироваться на учёбе",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildMiniAppLinkMarkup(),
                    cancellationToken: ct);
                return;
            }

            _scheduleSelections.Delete(userId);
        }

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "👋 <b>Привет! Я помогу тебе следить за расписанием, домашками и личными делами.</b>\n\n" +
                       "Давай сначала настроим расписание:\n" +
                       "1. Нажми /schedule\n" +
                       "2. Выбери направление, курс и подгруппу\n" +
                       "3. После этого я закреплю расписание за тобой\n\n" +
                       "Когда расписание будет выбрано:\n" +
                       "📚 /add_homework — ДЗ по предметам\n" +
                       "📋 /plan — личные дела с датой и временем\n" +
                       "⏱ /timer — таймер учёбы",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /help
    // ══════════════════════════════════════════════════════════

    /// <summary>Справка по всем командам</summary>
    public async Task HandleHelpAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "📖 <b>Что я умею в группе:</b>\n\n" +
                      "/schedule — выбрать или поменять расписание этой группы\n" +
                      "/add_homework — добавить общее ДЗ по предмету из расписания\n" +
                      "/homework — посмотреть общее ДЗ\n" +
                      "/miniapp — открыть mini app группы\n" +
                      "/reminders — настроить напоминания в этот чат\n" +
                      "/help — эта справка\n\n" +
                      "Сценарий простой: сначала /schedule, потом /add_homework, дальше /homework, /miniapp и /reminders.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "📖 <b>Список команд:</b>\n\n" +
                       "⏱ <b>Таймер учёбы:</b>\n" +
                       "/timer — запустить таймер (25/30/45/60 мин или своё)\n" +
                       "/stop — остановить текущий таймер\n\n" +
                       "☕ <b>Отдых:</b>\n" +
                       "/rest — запустить таймер отдыха\n\n" +
                       "📚 <b>Домашние задания:</b>\n" +
                       "/add_homework — в личке добавить ДЗ по расписанию, в группе — общее ДЗ\n" +
                       "/homework — посмотреть свои или общие ДЗ\n" +
                       "/reminders — настроить личные или групповые напоминания\n" +
                       "В группе ДЗ добавляется через выбор предмета из /add_homework.\n\n" +
                       "📋 <b>Планирование:</b>\n" +
                       "/plan — управление задачами\n\n" +
                       "📅 <b>Расписание:</b>\n" +
                       "/schedule — моё расписание занятий\n\n" +
                       "❓ /help — эта справка",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /timer
    // ══════════════════════════════════════════════════════════

    /// <summary>Показать меню выбора длительности рабочего таймера</summary>
    public async Task HandleMiniAppAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            var groupMarkup = BuildGroupMiniAppLinkMarkup(msg.Chat.Id);
            if (groupMarkup is null)
            {
                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "Групповой mini app пока не настроен до конца. Нужен short name mini app в конфиге бота, чтобы открыть его через Telegram direct link.",
                    cancellationToken: ct);
                return;
            }

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Открой mini app группы по кнопке ниже.",
                replyMarkup: groupMarkup,
                cancellationToken: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(_webAppUrl))
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Mini app пока не настроен. Укажи публичный WebAppUrl в конфигурации бота.",
                cancellationToken: ct);
            return;
        }

        var launchMessage = await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "Открой mini app по кнопке ниже.",
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);

        await TryPinMiniAppMessageAsync(msg.Chat.Id, launchMessage, ct);
    }

    public async Task HandleTimerAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            await SendGroupModeUnavailableAsync(msg.Chat.Id, "Таймер", ct);
            return;
        }

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        // Если уже идёт таймер — сообщаем пользователю
        string prefix = string.Empty;
        if (session.ActiveTimer is not null)
        {
            var remaining = session.ActiveTimer.Remaining;
            var typeLabel = session.ActiveTimer.Type == TimerType.Work ? "рабочий" : "отдых";
            prefix = $"⚠️ Уже идёт таймер <b>({typeLabel})</b>, осталось: " +
                     $"<b>{(int)remaining.TotalMinutes} мин {remaining.Seconds} сек</b>\n" +
                     $"Выбери новый, чтобы заменить текущий:\n\n";
        }

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        prefix + "⏱ <b>Выбери длительность рабочего таймера:</b>",
            parseMode:   ParseMode.Html,
            replyMarkup: BuildTimerKeyboard(),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /rest
    // ══════════════════════════════════════════════════════════

    /// <summary>Показать меню выбора длительности отдыха</summary>
    public async Task HandleRestAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            await SendGroupModeUnavailableAsync(msg.Chat.Id, "Таймер отдыха", ct);
            return;
        }

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        "☕ <b>Выбери длительность перерыва:</b>",
            parseMode:   ParseMode.Html,
            replyMarkup: BuildRestKeyboard(),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /stop
    // ══════════════════════════════════════════════════════════

    /// <summary>Досрочно остановить активный таймер</summary>
    public async Task HandleStopAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            await SendGroupModeUnavailableAsync(msg.Chat.Id, "Остановка таймера", ct);
            return;
        }

        var stopped = _timers.StopTimer(msg.From!.Id);

        var text = stopped
            ? "⏹ Таймер <b>остановлен</b>. Когда будешь готов — запускай снова!"
            : "ℹ️ Нет активного таймера.";

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /plan
    // ══════════════════════════════════════════════════════════

    /// <summary>Меню управления личными делами.</summary>
    public async Task HandlePlanAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            await SendGroupModeUnavailableAsync(msg.Chat.Id, "Планирование", ct);
            return;
        }

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var userId = msg.From!.Id;
        var pending = session.Tasks.Count(t => !t.IsCompleted && TaskSubjects.IsPersonal(t.Subject));
        var shouldShowIntro = !_featureIntros.HasSeenPlanIntro(userId);

        var text = BuildPlanMenuText(pending, shouldShowIntro);
        if (shouldShowIntro)
            _featureIntros.MarkPlanIntroSeen(userId);

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        text,
            parseMode:   ParseMode.Html,
            replyMarkup: BuildPlanKeyboard(),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /add_homework
    // ══════════════════════════════════════════════════════════

    public async Task HandleAddHomeworkAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            var groupSession = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
            groupSession.State = UserState.Idle;
            groupSession.ContinueHomeworkAfterScheduleSelection = false;
            groupSession.PendingHomeworkScheduleSelectionKey = null;
            groupSession.PendingGroupHomeworkChatId = null;
            groupSession.PendingGroupHomeworkChatTitle = null;
            groupSession.DraftTask = null;
            groupSession.HomeworkSubjectChoices.Clear();
            groupSession.HomeworkLessonTypeChoices.Clear();

            if (!TryGetAllScheduleEntries(msg.Chat.Id, out _, out _, out var groupEntries))
            {
                groupSession.ContinueHomeworkAfterScheduleSelection = true;
                groupSession.PendingHomeworkScheduleSelectionKey = msg.Chat.Id;
                await SendDirectionChoiceAsync(
                    msg.Chat.Id,
                    ct,
                    isGroup: true,
                    prefix: "Сначала подключим расписание этой группы, а потом я сразу предложу предмет для общего ДЗ.");
                return;
            }

            var groupSubjects = GetHomeworkSubjects(groupEntries);
            if (groupSubjects.Count == 0)
            {
                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "В расписании этой группы пока не нашлось предметов для выбора.",
                    cancellationToken: ct);
                return;
            }

            await SendGroupHomeworkSubjectChoiceAsync(msg.Chat.Id, groupSession, groupSubjects, ct);
            return;
        }

        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.ContinueHomeworkAfterScheduleSelection = false;
        session.PendingHomeworkScheduleSelectionKey = null;

        if (!TryGetAllScheduleEntries(userId, out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            session.ContinueHomeworkAfterScheduleSelection = true;
            session.PendingHomeworkScheduleSelectionKey = userId;
            await SendDirectionChoiceAsync(
                msg.Chat.Id,
                ct,
                prefix: "Сначала подключим твоё расписание, а потом я сразу предложу предмет для ДЗ.");
            return;
        }

        var subjects = GetHomeworkSubjects(entries);

        if (subjects.Count == 0)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "В твоём расписании пока нет предметов для выбора.",
                cancellationToken: ct);
            return;
        }

        await SendHomeworkSubjectChoiceAsync(msg.Chat.Id, userId, session, subjects, showAll: false, ct);
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

        await _bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private static string BuildPlanMenuText(int pending, bool includeIntro)
    {
        var text = pending > 0
            ? $"📋 <b>Личный план</b>\nАктивных дел: <b>{pending}</b>"
            : "📋 <b>Личный план</b>\nДел пока нет. Добавь первое!";

        if (includeIntro)
        {
            text += "\n\nЗдесь можно хранить дела вне учёбы: сходить в поликлинику, купить тетради, не забыть созвон.\n" +
                    "Я могу поставить дедлайн с датой и временем.";
        }

        return text + "\n\nЧто делаем?";
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

    // ══════════════════════════════════════════════════════════
    //  /homework
    // ══════════════════════════════════════════════════════════

    public async Task HandleHomeworkAsync(Message msg, CancellationToken ct)
    {
        if (IsGroupChat(msg.Chat.Type))
        {
            var tasks = _groupTasks.Get(msg.Chat.Id);
            var view = HomeworkListView.BuildGroup(msg.Chat.Title ?? "Группа", tasks);
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: view.Text,
                parseMode: ParseMode.Html,
                replyMarkup: view.Keyboard,
                cancellationToken: ct);
            return;
        }

        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        await SendHomeworkListAsync(msg.Chat.Id, session, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /reminders
    // ══════════════════════════════════════════════════════════

    public async Task HandleRemindersAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;

        session.ReminderTargetChatId = msg.Chat.Id;
        session.ReminderTargetChatTitle = msg.Chat.Title;
        session.ReminderTargetIsGroup = IsGroupChat(msg.Chat.Type);

        if (IsGroupChat(msg.Chat.Type))
        {
            var groupSettings = _groupReminders.Get(msg.Chat.Id);

            var groupText = groupSettings.IsEnabled
                ? $"⏰ <b>Групповые напоминания включены</b>\n" +
                  $"Частота: <b>{groupSettings.FrequencyText}</b>\n" +
                  $"Время: <b>{groupSettings.TimeText}</b> по МСК\n\n" +
                  "Я пришлю напоминание в этот чат и отмечу участников, которых уже видел в группе."
                : "⏰ <b>Групповые напоминания выключены</b>\n" +
                  "Давай настроим, как часто и во сколько удобно присылать напоминания в этот чат.";

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: groupText,
                parseMode: ParseMode.Html,
                replyMarkup: BuildGroupReminderKeyboard(groupSettings.IsEnabled),
                cancellationToken: ct);
            return;
        }

        var settings = _reminders.Get(userId);
        settings.ChatId = msg.Chat.Id;
        _reminders.Save(userId, settings);

        var text = settings.IsEnabled
            ? $"⏰ <b>Напоминания включены</b>\n" +
              $"Каждый день в <b>{settings.TimeText}</b> по МСК я буду присылать дедлайны на завтра."
            : "⏰ <b>Напоминания выключены</b>\n" +
              "Могу каждый день присылать дедлайны на завтра в удобное время.";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildReminderKeyboard(settings.IsEnabled),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /add_schedule  (и алиас /schedule)
    // ══════════════════════════════════════════════════════════

    /// <summary>Алиас для старой команды: теперь открывает выбор готового расписания.</summary>
    public Task HandleAddScheduleAsync(Message msg, CancellationToken ct)
        => HandleScheduleAsync(msg, ct);

    public async Task HandleScheduleAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;
        session.ContinueHomeworkAfterScheduleSelection = false;
        session.PendingHomeworkScheduleSelectionKey = null;
        var selectionKey = IsGroupChat(msg.Chat.Type) ? msg.Chat.Id : userId;
        var selection = _scheduleSelections.Get(selectionKey);
        if (selection is not null)
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (group is not null)
            {
                ApplySelectionToSession(session, group, selection.SubGroup);
                await SendSelectedScheduleMenuAsync(msg.Chat.Id, group, selection.SubGroup, ct, IsGroupChat(msg.Chat.Type));
                return;
            }

            _scheduleSelections.Delete(selectionKey);
        }

        await SendDirectionChoiceAsync(msg.Chat.Id, ct, IsGroupChat(msg.Chat.Type));
    }

    private async Task SendDirectionChoiceAsync(long chatId, CancellationToken ct, bool isGroup = false, string? prefix = null)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} — {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: string.IsNullOrWhiteSpace(prefix)
                ? (isGroup
                ? "Настроим расписание для этой группы.\n\nШаг 1/3. Выбери направление:"
                : "Шаг 1/3. Выбери направление:")
                : $"{prefix}\n\n" + (isGroup
                ? "Шаг 1/3. Выбери направление для этой группы:"
                : "Шаг 1/3. Выбери направление:"),
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

    private async Task SendHomeworkListAsync(
        long chatId,
        UserSession session,
        CancellationToken ct)
    {
        var view = HomeworkListView.Build(session);
        await _bot.SendMessage(
            chatId: chatId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        ScheduleGroup group,
        int? subGroup,
        CancellationToken ct,
        bool isGroup)
    {
        var weekLabel = _scheduleCatalog.GetCurrentWeekLabel();

        await _bot.SendMessage(
            chatId: chatId,
            text: $"{(isGroup ? "📅 <b>Расписание группы</b>" : "📅 <b>Твоё расписание</b>")}\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n" +
                  $"Текущая неделя: <b>{weekLabel}</b>\n\n" +
                  "Что показать?",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private void ApplySelectionToSession(UserSession session, ScheduleGroup group, int? subGroup)
    {
        var weekType = _scheduleCatalog.GetCurrentWeekType();

        session.CurrentWeekType = weekType;
        session.CurrentSubGroup = subGroup;
        session.Schedule = _scheduleCatalog.GetEntriesForSelection(group, subGroup, weekType);
        session.PendingSchedule = null;
    }

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static string FormatGroupTitle(ScheduleGroup group, int? subGroup)
        => subGroup.HasValue ? $"{group.Title}, подгруппа {subGroup.Value}" : group.Title;

    // ══════════════════════════════════════════════════════════
    //  Построители клавиатур (приватные)
    // ══════════════════════════════════════════════════════════

    /// <summary>Клавиатура выбора рабочего таймера</summary>
    private static InlineKeyboardMarkup BuildTimerKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⏱ 25 мин (Помодоро)", "timer_25"),
                InlineKeyboardButton.WithCallbackData("⏱ 30 мин", "timer_30")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⏱ 45 мин", "timer_45"),
                InlineKeyboardButton.WithCallbackData("⏱ 60 мин", "timer_60")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✏️ Своё время", "timer_custom"),
                InlineKeyboardButton.WithCallbackData("⏹ Стоп", "timer_stop")
            }
        });

    /// <summary>Клавиатура выбора перерыва</summary>
    private static InlineKeyboardMarkup BuildRestKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("☕ 5 мин (короткий)", "rest_5"),
                InlineKeyboardButton.WithCallbackData("☕ 15 мин (средний)", "rest_15")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🛌 30 мин (длинный)", "rest_30")
            }
        });

    /// <summary>Клавиатура меню планирования</summary>
    private static InlineKeyboardMarkup BuildPlanKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("➕ Добавить дело", "plan_add"),
                InlineKeyboardButton.WithCallbackData("📋 Показать план", "plan_list")
            }
        });

    private InlineKeyboardMarkup? BuildMiniAppLinkMarkup()
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl))
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithWebApp("Mini app", _webAppUrl)
            }
        });
    }

    private InlineKeyboardMarkup? BuildGroupMiniAppLinkMarkup(long chatId)
    {
        var groupUrl = BuildGroupMiniAppUrl(chatId);
        if (string.IsNullOrWhiteSpace(groupUrl))
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithUrl("Mini app", groupUrl)
            }
        });
    }

    private string? BuildGroupMiniAppUrl(long chatId)
    {
        if (string.IsNullOrWhiteSpace(_botIdentity.Username) ||
            string.IsNullOrWhiteSpace(_groupMiniAppShortName))
        {
            return null;
        }

        var token = _groupMiniAppAccess.CreateToken(chatId);
        var startParam = $"chat-{chatId}-{token}";
        return $"https://t.me/{_botIdentity.Username}/{_groupMiniAppShortName}?startapp={startParam}&mode=compact";
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

        var buttons = subjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .Append(("🔴 Отмена", "hw_cancel"));

        await _bot.SendMessage(
            chatId: chatId,
            text: "📚 <b>Выбери предмет из расписания группы:</b>",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private async Task TryPinMiniAppMessageAsync(ChatId chatId, Message launchMessage, CancellationToken ct)
    {
        try
        {
            var chat = await _bot.GetChat(chatId, ct);
            if (IsMiniAppPinned(chat.PinnedMessage))
                return;

            await _bot.PinChatMessage(
                chatId: chatId,
                messageId: launchMessage.Id,
                disableNotification: true,
                cancellationToken: ct);
        }
        catch
        {
            // Закрепление не критично: в некоторых чатах у бота может не быть прав.
        }
    }

    private static bool IsMiniAppPinned(Message? pinnedMessage)
    {
        if (pinnedMessage is null)
            return false;

        return string.Equals(
            pinnedMessage.Text?.Trim(),
            "Открой mini app по кнопке ниже.",
            StringComparison.Ordinal);
    }

    private static InlineKeyboardMarkup BuildReminderKeyboard(bool enabled)
    {
        if (!enabled)
        {
            return new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Указать время", "rem_set")
                }
            });
        }

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Изменить время", "rem_set"),
                InlineKeyboardButton.WithCallbackData("Выключить", "rem_off")
            }
        });
    }

    private static InlineKeyboardMarkup BuildGroupReminderKeyboard(bool enabled)
    {
        if (!enabled)
        {
            return new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Настроить напоминания", "rem_set")
                }
            });
        }

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Изменить настройки", "rem_set"),
                InlineKeyboardButton.WithCallbackData("Выключить", "rem_off")
            }
        });
    }

    private static string? ResolveWebAppUrl(IConfiguration configuration)
    {
        var configuredUrl = configuration["WebAppUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
            return configuredUrl;

        var railwayDomain = configuration["RAILWAY_PUBLIC_DOMAIN"];
        if (string.IsNullOrWhiteSpace(railwayDomain))
            return null;

        return $"https://{railwayDomain.TrimEnd('/')}/miniapp/";
    }

    private Task SendGroupModeUnavailableAsync(long chatId, string featureName, CancellationToken ct)
        => _bot.SendMessage(
            chatId: chatId,
            text: $"{featureName} доступен только в личном чате. В группах я работаю только с расписанием и общим ДЗ.",
            cancellationToken: ct);

    private static bool IsGroupChat(ChatType chatType)
        => chatType is ChatType.Group or ChatType.Supergroup;
}
