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
/// Распознавание расписания из фото удалено.
/// Подтвердить -> при наличии недель -> WaitingForWeekChoice -> сохранение
/// Исправить -> WaitingForScheduleCorrection -> правка -> WaitingForScheduleConfirmation
/// </summary>
public class TextHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService     _sessions;
    private readonly TimerService       _timers;

    public TextHandler(
        ITelegramBotClient bot,
        SessionService     sessions,
        TimerService       timers)
    {
        _bot      = bot;
        _sessions = sessions;
        _timers   = timers;
    }

    // Текстовые сообщения.

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
                    "Распознавание расписания из фото удалено. Чтобы выйти из этого режима — /start",
                    cancellationToken: ct);
                break;

            case UserState.WaitingForScheduleConfirmation:
                // Пользователь пишет текст вместо нажатия кнопки.
                await _bot.SendMessage(
                    msg.Chat.Id,
                    "Нажми кнопку ниже: ✅ Подтвердить, ✏️ Исправить или 🔎 Проверить по парам.",
                    cancellationToken: ct);
                break;

            case UserState.WaitingForScheduleCorrection:
                await HandleScheduleCorrectionAsync(msg, session, text, ct);
                break;

            case UserState.WaitingForScheduleReview:
                await _bot.SendMessage(
                    msg.Chat.Id,
                    "Нажми кнопку ниже: ✅ Верно или ✏️ Изменить.",
                    cancellationToken: ct);
                break;

            case UserState.WaitingForReviewSlotCorrection:
                await HandleReviewSlotCorrectionAsync(msg, session, text, ct);
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

    // Фотографии и парсинг расписания.

    /// <summary>
    /// Вызывается, когда пользователь присылает фото или изображение-документ.
    /// Если бот ждёт расписание, изображение скачивается и отправляется на парсинг.
    /// </summary>
    public async Task HandlePhotoAsync(Message msg, CancellationToken ct)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);
        session.State = UserState.Idle;

        await _bot.SendMessage(
            msg.Chat.Id,
            "Фото больше не обрабатываются. Распознавание расписания из изображения удалено из бота.",
            cancellationToken: ct);
    }

    // Исправление расписания.

    /// <summary>
    /// Пользователь описывает исправление расписания текстом.
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
            // Не смогли распознать исправление, просим уточнить.
            await _bot.SendMessage(
                chatId:    msg.Chat.Id,
                text:      "🤔 Не смог разобрать исправление.\n\n" +
                           "Попробуй так:\n" +
                           "<i>«3 парой во вторник не физика, а матан»</i>\n" +
                           "<i>«замени английский на историю в пятницу 2 пара»</i>\n" +
                           "<i>«убери 5 пару в среду»</i>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            // Остаёмся в WaitingForScheduleConfirmation и показываем текущее состояние снова.
        }
        else
        {
            await _bot.SendMessage(
                msg.Chat.Id, "✏️ Исправление применено!", cancellationToken: ct);
        }

        // В любом случае показываем актуальное расписание на подтверждение.
        await SendScheduleConfirmationAsync(msg.Chat.Id, updated, ct);
    }

    // Вспомогательные методы для подтверждения расписания.

    /// <summary>Отправить расписание на подтверждение с кнопками действий.</summary>
    internal async Task SendScheduleConfirmationAsync(
        long chatId, List<ScheduleEntry> entries, CancellationToken ct)
    {
        var summaryText = ScheduleService.FormatSchedule(entries);

        // Telegram ограничивает сообщение до 4096 символов.
        const int limit = 3800;
        var header  = $"📅 <b>Вот как я прочитал расписание</b> ({entries.Count} пар):\n\n";
        var body    = summaryText.Length > limit
            ? summaryText[..limit] + "\n<i>... (обрезано)</i>"
            : summaryText;
        var footer  = "\n\n<b>Если всё верно — нажми Подтвердить. Если нет — нажми Исправить или Проверь по парам.</b>";

        await _bot.SendMessage(
            chatId:      chatId,
            text:        header + body + footer,
            parseMode:   ParseMode.Html,
            replyMarkup: ScheduleKeyboards.Confirmation,
            cancellationToken: ct);
    }

    private async Task HandleReviewSlotCorrectionAsync(
        Message msg, UserSession session, string text, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            session.State = UserState.Idle;
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Нет расписания для пошаговой проверки.", cancellationToken: ct);
            return;
        }

        if (!TryApplyExactSlotCorrection(session.PendingSchedule, session.ReviewSlotIndex, text))
        {
            await _bot.SendMessage(
                msg.Chat.Id,
                "🤔 Не смог точно разобрать слот.\n\n" +
                "Напиши так:\n" +
                "<i>первая неделя: мат анализ\nвторая неделя: пары нет</i>\n\n" +
                "или:\n" +
                "<i>обе недели: история россии</i>\n\n" +
                "или:\n" +
                "<i>пары нет</i>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        session.ReviewSlotIndex++;
        session.State = UserState.WaitingForScheduleReview;

        await _bot.SendMessage(msg.Chat.Id, "✏️ Слот исправлен.", cancellationToken: ct);
        await SendReviewSlotAsync(msg.Chat.Id, session, ct);
    }

    private async Task SendReviewSlotAsync(long chatId, UserSession session, CancellationToken ct)
    {
        if (session.PendingSchedule is null)
        {
            await _bot.SendMessage(chatId, "ℹ️ Нет ожидающего расписания для проверки.", cancellationToken: ct);
            return;
        }

        if (session.ReviewSlotIndex >= 24)
        {
            session.State = UserState.WaitingForScheduleConfirmation;
            await _bot.SendMessage(
                chatId,
                "✅ Пошаговая проверка завершена. Ниже итоговое расписание.",
                cancellationToken: ct);
            await SendScheduleConfirmationAsync(chatId, session.PendingSchedule, ct);
            return;
        }

        var (day, lesson) = GetReviewSlot(session.ReviewSlotIndex);
        var slotEntries = session.PendingSchedule
            .Where(e => e.DayOfWeek == day && e.LessonNumber == lesson)
            .OrderBy(e => e.WeekType ?? 0)
            .ToList();

        var firstWeek = slotEntries.Where(e => e.WeekType is null or 1).Select(e => e.Subject).Distinct().ToList();
        var secondWeek = slotEntries.Where(e => e.WeekType is null or 2).Select(e => e.Subject).Distinct().ToList();

        await _bot.SendMessage(
            chatId,
            $"🔎 <b>Проверка {session.ReviewSlotIndex + 1}/24</b>\n" +
            $"<b>{GetDayName(day)}, {lesson} пара</b>\n\n" +
            $"Первая неделя: {FormatReviewWeek(firstWeek)}\n" +
            $"Вторая неделя: {FormatReviewWeek(secondWeek)}\n\n" +
            "Это верно?",
            parseMode: ParseMode.Html,
            replyMarkup: ScheduleKeyboards.ReviewSlotChoice,
            cancellationToken: ct);
    }

    // Парсер исправлений.

    /// <summary>
    /// Применяет текстовое исправление к списку записей расписания.
    /// Возвращает обновлённый список и признак успешного применения.
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
                WeekType = e.WeekType,
                SubGroup = e.SubGroup
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
        var knownSubjects = GetKnownSubjects(entries);

        if (day.HasValue && lesson.HasValue &&
            TryApplyWeekSpecificCorrection(entries, lower, day.Value, lesson.Value, knownSubjects))
        {
            return true;
        }

        var remove = Regex.IsMatch(lower,
            @"\b(\u0443\u0434\u0430\u043b\u0438|\u0443\u0434\u0430\u043b\u0438\u0442\u044c|\u0443\u0431\u0435\u0440\u0438|\u0443\u0431\u0440\u0430\u0442\u044c|\u0431\u0435\u0437\s+\u043f\u0430\u0440\u044b|\u043d\u0435\u0442\s+\u043f\u0430\u0440\u044b|\u043e\u043a\u043d\u043e)\b");

        string? oldSubject = null;
        string? newSubject = null;

        var m = Regex.Match(lower, @"\b\u043d\u0435\s+(.+?)[,\s]+\u0430\s+(.+)");
        if (m.Success)
        {
            oldSubject = ResolveSubjectName(m.Groups[1].Value.Trim(), knownSubjects);
            newSubject = ResolveSubjectName(m.Groups[2].Value.Trim(), knownSubjects);
        }

        if (newSubject is null)
        {
            m = Regex.Match(lower,
                @"\b(?:\u0437\u0430\u043c\u0435\u043d\u0438|\u043f\u043e\u043c\u0435\u043d\u044f\u0439|\u0438\u0437\u043c\u0435\u043d\u0438)\s+(.+?)\s+\u043d\u0430\s+(.+?)(?:\s+\b(?:\u0432|\u0432\u043e)\b\s+|$)");
            if (m.Success)
            {
                oldSubject = ResolveSubjectName(m.Groups[1].Value.Trim(), knownSubjects);
                newSubject = ResolveSubjectName(m.Groups[2].Value.Trim(), knownSubjects);
            }
        }

        if (newSubject is null)
        {
            m = Regex.Match(lower,
                @"\b\u0432\u043c\u0435\u0441\u0442\u043e\s+(.+?)\s+(?:\u043f\u043e\u0441\u0442\u0430\u0432\u044c\s+)?(.+?)(?:\s+\b(?:\u0432|\u0432\u043e)\b\s+|$)");
            if (m.Success)
            {
                oldSubject = ResolveSubjectName(m.Groups[1].Value.Trim(), knownSubjects);
                newSubject = ResolveSubjectName(m.Groups[2].Value.Trim(), knownSubjects);
            }
        }

        if (newSubject is null && !remove && day.HasValue && lesson.HasValue)
        {
            m = Regex.Match(lower,
                @"\b(?:\u043f\u043e\u0441\u0442\u0430\u0432\u044c|\u0441\u0434\u0435\u043b\u0430\u0439|\u0431\u0443\u0434\u0435\u0442|\u043f\u0443\u0441\u0442\u044c\s+\u0431\u0443\u0434\u0435\u0442)\s+(.+)$");
            if (m.Success)
                newSubject = ResolveSubjectName(m.Groups[1].Value.Trim(), knownSubjects);
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

        var normalized = ResolveSubjectName(newSubject, knownSubjects);
        foreach (var target in targets)
            target.Subject = normalized;

        return true;
    }

    private static bool TryApplyWeekSpecificCorrection(List<ScheduleEntry> entries, string lower, int day, int lesson, IReadOnlyList<string> knownSubjects)
    {
        var hasFirstWeek = Regex.IsMatch(lower, @"(?:\bпервая\b|\b1-?я\b|\bнечет\w*|\bнечёт\w*)\s+недел", RegexOptions.IgnoreCase);
        var hasSecondWeek = Regex.IsMatch(lower, @"(?:\bвторая\b|\b2-?я\b|\bчет\w*|\bчёт\w*)\s+недел", RegexOptions.IgnoreCase);

        if (!hasFirstWeek && !hasSecondWeek)
            return false;

        var firstWeek = ExtractWeekSubject(lower, firstWeek: true, knownSubjects);
        var secondWeek = ExtractWeekSubject(lower, firstWeek: false, knownSubjects);

        if (firstWeek is null && secondWeek is null)
            return false;

        entries.RemoveAll(e => e.DayOfWeek == day && e.LessonNumber == lesson);

        if (firstWeek is not null && secondWeek is not null &&
            string.Equals(firstWeek, secondWeek, StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = firstWeek,
                WeekType = null,
                SubGroup = null
            });
            return true;
        }

        if (firstWeek is not null)
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = firstWeek,
                WeekType = 1,
                SubGroup = null
            });
        }

        if (secondWeek is not null)
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = secondWeek,
                WeekType = 2,
                SubGroup = null
            });
        }

        return true;
    }

    private static string? ExtractWeekSubject(string lower, bool firstWeek, IReadOnlyList<string> knownSubjects)
    {
        var pattern = firstWeek
            ? @"(?:\bпервая\b|\b1-?я\b|\bнечет\w*|\bнечёт\w*)\s+недел\w*[:\s,-]*(.+?)(?=(?:\bвторая\b|\b2-?я\b|\bчет\w*|\bчёт\w*)\s+недел|$)"
            : @"(?:\bвторая\b|\b2-?я\b|\bчет\w*|\bчёт\w*)\s+недел\w*[:\s,-]*(.+)$";

        var match = Regex.Match(lower, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var raw = match.Groups[1].Value.Trim(' ', '.', ',', ';', ':', '-');
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (Regex.IsMatch(raw, @"^(?:-|\u043d\u0435\u0442|\u043f\u0430\u0440\u044b?\s+\u043d\u0435\u0442|\u043e\u043a\u043d\u043e)$", RegexOptions.IgnoreCase))
            return null;

        return ResolveSubjectName(raw, knownSubjects);
    }

    private static IReadOnlyList<string> GetKnownSubjects(List<ScheduleEntry> entries)
        => entries
            .Select(e => e.Subject)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolveSubjectName(string rawSubject, IReadOnlyList<string> knownSubjects)
    {
        var normalized = NormalizeSubject(rawSubject);
        if (string.IsNullOrWhiteSpace(normalized) || knownSubjects.Count == 0)
            return normalized;

        var ranked = knownSubjects
            .Select(subject => new
            {
                Subject = subject,
                Score = ScoreSubjectCandidate(normalized, subject)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Subject.Length)
            .ToList();

        var best = ranked[0];
        var secondScore = ranked.Count > 1 ? ranked[1].Score : 0.0;

        if (best.Score >= 0.90 || (best.Score >= 0.78 && best.Score - secondScore >= 0.08))
            return best.Subject;

        return normalized;
    }

    private static double ScoreSubjectCandidate(string rawSubject, string candidate)
    {
        var raw = NormalizeForMatch(rawSubject);
        var cand = NormalizeForMatch(candidate);

        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(cand))
            return 0;

        if (string.Equals(raw, cand, StringComparison.Ordinal))
            return 1.0;

        var fullSimilarity = Similarity(raw, cand);
        var containment = cand.Contains(raw, StringComparison.Ordinal) || raw.Contains(cand, StringComparison.Ordinal)
            ? 0.96
            : 0.0;

        var rawTokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candTokens = cand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenCoverage = rawTokens.Length == 0
            ? 0
            : rawTokens
                .Select(token => candTokens.Length == 0 ? 0 : candTokens.Max(c => Similarity(token, c)))
                .Average();

        return Math.Max(Math.Max(fullSimilarity, containment), tokenCoverage);
    }

    private static string NormalizeForMatch(string text)
    {
        var lower = text.ToLowerInvariant().Replace('ё', 'е');
        lower = Regex.Replace(lower, @"[^a-zа-я0-9]+", " ");
        lower = Regex.Replace(lower, @"\s+", " ").Trim();
        return lower;
    }

    private static double Similarity(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return 0;

        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1.0;

        if ((left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal)) &&
            Math.Min(left.Length, right.Length) >= 3)
        {
            return 0.92;
        }

        var distance = LevenshteinDistance(left, right);
        return 1.0 - distance / (double)Math.Max(left.Length, right.Length);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var dp = new int[left.Length + 1, right.Length + 1];

        for (var i = 0; i <= left.Length; i++)
            dp[i, 0] = i;

        for (var j = 0; j <= right.Length; j++)
            dp[0, j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[left.Length, right.Length];
    }

    private static bool TryApplyExactSlotCorrection(List<ScheduleEntry> entries, int slotIndex, string text)
    {
        var (day, lesson) = GetReviewSlot(slotIndex);
        var lower = text.ToLowerInvariant();
        var knownSubjects = GetKnownSubjects(entries);

        if (Regex.IsMatch(lower, @"^(?:-|\u043d\u0435\u0442|\u043f\u0430\u0440\u044b?\s+\u043d\u0435\u0442|\u043e\u043a\u043d\u043e)$", RegexOptions.IgnoreCase))
        {
            entries.RemoveAll(e => e.DayOfWeek == day && e.LessonNumber == lesson);
            return true;
        }

        var bothWeeks = Regex.Match(lower, @"(?:\b\u043e\u0431\u0435\b|\b\u043e\u0431\u0435\u0438\u0445\b)\s+\u043d\u0435\u0434\u0435\u043b\w*[:\s,-]*(.+)$", RegexOptions.IgnoreCase);
        if (bothWeeks.Success)
        {
            var subject = ResolveSubjectName(bothWeeks.Groups[1].Value, knownSubjects);
            if (string.IsNullOrWhiteSpace(subject))
                return false;

            entries.RemoveAll(e => e.DayOfWeek == day && e.LessonNumber == lesson);
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = subject,
                WeekType = null,
                SubGroup = null
            });
            return true;
        }

        var firstWeek = ExtractWeekSubject(lower, firstWeek: true, knownSubjects);
        var secondWeek = ExtractWeekSubject(lower, firstWeek: false, knownSubjects);

        if (firstWeek is null && secondWeek is null)
        {
            var fallback = ResolveSubjectName(text, knownSubjects);
            if (string.IsNullOrWhiteSpace(fallback))
                return false;

            entries.RemoveAll(e => e.DayOfWeek == day && e.LessonNumber == lesson);
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = fallback,
                WeekType = null,
                SubGroup = null
            });
            return true;
        }

        entries.RemoveAll(e => e.DayOfWeek == day && e.LessonNumber == lesson);

        if (firstWeek is not null && secondWeek is not null &&
            string.Equals(firstWeek, secondWeek, StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = firstWeek,
                WeekType = null,
                SubGroup = null
            });
            return true;
        }

        if (firstWeek is not null)
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = firstWeek,
                WeekType = 1,
                SubGroup = null
            });
        }

        if (secondWeek is not null)
        {
            entries.Add(new ScheduleEntry
            {
                DayOfWeek = day,
                LessonNumber = lesson,
                Subject = secondWeek,
                WeekType = 2,
                SubGroup = null
            });
        }

        return true;
    }

    private static (int Day, int Lesson) GetReviewSlot(int slotIndex)
        => (slotIndex / 4 + 1, slotIndex % 4 + 1);

    private static string GetDayName(int day) => day switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        _ => $"День {day}"
    };

    private static string FormatReviewWeek(List<string> subjects)
        => subjects.Count == 0 ? "пары нет" : string.Join("; ", subjects);

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

        if (Regex.IsMatch(lower, @"\b\u043f\u0435\u0440\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 1;
        if (Regex.IsMatch(lower, @"\b\u0432\u0442\u043e\u0440\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 2;
        if (Regex.IsMatch(lower, @"\b\u0442\u0440\u0435\u0442\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 3;
        if (Regex.IsMatch(lower, @"\b\u0447\u0435\u0442\u0432[\u0435\u0451]\u0440\u0442\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 4;
        if (Regex.IsMatch(lower, @"\b\u043f\u044f\u0442\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 5;
        if (Regex.IsMatch(lower, @"\b\u0448\u0435\u0441\u0442\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 6;
        if (Regex.IsMatch(lower, @"\b\u0441\u0435\u0434\u044c\u043c\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 7;
        if (Regex.IsMatch(lower, @"\b\u0432\u043e\u0441\u044c\u043c\w*\s+(?:\u043f\u0430\u0440\w*|\u0443\u0440\u043e\u043a\w*)")) return 8;

        if (!lower.Contains("\u043d\u0435\u0434\u0435\u043b", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(lower, @"\b\u043f\u0435\u0440(\u0432|\u0432\u043e)\w*")) return 1;
            if (Regex.IsMatch(lower, @"\b\u0432\u0442\u043e\u0440\w*")) return 2;
            if (Regex.IsMatch(lower, @"\b\u0442\u0440\u0435\u0442\w*")) return 3;
            if (Regex.IsMatch(lower, @"\b\u0447\u0435\u0442\u0432[\u0435\u0451]\u0440\u0442\w*")) return 4;
            if (Regex.IsMatch(lower, @"\b\u043f\u044f\u0442\w*")) return 5;
            if (Regex.IsMatch(lower, @"\b\u0448\u0435\u0441\u0442\w*")) return 6;
            if (Regex.IsMatch(lower, @"\b\u0441\u0435\u0434\u044c\u043c\w*")) return 7;
            if (Regex.IsMatch(lower, @"\b\u0432\u043e\u0441\u044c\u043c\w*")) return 8;
        }

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

    // Утилиты.

}

