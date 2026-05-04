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
    private readonly BotIdentityService _identity;
    private readonly ILogger<BotService> _logger;

    private CancellationTokenSource? _cts;

    public BotService(
        ITelegramBotClient bot,
        UpdateRouter router,
        BotIdentityService identity,
        ILogger<BotService> logger)
    {
        _bot = bot;
        _router = router;
        _identity = identity;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var me = await _bot.GetMe(cancellationToken);
        _identity.SetUsername(me.Username);
        _logger.LogInformation("Р‘РѕС‚ Р·Р°РїСѓС‰РµРЅ: @{Username} (ID: {Id})", me.Username, me.Id);

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

        _logger.LogInformation("Long polling Р·Р°РїСѓС‰РµРЅ. РћР¶РёРґР°СЋ СЃРѕРѕР±С‰РµРЅРёР№...");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("РћСЃС‚Р°РЅРѕРІРєР° Р±РѕС‚Р°...");
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        var privateCommands = new[]
        {
            new BotCommand { Command = "miniapp", Description = "РћС‚РєСЂС‹С‚СЊ mini app" },
            new BotCommand { Command = "add_homework", Description = "Р”РѕР±Р°РІРёС‚СЊ Р”Р—" },
            new BotCommand { Command = "homework", Description = "Р”РѕРјР°С€РЅРёРµ Р·Р°РґР°РЅРёСЏ" },
            new BotCommand { Command = "homework_settings", Description = "Configure homework subjects" },
            new BotCommand { Command = "reminders", Description = "РќР°РїРѕРјРёРЅР°РЅРёСЏ" },
            new BotCommand { Command = "plan", Description = "РЈРїСЂР°РІР»РµРЅРёРµ Р·Р°РґР°С‡Р°РјРё" },
            new BotCommand { Command = "schedule", Description = "РњРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ Р·Р°РЅСЏС‚РёР№" },
            new BotCommand { Command = "timer", Description = "Р—Р°РїСѓСЃС‚РёС‚СЊ С‚Р°Р№РјРµСЂ СѓС‡РµР±С‹" },
            new BotCommand { Command = "rest", Description = "Р—Р°РїСѓСЃС‚РёС‚СЊ С‚Р°Р№РјРµСЂ РѕС‚РґС‹С…Р°" },
            new BotCommand { Command = "stop", Description = "РћСЃС‚Р°РЅРѕРІРёС‚СЊ С‚Р°Р№РјРµСЂ" },
            new BotCommand { Command = "help", Description = "РЎРїРёСЃРѕРє РєРѕРјР°РЅРґ" }
        };

        var groupCommands = new[]
        {
            new BotCommand { Command = "add_homework", Description = "Р”РѕР±Р°РІРёС‚СЊ РѕР±С‰РµРµ Р”Р—" },
            new BotCommand { Command = "homework", Description = "РћР±С‰РёР№ СЃРїРёСЃРѕРє Р”Р—" },
            new BotCommand { Command = "homework_settings", Description = "Configure homework subjects" },
            new BotCommand { Command = "reminders", Description = "РќР°РїРѕРјРёРЅР°РЅРёСЏ РІ РіСЂСѓРїРїСѓ" },
            new BotCommand { Command = "schedule", Description = "Р Р°СЃРїРёСЃР°РЅРёРµ РіСЂСѓРїРїС‹" },
            new BotCommand { Command = "help", Description = "РЎРїРёСЃРѕРє РєРѕРјР°РЅРґ" }
        };

        try
        {
            await _bot.SetMyCommands(privateCommands, cancellationToken: ct);
            await _bot.SetMyCommands(
                groupCommands,
                scope: new BotCommandScopeAllGroupChats(),
                cancellationToken: ct);

            _logger.LogInformation(
                "РљРѕРјР°РЅРґС‹ РјРµРЅСЋ Р·Р°СЂРµРіРёСЃС‚СЂРёСЂРѕРІР°РЅС‹: private={PrivateCount}, group={GroupCount}",
                privateCommands.Length,
                groupCommands.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "РќРµ СѓРґР°Р»РѕСЃСЊ Р·Р°СЂРµРіРёСЃС‚СЂРёСЂРѕРІР°С‚СЊ РєРѕРјР°РЅРґС‹ РјРµРЅСЋ");
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

            _logger.LogInformation("РљРЅРѕРїРєР° РјРµРЅСЋ Telegram РІРѕР·РІСЂР°С‰РµРЅР° Рє СЃРїРёСЃРєСѓ РєРѕРјР°РЅРґ");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "РќРµ СѓРґР°Р»РѕСЃСЊ РІРµСЂРЅСѓС‚СЊ РєРЅРѕРїРєСѓ РјРµРЅСЋ Telegram Рє СЃРїРёСЃРєСѓ РєРѕРјР°РЅРґ");
        }
    }

    private Task HandleErrorAsync(
        ITelegramBotClient bot,
        Exception ex,
        HandleErrorSource source,
        CancellationToken ct)
    {
        _logger.LogError(ex, "РћС€РёР±РєР° polling. РСЃС‚РѕС‡РЅРёРє: {Source}", source);
        return Task.CompletedTask;
    }
}


