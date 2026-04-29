using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Handlers;

namespace TelegramStudentBot.Services;

/// <summary>
/// Background service that starts Telegram long polling and registers bot commands.
/// </summary>
public class BotService : IHostedService
{
    private readonly ITelegramBotClient _bot;
    private readonly UpdateRouter _router;
    private readonly ILogger<BotService> _logger;

    private CancellationTokenSource? _cts;

    public BotService(
        ITelegramBotClient bot,
        UpdateRouter router,
        ILogger<BotService> logger)
    {
        _bot = bot;
        _router = router;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var me = await _bot.GetMe(cancellationToken);
        _logger.LogInformation("Бот запущен: @{Username} (ID: {Id})", me.Username, me.Id);

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
            updateHandler: _router.HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token);

        _logger.LogInformation("Long polling запущен. Ожидаю сообщений...");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Остановка бота...");
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        var privateCommands = new[]
        {
            new BotCommand { Command = "miniapp", Description = "Открыть mini app" },
            new BotCommand { Command = "add_homework", Description = "Добавить ДЗ" },
            new BotCommand { Command = "homework", Description = "Домашние задания" },
            new BotCommand { Command = "reminders", Description = "Напоминания" },
            new BotCommand { Command = "plan", Description = "Управление задачами" },
            new BotCommand { Command = "schedule", Description = "Мое расписание занятий" },
            new BotCommand { Command = "timer", Description = "Запустить таймер учебы" },
            new BotCommand { Command = "rest", Description = "Запустить таймер отдыха" },
            new BotCommand { Command = "stop", Description = "Остановить таймер" },
            new BotCommand { Command = "help", Description = "Список команд" }
        };

        var groupCommands = new[]
        {
            new BotCommand { Command = "add_homework", Description = "Добавить общее ДЗ" },
            new BotCommand { Command = "homework", Description = "Общий список ДЗ" },
            new BotCommand { Command = "reminders", Description = "Напоминания в группу" },
            new BotCommand { Command = "schedule", Description = "Расписание группы" },
            new BotCommand { Command = "help", Description = "Список команд" }
        };

        try
        {
            await _bot.SetMyCommands(privateCommands, cancellationToken: ct);
            await _bot.SetMyCommands(
                groupCommands,
                scope: new BotCommandScopeAllGroupChats(),
                cancellationToken: ct);

            _logger.LogInformation(
                "Команды меню зарегистрированы: private={PrivateCount}, group={GroupCount}",
                privateCommands.Length,
                groupCommands.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось зарегистрировать команды меню");
        }

        await RestoreCommandsMenuButtonAsync(ct);
    }

    private async Task RestoreCommandsMenuButtonAsync(CancellationToken ct)
    {
        try
        {
            await _bot.SetChatMenuButton(
                chatId: null,
                menuButton: new MenuButtonCommands(),
                cancellationToken: ct);

            _logger.LogInformation("Кнопка меню Telegram возвращена к списку команд");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось вернуть кнопку меню Telegram к списку команд");
        }
    }

    private Task HandleErrorAsync(
        ITelegramBotClient bot,
        Exception ex,
        HandleErrorSource source,
        CancellationToken ct)
    {
        _logger.LogError(ex, "Ошибка polling. Источник: {Source}", source);
        return Task.CompletedTask;
    }
}
