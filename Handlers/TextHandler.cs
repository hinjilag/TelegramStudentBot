using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// РћР±СЂР°Р±РѕС‚С‡РёРє С‚РµРєСЃС‚РѕРІС‹С… СЃРѕРѕР±С‰РµРЅРёР№ Рё С„РѕС‚РѕРіСЂР°С„РёР№.
/// Р Р°Р±РѕС‚Р°РµС‚ РєР°Рє РјР°С€РёРЅР° СЃРѕСЃС‚РѕСЏРЅРёР№.
///
/// РџРѕС‚РѕРє Р·Р°РіСЂСѓР·РєРё СЂР°СЃРїРёСЃР°РЅРёСЏ:
///   /add_schedule в†’ WaitingForSchedulePhoto
///   в†’ С„РѕС‚Рѕ в†’ РїР°СЂСЃРёРЅРі в†’ WaitingForScheduleConfirmation (РїРѕРєР°Р·С‹РІР°РµРј СЂР°СЃРїРёСЃР°РЅРёРµ + РєРЅРѕРїРєРё)
///   в†’ [РџРѕРґС‚РІРµСЂРґРёС‚СЊ] в†’ РµСЃР»Рё РµСЃС‚СЊ РґРІРѕР№РЅС‹Рµ РїР°СЂС‹ в†’ WaitingForWeekChoice в†’ [РќРµС‡С‘С‚РЅР°СЏ/Р§С‘С‚РЅР°СЏ] в†’ СЃРѕС…СЂР°РЅРµРЅРѕ
///                  в†’ РµСЃР»Рё РЅРµС‚              в†’ СЃСЂР°Р·Сѓ СЃРѕС…СЂР°РЅСЏРµРј
///   в†’ [РСЃРїСЂР°РІРёС‚СЊ]  в†’ WaitingForScheduleCorrection в†’ РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ РїРёС€РµС‚ РїСЂР°РІРєСѓ в†’ WaitingForScheduleConfirmation
/// </summary>
public class TextHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService     _sessions;
    private readonly TimerService       _timers;
    private readonly ScheduleService    _schedule;

    public TextHandler(
        ITelegramBotClient bot,
        SessionService     sessions,
        TimerService       timers,
        ScheduleService    schedule)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
        _schedule = schedule;
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  РўРµРєСЃС‚РѕРІС‹Рµ СЃРѕРѕР±С‰РµРЅРёСЏ
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

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

            case UserState.WaitingForSchedulePhoto:
                await _bot.SendMessage(
                    msg.Chat.Id,
                    "📸 Жду фотографию расписания.\nЧтобы отменить — /start",
                    cancellationToken: ct);
                break;

            case UserState.WaitingForScheduleConfirmation:
                // РџРѕР»СЊР·РѕРІР°С‚РµР»СЊ РїРёС€РµС‚ С‚РµРєСЃС‚ РІРјРµСЃС‚Рѕ РЅР°Р¶Р°С‚РёСЏ РєРЅРѕРїРєРё
                await _bot.SendMessage(
                    msg.Chat.Id,
                    "Нажми кнопку ниже: ✅ Подтвердить или ✏️ Исправить.",
                    cancellationToken: ct);
                break;

            case UserState.WaitingForScheduleCorrection:
                await HandleScheduleCorrectionAsync(msg, session, text, ct);
                break;

            default:
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "в„№пёЏ РЇ РЅРµ РїРѕРЅРёРјР°СЋ СЌС‚Рѕ СЃРѕРѕР±С‰РµРЅРёРµ.\nРСЃРїРѕР»СЊР·СѓР№ /help РґР»СЏ РїСЂРѕСЃРјРѕС‚СЂР° РєРѕРјР°РЅРґ.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  Р¤РѕС‚РѕРіСЂР°С„РёРё вЂ” РїР°СЂСЃРёРЅРі СЂР°СЃРїРёСЃР°РЅРёСЏ
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>
    /// Р’С‹Р·С‹РІР°РµС‚СЃСЏ РєРѕРіРґР° РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ РїСЂРёСЃС‹Р»Р°РµС‚ С„РѕС‚Рѕ РёР»Рё РёР·РѕР±СЂР°Р¶РµРЅРёРµ-РґРѕРєСѓРјРµРЅС‚.
    /// Р•СЃР»Рё Р±РѕС‚ РІ СЃРѕСЃС‚РѕСЏРЅРёРё WaitingForSchedulePhoto вЂ” СЃРєР°С‡РёРІР°РµС‚ Рё РїР°СЂСЃРёС‚.
    /// </summary>
    public async Task HandlePhotoAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        if (session.State != UserState.WaitingForSchedulePhoto)
        {
            await _bot.SendMessage(
                msg.Chat.Id,
                "📸 Для загрузки расписания сначала используй /add_schedule",
                cancellationToken: ct);
            return;
        }

        // Р’С‹Р±РёСЂР°РµРј fileId: photo (СЃР¶Р°С‚РѕРµ) РёР»Рё document (Р±РµР· СЃР¶Р°С‚РёСЏ)
        string? fileId = null;

        if (msg.Photo is { Length: > 0 })
            fileId = msg.Photo.OrderByDescending(p => p.FileSize ?? 0).First().FileId;
        else if (msg.Document is { MimeType: { } mime } doc && mime.StartsWith("image/"))
            fileId = doc.FileId;

        if (fileId is null)
        {
            await _bot.SendMessage(
                msg.Chat.Id,
                "⚠️ Не удалось получить изображение. Пришли обычное фото или изображение как документ.",
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(msg.Chat.Id, "⏳ Анализирую расписание... Это займет 1-3 минуты.", cancellationToken: ct);

        byte[] imageBytes;
        try { imageBytes = await DownloadFileAsync(fileId, ct); }
        catch (Exception ex)
        {
            await _bot.SendMessage(msg.Chat.Id, $"⚠️ Не удалось скачать фото: {ex.Message}", cancellationToken: ct);
            return;
        }

        List<ScheduleEntry> entries;
        try { entries = await _schedule.ParseScheduleAsync(imageBytes, ct); }
        catch (InvalidOperationException ex)
        {
            session.State = UserState.Idle;
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      $"❌ <b>Ошибка:</b> {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }
        catch (Exception ex)
        {
            session.State = UserState.Idle;
            await _bot.SendMessage(msg.Chat.Id, $"❌ Неожиданная ошибка: {ex.Message}", cancellationToken: ct);
            return;
        }

        if (entries.Count == 0)
        {
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      "😕 <b>Не удалось распознать расписание.</b>\n\n" +
                           "Попробуй:\n• Фото четче и с ровным освещением\n" +
                           "• Чтобы вся таблица была в кадре\n" +
                           "• Отправить изображение как документ (без сжатия)\n\n" +
                           "Используй /add_schedule, чтобы попробовать снова.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            session.State = UserState.Idle;
            return;
        }

        // РџРµСЂРµС…РѕРґРёРј РІ СЂРµР¶РёРј РїРѕРґС‚РІРµСЂР¶РґРµРЅРёСЏ
        session.PendingSchedule = entries;
        session.State           = UserState.WaitingForScheduleConfirmation;

        await SendScheduleConfirmationAsync(msg.Chat.Id, entries, ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  РСЃРїСЂР°РІР»РµРЅРёРµ СЂР°СЃРїРёСЃР°РЅРёСЏ
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>
    /// РџРѕР»СЊР·РѕРІР°С‚РµР»СЊ РѕРїРёСЃС‹РІР°РµС‚ РёСЃРїСЂР°РІР»РµРЅРёРµ С‚РµРєСЃС‚РѕРј.
    /// РџСЂРёРјРµСЂ: "РїРµСЂРІРѕР№ РїР°СЂРѕР№ РІ СЃСЂРµРґСѓ Сѓ РјРµРЅСЏ РЅРµ РјР°С‚ Р°РЅР°Р»РёР·, Р° Р»РёРЅРµР№РЅР°СЏ Р°Р»РіРµР±СЂР°"
    /// РР»Рё:    "Р·Р°РјРµРЅРё С„РёР·РёРєСѓ РЅР° С…РёРјРёСЋ РІРѕ РІС‚РѕСЂРЅРёРє"
    /// </summary>
    private async Task HandleScheduleCorrectionAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (session.PendingSchedule is null || session.PendingSchedule.Count == 0)
        {
            session.State = UserState.Idle;
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Нет расписания для исправления.", cancellationToken: ct);
            return;
        }

        var (updated, applied) = ApplyCorrection(session.PendingSchedule, text);
        session.PendingSchedule = updated;
        session.State           = UserState.WaitingForScheduleConfirmation;

        if (!applied)
        {
            // РќРµ СЃРјРѕРіР»Рё СЂР°СЃРїРѕР·РЅР°С‚СЊ РёСЃРїСЂР°РІР»РµРЅРёРµ вЂ” РїСЂРѕСЃРёРј СѓС‚РѕС‡РЅРёС‚СЊ
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      "🤔 Не смог разобрать исправление.\n\n" +
                           "Попробуй так:\n" +
                           "<i>«3 парой во вторник не физика, а матан»</i>\n" +
                           "<i>«замени английский на историю в пятницу 2 пара»</i>\n" +
                           "<i>«убери 5 пару в среду»</i>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            // РћСЃС‚Р°С‘РјСЃСЏ РІ WaitingForScheduleConfirmation вЂ” РїРѕРєР°Р·С‹РІР°РµРј С‚РµРєСѓС‰РµРµ СЃРѕСЃС‚РѕСЏРЅРёРµ СЃРЅРѕРІР°
        }
        else
        {
            await _bot.SendMessage(
                msg.Chat.Id, "✏️ Исправление применено!", cancellationToken: ct);
        }

        // Р’ Р»СЋР±РѕРј СЃР»СѓС‡Р°Рµ РїРѕРєР°Р·С‹РІР°РµРј Р°РєС‚СѓР°Р»СЊРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ РЅР° РїРѕРґС‚РІРµСЂР¶РґРµРЅРёРµ
        await SendScheduleConfirmationAsync(msg.Chat.Id, updated, ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  Р’СЃРїРѕРјРѕРіР°С‚РµР»СЊРЅС‹Рµ: РѕС‚РѕР±СЂР°Р¶РµРЅРёРµ СЂР°СЃРїРёСЃР°РЅРёСЏ РЅР° РїРѕРґС‚РІРµСЂР¶РґРµРЅРёРµ
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>РћС‚РїСЂР°РІРёС‚СЊ СЂР°СЃРїРёСЃР°РЅРёРµ РЅР° РїРѕРґС‚РІРµСЂР¶РґРµРЅРёРµ СЃ РєРЅРѕРїРєР°РјРё [РџРѕРґС‚РІРµСЂРґРёС‚СЊ] [РСЃРїСЂР°РІРёС‚СЊ]</summary>
    internal async Task SendScheduleConfirmationAsync(
        long chatId, List<ScheduleEntry> entries, CancellationToken ct)
    {
        var summaryText = ScheduleService.FormatSchedule(entries);

        // Telegram РѕРіСЂР°РЅРёС‡РёРІР°РµС‚ СЃРѕРѕР±С‰РµРЅРёРµ РґРѕ 4096 СЃРёРјРІРѕР»РѕРІ
        const int limit = 3800;
        var header  = $"рџ“… <b>Р Р°СЃРїРѕР·РЅР°РЅРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ</b> ({entries.Count} РїР°СЂ):\n\n";
        var body    = summaryText.Length > limit
            ? summaryText[..limit] + "\n<i>... (РѕР±СЂРµР·Р°РЅРѕ)</i>"
            : summaryText;
        var footer  = "\n\n<b>Р’СЃС‘ РІРµСЂРЅРѕ?</b>";

        await _bot.SendMessage(
            chatId:      chatId,
            text:        header + body + footer,
            parseMode:   ParseMode.Html,
            replyMarkup: ScheduleKeyboards.Confirmation,
            cancellationToken: ct);
    }

    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ
    //  РџР°СЂСЃРµСЂ РёСЃРїСЂР°РІР»РµРЅРёР№
    // в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ

    /// <summary>
    /// РџСЂРёРјРµРЅСЏРµС‚ С‚РµРєСЃС‚РѕРІРѕРµ РёСЃРїСЂР°РІР»РµРЅРёРµ Рє СЃРїРёСЃРєСѓ Р·Р°РїРёСЃРµР№ СЂР°СЃРїРёСЃР°РЅРёСЏ.
    /// Р’РѕР·РІСЂР°С‰Р°РµС‚ (РѕР±РЅРѕРІР»С‘РЅРЅС‹Р№ СЃРїРёСЃРѕРє, Р±С‹Р»Рѕ Р»Рё РёСЃРїСЂР°РІР»РµРЅРёРµ РїСЂРёРјРµРЅРµРЅРѕ).
    /// </summary>
    private static (List<ScheduleEntry> Entries, bool Applied) ApplyCorrection(
        List<ScheduleEntry> entries, string text)
    {
        var result = entries
            .Select(e => new ScheduleEntry
            {
                DayOfWeek = e.DayOfWeek,
                LessonNumber = e.LessonNumber,
                Subject = e.Subject,
                WeekType = e.WeekType
            })
            .ToList();
        var instructions = Regex
            .Split(text, @"[\r\n;]+")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (instructions.Count == 0)
            instructions.Add(text.Trim());
        var appliedAny = false;
        foreach (var instruction in instructions)
            appliedAny |= ApplySingleInstruction(result, instruction);
        return (result, appliedAny);
    }
    private static bool ApplySingleInstruction(List<ScheduleEntry> entries, string instruction)
    {
        var lower = instruction.ToLowerInvariant();
        var day = ExtractDay(lower);
        var lesson = ExtractLesson(lower);

        var remove = Regex.IsMatch(lower,
            @"\b(\u0443\u0434\u0430\u043b\u0438|\u0443\u0434\u0430\u043b\u0438\u0442\u044c|\u0443\u0431\u0435\u0440\u0438|\u0443\u0431\u0440\u0430\u0442\u044c|\u0431\u0435\u0437\s+\u043f\u0430\u0440\u044b|\u043d\u0435\u0442\s+\u043f\u0430\u0440\u044b|\u043e\u043a\u043d\u043e)\b");

        string? oldSubject = null;
        string? newSubject = null;

        var m = Regex.Match(lower, @"\b\u043d\u0435\s+(.+?)[,\s]+\u0430\s+(.+)");
        if (m.Success)
        {
            oldSubject = m.Groups[1].Value.Trim();
            newSubject = m.Groups[2].Value.Trim();
        }

        if (newSubject is null)
        {
            m = Regex.Match(lower,
                @"\b(?:\u0437\u0430\u043c\u0435\u043d\u0438|\u043f\u043e\u043c\u0435\u043d\u044f\u0439|\u0438\u0437\u043c\u0435\u043d\u0438)\s+(.+?)\s+\u043d\u0430\s+(.+?)(?:\s+\b(?:\u0432|\u0432\u043e)\b\s+|$)");
            if (m.Success)
            {
                oldSubject = m.Groups[1].Value.Trim();
                newSubject = m.Groups[2].Value.Trim();
            }
        }

        if (newSubject is null)
        {
            m = Regex.Match(lower,
                @"\b\u0432\u043c\u0435\u0441\u0442\u043e\s+(.+?)\s+(?:\u043f\u043e\u0441\u0442\u0430\u0432\u044c\s+)?(.+?)(?:\s+\b(?:\u0432|\u0432\u043e)\b\s+|$)");
            if (m.Success)
            {
                oldSubject = m.Groups[1].Value.Trim();
                newSubject = m.Groups[2].Value.Trim();
            }
        }

        if (newSubject is null && !remove && day.HasValue && lesson.HasValue)
        {
            m = Regex.Match(lower,
                @"\b(?:\u043f\u043e\u0441\u0442\u0430\u0432\u044c|\u0441\u0434\u0435\u043b\u0430\u0439|\u0431\u0443\u0434\u0435\u0442|\u043f\u0443\u0441\u0442\u044c\s+\u0431\u0443\u0434\u0435\u0442)\s+(.+)$");
            if (m.Success)
                newSubject = m.Groups[1].Value.Trim();
        }

        var targets = entries
            .Where(e => !day.HasValue || e.DayOfWeek == day.Value)
            .Where(e => !lesson.HasValue || e.LessonNumber == lesson.Value)
            .Where(e => oldSubject is null || e.Subject.Contains(oldSubject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targets.Count == 0)
            return false;

        if (remove)
        {
            foreach (var target in targets)
                entries.Remove(target);
            return true;
        }

        if (string.IsNullOrWhiteSpace(newSubject))
            return false;

        var normalized = NormalizeSubject(newSubject);
        foreach (var target in targets)
            target.Subject = normalized;

        return true;
    }

    private static int? ExtractDay(string lower)
    {
        if (lower.Contains("\u043f\u043e\u043d\u0435\u0434\u0435\u043b")) return 1;
        if (lower.Contains("\u0432\u0442\u043e\u0440\u043d\u0438\u043a") || Regex.IsMatch(lower, @"\b\u0432\u0442\b")) return 2;
        if (lower.Contains("\u0441\u0440\u0435\u0434") || Regex.IsMatch(lower, @"\b\u0441\u0440\b")) return 3;
        if (lower.Contains("\u0447\u0435\u0442\u0432\u0435\u0440\u0433") || Regex.IsMatch(lower, @"\b\u0447\u0442\b")) return 4;
        if (lower.Contains("\u043f\u044f\u0442\u043d\u0438\u0446") || Regex.IsMatch(lower, @"\b\u043f\u0442\b")) return 5;
        if (lower.Contains("\u0441\u0443\u0431\u0431\u043e\u0442") || Regex.IsMatch(lower, @"\b\u0441\u0431\b")) return 6;
        if (lower.Contains("\u0432\u043e\u0441\u043a\u0440\u0435\u0441") || Regex.IsMatch(lower, @"\b\u0432\u0441\b")) return 7;
        return null;
    }

    private static int? ExtractLesson(string lower)
    {
        var m = Regex.Match(lower,
            @"\b([1-9])\s*(?:-?\s*(?:\u044f|\u0439))?\s*(?:\u043f\u0430\u0440\u0430|\u0443\u0440\u043e\u043a)\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;

        if (Regex.IsMatch(lower, @"\b\u043f\u0435\u0440(\u0432|\u0432\u043e)\w*")) return 1;
        if (Regex.IsMatch(lower, @"\b\u0432\u0442\u043e\u0440\w*")) return 2;
        if (Regex.IsMatch(lower, @"\b\u0442\u0440\u0435\u0442\w*")) return 3;
        if (Regex.IsMatch(lower, @"\b\u0447\u0435\u0442\u0432[\u0435\u0451]\u0440\u0442\w*")) return 4;
        if (Regex.IsMatch(lower, @"\b\u043f\u044f\u0442\w*")) return 5;
        if (Regex.IsMatch(lower, @"\b\u0448\u0435\u0441\u0442\w*")) return 6;
        if (Regex.IsMatch(lower, @"\b\u0441\u0435\u0434\u044c\u043c\w*")) return 7;
        if (Regex.IsMatch(lower, @"\b\u0432\u043e\u0441\u044c\u043c\w*")) return 8;
        return null;
    }
    private static string NormalizeSubject(string subject)
    {
        var s = Regex.Replace(subject, @"\s+", " ").Trim(' ', '.', ',', ';', ':');
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return char.ToUpper(s[0]) + s[1..];
    }
    private async Task HandleTaskTitleAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "вљ пёЏ РќР°Р·РІР°РЅРёРµ РЅРµ РјРѕР¶РµС‚ Р±С‹С‚СЊ РїСѓСЃС‚С‹Рј. Р’РІРµРґРё РЅР°Р·РІР°РЅРёРµ Р·Р°РґР°С‡Рё:", cancellationToken: ct);
            return;
        }
        session.DraftTask = new StudyTask { Title = text };
        session.State     = UserState.WaitingForTaskSubject;
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"вњ… РќР°Р·РІР°РЅРёРµ: <b>{text}</b>\n\nРўРµРїРµСЂСЊ РІРІРµРґРё <b>РїСЂРµРґРјРµС‚</b> (РЅР°РїСЂРёРјРµСЂ: РњР°С‚РµРјР°С‚РёРєР°):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskSubjectAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "вљ пёЏ РџСЂРµРґРјРµС‚ РЅРµ РјРѕР¶РµС‚ Р±С‹С‚СЊ РїСѓСЃС‚С‹Рј.", cancellationToken: ct);
            return;
        }
        session.DraftTask!.Subject = text;
        session.State              = UserState.WaitingForTaskDeadline;
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"вњ… РџСЂРµРґРјРµС‚: <b>{text}</b>\n\n" +
                       "Р’РІРµРґРё <b>РґРµРґР»Р°Р№РЅ</b> РІ С„РѕСЂРјР°С‚Рµ Р”Р”.РњРњ.Р“Р“Р“Р“\n(РёР»Рё РЅР°РїРёС€Рё <b>РЅРµС‚</b>):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskDeadlineAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (text.Equals("РЅРµС‚", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no",  StringComparison.OrdinalIgnoreCase) ||
            text == "-")
        {
            session.DraftTask!.Deadline = null;
        }
        else
        {
            if (!DateTime.TryParseExact(text,
                    new[] { "dd.MM.yyyy", "dd/MM/yyyy", "d.M.yyyy" },
                    null, System.Globalization.DateTimeStyles.None, out var deadline))
            {
                await _bot.SendMessage(
                    chatId:    msg.Chat.Id,
                    text:      "вљ пёЏ РќРµРІРµСЂРЅС‹Р№ С„РѕСЂРјР°С‚ РґР°С‚С‹. РСЃРїРѕР»СЊР·СѓР№ Р”Р”.РњРњ.Р“Р“Р“Р“\nРР»Рё РЅР°РїРёС€Рё <b>РЅРµС‚</b>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;
            }
            session.DraftTask!.Deadline = deadline;
        }

        var task = session.DraftTask!;
        session.Tasks.Add(task);
        session.DraftTask = null;
        session.State     = UserState.Idle;

        var dl = task.Deadline.HasValue ? task.Deadline.Value.ToString("dd.MM.yyyy") : "РЅРµ Р·Р°РґР°РЅ";
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"рџЋ‰ <b>Р—Р°РґР°С‡Р° РґРѕР±Р°РІР»РµРЅР°!</b>\n\nрџ“Њ <b>{task.Title}</b>\nрџ“љ {task.Subject}\nрџ“… {dl}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleCustomTimerAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (!int.TryParse(text, out int minutes) || minutes < 1 || minutes > 300)
        {
            await _bot.SendMessage(msg.Chat.Id, "вљ пёЏ Р’РІРµРґРё С‡РёСЃР»Рѕ РјРёРЅСѓС‚ РѕС‚ 1 РґРѕ 300:", cancellationToken: ct);
            return;
        }
        session.State = UserState.Idle;
        await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }

    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    //  РЈС‚РёР»РёС‚С‹
    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    private async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        var file = await _bot.GetFile(fileId, ct);
        using var stream = new MemoryStream();
        await _bot.DownloadFile(file.FilePath!, stream, ct);
        return stream.ToArray();
    }
}

