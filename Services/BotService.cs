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

        // Регистрируем команды — они появятся в меню "/" у каждого пользователя
        await _bot.SetMyCommands(
            new[]
            {
                new Telegram.Bot.Types.BotCommand { Command = "plan",     Description = "📋 Задачи и ИИ-план учёбы" },
                new Telegram.Bot.Types.BotCommand { Command = "schedule", Description = "🗓 Расписание занятий" },
                new Telegram.Bot.Types.BotCommand { Command = "timer",    Description = "⏱ Таймер учёбы (Помодоро)" },
                new Telegram.Bot.Types.BotCommand { Command = "rest",     Description = "☕ Таймер отдыха" },
                new Telegram.Bot.Types.BotCommand { Command = "stop",     Description = "⏹ Остановить таймер" },
                new Telegram.Bot.Types.BotCommand { Command = "fatigue",  Description = "😴 Уровень усталости" },
                new Telegram.Bot.Types.BotCommand { Command = "status",   Description = "📊 Общий статус" },
                new Telegram.Bot.Types.BotCommand { Command = "help",     Description = "❓ Справка" },
            },
            cancellationToken: cancellationToken);
        _logger.LogInformation("Команды бота зарегистрированы.");

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
