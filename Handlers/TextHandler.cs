using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик обычных текстовых сообщений.
/// Работает как машина состояний: реакция зависит от текущего состояния сессии.
/// Используется для пошагового ввода данных (создание задачи, ввод времени таймера).
/// </summary>
public class TextHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;

    public TextHandler(ITelegramBotClient bot, SessionService sessions, TimerService timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    /// <summary>
    /// Обработать входящее текстовое сообщение.
    /// Маршрутизирует в нужный обработчик по текущему состоянию.
    /// </summary>
    public async Task HandleAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var text    = msg.Text?.Trim() ?? string.Empty;

        switch (session.State)
        {
            case UserState.WaitingForTaskTitle:
                await HandleTaskTitleAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskSubject:
                await HandleTaskSubjectAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTaskDeadline:
                await HandleTaskDeadlineAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForTimerMinutes:
                await HandleCustomTimerAsync(msg, session, text, ct);
                break;

            default:
                // В состоянии Idle — подсказываем команды
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "ℹ️ Я не понимаю это сообщение.\nИспользуй /help для просмотра команд.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Шаги создания задачи
    // ──────────────────────────────────────────────────────────

    /// <summary>Шаг 1: Пользователь вводит название задачи</summary>
    private async Task HandleTaskTitleAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Название не может быть пустым. Введи название задачи:", cancellationToken: ct);
            return;
        }

        // Сохраняем название в черновик и переходим к следующему шагу
        session.DraftTask = new StudyTask { Title = text };
        session.State     = UserState.WaitingForTaskSubject;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Название: <b>{text}</b>\n\nТеперь введи <b>предмет</b> (например: Математика, Физика):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>Шаг 2: Пользователь вводит предмет</summary>
    private async Task HandleTaskSubjectAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Предмет не может быть пустым. Введи название предмета:", cancellationToken: ct);
            return;
        }

        session.DraftTask!.Subject = text;
        session.State              = UserState.WaitingForTaskDeadline;

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Предмет: <b>{text}</b>\n\n" +
                       $"Введи <b>дедлайн</b> в формате ДД.ММ.ГГГГ\n" +
                       $"(или напиши <b>нет</b>, чтобы пропустить):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>Шаг 3: Пользователь вводит дедлайн или пропускает</summary>
    private async Task HandleTaskDeadlineAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        // Пропустить дедлайн
        if (text.Equals("нет", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no",  StringComparison.OrdinalIgnoreCase) ||
            text == "-")
        {
            session.DraftTask!.Deadline = null;
        }
        else
        {
            // Пробуем разобрать дату в форматах ДД.ММ.ГГГГ или ДД/ММ/ГГГГ
            if (!DateTime.TryParseExact(text, new[] { "dd.MM.yyyy", "dd/MM/yyyy", "d.M.yyyy" },
                    null, System.Globalization.DateTimeStyles.None, out var deadline))
            {
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "⚠️ Неверный формат даты. Используй ДД.ММ.ГГГГ (например: 25.05.2025)\nИли напиши <b>нет</b> чтобы пропустить.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }
            session.DraftTask!.Deadline = deadline;
        }

        // Сохраняем задачу в список
        var task = session.DraftTask!;
        session.Tasks.Add(task);
        session.DraftTask = null;
        session.State     = UserState.Idle;

        // Строим итоговое сообщение
        var deadlineText = task.Deadline.HasValue
            ? task.Deadline.Value.ToString("dd.MM.yyyy")
            : "не задан";

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"🎉 <b>Задача добавлена!</b>\n\n" +
                       $"📌 <b>{task.Title}</b>\n" +
                       $"📚 Предмет: {task.Subject}\n" +
                       $"📅 Дедлайн: {deadlineText}\n\n" +
                       $"Используй /plan → «Показать план» чтобы увидеть все задачи.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // ──────────────────────────────────────────────────────────
    //  Произвольное время таймера
    // ──────────────────────────────────────────────────────────

    /// <summary>Пользователь вводит произвольное время таймера в минутах</summary>
    private async Task HandleCustomTimerAsync(Message msg, UserSession session, string text, CancellationToken ct)
    {
        // Парсим введённое число
        if (!int.TryParse(text, out int minutes) || minutes < 1 || minutes > 300)
        {
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      "⚠️ Введи число минут от 1 до 300:",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        session.State = UserState.Idle;
        await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }
}
