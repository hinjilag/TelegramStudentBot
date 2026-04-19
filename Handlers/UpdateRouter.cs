using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Маршрутизатор обновлений от Telegram.
/// Получает сырой Update и отправляет его в нужный обработчик:
///   - Команды (/start, /help и т.д.) → CommandHandler
///   - Обычный текст в состоянии диалога → TextHandler
///   - Нажатие инлайн-кнопки → CallbackHandler
/// </summary>
public class UpdateRouter
{
    private readonly CommandHandler _commands;
    private readonly TextHandler    _text;
    private readonly CallbackHandler _callbacks;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        CommandHandler commands,
        TextHandler    text,
        CallbackHandler callbacks,
        ILogger<UpdateRouter> logger)
    {
        _commands  = commands;
        _text      = text;
        _callbacks = callbacks;
        _logger    = logger;
    }

    /// <summary>
    /// Точка входа для всех обновлений от Telegram.
    /// Вызывается из BotService при каждом входящем сообщении.
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            // Нажатие инлайн-кнопки
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
            {
                await _callbacks.HandleAsync(update.CallbackQuery, ct);
                return;
            }

            // Текстовое сообщение
            if (update.Type == UpdateType.Message && update.Message?.Text is not null)
            {
                await HandleMessageAsync(update.Message, ct);
                return;
            }

            // Фотография (сжатая Telegram'ом)
            if (update.Type == UpdateType.Message && update.Message?.Photo is not null)
            {
                await _text.HandlePhotoAsync(update.Message, ct);
                return;
            }

            // Документ — возможно несжатое изображение
            if (update.Type == UpdateType.Message && update.Message?.Document is not null)
            {
                var mime = update.Message.Document.MimeType ?? string.Empty;
                if (mime.StartsWith("image/"))
                {
                    await _text.HandlePhotoAsync(update.Message, ct);
                    return;
                }
            }

            // Остальные типы обновлений (стикеры, аудио и т.д.) игнорируем
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка при обработке обновления {UpdateType}", update.Type);
        }
    }

    /// <summary>
    /// Обработать входящее сообщение.
    /// Если сообщение начинается с '/' — это команда, иначе — текст.
    /// </summary>
    private async Task HandleMessageAsync(Message msg, CancellationToken ct)
    {
        var text = msg.Text!.Trim();

        // Проверяем, является ли это командой
        // Извлекаем команду без @username_бота (например /start@MyBot → /start)
        var commandPart = text.Split(' ')[0].Split('@')[0].ToLowerInvariant();

        if (commandPart.StartsWith('/'))
        {
            await RouteCommandAsync(msg, commandPart, ct);
        }
        else
        {
            // Обычный текст — обрабатываем через машину состояний
            await _text.HandleAsync(msg, ct);
        }
    }

    /// <summary>Маршрутизация команд по имени</summary>
    private async Task RouteCommandAsync(Message msg, string command, CancellationToken ct)
    {
        _logger.LogDebug("Команда {Command} от пользователя {UserId}", command, msg.From?.Id);

        switch (command)
        {
            case "/start":
                await _commands.HandleStartAsync(msg, ct);
                break;

            case "/help":
                await _commands.HandleHelpAsync(msg, ct);
                break;

            case "/timer":
                await _commands.HandleTimerAsync(msg, ct);
                break;

            case "/rest":
                await _commands.HandleRestAsync(msg, ct);
                break;

            case "/stop":
                await _commands.HandleStopAsync(msg, ct);
                break;

            case "/fatigue":
                await _commands.HandleFatigueAsync(msg, ct);
                break;

            case "/status":
                await _commands.HandleStatusAsync(msg, ct);
                break;

            case "/plan":
                await _commands.HandlePlanAsync(msg, ct);
                break;

            case "/add_homework":
                await _commands.HandleAddHomeworkAsync(msg, ct);
                break;

            case "/homework":
                await _commands.HandleHomeworkAsync(msg, ct);
                break;

            case "/reminders":
                await _commands.HandleRemindersAsync(msg, ct);
                break;

            case "/schedule":
                await _commands.HandleScheduleAsync(msg, ct);
                break;

            case "/add_schedule":
                await _commands.HandleAddScheduleAsync(msg, ct);
                break;

            default:
                // Неизвестная команда
                await _text.HandleAsync(msg, ct);
                break;
        }
    }
}
