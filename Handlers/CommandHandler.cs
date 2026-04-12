using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик команд (/start, /help, /timer, /rest, /plan, /fatigue, /status, /stop, /ai).
/// Каждый метод соответствует одной команде.
/// </summary>
public class CommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;

    public CommandHandler(ITelegramBotClient bot, SessionService sessions, TimerService timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    // ══════════════════════════════════════════════════════════
    //  /start
    // ══════════════════════════════════════════════════════════

    /// <summary>Приветствие при первом запуске или перезапуске</summary>
    public async Task HandleStartAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        session.State = UserState.Idle;
        var firstName = TelegramHtml.Escape(session.FirstName);

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        $"👋 Привет, <b>{firstName}</b>!\n\n" +
                         $"Я помогу тебе учиться эффективно:\n" +
                         $"📋 Планировать задачи\n" +
                         $"🗓 Вести расписание занятий\n" +
                         $"⏱ Запускать таймеры (Помодоро)\n" +
                         $"😴 Следить за усталостью\n\n" +
                         $"Все команды доступны через меню — нажми <b>/</b> чтобы увидеть список.",
            parseMode:   ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
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
                       "📋 <b>Планирование:</b>\n" +
                       "/plan — управление задачами\n\n" +
                       "😴 <b>Усталость:</b>\n" +
                       "/fatigue — показать уровень усталости\n\n" +
                       "📊 <b>Статус:</b>\n" +
                       "/status — общий дашборд (таймер + усталость + задачи)\n\n" +
                       "🗓 <b>Расписание:</b>\n" +
                       "/schedule — добавить и посмотреть расписание занятий\n\n" +
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
    //  /fatigue
    // ══════════════════════════════════════════════════════════

    /// <summary>Показать уровень усталости и советы</summary>
    public async Task HandleFatigueAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        // Визуальная шкала усталости (10 делений)
        var filled  = session.FatigueLevel / 10;
        var empty   = 10 - filled;
        var bar     = new string('█', filled) + new string('░', empty);

        var advice = session.FatigueLevel switch
        {
            <= 30 => "💡 Ты в отличной форме! Самое время учиться.",
            <= 60 => "💡 Умеренная усталость. Продолжай, но не забывай про перерывы.",
            <= 85 => "💡 Высокая усталость! Рекомендую сделать перерыв → /rest",
            _      => "💡 Истощение! Нужен длительный отдых (30+ мин) → /rest"
        };

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"😴 <b>Уровень усталости:</b>\n\n" +
                       $"[{bar}] {session.FatigueLevel}%\n" +
                       $"Статус: {session.FatigueDescription}\n" +
                       $"Сессий без отдыха: {session.WorkSessionsWithoutRest}\n\n" +
                       $"{advice}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  /status
    // ══════════════════════════════════════════════════════════

    /// <summary>Общий дашборд: таймер + усталость + задачи</summary>
    public async Task HandleStatusAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var firstName = TelegramHtml.Escape(session.FirstName);

        // Блок таймера
        string timerBlock;
        if (session.ActiveTimer is not null)
        {
            var t         = session.ActiveTimer;
            var remaining = t.Remaining;
            var typeLabel = t.Type == TimerType.Work ? "⏱ Работа" : "☕ Отдых";
            timerBlock = $"{typeLabel}: осталось <b>{(int)remaining.TotalMinutes} мин {remaining.Seconds} сек</b>\n" +
                         $"Завершится в {t.EndsAt:HH:mm}";
        }
        else
        {
            timerBlock = "⏹ Таймер не запущен";
        }

        // Блок усталости
        var filled   = session.FatigueLevel / 10;
        var bar      = new string('█', filled) + new string('░', 10 - filled);
        var fatBlock = $"[{bar}] {session.FatigueLevel}% — {session.FatigueDescription}";

        // Блок задач
        var pending   = session.Tasks.Count(t => !t.IsCompleted);
        var completed = session.Tasks.Count(t => t.IsCompleted);
        var taskBlock = session.Tasks.Count == 0
            ? "Список задач пуст"
            : $"Выполнено: {completed}, Осталось: {pending}";

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"📊 <b>Твой статус, {firstName}</b>\n\n" +
                       $"🕐 <b>Таймер:</b>\n{timerBlock}\n\n" +
                       $"😴 <b>Усталость:</b>\n{fatBlock}\n\n" +
                       $"📋 <b>Задачи:</b>\n{taskBlock}",
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
    //  Построители клавиатур
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
                InlineKeyboardButton.WithCallbackData("📋 Показать план",   "plan_list")
            }
        });

    // ══════════════════════════════════════════════════════════
    //  /schedule
    // ══════════════════════════════════════════════════════════

    /// <summary>Меню управления расписанием занятий</summary>
    public async Task HandleScheduleAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var count   = session.Schedule.Count;

        var text = count > 0
            ? $"🗓 <b>Расписание занятий</b>\nЗаписей: <b>{count}</b>\n\nЧто делаем?"
            : "🗓 <b>Расписание занятий</b>\nРасписание пока не добавлено.";

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        text,
            parseMode:   ParseMode.Html,
            replyMarkup: BuildScheduleKeyboard(),
            cancellationToken: ct);
    }

    /// <summary>Клавиатура меню расписания</summary>
    private static InlineKeyboardMarkup BuildScheduleKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("➕ Добавить занятие",    "schedule_add"),
                InlineKeyboardButton.WithCallbackData("📋 Показать расписание", "schedule_list")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Очистить всё",        "schedule_clear")
            }
        });
}
