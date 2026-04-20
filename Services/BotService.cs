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
/// Запускается при старте приложения, регистрирует команды меню,
/// подключается к Telegram через Long Polling и начинает принимать обновления.
/// </summary>
public class BotService : IHostedService
{
    private readonly ITelegramBotClient _bot;
    private readonly UpdateRouter _router;
    private readonly ILogger<BotService> _logger;

    private CancellationTokenSource? _cts;

    public BotService(ITelegramBotClient bot, UpdateRouter router, ILogger<BotService> logger)
    {
        _bot    = bot;
        _router = router;
        _logger = logger;
    }

    /// <summary>Запуск бота — регистрируем команды меню и запускаем polling</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var me = await _bot.GetMe(cancellationToken);
        _logger.LogInformation("Бот запущен: @{Username} (ID: {Id})", me.Username, me.Id);

        // Регистрируем команды меню (кнопка «≡» слева от поля ввода)
        await RegisterCommandsAsync(cancellationToken);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
            {
                UpdateType.Message,
                UpdateType.CallbackQuery
            },
            DropPendingUpdates = true
        };

        _bot.StartReceiving(
            updateHandler:   _router.HandleUpdateAsync,
            errorHandler:    HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token);

        _logger.LogInformation("Long polling запущен. Ожидаю сообщений...");
    }

    /// <summary>Остановка бота</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Остановка бота...");
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    //  Регистрация команд меню Telegram
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Устанавливает список команд бота через SetMyCommands.
    /// Эти команды отображаются в меню слева от поля ввода сообщений.
    /// </summary>
    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        var commands = new[]
        {
            new BotCommand { Command = "add_homework", Description = "Добавить ДЗ" },
            new BotCommand { Command = "homework",     Description = "Домашние задания" },
            new BotCommand { Command = "reminders",    Description = "Напоминания" },
            new BotCommand { Command = "plan",         Description = "Управление задачами" },
            new BotCommand { Command = "schedule",     Description = "Моё расписание занятий" },
            new BotCommand { Command = "timer",        Description = "Запустить таймер учёбы" },
            new BotCommand { Command = "rest",         Description = "Запустить таймер отдыха" },
            new BotCommand { Command = "stop",         Description = "Остановить таймер" },
            new BotCommand { Command = "help",         Description = "Список команд" },
        };

        try
        {
            await _bot.SetMyCommands(commands, cancellationToken: ct);
            _logger.LogInformation("Команды меню зарегистрированы ({Count} шт.)", commands.Length);
        }
        catch (Exception ex)
        {
            // Не критично — бот продолжит работу без меню команд
            _logger.LogWarning(ex, "Не удалось зарегистрировать команды меню");
        }
    }

    // ──────────────────────────────────────────────────────────

    private Task HandleErrorAsync(
        ITelegramBotClient bot, Exception ex,
        HandleErrorSource source, CancellationToken ct)
    {
        _logger.LogError(ex, "Ошибка polling. Источник: {Source}", source);
        return Task.CompletedTask;
    }
}
