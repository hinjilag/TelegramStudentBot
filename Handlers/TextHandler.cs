using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик текстовых сообщений и фотографий.
/// Работает как машина состояний.
///
/// Поток загрузки расписания:
///   /add_schedule → WaitingForSchedulePhoto
///   → фото → парсинг → WaitingForScheduleConfirmation (показываем расписание + кнопки)
///   → [Подтвердить] → если есть двойные пары → WaitingForWeekChoice → [Нечётная/Чётная] → сохранено
///                  → если нет              → сразу сохраняем
///   → [Исправить]  → WaitingForScheduleCorrection → пользователь пишет правку → WaitingForScheduleConfirmation
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

    // ══════════════════════════════════════════════════════════
    //  Текстовые сообщения
    // ══════════════════════════════════════════════════════════

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
                // Пользователь пишет текст вместо нажатия кнопки
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
                    text:      "ℹ️ Я не понимаю это сообщение.\nИспользуй /help для просмотра команд.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Фотографии — парсинг расписания
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Вызывается когда пользователь присылает фото или изображение-документ.
    /// Если бот в состоянии WaitingForSchedulePhoto — скачивает и парсит.
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

        // Выбираем fileId: photo (сжатое) или document (без сжатия)
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

        await _bot.SendMessage(msg.Chat.Id, "⏳ Анализирую расписание... Это займёт 1–3 минуты.", cancellationToken: ct);

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
                           "Попробуй:\n• Фото чётче и с ровным освещением\n" +
                           "• Вся таблица должна быть видна\n" +
                           "• Отправь как документ (без сжатия)\n\n" +
                           "Используй /add_schedule чтобы попробовать снова.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            session.State = UserState.Idle;
            return;
        }

        // Переходим в режим подтверждения
        session.PendingSchedule = entries;
        session.State           = UserState.WaitingForScheduleConfirmation;

        await SendScheduleConfirmationAsync(msg.Chat.Id, entries, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Исправление расписания
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Пользователь описывает исправление текстом.
    /// Пример: "первой парой в среду у меня не мат анализ, а линейная алгебра"
    /// Или:    "замени физику на химию во вторник"
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
            // Не смогли распознать исправление — просим уточнить
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      "🤔 Не смог разобрать исправление.\n\n" +
                           "Попробуй написать в формате:\n" +
                           "<i>«[N]-й парой в [день] у меня не [старое], а [новое]»</i>\n\n" +
                           "Например:\n" +
                           "<i>«первой парой в среду у меня не мат анализ, а линейная алгебра»</i>\n" +
                           "<i>«замени физику на химию в пятницу»</i>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            // Остаёмся в WaitingForScheduleConfirmation — показываем текущее состояние снова
        }
        else
        {
            await _bot.SendMessage(
                msg.Chat.Id, "✏️ Исправление применено!", cancellationToken: ct);
        }

        // В любом случае показываем актуальное расписание на подтверждение
        await SendScheduleConfirmationAsync(msg.Chat.Id, updated, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Вспомогательные: отображение расписания на подтверждение
    // ══════════════════════════════════════════════════════════

    /// <summary>Отправить расписание на подтверждение с кнопками [Подтвердить] [Исправить]</summary>
    internal async Task SendScheduleConfirmationAsync(
        long chatId, List<ScheduleEntry> entries, CancellationToken ct)
    {
        var summaryText = ScheduleService.FormatSchedule(entries);

        // Telegram ограничивает сообщение до 4096 символов
        const int limit = 3800;
        var header  = $"📅 <b>Распознанное расписание</b> ({entries.Count} пар):\n\n";
        var body    = summaryText.Length > limit
            ? summaryText[..limit] + "\n<i>... (обрезано)</i>"
            : summaryText;
        var footer  = "\n\n<b>Всё верно?</b>";

        await _bot.SendMessage(
            chatId:      chatId,
            text:        header + body + footer,
            parseMode:   ParseMode.Html,
            replyMarkup: ScheduleKeyboards.Confirmation,
            cancellationToken: ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Парсер исправлений
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Применяет текстовое исправление к списку записей расписания.
    /// Возвращает (обновлённый список, было ли исправление применено).
    /// </summary>
    private static (List<ScheduleEntry> Entries, bool Applied) ApplyCorrection(
        List<ScheduleEntry> entries, string text)
    {
        // Создаём изменяемую копию
        var result = entries
            .Select(e => new ScheduleEntry
            {
                DayOfWeek    = e.DayOfWeek,
                LessonNumber = e.LessonNumber,
                Subject      = e.Subject,
                WeekType     = e.WeekType
            })
            .ToList();

        var lower = text.ToLowerInvariant();

        // ── Определяем день недели ────────────────────────────
        int? day = null;
        var dayPatterns = new (string[] Words, int Num)[]
        {
            (new[] { "понедельник", "пн" },        1),
            (new[] { "вторник", "вт" },             2),
            (new[] { "среду", "среда", "ср" },      3),
            (new[] { "четверг", "чт" },             4),
            (new[] { "пятницу", "пятница", "пт" },  5),
            (new[] { "субботу", "суббота", "сб" },  6),
            (new[] { "воскресенье", "вс" },         7),
        };
        foreach (var (words, num) in dayPatterns)
            if (words.Any(w => lower.Contains(w))) { day = num; break; }

        // ── Определяем номер пары ─────────────────────────────
        int? lesson = null;
        var lessonPatterns = new (string[] Words, int Num)[]
        {
            (new[] { "первой", "первая", "1-й", "1й", "1 " }, 1),
            (new[] { "второй", "вторая", "2-й", "2й", "2 " }, 2),
            (new[] { "третьей", "третья", "3-й", "3й", "3 " }, 3),
            (new[] { "четвёртой", "четвертой", "4-й", "4й", "4 " }, 4),
            (new[] { "пятой", "пятая", "5-й", "5й", "5 " }, 5),
            (new[] { "шестой", "шестая", "6-й", "6й", "6 " }, 6),
            (new[] { "седьмой", "седьмая", "7-й", "7й", "7 " }, 7),
            (new[] { "восьмой", "восьмая", "8-й", "8й", "8 " }, 8),
        };
        foreach (var (words, num) in lessonPatterns)
            if (words.Any(w => lower.Contains(w))) { lesson = num; break; }

        // ── Извлекаем старое и новое название ────────────────
        string? oldSubject = null;
        string? newSubject = null;

        // Паттерн: "не [старое], а [новое]"
        var m = Regex.Match(lower, @"не\s+(.+?)[,\s]+а\s+(.+)");
        if (m.Success)
        {
            oldSubject = m.Groups[1].Value.Trim();
            newSubject = m.Groups[2].Value.Trim();
        }

        // Паттерн: "замени [старое] на [новое]"
        if (newSubject is null)
        {
            m = Regex.Match(lower, @"замени\s+(.+?)\s+на\s+(.+?)(?:\s+в\s+|$)");
            if (m.Success)
            {
                oldSubject = m.Groups[1].Value.Trim();
                newSubject = m.Groups[2].Value.Trim();
            }
        }

        // Паттерн: "вместо [старого] [новое]"
        if (newSubject is null)
        {
            m = Regex.Match(lower, @"вместо\s+(.+?)\s+(.+?)(?:\s+в\s+|$)");
            if (m.Success)
            {
                oldSubject = m.Groups[1].Value.Trim();
                newSubject = m.Groups[2].Value.Trim();
            }
        }

        if (newSubject is null) return (result, false);

        // ── Применяем к подходящим записям ───────────────────
        var applied = false;
        foreach (var entry in result)
        {
            var matchDay    = !day.HasValue    || entry.DayOfWeek    == day;
            var matchLesson = !lesson.HasValue || entry.LessonNumber == lesson;
            var matchSubj   = oldSubject is null
                || entry.Subject.Contains(oldSubject, StringComparison.OrdinalIgnoreCase);

            if (matchDay && matchLesson && matchSubj)
            {
                // Capitalize
                entry.Subject = char.ToUpper(newSubject[0]) + newSubject[1..];
                applied = true;
            }
        }

        return (result, applied);
    }

    // ══════════════════════════════════════════════════════════
    //  Шаги создания задачи
    // ══════════════════════════════════════════════════════════

    private async Task HandleTaskTitleAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Название не может быть пустым. Введи название задачи:", cancellationToken: ct);
            return;
        }
        session.DraftTask = new StudyTask { Title = text };
        session.State     = UserState.WaitingForTaskSubject;
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Название: <b>{text}</b>\n\nТеперь введи <b>предмет</b> (например: Математика):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskSubjectAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Предмет не может быть пустым.", cancellationToken: ct);
            return;
        }
        session.DraftTask!.Subject = text;
        session.State              = UserState.WaitingForTaskDeadline;
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"✅ Предмет: <b>{text}</b>\n\n" +
                       "Введи <b>дедлайн</b> в формате ДД.ММ.ГГГГ\n(или напиши <b>нет</b>):",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleTaskDeadlineAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (text.Equals("нет", StringComparison.OrdinalIgnoreCase) ||
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
                    text:      "⚠️ Неверный формат даты. Используй ДД.ММ.ГГГГ\nИли напиши <b>нет</b>.",
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

        var dl = task.Deadline.HasValue ? task.Deadline.Value.ToString("dd.MM.yyyy") : "не задан";
        await _bot.SendMessage(
            chatId:    msg.Chat.Id,
            text:      $"🎉 <b>Задача добавлена!</b>\n\n📌 <b>{task.Title}</b>\n📚 {task.Subject}\n📅 {dl}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task HandleCustomTimerAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (!int.TryParse(text, out int minutes) || minutes < 1 || minutes > 300)
        {
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Введи число минут от 1 до 300:", cancellationToken: ct);
            return;
        }
        session.State = UserState.Idle;
        await _timers.StartWorkTimerAsync(msg.Chat.Id, msg.From!.Id, minutes);
    }

    // ──────────────────────────────────────────────────────────
    //  Утилиты
    // ──────────────────────────────────────────────────────────

    private async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        var file = await _bot.GetFile(fileId, ct);
        using var stream = new MemoryStream();
        await _bot.DownloadFile(file.FilePath!, stream, ct);
        return stream.ToArray();
    }
}
