using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Handlers;

namespace TelegramStudentBot.Services;

/// <summary>
/// Фоновый сервис бота (IHostedService).
/// Запускается при старте приложения, подключается к Telegram через Long Polling
/// и начинает принимать обновления.
/// </summary>
public class BotService : IHostedService
{
    private readonly ITelegramBotClient _bot;
    private readonly UpdateRouter _router;
    private readonly ILogger<BotService> _logger;

    // Токен для остановки polling при завершении работы
    private CancellationTokenSource? _cts;

    public BotService(ITelegramBotClient bot, UpdateRouter router, ILogger<BotService> logger)
    {
        _bot    = bot;
        _router = router;
        _logger = logger;
    }

    /// <summary>Запуск бота — начинаем получать обновления от Telegram</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Проверяем токен, получаем информацию о боте
        var me = await _bot.GetMe(cancellationToken);
        _logger.LogInformation("Бот запущен: @{Username} (ID: {Id})", me.Username, me.Id);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Настройки polling: принимаем только сообщения и callback query
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
            {
                UpdateType.Message,
                UpdateType.CallbackQuery
            },
            // Пропускаем обновления, накопившиеся пока бот был выключен
            DropPendingUpdates = true
        };

        // Запускаем polling в фоне (не блокируем StartAsync)
        _bot.StartReceiving(
            updateHandler: _router.HandleUpdateAsync,
            errorHandler:  HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token);

        _logger.LogInformation("Long polling запущен. Ожидаю сообщений...");
    }

    /// <summary>Остановка бота — отменяем polling</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Остановка бота...");
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Обработчик ошибок polling (разрыв сети, ошибки Telegram API и т.д.)</summary>
    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        // Логируем ошибку, но не падаем — polling автоматически переподключится
        _logger.LogError(ex, "Ошибка polling. Источник: {Source}", source);
        return Task.CompletedTask;
    }
}
