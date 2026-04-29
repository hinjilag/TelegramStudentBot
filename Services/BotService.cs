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
    private readonly string? _webAppUrl;

    private CancellationTokenSource? _cts;

    public BotService(
        ITelegramBotClient bot,
        UpdateRouter router,
        ILogger<BotService> logger,
        IConfiguration configuration)
    {
        _bot = bot;
        _router = router;
        _logger = logger;
        _webAppUrl = ResolveWebAppUrl(configuration);
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
        var commands = new[]
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

        try
        {
            await _bot.SetMyCommands(commands, cancellationToken: ct);
            _logger.LogInformation("Команды меню зарегистрированы ({Count} шт.)", commands.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось зарегистрировать команды меню");
        }

        await RegisterMiniAppMenuButtonAsync(ct);
    }

    private async Task RegisterMiniAppMenuButtonAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl))
        {
            _logger.LogInformation("WebAppUrl не настроен, кнопку mini app в меню Telegram пропускаю");
            return;
        }

        try
        {
            await _bot.SetChatMenuButton(
                chatId: null,
                menuButton: new MenuButtonWebApp
                {
                    Text = "Mini app",
                    WebApp = new WebAppInfo(_webAppUrl)
                },
                cancellationToken: ct);

            _logger.LogInformation("Кнопка mini app добавлена в меню Telegram");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось добавить кнопку mini app в меню Telegram");
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

    private static string? ResolveWebAppUrl(IConfiguration configuration)
    {
        var configuredUrl = configuration["WebAppUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
            return configuredUrl;

        var railwayDomain = configuration["RAILWAY_PUBLIC_DOMAIN"];
        if (string.IsNullOrWhiteSpace(railwayDomain))
            return null;

        return $"https://{railwayDomain.TrimEnd('/')}/miniapp/";
    }
}
