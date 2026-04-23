using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
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
    private readonly BotVisitLogService _visits;

    public CommandHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ReminderSettingsService reminders,
        BotVisitLogService visits)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _reminders = reminders;
        _visits = visits;
    }

    // ══════════════════════════════════════════════════════════
    //  /start
    // ══════════════════════════════════════════════════════════

    /// <summary>Приветствие при первом запуске или перезапуске</summary>
    public async Task HandleStartAsync(Message msg, CancellationToken ct)
    {
        _visits.RecordVisit(msg.From!);

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
                          $"Расписание уже настроено: <b>{Escape(FormatGroupTitle(group, selection.SubGroup))}</b>.\n\n" +
                          "Можешь посмотреть пары через /schedule, добавить ДЗ через /add_homework или открыть список заданий через /homework.\n" +
                          "Если нужно сфокусироваться на учёбе, запускай таймер через /timer.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }

            _scheduleSelections.Delete(userId);
        }

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "👋 <b>Привет! Я помогу тебе следить за расписанием, домашками и дедлайнами.</b>\n\n" +
                       "Давай сначала настроим расписание:\n" +
                       "1. Нажми /schedule\n" +
                       "2. Выбери направление, курс и подгруппу\n" +
                       "3. После этого я закреплю расписание за тобой\n\n" +
                       "Когда расписание будет выбрано, ты сможешь добавлять ДЗ по предметам из своего расписания.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /help
    // ══════════════════════════════════════════════════════════

    /// <summary>Справка по всем командам</summary>
    public async Task HandleHelpAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "📖 <b>Список команд:</b>\n\n" +
                       "⏱ <b>Таймер учёбы:</b>\n" +
                       "/timer — запустить таймер (25/30/45/60 мин или своё)\n" +
                       "/stop — остановить текущий таймер\n\n" +
                       "☕ <b>Отдых:</b>\n" +
                       "/rest — запустить таймер отдыха\n\n" +
                       "📚 <b>Домашние задания:</b>\n" +
                       "/add_homework — добавить ДЗ по предмету из расписания\n" +
                       "/homework — посмотреть ДЗ и задачи\n" +
                       "/reminders — настроить напоминания\n\n" +
                       "📋 <b>Планирование:</b>\n" +
                       "/plan — управление задачами\n\n" +
                       "📅 <b>Расписание:</b>\n" +
                       "/schedule — моё расписание занятий\n\n" +
                       "❓ /help — эта справка",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /timer
    // ══════════════════════════════════════════════════════════

    /// <summary>Показать меню выбора длительности рабочего таймера</summary>
    public async Task HandleTimerAsync(Message msg, CancellationToken ct)
    {
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

    /// <summary>Меню управления учебными задачами</summary>
    public async Task HandlePlanAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var pending  = session.PendingTasksCount;

        var text = pending > 0
            ? $"📋 <b>Твой план</b>\nНевыполненных задач: <b>{pending}</b>\n\nЧто делаем?"
            : "📋 <b>Твой план</b>\nЗадач пока нет. Добавь первую!";

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
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);

        if (!TryGetAllScheduleEntriesForUser(userId, out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Сначала выбери своё расписание через /schedule: укажи направление, курс и подгруппу. После этого я покажу предметы и смогу добавлять ДЗ с дедлайнами.",
                cancellationToken: ct);
            return;
        }

        var subjects = entries
            .Select(e => ScheduleCatalogService.GetHomeworkSubjectTitle(e.Subject))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScheduleCatalogService.GetHomeworkSubjectSortGroup)
            .ThenBy(s => s)
            .ToList();

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

        session.State = UserState.Idle;
        session.DraftTask = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var buttons = subjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .Append(("Отмена", "hw_cancel"));

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "📚 <b>Выбери предмет, по которому задали ДЗ:</b>",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /homework
    // ══════════════════════════════════════════════════════════

    public async Task HandleHomeworkAsync(Message msg, CancellationToken ct)
    {
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

        var selection = _scheduleSelections.Get(userId);
        if (selection is not null)
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (group is not null)
            {
                ApplySelectionToSession(session, group, selection.SubGroup);
                await SendSelectedScheduleMenuAsync(msg.Chat.Id, group, selection.SubGroup, ct);
                return;
            }

            _scheduleSelections.Delete(userId);
        }

        await SendDirectionChoiceAsync(msg.Chat.Id, ct);
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

    private bool TryGetAllScheduleEntriesForUser(
        long userId,
        out ScheduleGroup? group,
        out int? subGroup,
        out List<ScheduleEntry> entries)
    {
        group = null;
        subGroup = null;
        entries = new List<ScheduleEntry>();

        var selection = _scheduleSelections.Get(userId);
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
        var active = session.Tasks
            .Where(t => !t.IsCompleted)
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var completed = session.Tasks
            .Where(t => t.IsCompleted)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        if (session.Tasks.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "📚 <b>Домашних заданий и задач пока нет.</b>\nДобавить ДЗ можно через /add_homework.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(
            chatId: chatId,
            text: $"📚 <b>Домашние задания и задачи</b>\nАктивных: <b>{active.Count}</b> | Выполнено: <b>{completed.Count}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        foreach (var task in active.Take(20))
        {
            var deadlineText = task.Deadline.HasValue
                ? task.Deadline.Value.ToString("dd.MM.yyyy")
                : "без дедлайна";

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
                text: $"📌 <b>{Escape(task.Title)}</b>{FormatTaskUrgency(task)}\n" +
                      $"📚 {Escape(task.Subject)}\n" +
                      $"📅 {deadlineText}",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);
        }

        if (active.Count > 20)
            await _bot.SendMessage(chatId, $"... и ещё {active.Count - 20} задач(и).", cancellationToken: ct);

        if (active.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "Активных ДЗ и задач нет.",
                cancellationToken: ct);
        }

        if (completed.Count == 0)
            return;

        var completedText = string.Join("\n", completed
            .Take(5)
            .Select(t => $"✅ {Escape(t.Title)} ({Escape(t.Subject)})"));

        await _bot.SendMessage(
            chatId: chatId,
            text: $"<b>Выполнено:</b>\n{completedText}" +
                  (completed.Count > 5 ? $"\n... и ещё {completed.Count - 5}" : string.Empty),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private static string FormatTaskUrgency(StudyTask task)
    {
        if (!task.Deadline.HasValue)
            return string.Empty;

        var days = (task.Deadline.Value.Date - DateTime.Today).Days;
        return days switch
        {
            < 0 => " 🔴 <b>Просрочено!</b>",
            0 => " 🟡 <b>Сдать сегодня!</b>",
            1 => " 🟡 Завтра",
            <= 3 => $" 🟠 Через {days} дня",
            _ => $" ✅ Через {days} дней"
        };
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        ScheduleGroup group,
        int? subGroup,
        CancellationToken ct)
    {
        var weekLabel = _scheduleCatalog.GetCurrentWeekLabel();

        await _bot.SendMessage(
            chatId: chatId,
            text: $"📅 <b>Твоё расписание</b>\n" +
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
                InlineKeyboardButton.WithCallbackData("➕ Добавить задачу", "plan_add"),
                InlineKeyboardButton.WithCallbackData("📋 Показать план", "plan_list")
            }
        });

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
}
