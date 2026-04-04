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
///   - Фото → MediaHandler
///   - Документ (PDF / изображение) → MediaHandler
/// </summary>
public class UpdateRouter
{
    private readonly CommandHandler _commands;
    private readonly TextHandler    _text;
    private readonly CallbackHandler _callbacks;
    private readonly MediaHandler   _media;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        CommandHandler commands,
        TextHandler    text,
        CallbackHandler callbacks,
        MediaHandler   media,
        ILogger<UpdateRouter> logger)
    {
        _commands  = commands;
        _text      = text;
        _callbacks = callbacks;
        _media     = media;
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

            if (update.Type == UpdateType.Message && update.Message is not null)
            {
                await HandleMessageAsync(update.Message, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка при обработке обновления {UpdateType}", update.Type);
        }
    }

    /// <summary>
    /// Обработать входящее сообщение.
    /// Маршрутизирует по типу содержимого: текст, фото, документ.
    /// </summary>
    private async Task HandleMessageAsync(Message msg, CancellationToken ct)
    {
        // ── Фото ─────────────────────────────────────────────
        if (msg.Photo is not null)
        {
            await _media.HandlePhotoAsync(msg, ct);
            return;
        }

        // ── Документ (PDF, изображение-файлом и т.д.) ────────
        if (msg.Document is not null)
        {
            // Документы, присланные как фото через "Send as file", приходят сюда
            await _media.HandleDocumentAsync(msg, ct);
            return;
        }

        // ── Текстовое сообщение ───────────────────────────────
        if (msg.Text is not null)
        {
            var text = msg.Text.Trim();

            // Ищем часть, начинающуюся с '/' — поддерживает кнопки вида "📋 /plan"
            var commandPart = text
                .Split(' ')
                .Select(p => p.Split('@')[0].ToLowerInvariant())
                .FirstOrDefault(p => p.StartsWith('/'));

            if (commandPart is not null)
            {
                await RouteCommandAsync(msg, commandPart, ct);
            }
            else
            {
                await _text.HandleAsync(msg, ct);
            }
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

            case "/schedule":
                await _commands.HandleScheduleAsync(msg, ct);
                break;

            default:
                await _text.HandleAsync(msg, ct);
                break;
        }
    }
}
