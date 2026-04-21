using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class CommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ReminderSettingsService _reminders;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly string? _webAppUrl;
    private readonly string? _webAppStopUrl;

    public CommandHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ReminderSettingsService reminders,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        IConfiguration config)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _reminders = reminders;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _webAppUrl = config["WebAppUrl"]?.TrimEnd('/');
        _webAppStopUrl = config["WebAppStopUrl"]?.TrimEnd('/');
    }

    public async Task HandleStartAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        session.State = UserState.Idle;
        var firstName = TelegramHtml.Escape(session.FirstName);

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"👋 Привет, <b>{firstName}</b>!\n\n" +
                  "Я помогу тебе учиться спокойнее и собраннее:\n" +
                  "📋 вести задачи\n" +
                  "🗓 смотреть расписание по группе\n" +
                  "⏰ получать напоминания о дедлайнах\n" +
                  "⏱ запускать таймеры\n" +
                  "😴 следить за усталостью\n\n" +
                  "Открой Mini App, чтобы управлять всем в одном месте.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppKeyboard(msg),
            cancellationToken: ct);
    }

    public async Task HandleHelpAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "📖 <b>Команды</b>\n\n" +
                  "📱 <b>Mini App</b>\n/app — открыть Mini App\n\n" +
                  "⏱ <b>Таймеры</b>\n/timer — рабочий таймер\n/rest — таймер отдыха\n/stop — остановить таймер\n\n" +
                  "📋 <b>Задачи</b>\n/plan — задачи и план\n\n" +
                  "🗓 <b>Расписание</b>\n/schedule — выбрать группу и посмотреть расписание\n\n" +
                  "⏰ <b>Напоминания</b>\n/reminders — настроить ежедневные напоминания\n\n" +
                  "😴 <b>Состояние</b>\n/fatigue — уровень усталости\n/status — общий статус\n\n" +
                  "❓ /help — эта справка",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppKeyboard(msg),
            cancellationToken: ct);
    }

    public async Task HandleAppAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "📱 <b>Mini App</b>\n\nЗдесь можно запускать таймеры, вести задачи, выбирать группу и настраивать напоминания.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppKeyboard(msg),
            cancellationToken: ct);
    }

    public async Task HandleTimerAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        var prefix = string.Empty;
        if (session.ActiveTimer is not null)
        {
            var remaining = session.ActiveTimer.Remaining;
            var typeLabel = session.ActiveTimer.Type == TimerType.Work ? "рабочий" : "отдых";
            prefix = $"⚠️ Уже идёт таймер <b>{typeLabel}</b>, осталось <b>{(int)remaining.TotalMinutes} мин {remaining.Seconds} сек</b>.\n\n";
        }

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: prefix + "⏱ <b>Выбери длительность рабочего таймера:</b>",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTimerKeyboard(),
            cancellationToken: ct);
    }

    public async Task HandleRestAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "☕ <b>Выбери длительность перерыва:</b>",
            parseMode: ParseMode.Html,
            replyMarkup: BuildRestKeyboard(),
            cancellationToken: ct);
    }

    public async Task HandleStopAsync(Message msg, CancellationToken ct)
    {
        var stopped = _timers.StopTimer(msg.From!.Id);
        var text = stopped
            ? "⏹ Таймер <b>остановлен</b>."
            : "Активного таймера нет.";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    public async Task HandleFatigueAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var filled = session.FatigueLevel / 10;
        var empty = 10 - filled;
        var bar = new string('█', filled) + new string('░', empty);

        var advice = session.FatigueLevel switch
        {
            <= 30 => "Ты в хорошей форме.",
            <= 60 => "Усталость умеренная, не забывай про перерывы.",
            <= 85 => "Уже стоит передохнуть.",
            _ => "Лучше сделать длинный отдых."
        };

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"😴 <b>Уровень усталости</b>\n\n[{bar}] {session.FatigueLevel}%\n" +
                  $"Статус: {session.FatigueDescription}\n" +
                  $"Сессий без отдыха: {session.WorkSessionsWithoutRest}\n\n{advice}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    public async Task HandleStatusAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var firstName = TelegramHtml.Escape(session.FirstName);

        var timerBlock = session.ActiveTimer is null
            ? "Таймер не запущен"
            : $"{(session.ActiveTimer.Type == TimerType.Work ? "Работа" : "Отдых")}: " +
              $"ещё <b>{(int)session.ActiveTimer.Remaining.TotalMinutes} мин {session.ActiveTimer.Remaining.Seconds} сек</b>";

        var fatigueBar = new string('█', session.FatigueLevel / 10) + new string('░', 10 - (session.FatigueLevel / 10));
        var taskPending = session.Tasks.Count(task => !task.IsCompleted);
        var reminderSettings = _reminders.Get(msg.From.Id);
        var reminderBlock = reminderSettings.IsEnabled ? $"Включены на <b>{reminderSettings.TimeText}</b>" : "Выключены";

        var selection = _scheduleSelections.Get(msg.From.Id);
        var scheduleBlock = selection is null
            ? "Группа не выбрана"
            : TelegramHtml.Escape(_scheduleCatalog.GetGroup(selection.ScheduleId)?.Title ?? selection.ScheduleId);

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: $"📊 <b>Статус: {firstName}</b>\n\n" +
                  $"🕐 <b>Таймер</b>\n{timerBlock}\n\n" +
                  $"😴 <b>Усталость</b>\n[{fatigueBar}] {session.FatigueLevel}%\n\n" +
                  $"📋 <b>Задачи</b>\nАктивных: <b>{taskPending}</b>\n\n" +
                  $"🗓 <b>Расписание</b>\n{scheduleBlock}\n\n" +
                  $"⏰ <b>Напоминания</b>\n{reminderBlock}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    public async Task HandlePlanAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var pending = session.PendingTasksCount;

        var text = pending > 0
            ? $"📋 <b>Твой план</b>\nНевыполненных задач: <b>{pending}</b>\n\nЧто делаем?"
            : "📋 <b>Твой план</b>\nЗадач пока нет. Добавь первую.";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildPlanKeyboard(),
            cancellationToken: ct);
    }

    public async Task HandleScheduleAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var selection = _scheduleSelections.Get(userId);
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        var currentWeek = _scheduleCatalog.GetCurrentWeekLabel();

        string text;
        if (selection is null)
        {
            text =
                "<b>Расписание занятий</b>\n\n" +
                "Как это работает:\n" +
                "1. Нажми <b>Выбрать группу</b>\n" +
                "2. Выбери свою группу\n" +
                "3. Если нужно, выбери подгруппу\n" +
                "4. Потом можно открыть расписание на сегодня или на всю неделю";
        }
        else
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            var subgroupText = selection.SubGroup.HasValue ? $"\nПодгруппа: <b>{selection.SubGroup.Value}</b>" : string.Empty;
            text =
                "<b>Расписание занятий</b>\n\n" +
                $"Группа: <b>{TelegramHtml.Escape(group?.Title ?? selection.ScheduleId)}</b>{subgroupText}\n" +
                $"Записей в расписании: <b>{session.Schedule.Count}</b>\n" +
                $"Текущая неделя: <b>{TelegramHtml.Escape(currentWeek)}</b>\n\n" +
                "Что можно сделать:\n" +
                "• посмотреть пары на сегодня\n" +
                "• открыть расписание на всю неделю\n" +
                "• сменить группу";
        }

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildScheduleKeyboard(),
            cancellationToken: ct);
    }

    public async Task HandleRemindersAsync(Message msg, CancellationToken ct)
    {
        var settings = _reminders.Get(msg.From!.Id);
        var status = settings.IsEnabled
            ? $"✅ Включены на <b>{settings.TimeText}</b>"
            : "⛔ Выключены";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: "⏰ <b>Напоминания о дедлайнах</b>\n\n" +
                  "Раз в день бот проверяет задачи с дедлайном на завтра и присылает короткий список.\n\n" +
                  $"Статус: {status}",
            parseMode: ParseMode.Html,
            replyMarkup: BuildReminderKeyboard(settings),
            cancellationToken: ct);
    }

    private static InlineKeyboardMarkup BuildTimerKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⏱ 25 мин", "timer_25"),
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

    private static InlineKeyboardMarkup BuildRestKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("☕ 5 мин", "rest_5"),
                InlineKeyboardButton.WithCallbackData("☕ 15 мин", "rest_15")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🛌 30 мин", "rest_30")
            }
        });

    private static InlineKeyboardMarkup BuildPlanKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("➕ Добавить задачу", "plan_add"),
                InlineKeyboardButton.WithCallbackData("📋 Показать план", "plan_list")
            }
        });

    private static InlineKeyboardMarkup BuildScheduleKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🎓 Выбрать группу", "schedule_select"),
                InlineKeyboardButton.WithCallbackData("📅 На сегодня", "schedule_today")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗂 Вся неделя", "schedule_week"),
                InlineKeyboardButton.WithCallbackData("🗑 Сбросить", "schedule_clear")
            }
        });

    private static InlineKeyboardMarkup BuildReminderKeyboard(UserReminderSettings settings) =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    settings.IsEnabled ? "🕗 Изменить время" : "✅ Включить на 20:00",
                    settings.IsEnabled ? "reminder_change_time" : "reminder_enable_default")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✏️ Своё время", "reminder_change_time"),
                InlineKeyboardButton.WithCallbackData("⛔ Выключить", "reminder_disable")
            }
        });

    private InlineKeyboardMarkup? BuildMiniAppKeyboard(Message msg, string view = "overview")
    {
        var url = BuildMiniAppUrl(msg, view);
        if (url is null)
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithWebApp("📱 Открыть Mini App", new WebAppInfo { Url = url }) }
        });
    }

    private string? BuildMiniAppUrl(Message msg, string view)
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl) || msg.From is null)
            return null;

        var isStaticPage = _webAppUrl.Contains("github.io", StringComparison.OrdinalIgnoreCase);
        var pagePath = isStaticPage ? "/timer.html" : "/app";
        var url = $"{_webAppUrl}{pagePath}?view={Uri.EscapeDataString(view)}&userId={msg.From.Id}&chatId={msg.Chat.Id}";

        var apiBase = isStaticPage ? _webAppStopUrl : _webAppUrl;
        if (!string.IsNullOrWhiteSpace(apiBase))
            url += $"&apiBase={Uri.EscapeDataString(apiBase)}";

        return url;
    }
}
