using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;
using System.Net;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// РћР±СЂР°Р±РѕС‚С‡РёРє РєРѕРјР°РЅРґ (/start, /help, /timer, /rest, /plan, /stop, /schedule).
/// РљР°Р¶РґС‹Р№ РјРµС‚РѕРґ СЃРѕРѕС‚РІРµС‚СЃС‚РІСѓРµС‚ РѕРґРЅРѕР№ РєРѕРјР°РЅРґРµ.
/// </summary>
public class CommandHandler
{
    private const string MiniAppLaunchMessageText = "РћС‚РєСЂРѕР№ mini app РїРѕ РєРЅРѕРїРєРµ РЅРёР¶Рµ.";

    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly TimerService _timers;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ReminderSettingsService _reminders;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;
    private readonly UserFeatureIntroService _featureIntros;
    private readonly BotVisitLogService _visits;
    private readonly string? _webAppUrl;

    public CommandHandler(
        ITelegramBotClient bot,
        SessionService sessions,
        TimerService timers,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        ReminderSettingsService reminders,
        HomeworkSubjectPreferencesService homeworkSubjects,
        UserFeatureIntroService featureIntros,
        BotVisitLogService visits,
        IConfiguration configuration)
    {
        _bot = bot;
        _sessions = sessions;
        _timers = timers;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _reminders = reminders;
        _homeworkSubjects = homeworkSubjects;
        _featureIntros = featureIntros;
        _visits = visits;
        _webAppUrl = ResolveWebAppUrl(configuration);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /start
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РџСЂРёРІРµС‚СЃС‚РІРёРµ РїСЂРё РїРµСЂРІРѕРј Р·Р°РїСѓСЃРєРµ РёР»Рё РїРµСЂРµР·Р°РїСѓСЃРєРµ</summary>
    public async Task HandleStartAsync(Message msg, CancellationToken ct)
    {
        _visits.RecordVisit(msg.From!);

        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;

        var selection = _scheduleSelections.Get(userId);
        if (selection is not null)
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (group is not null)
            {
                ApplySelectionToSession(session, group, selection.SubGroup);

                await _bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "рџ‘‹ <b>РЎ РІРѕР·РІСЂР°С‰РµРЅРёРµРј!</b>\n\n" +
                          "РЇ СѓР¶Рµ РїРѕРјРЅСЋ С‚РІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ:\n" +
                          $"<b>{Escape(FormatGroupTitle(group, selection.SubGroup))}</b>.\n\n" +
                          "РњРѕР¶РµС€СЊ СЃСЂР°Р·Сѓ РїРµСЂРµР№С‚Рё Рє РЅСѓР¶РЅРѕРјСѓ:\n" +
                          "рџ“… /schedule вЂ” РїР°СЂС‹ РЅР° РґРµРЅСЊ\n" +
                          "рџ“ќ /homework вЂ” РґРѕРјР°С€РЅРёРµ Р·Р°РґР°РЅРёСЏ\n" +
                          "вћ• /add_homework вЂ” РґРѕР±Р°РІРёС‚СЊ РЅРѕРІРѕРµ Р”Р—\n" +
                          "рџ“‹ /plan вЂ” Р»РёС‡РЅС‹Рµ РґРµР»Р° СЃ РґРµРґР»Р°Р№РЅР°РјРё\n" +
                          "вЏ± /timer вЂ” СЃС„РѕРєСѓСЃРёСЂРѕРІР°С‚СЊСЃСЏ РЅР° СѓС‡С‘Р±Рµ",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildMiniAppLinkMarkup(),
                    cancellationToken: ct);

                return;
            }

            _scheduleSelections.Delete(userId);
        }

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "рџ‘‹ <b>РџСЂРёРІРµС‚! РЇ РїРѕРјРѕРіСѓ С‚РµР±Рµ СЃР»РµРґРёС‚СЊ Р·Р° СЂР°СЃРїРёСЃР°РЅРёРµРј, РґРѕРјР°С€РєР°РјРё Рё Р»РёС‡РЅС‹РјРё РґРµР»Р°РјРё.</b>\n\n" +
                       "Р”Р°РІР°Р№ СЃРЅР°С‡Р°Р»Р° РЅР°СЃС‚СЂРѕРёРј СЂР°СЃРїРёСЃР°РЅРёРµ:\n" +
                       "1. РќР°Р¶РјРё /schedule\n" +
                       "2. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ, РєСѓСЂСЃ Рё РїРѕРґРіСЂСѓРїРїСѓ\n" +
                       "3. РџРѕСЃР»Рµ СЌС‚РѕРіРѕ СЏ Р·Р°РєСЂРµРїР»СЋ СЂР°СЃРїРёСЃР°РЅРёРµ Р·Р° С‚РѕР±РѕР№\n\n" +
                       "РљРѕРіРґР° СЂР°СЃРїРёСЃР°РЅРёРµ Р±СѓРґРµС‚ РІС‹Р±СЂР°РЅРѕ:\n" +
                       "рџ“љ /add_homework вЂ” Р”Р— РїРѕ РїСЂРµРґРјРµС‚Р°Рј\n" +
                       "рџ“‹ /plan вЂ” Р»РёС‡РЅС‹Рµ РґРµР»Р° СЃ РґР°С‚РѕР№ Рё РІСЂРµРјРµРЅРµРј\n" +
                       "вЏ± /timer вЂ” С‚Р°Р№РјРµСЂ СѓС‡С‘Р±С‹",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);

    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /help
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РЎРїСЂР°РІРєР° РїРѕ РІСЃРµРј РєРѕРјР°РЅРґР°Рј</summary>
    public async Task HandleHelpAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      "рџ“– <b>РЎРїРёСЃРѕРє РєРѕРјР°РЅРґ:</b>\n\n" +
                       "вЏ± <b>РўР°Р№РјРµСЂ СѓС‡С‘Р±С‹:</b>\n" +
                       "/timer вЂ” Р·Р°РїСѓСЃС‚РёС‚СЊ С‚Р°Р№РјРµСЂ (25/30/45/60 РјРёРЅ РёР»Рё СЃРІРѕС‘)\n" +
                       "/stop вЂ” РѕСЃС‚Р°РЅРѕРІРёС‚СЊ С‚РµРєСѓС‰РёР№ С‚Р°Р№РјРµСЂ\n\n" +
                       "в• <b>РћС‚РґС‹С…:</b>\n" +
                       "/rest вЂ” Р·Р°РїСѓСЃС‚РёС‚СЊ С‚Р°Р№РјРµСЂ РѕС‚РґС‹С…Р°\n\n" +
                       "рџ“љ <b>Р”РѕРјР°С€РЅРёРµ Р·Р°РґР°РЅРёСЏ:</b>\n" +
                       "/add_homework вЂ” РґРѕР±Р°РІРёС‚СЊ Р”Р— РїРѕ РїСЂРµРґРјРµС‚Сѓ РёР· СЂР°СЃРїРёСЃР°РЅРёСЏ\n" +
                       "/homework вЂ” РїРѕСЃРјРѕС‚СЂРµС‚СЊ Р”Р— Рё Р·Р°РґР°С‡Рё\n" +
                       "/reminders вЂ” РЅР°СЃС‚СЂРѕРёС‚СЊ РЅР°РїРѕРјРёРЅР°РЅРёСЏ\n\n" +
                       "рџ“‹ <b>РџР»Р°РЅРёСЂРѕРІР°РЅРёРµ:</b>\n" +
                       "/plan вЂ” СѓРїСЂР°РІР»РµРЅРёРµ Р·Р°РґР°С‡Р°РјРё\n\n" +
                       "рџ“… <b>Р Р°СЃРїРёСЃР°РЅРёРµ:</b>\n" +
                       "/schedule вЂ” РјРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ Р·Р°РЅСЏС‚РёР№\n\n" +
                       "вќ“ /help вЂ” СЌС‚Р° СЃРїСЂР°РІРєР°",
            parseMode: ParseMode.Html,
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /timer
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РџРѕРєР°Р·Р°С‚СЊ РјРµРЅСЋ РІС‹Р±РѕСЂР° РґР»РёС‚РµР»СЊРЅРѕСЃС‚Рё СЂР°Р±РѕС‡РµРіРѕ С‚Р°Р№РјРµСЂР°</summary>
    public async Task HandleMiniAppAsync(Message msg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl))
        {
            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Mini app РїРѕРєР° РЅРµ РЅР°СЃС‚СЂРѕРµРЅ. РЈРєР°Р¶Рё РїСѓР±Р»РёС‡РЅС‹Р№ WebAppUrl РІ РєРѕРЅС„РёРіСѓСЂР°С†РёРё Р±РѕС‚Р°.",
                cancellationToken: ct);
            return;
        }

        var launchMessage = await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: MiniAppLaunchMessageText,
            replyMarkup: BuildMiniAppLinkMarkup(),
            cancellationToken: ct);

        await TryPinMiniAppMessageAsync(msg.Chat.Id, launchMessage, ct);
    }

    public async Task HandleTimerAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        // Р•СЃР»Рё СѓР¶Рµ РёРґС‘С‚ С‚Р°Р№РјРµСЂ вЂ” СЃРѕРѕР±С‰Р°РµРј РїРѕР»СЊР·РѕРІР°С‚РµР»СЋ
        string prefix = string.Empty;
        if (session.ActiveTimer is not null)
        {
            var remaining = session.ActiveTimer.Remaining;
            var typeLabel = session.ActiveTimer.Type == TimerType.Work ? "СЂР°Р±РѕС‡РёР№" : "РѕС‚РґС‹С…";
            prefix = $"вљ пёЏ РЈР¶Рµ РёРґС‘С‚ С‚Р°Р№РјРµСЂ <b>({typeLabel})</b>, РѕСЃС‚Р°Р»РѕСЃСЊ: " +
                     $"<b>{(int)remaining.TotalMinutes} РјРёРЅ {remaining.Seconds} СЃРµРє</b>\n" +
                     $"Р’С‹Р±РµСЂРё РЅРѕРІС‹Р№, С‡С‚РѕР±С‹ Р·Р°РјРµРЅРёС‚СЊ С‚РµРєСѓС‰РёР№:\n\n";
        }

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        prefix + "вЏ± <b>Р’С‹Р±РµСЂРё РґР»РёС‚РµР»СЊРЅРѕСЃС‚СЊ СЂР°Р±РѕС‡РµРіРѕ С‚Р°Р№РјРµСЂР°:</b>",
            parseMode:   ParseMode.Html,
            replyMarkup: BuildTimerKeyboard(),
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /rest
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РџРѕРєР°Р·Р°С‚СЊ РјРµРЅСЋ РІС‹Р±РѕСЂР° РґР»РёС‚РµР»СЊРЅРѕСЃС‚Рё РѕС‚РґС‹С…Р°</summary>
    public async Task HandleRestAsync(Message msg, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        "в• <b>Р’С‹Р±РµСЂРё РґР»РёС‚РµР»СЊРЅРѕСЃС‚СЊ РїРµСЂРµСЂС‹РІР°:</b>",
            parseMode:   ParseMode.Html,
            replyMarkup: BuildRestKeyboard(),
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /stop
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>Р”РѕСЃСЂРѕС‡РЅРѕ РѕСЃС‚Р°РЅРѕРІРёС‚СЊ Р°РєС‚РёРІРЅС‹Р№ С‚Р°Р№РјРµСЂ</summary>
    public async Task HandleStopAsync(Message msg, CancellationToken ct)
    {
        var stopped = _timers.StopTimer(msg.From!.Id);

        var text = stopped
            ? "вЏ№ РўР°Р№РјРµСЂ <b>РѕСЃС‚Р°РЅРѕРІР»РµРЅ</b>. РљРѕРіРґР° Р±СѓРґРµС€СЊ РіРѕС‚РѕРІ вЂ” Р·Р°РїСѓСЃРєР°Р№ СЃРЅРѕРІР°!"
            : "в„№пёЏ РќРµС‚ Р°РєС‚РёРІРЅРѕРіРѕ С‚Р°Р№РјРµСЂР°.";

        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /plan
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РњРµРЅСЋ СѓРїСЂР°РІР»РµРЅРёСЏ Р»РёС‡РЅС‹РјРё РґРµР»Р°РјРё.</summary>
    public async Task HandlePlanAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        var userId = msg.From!.Id;
        var pending = session.Tasks.Count(t => !t.IsCompleted && TaskSubjects.IsPersonal(t.Subject));
        var shouldShowIntro = !_featureIntros.HasSeenPlanIntro(userId);

        var text = BuildPlanMenuText(pending, shouldShowIntro);
        if (shouldShowIntro)
            _featureIntros.MarkPlanIntroSeen(userId);

        await _bot.SendMessage(
            chatId:      msg.Chat.Id,
            text:        text,
            parseMode:   ParseMode.Html,
            replyMarkup: BuildPlanKeyboard(),
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /add_homework
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    public async Task HandleAddHomeworkAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);

        if (!TryGetAllScheduleEntriesForUser(userId, out _, out _, out var entries))
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРё СЃРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ С‡РµСЂРµР· /schedule: СѓРєР°Р¶Рё РЅР°РїСЂР°РІР»РµРЅРёРµ, РєСѓСЂСЃ Рё РїРѕРґРіСЂСѓРїРїСѓ. РџРѕСЃР»Рµ СЌС‚РѕРіРѕ СЏ РїРѕРєР°Р¶Сѓ РїСЂРµРґРјРµС‚С‹ Рё СЃРјРѕРіСѓ РґРѕР±Р°РІР»СЏС‚СЊ Р”Р— СЃ РґРµРґР»Р°Р№РЅР°РјРё.",
                cancellationToken: ct);
            return;
        }

        var subjects = GetHomeworkSubjects(entries);

        if (subjects.Count == 0)
        {
            session.State = UserState.Idle;
            session.DraftTask = null;
            session.HomeworkSubjectChoices.Clear();
            session.HomeworkLessonTypeChoices.Clear();

            await _bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Р’ С‚РІРѕС‘Рј СЂР°СЃРїРёСЃР°РЅРёРё РїРѕРєР° РЅРµС‚ РїСЂРµРґРјРµС‚РѕРІ РґР»СЏ РІС‹Р±РѕСЂР°.",
                cancellationToken: ct);
            return;
        }

        await SendHomeworkSubjectChoiceAsync(msg.Chat.Id, userId, session, subjects, showAll: false, ct);
    }

    private async Task SendHomeworkSubjectChoiceAsync(
        long chatId,
        long userId,
        UserSession session,
        List<string> allSubjects,
        bool showAll,
        CancellationToken ct)
    {
        session.State = UserState.Idle;
        session.DraftTask = null;
        session.HomeworkSubjectChoices.Clear();
        session.HomeworkLessonTypeChoices.Clear();

        var preferences = _homeworkSubjects.Get(userId);
        var favoriteSubjects = preferences.FavoriteSubjects
            .Where(favorite => allSubjects.Contains(favorite, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var visibleSubjects = preferences.IsConfigured && !showAll
            ? favoriteSubjects
            : allSubjects;

        var buttons = visibleSubjects
            .Select((subject, index) =>
            {
                var key = index.ToString();
                session.HomeworkSubjectChoices[key] = subject;
                return (subject, $"hw_subject_{key}");
            })
            .ToList();

        if (!showAll && preferences.IsConfigured)
            buttons.Add(("рџ‘Ђ РџРѕРєР°Р·Р°С‚СЊ РІСЃРµ", "hw_show_all"));

        buttons.Add(("вљ™пёЏ РќР°СЃС‚СЂРѕРёС‚СЊ", "hw_config"));
        buttons.Add(("рџ”ґ РћС‚РјРµРЅР°", "hw_cancel"));

        var text = visibleSubjects.Count == 0
            ? "рџ“љ <b>Р’ СЃРїРёСЃРєРµ Р”Р— РїРѕРєР° РЅРµС‚ РІС‹Р±СЂР°РЅРЅС‹С… РїСЂРµРґРјРµС‚РѕРІ.</b>\nРќР°Р¶РјРё В«РќР°СЃС‚СЂРѕРёС‚СЊВ» Рё РѕС‚РјРµС‚СЊ РЅСѓР¶РЅС‹Рµ."
            : preferences.IsConfigured || showAll
                ? "рџ“љ <b>Р’С‹Р±РµСЂРё РїСЂРµРґРјРµС‚, РїРѕ РєРѕС‚РѕСЂРѕРјСѓ Р·Р°РґР°Р»Рё Р”Р—:</b>"
                : "рџ“љ <b>Р’С‹Р±РµСЂРё РїСЂРµРґРјРµС‚, РїРѕ РєРѕС‚РѕСЂРѕРјСѓ Р·Р°РґР°Р»Рё Р”Р—:</b>\n\n" +
                  "Р•СЃР»Рё С‚СѓС‚ РµСЃС‚СЊ Р»РёС€РЅРёРµ РїСЂРµРґРјРµС‚С‹, РЅР°Р¶РјРё В«вљ™пёЏ РќР°СЃС‚СЂРѕРёС‚СЊВ» Рё РѕСЃС‚Р°РІСЊ С‚РѕР»СЊРєРѕ РЅСѓР¶РЅС‹Рµ.\n\n" +
                  "РџСЂРµРґРјРµС‚С‹ Р±СѓРґСѓС‚ РёРґС‚Рё РІ С‚РѕРј РїРѕСЂСЏРґРєРµ, РІ РєРѕС‚РѕСЂРѕРј С‚С‹ РёС… РѕС‚РјРµС‚РёС€СЊ.";

        await _bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private static string BuildPlanMenuText(int pending, bool includeIntro)
    {
        var text = pending > 0
            ? $"рџ“‹ <b>Р›РёС‡РЅС‹Р№ РїР»Р°РЅ</b>\nРђРєС‚РёРІРЅС‹С… РґРµР»: <b>{pending}</b>"
            : "рџ“‹ <b>Р›РёС‡РЅС‹Р№ РїР»Р°РЅ</b>\nР”РµР» РїРѕРєР° РЅРµС‚. Р”РѕР±Р°РІСЊ РїРµСЂРІРѕРµ!";

        if (includeIntro)
        {
            text += "\n\nР—РґРµСЃСЊ РјРѕР¶РЅРѕ С…СЂР°РЅРёС‚СЊ РґРµР»Р° РІРЅРµ СѓС‡С‘Р±С‹: СЃС…РѕРґРёС‚СЊ РІ РїРѕР»РёРєР»РёРЅРёРєСѓ, РєСѓРїРёС‚СЊ С‚РµС‚СЂР°РґРё, РЅРµ Р·Р°Р±С‹С‚СЊ СЃРѕР·РІРѕРЅ.\n" +
                    "РЇ РјРѕРіСѓ РїРѕСЃС‚Р°РІРёС‚СЊ РґРµРґР»Р°Р№РЅ СЃ РґР°С‚РѕР№ Рё РІСЂРµРјРµРЅРµРј.";
        }

        return text + "\n\nР§С‚Рѕ РґРµР»Р°РµРј?";
    }

    private static List<string> GetHomeworkSubjects(List<ScheduleEntry> entries)
    {
        return entries
            .Select(e => ScheduleCatalogService.GetHomeworkSubjectTitle(e.Subject))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScheduleCatalogService.GetHomeworkSubjectSortGroup)
            .ThenBy(s => s)
            .ToList();
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /homework
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    public async Task HandleHomeworkAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        await SendHomeworkListAsync(msg.Chat.Id, session, ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /reminders
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    public async Task HandleRemindersAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;

        var settings = _reminders.Get(userId);
        settings.ChatId = msg.Chat.Id;
        _reminders.Save(userId, settings);

        var text = settings.IsEnabled
            ? $"вЏ° <b>РќР°РїРѕРјРёРЅР°РЅРёСЏ РІРєР»СЋС‡РµРЅС‹</b>\n" +
              $"РљР°Р¶РґС‹Р№ РґРµРЅСЊ РІ <b>{settings.TimeText}</b> РїРѕ РњРЎРљ СЏ Р±СѓРґСѓ РїСЂРёСЃС‹Р»Р°С‚СЊ РґРµРґР»Р°Р№РЅС‹ РЅР° Р·Р°РІС‚СЂР°."
            : "вЏ° <b>РќР°РїРѕРјРёРЅР°РЅРёСЏ РІС‹РєР»СЋС‡РµРЅС‹</b>\n" +
              "РњРѕРіСѓ РєР°Р¶РґС‹Р№ РґРµРЅСЊ РїСЂРёСЃС‹Р»Р°С‚СЊ РґРµРґР»Р°Р№РЅС‹ РЅР° Р·Р°РІС‚СЂР° РІ СѓРґРѕР±РЅРѕРµ РІСЂРµРјСЏ.";

        await _bot.SendMessage(
            chatId: msg.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildReminderKeyboard(settings.IsEnabled),
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  /add_schedule  (Рё Р°Р»РёР°СЃ /schedule)
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РђР»РёР°СЃ РґР»СЏ СЃС‚Р°СЂРѕР№ РєРѕРјР°РЅРґС‹: С‚РµРїРµСЂСЊ РѕС‚РєСЂС‹РІР°РµС‚ РІС‹Р±РѕСЂ РіРѕС‚РѕРІРѕРіРѕ СЂР°СЃРїРёСЃР°РЅРёСЏ.</summary>
    public Task HandleAddScheduleAsync(Message msg, CancellationToken ct)
        => HandleScheduleAsync(msg, ct);

    public async Task HandleScheduleAsync(Message msg, CancellationToken ct)
    {
        var userId = msg.From!.Id;
        var session = _sessions.GetOrCreate(userId, msg.From.FirstName);
        session.State = UserState.Idle;

        var selection = _scheduleSelections.Get(userId);
        if (selection is not null)
        {
            var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (group is not null)
            {
                ApplySelectionToSession(session, group, selection.SubGroup);
                await SendSelectedScheduleMenuAsync(msg.Chat.Id, group, selection.SubGroup, ct);
                return;
            }

            _scheduleSelections.Delete(userId);
        }

        await SendDirectionChoiceAsync(msg.Chat.Id, ct);
    }

    private async Task SendDirectionChoiceAsync(long chatId, CancellationToken ct)
    {
        var buttons = _scheduleCatalog.GetDirections()
            .Select(d => ($"{d.ShortTitle} вЂ” {d.DirectionName}", $"sched_dir_{d.DirectionCode}"));

        await _bot.SendMessage(
            chatId: chatId,
            text: "РЁР°Рі 1/3. Р’С‹Р±РµСЂРё РЅР°РїСЂР°РІР»РµРЅРёРµ:",
            replyMarkup: ScheduleKeyboards.SingleColumn(buttons),
            cancellationToken: ct);
    }

    private bool TryGetAllScheduleEntriesForUser(
        long userId,
        out ScheduleGroup? group,
        out int? subGroup,
        out List<ScheduleEntry> entries)
    {
        group = null;
        subGroup = null;
        entries = new List<ScheduleEntry>();

        var selection = _scheduleSelections.Get(userId);
        if (selection is null)
            return false;

        group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
            return false;

        subGroup = selection.SubGroup;
        entries = _scheduleCatalog.GetAllEntriesForSelection(group, subGroup);
        return true;
    }

    private async Task SendHomeworkListAsync(
        long chatId,
        UserSession session,
        CancellationToken ct)
    {
        var view = HomeworkListView.Build(session);
        await _bot.SendMessage(
            chatId: chatId,
            text: view.Text,
            parseMode: ParseMode.Html,
            replyMarkup: view.Keyboard,
            cancellationToken: ct);
    }

    private async Task SendSelectedScheduleMenuAsync(
        long chatId,
        ScheduleGroup group,
        int? subGroup,
        CancellationToken ct)
    {
        var weekLabel = _scheduleCatalog.GetCurrentWeekLabel();

        await _bot.SendMessage(
            chatId: chatId,
            text: $"рџ“… <b>РўРІРѕС‘ СЂР°СЃРїРёСЃР°РЅРёРµ</b>\n" +
                  $"{Escape(FormatGroupTitle(group, subGroup))}\n" +
                  $"РўРµРєСѓС‰Р°СЏ РЅРµРґРµР»СЏ: <b>{weekLabel}</b>\n\n" +
                  "Р§С‚Рѕ РїРѕРєР°Р·Р°С‚СЊ?",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ScheduleMenu,
            cancellationToken: ct);
    }

    private void ApplySelectionToSession(UserSession session, ScheduleGroup group, int? subGroup)
    {
        var weekType = _scheduleCatalog.GetCurrentWeekType();

        session.CurrentWeekType = weekType;
        session.CurrentSubGroup = subGroup;
        session.Schedule = _scheduleCatalog.GetEntriesForSelection(group, subGroup, weekType);
        session.PendingSchedule = null;
    }

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static string FormatGroupTitle(ScheduleGroup group, int? subGroup)
        => subGroup.HasValue ? $"{group.Title}, РїРѕРґРіСЂСѓРїРїР° {subGroup.Value}" : group.Title;

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  РџРѕСЃС‚СЂРѕРёС‚РµР»Рё РєР»Р°РІРёР°С‚СѓСЂ (РїСЂРёРІР°С‚РЅС‹Рµ)
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РљР»Р°РІРёР°С‚СѓСЂР° РІС‹Р±РѕСЂР° СЂР°Р±РѕС‡РµРіРѕ С‚Р°Р№РјРµСЂР°</summary>
    private static InlineKeyboardMarkup BuildTimerKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("вЏ± 25 РјРёРЅ (РџРѕРјРѕРґРѕСЂРѕ)", "timer_25"),
                InlineKeyboardButton.WithCallbackData("вЏ± 30 РјРёРЅ", "timer_30")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("вЏ± 45 РјРёРЅ", "timer_45"),
                InlineKeyboardButton.WithCallbackData("вЏ± 60 РјРёРЅ", "timer_60")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("вњЏпёЏ РЎРІРѕС‘ РІСЂРµРјСЏ", "timer_custom"),
                InlineKeyboardButton.WithCallbackData("вЏ№ РЎС‚РѕРї", "timer_stop")
            }
        });

    /// <summary>РљР»Р°РІРёР°С‚СѓСЂР° РІС‹Р±РѕСЂР° РїРµСЂРµСЂС‹РІР°</summary>
    private static InlineKeyboardMarkup BuildRestKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("в• 5 РјРёРЅ (РєРѕСЂРѕС‚РєРёР№)", "rest_5"),
                InlineKeyboardButton.WithCallbackData("в• 15 РјРёРЅ (СЃСЂРµРґРЅРёР№)", "rest_15")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("рџ›Њ 30 РјРёРЅ (РґР»РёРЅРЅС‹Р№)", "rest_30")
            }
        });

    /// <summary>РљР»Р°РІРёР°С‚СѓСЂР° РјРµРЅСЋ РїР»Р°РЅРёСЂРѕРІР°РЅРёСЏ</summary>
    private static InlineKeyboardMarkup BuildPlanKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("вћ• Р”РѕР±Р°РІРёС‚СЊ РґРµР»Рѕ", "plan_add"),
                InlineKeyboardButton.WithCallbackData("рџ“‹ РџРѕРєР°Р·Р°С‚СЊ РїР»Р°РЅ", "plan_list")
            }
        });

    private InlineKeyboardMarkup? BuildMiniAppLinkMarkup()
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl))
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithWebApp("Mini app", _webAppUrl)
            }
        });
    }

    private async Task TryPinMiniAppMessageAsync(ChatId chatId, Message launchMessage, CancellationToken ct)
    {
        try
        {
            var chat = await _bot.GetChat(chatId, ct);
            var messageToPin = IsMiniAppPinned(chat.PinnedMessage)
                ? chat.PinnedMessage!
                : launchMessage;

            await _bot.PinChatMessage(
                chatId: chatId,
                messageId: messageToPin.Id,
                disableNotification: true,
                cancellationToken: ct);
        }
        catch
        {
            // Р—Р°РєСЂРµРїР»РµРЅРёРµ РЅРµ РєСЂРёС‚РёС‡РЅРѕ: РІ РЅРµРєРѕС‚РѕСЂС‹С… С‡Р°С‚Р°С… Сѓ Р±РѕС‚Р° РјРѕР¶РµС‚ РЅРµ Р±С‹С‚СЊ РїСЂР°РІ.
        }
    }

    private bool IsMiniAppPinned(Message? pinnedMessage)
    {
        if (pinnedMessage is null)
            return false;

        if (string.Equals(
            pinnedMessage.Text?.Trim(),
            MiniAppLaunchMessageText,
            StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_webAppUrl) || pinnedMessage.ReplyMarkup is not InlineKeyboardMarkup markup)
            return false;

        return markup.InlineKeyboard
            .SelectMany(row => row)
            .Any(button => string.Equals(button.WebApp?.Url, _webAppUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static InlineKeyboardMarkup BuildReminderKeyboard(bool enabled)
    {
        if (!enabled)
        {
            return new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("РЈРєР°Р·Р°С‚СЊ РІСЂРµРјСЏ", "rem_set")
                }
            });
        }

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("РР·РјРµРЅРёС‚СЊ РІСЂРµРјСЏ", "rem_set"),
                InlineKeyboardButton.WithCallbackData("Р’С‹РєР»СЋС‡РёС‚СЊ", "rem_off")
            }
        });
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
