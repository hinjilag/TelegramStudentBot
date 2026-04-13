using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Сервис парсинга расписания из фотографии через Ollama, Groq или Gemini.
/// </summary>
public class ScheduleService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScheduleService> _logger;
    private readonly string _ollamaUrl;
    private readonly string _model;
    private readonly string? _groqApiKey;
    private readonly string _groqModel;
    private readonly string? _geminiApiKey;
    private readonly string _geminiModel;
    private readonly string _provider;

    // Основной промпт для извлечения расписания в JSON.
    private const string SchedulePrompt = """
        Ты анализируешь фотографию расписания занятий российского университета.

        ЗАДАЧА: извлеки ВСЕ пары и верни ТОЛЬКО JSON без пояснений.

        ВАЖНО:
        - В таблице есть две подгруппы. Номера подгрупп смотри в шапке таблицы.
        - В некоторых ячейках есть разбиение по неделям: верхняя половина или верхний текстовый блок = первая неделя (нечётная, weekType=1),
          нижняя половина или нижний текстовый блок = вторая неделя (чётная, weekType=2).
        - Если одна из половин пуста, для этой недели запись НЕ создавай.
        - Если предмет общий для обеих подгрупп, ставь subGroup: null.
        - Если предмет только в колонке одной подгруппы, ставь номер этой подгруппы из шапки.
        - Если у пары нет деления по неделям, ставь weekType: null.
        - Бери только название дисциплины. Не включай преподавателя, аудиторию, "(лекция)", "(практ.)", "(лаб.)".

        СТРУКТУРА:
        - day: день недели (Понедельник=1, Вторник=2, Среда=3, Четверг=4, Пятница=5, Суббота=6)
        - lesson: номер пары
        - subject: название дисциплины
        - subGroup: null или номер подгруппы из шапки
        - weekType: null, 1 или 2

        ПРАВИЛА ЧТЕНИЯ:
        1. Если одна ячейка растянута сразу на обе подгруппы, это общая пара: subGroup = null.
        2. Общая ячейка ТОЖЕ может быть разделена по неделям.
           Пример: понедельник, 1 пара:
           верхняя половина = "Математический анализ" => {"day":1,"lesson":1,"subject":"Математический анализ","subGroup":null,"weekType":1}
           нижняя половина пустая => для weekType 2 записи нет.
        3. Если слева и справа разные предметы, это две записи для двух подгрупп из шапки.
        4. Если в одной ячейке предмет сверху и предмет снизу, или внутри одной пары идут два текстовых блока друг под другом,
           это ДВЕ записи:
           верх = weekType 1, низ = weekType 2.
        5. Даже если после удаления преподавателя, аудитории и типа занятия название сверху и снизу совпадает,
           это всё равно разные недели, если на фото видно два отдельных блока.
        6. Если внизу пусто, значит для второй недели пары нет.
        7. Если сверху пусто, значит для первой недели пары нет.
        8. Не дублируй одинаковые записи.

        ВЕРНИ СТРОГО ТАКОЙ JSON:
        {"schedule":[
          {"day":1,"lesson":1,"subject":"Предмет A","subGroup":null,"weekType":null},
          {"day":1,"lesson":1,"subject":"Предмет A","subGroup":null,"weekType":1},
          {"day":1,"lesson":3,"subject":"Предмет B","subGroup":1,"weekType":null},
          {"day":1,"lesson":3,"subject":"Предмет C","subGroup":2,"weekType":null},
          {"day":2,"lesson":4,"subject":"Предмет D","subGroup":null,"weekType":1},
          {"day":2,"lesson":4,"subject":"Предмет E","subGroup":null,"weekType":2}
        ]}
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string RetryStrictSuffix = """
        Repeat extraction more strictly.
        Return ONLY a valid JSON object with fields: day, lesson, subject, subGroup, weekType.
        subGroup must be only null or a subgroup number from the table header.
        weekType must be only null, 1, or 2.
        If only part of the table is readable, return only confident rows.
        """;

    private const string ReviewPromptTemplate = """
        Ты повторно проверяешь фотографию расписания и черновой JSON ниже.
        Нужно ИСПРАВИТЬ ошибки распознавания, а не пересказывать правила.

        ЧЕРНОВОЙ JSON:
        {CANDIDATE_JSON}

        ПРОВЕРКИ:
        - Перепроверь соответствие предмета конкретной паре и конкретному дню.
        - Не сдвигай предметы на соседнюю пару.
        - Если предмет общий на обе подгруппы, subGroup = null.
        - Общая пара тоже может иметь деление по неделям: subGroup = null и weekType = 1/2.
        - Если предмет только у одной подгруппы, ставь номер этой подгруппы из шапки.
        - Если верх и низ одной ячейки относятся к неделям, используй weekType 1 и 2.
        - Если внутри одной пары видны два текстовых блока друг под другом, это тоже может быть разбиение по неделям,
          даже если линия-разделитель выражена слабо.
        - Даже если после очистки названия сверху и снизу совпадают, всё равно оставь две недели,
          если на фото это два разных блока внутри одной пары.
        - Если одна неделя пустая, запись для неё не создавай.
        - Суббота тоже может содержать пары, не пропускай её.
        - Верни ТОЛЬКО исправленный JSON объекта вида {"schedule":[...]}.
        - Поля каждой записи: day, lesson, subject, subGroup, weekType.
        """;

    private const string OcrPrompt = """
        Прочитай текст на изображении и верни его обратно.
        Правила:
        - Верни только распознанный текст без пояснений.
        - Сохраняй переносы строк, если они есть.
        - Если текста почти нет или он не читается, верни: [TEXT_NOT_FOUND]
        """;

    private const string DetectSubGroupsPrompt = """
        Посмотри только на верхнюю часть таблицы расписания.
        Найди две подгруппы в шапке слева направо.

        Верни ТОЛЬКО JSON:
        {"subGroups":[1,2]}

        ПРАВИЛА:
        - Верни два числа слева направо.
        - Если видишь "Подгруппа 3" и "Подгруппа 4", верни {"subGroups":[3,4]}.
        - Если видишь "Подгруппа 1" и "Подгруппа 2", верни {"subGroups":[1,2]}.
        - Не пиши пояснений и markdown.
        """;

    private const string StructuredGridPrompt = """
        Ты читаешь фотографию расписания занятий как СТРОГУЮ ТАБЛИЦУ.

        Сначала найди:
        - колонку с днями недели,
        - колонку с номером пары,
        - две колонки подгрупп в шапке таблицы: левая и правая.

        Затем для КАЖДОЙ пары заполни, что есть у левой и у правой подгруппы
        на первой и второй неделе.

        Верни ТОЛЬКО 24 строки, строго в этом формате и порядке:

        D1L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D1L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D1L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D1L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D2L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D2L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D2L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D2L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D3L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D3L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D3L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D3L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D4L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D4L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D4L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D4L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D5L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D5L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D5L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D5L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D6L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D6L2 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D6L3 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        D6L4 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...

        ПРАВИЛА:
        - D = номер дня недели от 1 до 6.
        - L = номер пары от 1 до 4.
        - ODD = первая неделя.
        - EVEN = вторая неделя.
        - Если одна общая ячейка относится к обеим подгруппам, продублируй её и в A, и в B.
        - Если внутри одной пары видны два текстовых блока друг под другом, считай: верхний блок = ODD, нижний блок = EVEN.
        - Если у подгруппы виден только один цельный предмет без второго блока, повтори его и в ODD, и в EVEN.
        - Не выдумывай деление на ODD и EVEN, если второго текстового блока нет.
        - Если верхний и нижний блок после очистки дают одинаковое название предмета, всё равно заполни и ODD, и EVEN отдельно.
        - Если в одной из недель пары нет, пиши "-".
        - Если у подгруппы на обеих неделях пары нет, пиши A_ODD=-/A_EVEN=- или B_ODD=-/B_EVEN=-.
        - Нельзя переносить предмет на соседнюю пару или соседний день.
        - Бери только название дисциплины.
        - Не добавляй преподавателя, аудиторию, лекция/практика/лаб.
        - Понедельник=1, Вторник=2, Среда=3, Четверг=4, Пятница=5, Суббота=6.
        - Не пропускай строки и не меняй порядок.
        - Не пиши пояснений, markdown и JSON.

        ПРИМЕРЫ:
        D1L1 | A_ODD=Предмет A | A_EVEN=- | B_ODD=Предмет A | B_EVEN=-
        D1L3 | A_ODD=Предмет B | A_EVEN=Предмет B | B_ODD=Предмет C | B_EVEN=Предмет C
        D2L4 | A_ODD=Предмет D | A_EVEN=Предмет E | B_ODD=Предмет D | B_EVEN=Предмет E
        """;

    private const string StructuredGridReviewPromptTemplate = """
        Ты повторно проверяешь фотографию расписания и черновую 24-строчную таблицу ниже.
        Нужно ИСПРАВИТЬ ошибки чтения, а не пересказывать правила.

        ЧЕРНОВИК:
        {CANDIDATE_GRID}

        ВАЖНО:
        - Для каждой строки D?L? проверь, что предмет не съехал на соседнюю пару.
        - Если ячейка общая на обе подгруппы, продублируй её и в A, и в B.
        - Если внутри одной пары видны два текстовых блока друг под другом, считай: верхний блок = ODD, нижний блок = EVEN.
        - Если у подгруппы виден только один цельный предмет, повтори его в ODD и EVEN.
        - Не превращай ячейку в ODD-only или EVEN-only, если второго текстового блока нет.
        - Если верхний и нижний блок после очистки выглядят одинаково, всё равно оставь ODD и EVEN раздельно,
          если на фото это два разных блока.
        - Если одна из недель пустая, ставь "-".
        - Если у подгруппы обе недели пустые, ставь "-" и "-" .
        - Не смешивай левую и правую подгруппы.
        - Не пропускай строки.
        - Верни ТОЛЬКО исправленные 24 строки в том же формате:
          D1L1 | A_ODD=... | A_EVEN=... | B_ODD=... | B_EVEN=...
        """;

    private const string DayGridPromptTemplate = """
        Ты читаешь ТОЛЬКО день недели "{DAY_NAME}" в таблице расписания.
        Игнорируй все другие дни.

        Нужна только подгруппа {SUBGROUP} и общие пары, которые относятся и к ней тоже.

        Сначала найди слева строку дня "{DAY_NAME}", затем прочитай только 4 пары этого дня.
        Нельзя переносить предметы из соседнего дня или соседней пары.

        Верни РОВНО 4 строки в таком формате:
        L1 | ODD=... | EVEN=...
        L2 | ODD=... | EVEN=...
        L3 | ODD=... | EVEN=...
        L4 | ODD=... | EVEN=...

        ПРАВИЛА:
        - ODD = первая неделя.
        - EVEN = вторая неделя.
        - Если предмет общий для обеих подгрупп, включи его для подгруппы {SUBGROUP}.
        - Если предмет только у другой подгруппы, для подгруппы {SUBGROUP} ставь "-".
        - Если внутри одной пары видны два текстовых блока друг под другом, верхний блок = ODD, нижний блок = EVEN.
        - Если виден только один цельный предмет, повтори его в ODD и EVEN.
        - Нельзя придумывать ODD-only или EVEN-only, если второго текстового блока нет.
        - Если верхний и нижний блок после очистки дают одинаковое название, всё равно заполни обе недели,
          если на фото это два разных блока.
        - Если на одной неделе пары нет, пиши "-".
        - Если на обеих неделях пары нет, пиши ODD=- и EVEN=-.
        - Бери только название дисциплины, без преподавателя, аудитории, лекция/практика/лаб.
        - Не пиши пояснений, markdown и JSON.

        ПРИМЕР:
        L1 | ODD=Предмет A | EVEN=-
        L2 | ODD=Предмет B | EVEN=Предмет C
        L3 | ODD=Предмет D | EVEN=Предмет D
        L4 | ODD=- | EVEN=-
        """;

    private const string DayGridReviewPromptTemplate = """
        Ты повторно проверяешь ТОЛЬКО день недели "{DAY_NAME}" в таблице расписания.
        Игнорируй все другие дни.

        ЧЕРНОВИК:
        {CANDIDATE_DAY_GRID}

        Исправь ошибки чтения.

        ВАЖНО:
        - Нельзя переносить предметы из соседнего дня.
        - Нельзя переносить предметы между парами L1-L4.
        - Нужна только подгруппа {SUBGROUP} и общие пары для неё.
        - Если предмет общий, включи его.
        - Если предмет только у другой подгруппы, ставь "-".
        - Если внутри одной пары видны два текстовых блока друг под другом, верхний блок = ODD, нижний блок = EVEN.
        - Если виден только один цельный предмет, повтори его и в ODD, и в EVEN.
        - Не превращай ячейку в ODD-only или EVEN-only, если второго текстового блока нет.
        - Если верхний и нижний блок после очистки дают одинаковое название, всё равно оставь обе недели,
          если на фото это два разных блока.
        - Верни РОВНО 4 строки в формате:
          L1 | ODD=... | EVEN=...
          L2 | ODD=... | EVEN=...
          L3 | ODD=... | EVEN=...
          L4 | ODD=... | EVEN=...
        """;

    public ScheduleService(HttpClient http, IConfiguration config, ILogger<ScheduleService> logger)
    {
        _http      = http;
        _logger    = logger;
        _ollamaUrl = (config["OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _model     = config["OllamaModel"] ?? "qwen2.5vl:7b";
        _groqApiKey = config["GroqApiKey"]?.Trim();
        _groqModel  = config["GroqModel"] ?? "meta-llama/llama-4-scout-17b-16e-instruct";
        _geminiApiKey = config["GeminiApiKey"]?.Trim();
        _geminiModel  = config["GeminiModel"] ?? "gemini-2.0-flash";
        _provider     = (config["ScheduleAiProvider"] ?? "auto").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Разобрать изображение расписания и вернуть список пар.
    /// </summary>
    /// <param name="imageBytes">Байты изображения JPEG/PNG.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Список записей расписания.</returns>
    public async Task<List<ScheduleEntry>> ParseScheduleAsync(byte[] imageBytes, CancellationToken ct)
        => await ParseScheduleAsync(imageBytes, null, ct);

    public async Task<List<ScheduleEntry>> ParseScheduleAsync(byte[] imageBytes, int? selectedSubGroup, CancellationToken ct)
    {
        if (ShouldUseGroq())
            return await ParseWithGroqAsync(imageBytes, selectedSubGroup, ct);

        if (ShouldUseGemini())
            return await ParseWithGeminiAsync(imageBytes, ct);

        return await ParseWithOllamaAsync(imageBytes, ct);
    }

    public async Task<string> ExtractTextAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (ShouldUseGroq())
            return await ExtractTextWithGroqAsync(imageBytes, ct);

        if (ShouldUseGemini())
            return await ExtractTextWithGeminiAsync(imageBytes, ct);

        throw new InvalidOperationException("OCR-проверка сейчас поддерживается только для облачных провайдеров Groq или Gemini.");
    }

    public async Task<List<int>> DetectSubGroupsAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (ShouldUseGroq())
        {
            var rawText = await SendGroqVisionPromptAsync(imageBytes, DetectSubGroupsPrompt, ct, useJsonMode: true, maxCompletionTokens: 120);
            return ExtractSubGroups(rawText);
        }

        var ocrText = await ExtractTextAsync(imageBytes, ct);
        return ExtractSubGroups(ocrText);
    }

    public async Task<List<ScheduleEntry>> ParseScheduleForSubGroupAsync(
        byte[] imageBytes,
        int selectedSubGroup,
        IReadOnlyList<int> availableSubGroups,
        CancellationToken ct)
    {
        var subgroupColumns = ResolveSubGroupColumns(availableSubGroups, selectedSubGroup);

        if (!ShouldUseGroq())
            return await ParseScheduleAsync(imageBytes, selectedSubGroup, ct);

        List<ScheduleEntry> bestCandidate = new();
        List<ScheduleEntry> fastFallback = new();

        try
        {
            var firstRaw = await SendGroqVisionPromptAsync(imageBytes, StructuredGridPrompt, ct, useJsonMode: false, maxCompletionTokens: 900);
            _logger.LogInformation("Черновая subgroup-grid таблица ({Count} строк): {Preview}",
                CountStructuredGridLines(firstRaw),
                firstRaw[..Math.Min(500, firstRaw.Length)]);

            var firstPass = ParseStructuredGridScheduleText(firstRaw, selectedSubGroup, subgroupColumns);
            var normalizedFirstPass = NormalizeSelectedSubGroupEntries(firstPass);
            var firstCoverage = CountStructuredGridLines(firstRaw);

            var reviewedRaw = await ReviewGroqStructuredGridAsync(imageBytes, firstRaw, ct);
            var reviewedPass = ParseStructuredGridScheduleText(reviewedRaw, selectedSubGroup, subgroupColumns);

            var reviewedCoverage = CountStructuredGridLines(reviewedRaw);

            if (reviewedCoverage >= firstCoverage && reviewedPass.Count > 0)
                bestCandidate = NormalizeSelectedSubGroupEntries(reviewedPass);
            else if (normalizedFirstPass.Count > 0)
                bestCandidate = normalizedFirstPass;
        }
        catch (Exception ex) when (IsRecoverableGroqFailure(ex))
        {
            _logger.LogWarning(ex, "Structured-grid разбор через Groq сорвался, пробую быстрый JSON-режим");
        }

        try
        {
            fastFallback = NormalizeSelectedSubGroupEntries(await ParseScheduleAsync(imageBytes, selectedSubGroup, ct));
            if (fastFallback.Count > bestCandidate.Count)
                bestCandidate = fastFallback;
        }
        catch (Exception ex) when (IsRecoverableGroqFailure(ex))
        {
            _logger.LogWarning(ex, "Быстрый JSON-разбор через Groq сорвался, пробую точечный day-by-day резерв");
        }

        var daysToRefine = GetDaysNeedingRefinement(bestCandidate, fastFallback);

        if (daysToRefine.Count == 0 && IsReasonableSubgroupResult(bestCandidate))
            return bestCandidate;

        try
        {
            var refined = new List<ScheduleEntry>(bestCandidate);

            foreach (var day in daysToRefine)
            {
                var dayEntries = await ParseScheduleDayForSubGroupAsync(imageBytes, day, selectedSubGroup, ct);
                refined.RemoveAll(e => e.DayOfWeek == day);
                refined.AddRange(dayEntries);
            }

            var normalizedRefined = NormalizeSelectedSubGroupEntries(RemoveConflictingDuplicates(refined));
            if (IsReasonableSubgroupResult(normalizedRefined))
                return normalizedRefined;

            if (normalizedRefined.Count > bestCandidate.Count)
                bestCandidate = normalizedRefined;

            if (normalizedRefined.Count > 0)
            {
                _logger.LogWarning(
                    "Точечный day-by-day резерв дал неполное покрытие. Записей: {Count}, дней: {Days}, слотов: {Slots}",
                    normalizedRefined.Count,
                    normalizedRefined.Select(e => e.DayOfWeek).Distinct().Count(),
                    normalizedRefined.Select(e => (e.DayOfWeek, e.LessonNumber)).Distinct().Count());
            }
        }
        catch (Exception ex) when (IsRecoverableGroqFailure(ex))
        {
            _logger.LogWarning(ex, "Точечный day-by-day резерв через Groq сорвался");
        }

        return bestCandidate;
    }

    private bool ShouldUseGroq()
    {
        var hasGroqKey = !string.IsNullOrWhiteSpace(_groqApiKey)
                         && !_groqApiKey.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase);

        return _provider switch
        {
            "groq" => hasGroqKey,
            "gemini" or "ollama" => false,
            _ => hasGroqKey
        };
    }

    private bool ShouldUseGemini()
    {
        var hasGeminiKey = !string.IsNullOrWhiteSpace(_geminiApiKey)
                           && !_geminiApiKey.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase);

        return _provider switch
        {
            "gemini" => hasGeminiKey,
            "groq" or "ollama" => false,
            _ => hasGeminiKey
        };
    }

    private async Task<List<ScheduleEntry>> ParseWithGroqAsync(byte[] imageBytes, int? selectedSubGroup, CancellationToken ct)
    {
        var prompt = BuildSchedulePrompt(selectedSubGroup);
        var rawText = await SendGroqVisionPromptAsync(imageBytes, prompt, ct, useJsonMode: true, maxCompletionTokens: 1200);
        var firstPass = ExtractSchedule(rawText);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Groq вернул пустой ответ для расписания");
            return new();
        }

        if (firstPass.Count == 0)
            return new();

        if (selectedSubGroup is > 0)
        {
            var normalizedFirstPass = NormalizeSelectedSubGroupEntries(firstPass);
            if (IsReasonableSubgroupResult(normalizedFirstPass))
                return normalizedFirstPass;
        }

        var reviewedRaw = await ReviewGroqScheduleAsync(imageBytes, rawText, selectedSubGroup, ct);
        if (string.IsNullOrWhiteSpace(reviewedRaw))
            return firstPass;

        var reviewed = ExtractSchedule(reviewedRaw);
        return reviewed.Count >= firstPass.Count / 2 ? reviewed : firstPass;
    }

    private async Task<string> ReviewGroqScheduleAsync(byte[] imageBytes, string candidateJson, int? selectedSubGroup, CancellationToken ct)
    {
        var prompt = ReviewPromptTemplate.Replace("{CANDIDATE_JSON}", candidateJson);
        if (selectedSubGroup is > 0)
        {
            prompt += $"\nДополнительно: оставь только пары, относящиеся к подгруппе {selectedSubGroup}, и общие пары с subGroup = null.";
        }
        return await SendGroqVisionPromptAsync(imageBytes, prompt, ct, useJsonMode: true, maxCompletionTokens: 1200);
    }

    private async Task<string> ReviewGroqStructuredGridAsync(byte[] imageBytes, string candidateGrid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidateGrid))
            return string.Empty;

        var prompt = StructuredGridReviewPromptTemplate.Replace("{CANDIDATE_GRID}", candidateGrid);
        var reviewed = await SendGroqVisionPromptAsync(imageBytes, prompt, ct, useJsonMode: false, maxCompletionTokens: 900);

        _logger.LogInformation("Проверенная subgroup-grid таблица ({Count} строк): {Preview}",
            CountStructuredGridLines(reviewed),
            reviewed[..Math.Min(500, reviewed.Length)]);

        return reviewed;
    }

    private async Task<List<ScheduleEntry>> ParseScheduleDayForSubGroupAsync(byte[] imageBytes, int day, int selectedSubGroup, CancellationToken ct)
    {
        var croppedResult = await ParseScheduleDayVariantAsync(imageBytes, day, selectedSubGroup, ct, useCrop: true);

        if (!NeedsWholeImageRetryForDay(day, croppedResult))
            return croppedResult;

        var wholeImageResult = await ParseScheduleDayVariantAsync(imageBytes, day, selectedSubGroup, ct, useCrop: false);
        return ChooseBetterDayResult(croppedResult, wholeImageResult);
    }

    private async Task<List<ScheduleEntry>> ParseScheduleDayVariantAsync(
        byte[] imageBytes,
        int day,
        int selectedSubGroup,
        CancellationToken ct,
        bool useCrop)
    {
        var dayName = GetRussianDayName(day);
        var prompt = DayGridPromptTemplate
            .Replace("{DAY_NAME}", dayName)
            .Replace("{SUBGROUP}", selectedSubGroup.ToString());

        var dayImageBytes = useCrop ? TryCropScheduleDayImage(imageBytes, day) : imageBytes;

        var firstRaw = await SendGroqVisionPromptAsync(dayImageBytes, prompt, ct, useJsonMode: false, maxCompletionTokens: 220);
        _logger.LogInformation("Черновик для дня {DayName} ({Count} строк, crop={UseCrop}): {Preview}",
            dayName,
            CountDayGridLines(firstRaw),
            useCrop,
            firstRaw[..Math.Min(400, firstRaw.Length)]);

        var firstPass = ParseDayGridScheduleText(firstRaw, day);
        var firstCoverage = CountDayGridLines(firstRaw);

        if (!NeedsDayGridReview(firstRaw, firstPass))
            return firstPass;

        var reviewPrompt = DayGridReviewPromptTemplate
            .Replace("{DAY_NAME}", dayName)
            .Replace("{SUBGROUP}", selectedSubGroup.ToString())
            .Replace("{CANDIDATE_DAY_GRID}", firstRaw);

        var reviewedRaw = await SendGroqVisionPromptAsync(dayImageBytes, reviewPrompt, ct, useJsonMode: false, maxCompletionTokens: 220);
        _logger.LogInformation("Проверка для дня {DayName} ({Count} строк, crop={UseCrop}): {Preview}",
            dayName,
            CountDayGridLines(reviewedRaw),
            useCrop,
            reviewedRaw[..Math.Min(400, reviewedRaw.Length)]);

        var reviewedPass = ParseDayGridScheduleText(reviewedRaw, day);
        var reviewedCoverage = CountDayGridLines(reviewedRaw);

        if (reviewedCoverage >= firstCoverage && reviewedPass.Count > 0)
            return reviewedPass;

        return firstPass;
    }

    private static string BuildSchedulePrompt(int? selectedSubGroup)
    {
        if (selectedSubGroup is null or <= 0)
            return SchedulePrompt;

        return SchedulePrompt + $"""

            ДОПОЛНИТЕЛЬНОЕ УСЛОВИЕ:
            - Извлекай только пары для подгруппы {selectedSubGroup} и общие пары для обеих подгрупп.
            - Игнорируй пары, которые относятся только к другой подгруппе.
            - Если пара общая, оставляй subGroup: null.
            - Если пара только для подгруппы {selectedSubGroup}, ставь subGroup: {selectedSubGroup}.
            """;
    }

    private async Task<string> ExtractTextWithGroqAsync(byte[] imageBytes, CancellationToken ct)
    {
        var rawText = await SendGroqVisionPromptAsync(imageBytes, OcrPrompt, ct, useJsonMode: false, maxCompletionTokens: 300);
        return string.IsNullOrWhiteSpace(rawText) ? "[TEXT_NOT_FOUND]" : rawText.Trim();
    }

    private async Task<string> SendGroqVisionPromptAsync(byte[] imageBytes, string prompt, CancellationToken ct, bool useJsonMode, int maxCompletionTokens = 1500)
    {
        if (string.IsNullOrWhiteSpace(_groqApiKey))
            throw new InvalidOperationException("GroqApiKey не задан.");

        var base64Image = Convert.ToBase64String(imageBytes);
        var mimeType = DetectImageMimeType(imageBytes);
        var dataUrl = $"data:{mimeType};base64,{base64Image}";

        const int maxAttempts = 2;
        var requestTimeout = TimeSpan.FromSeconds(60);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var payload = new GroqChatRequest(
                Model: _groqModel,
                Messages:
                [
                    new GroqChatMessage(
                        Role: "user",
                        Content:
                        [
                            new GroqContentPart(Type: "text", Text: prompt),
                            new GroqContentPart(
                                Type: "image_url",
                                ImageUrl: new GroqImageUrl(dataUrl))
                        ])
                ],
                Temperature: 0.05,
                MaxCompletionTokens: maxCompletionTokens,
                ResponseFormat: useJsonMode ? new GroqResponseFormat("json_object") : null
            );

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
            {
                Content = JsonContent.Create(payload, options: JsonOpts)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _groqApiKey);

            _logger.LogInformation("Запрос к Groq ({Model}), размер фото: {Kb} КБ, попытка {Attempt}/{MaxAttempts}, max_tokens={Tokens}",
                _groqModel, imageBytes.Length / 1024, attempt, maxAttempts, maxCompletionTokens);

            HttpResponseMessage httpResponse;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(requestTimeout);
                httpResponse = await _http.SendAsync(request, cts.Token);
            }
            catch (TaskCanceledException) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                var delay = GetGroqTransientRetryDelay(attempt);
                _logger.LogWarning("Groq не ответил вовремя. Ждём {Delay} и повторяем попытку {NextAttempt}/{MaxAttempts}.",
                    delay, attempt + 1, maxAttempts);
                await Task.Delay(delay, ct);
                continue;
            }
            catch (TaskCanceledException)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);

                throw new InvalidOperationException($"Groq не ответил за {requestTimeout.TotalSeconds:0} секунд.");
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                var delay = GetGroqTransientRetryDelay(attempt);
                _logger.LogWarning(ex, "Ошибка подключения к Groq API. Ждём {Delay} и повторяем попытку {NextAttempt}/{MaxAttempts}.",
                    delay, attempt + 1, maxAttempts);
                await Task.Delay(delay, ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Ошибка подключения к Groq API");
                throw new InvalidOperationException("Не удалось подключиться к Groq API.", ex);
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                var err = await httpResponse.Content.ReadAsStringAsync(ct);

                if ((int)httpResponse.StatusCode == 429 && attempt < maxAttempts)
                {
                    var delay = GetGroqRetryDelay(httpResponse, err, attempt);
                    _logger.LogWarning("Groq rate limit. Ждём {Delay} и повторяем попытку {NextAttempt}/{MaxAttempts}. Тело: {Body}",
                        delay, attempt + 1, maxAttempts, err[..Math.Min(300, err.Length)]);
                    await Task.Delay(delay, ct);
                    continue;
                }

                _logger.LogError("Groq вернул {Code}: {Body}", httpResponse.StatusCode, err);
                throw new InvalidOperationException(
                    $"Ошибка Groq {(int)httpResponse.StatusCode}: {err[..Math.Min(300, err.Length)]}");
            }

            var groq = await httpResponse.Content.ReadFromJsonAsync<GroqChatResponse>(JsonOpts, ct);
            var rawText = string.Join("\n",
                groq?.Choices?
                    .Select(c => c.Message?.Content)
                    .Where(t => !string.IsNullOrWhiteSpace(t))!
                ?? []);

            _logger.LogInformation("Ответ от Groq ({Len} символов): {Preview}",
                rawText.Length, rawText[..Math.Min(200, rawText.Length)]);

            return rawText;
        }

        throw new InvalidOperationException("Groq не вернул ответ после нескольких попыток.");
    }

    private async Task<string> ExtractTextWithGeminiAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_geminiApiKey))
            throw new InvalidOperationException("GeminiApiKey не задан.");

        var base64Image = Convert.ToBase64String(imageBytes);
        var mimeType = DetectImageMimeType(imageBytes);

        var payload = new GeminiRequest(
            Contents:
            [
                new GeminiContent(
                    Parts:
                    [
                        new GeminiPart(Text: OcrPrompt),
                        new GeminiPart(InlineData: new GeminiInlineData(MimeType: mimeType, Data: base64Image))
                    ])
            ],
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: 0.05
            )
        );

        var endpoint =
            $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiModel}:generateContent?key={Uri.EscapeDataString(_geminiApiKey)}";

        using var content = JsonContent.Create(payload, options: JsonOpts);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));

        var httpResponse = await _http.PostAsync(endpoint, content, cts.Token);
        if (!httpResponse.IsSuccessStatusCode)
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ошибка Gemini {(int)httpResponse.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        var gemini = await httpResponse.Content.ReadFromJsonAsync<GeminiResponse>(JsonOpts, ct);
        var rawText = string.Join("\n",
            gemini?.Candidates?
                .SelectMany(c => c.Content?.Parts ?? [])
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))!
            ?? []);

        return string.IsNullOrWhiteSpace(rawText) ? "[TEXT_NOT_FOUND]" : rawText.Trim();
    }

    private async Task<List<ScheduleEntry>> ParseWithGeminiAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_geminiApiKey))
            throw new InvalidOperationException("GeminiApiKey не задан.");

        var base64Image = Convert.ToBase64String(imageBytes);
        var mimeType = DetectImageMimeType(imageBytes);

        var payload = new GeminiRequest(
            Contents:
            [
                new GeminiContent(
                    Parts:
                    [
                        new GeminiPart(Text: SchedulePrompt),
                        new GeminiPart(InlineData: new GeminiInlineData(MimeType: mimeType, Data: base64Image))
                    ])
            ],
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: 0.05,
                ResponseMimeType: "application/json"
            )
        );

        var endpoint =
            $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiModel}:generateContent?key={Uri.EscapeDataString(_geminiApiKey)}";

        using var content = JsonContent.Create(payload, options: JsonOpts);

        _logger.LogInformation("Запрос к Gemini ({Model}), размер фото: {Kb} КБ", _geminiModel, imageBytes.Length / 1024);

        HttpResponseMessage httpResponse;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            httpResponse = await _http.PostAsync(endpoint, content, cts.Token);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Gemini не ответила за 3 минуты.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка подключения к Gemini API");
            throw new InvalidOperationException("Не удалось подключиться к Gemini API.", ex);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gemini вернула {Code}: {Body}", httpResponse.StatusCode, err);
            throw new InvalidOperationException(
                $"Ошибка Gemini {(int)httpResponse.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        var gemini = await httpResponse.Content.ReadFromJsonAsync<GeminiResponse>(JsonOpts, ct);
        var rawText = string.Join("\n",
            gemini?.Candidates?
                .SelectMany(c => c.Content?.Parts ?? [])
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))!
            ?? []);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Gemini вернула пустой ответ для расписания");
            return new();
        }

        _logger.LogInformation("Ответ от Gemini ({Len} символов): {Preview}",
            rawText.Length, rawText[..Math.Min(200, rawText.Length)]);

        return ExtractSchedule(rawText);
    }

    private async Task<List<ScheduleEntry>> ParseWithOllamaAsync(byte[] imageBytes, CancellationToken ct)
    {
        var base64Image = Convert.ToBase64String(imageBytes);
        _logger.LogInformation("Request to Ollama ({Model}), image size: {Kb} KB",
            _model, imageBytes.Length / 1024);

        var firstRaw = await SendOllamaGenerateAsync(base64Image, SchedulePrompt, ct);
        var firstParsed = ExtractSchedule(firstRaw);
        if (firstParsed.Count > 0)
            return firstParsed;

        _logger.LogWarning("First Ollama attempt returned 0 entries, running retry");
        var retryRaw = await SendOllamaGenerateAsync(base64Image, SchedulePrompt + RetryStrictSuffix, ct);
        return ExtractSchedule(retryRaw);
    }

    private async Task<string> SendOllamaGenerateAsync(string base64Image, string prompt, CancellationToken ct)
    {
        var payload = new OllamaRequest(
            Model: _model,
            Prompt: prompt,
            Images: new[] { base64Image },
            Stream: false,
            Format: "json",
            Options: new OllamaOptions(Temperature: 0.0, TopP: 0.9)
        );
        var requestJson = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        HttpResponseMessage httpResponse;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            httpResponse = await _http.PostAsync($"{_ollamaUrl}/api/generate", content, cts.Token);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Ollama did not respond within 5 minutes. The model may still be loading.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama ({Url})", _ollamaUrl);
            throw new InvalidOperationException(
                $"Could not connect to Ollama at {_ollamaUrl}. Make sure Ollama is running: ollama serve", ex);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Ollama returned {Code}: {Body}", httpResponse.StatusCode, err);
            throw new InvalidOperationException($"Ollama error {(int)httpResponse.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        var ollamaResult = await httpResponse.Content.ReadFromJsonAsync<OllamaResponse>(JsonOpts, ct);
        var rawText = ollamaResult?.Response ?? string.Empty;

        _logger.LogInformation("Ollama response ({Len} chars): {Preview}",
            rawText.Length, rawText[..Math.Min(200, rawText.Length)]);

        return rawText;
    }
    private static string DetectImageMimeType(byte[] image)
    {
        if (image.Length >= 8 &&
            image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47)
            return "image/png";

        if (image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF)
            return "image/jpeg";

        return "image/jpeg";
    }

    private byte[] TryCropScheduleDayImage(byte[] imageBytes, int day)
    {
        try
        {
            using var input = new MemoryStream(imageBytes);
            using var original = new Bitmap(input);

            var pageRect = FindNonDarkBounds(original);
            var bodyRect = EstimateScheduleBodyRect(original, pageRect);
            var dayRect = GetDayBandRect(original, bodyRect, day);

            using var cropped = CropBitmap(original, dayRect);
            using var upscaled = ResizeBitmap(cropped, targetHeight: 700, maxScale: 5.0);
            using var output = new MemoryStream();
            upscaled.Save(output, ImageFormat.Png);

            _logger.LogInformation(
                "День {Day}: crop {CropW}x{CropH} из исходного {SrcW}x{SrcH}, страница={Page}, тело={Body}, день={DayRect}",
                day,
                upscaled.Width,
                upscaled.Height,
                original.Width,
                original.Height,
                pageRect,
                bodyRect,
                dayRect);

            return output.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось подготовить crop для дня {Day}, отправляю исходное изображение", day);
            return imageBytes;
        }
    }

    private static Rectangle FindNonDarkBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width - 1;
        var minY = bitmap.Height - 1;
        var maxX = 0;
        var maxY = 0;
        var found = false;

        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R + pixel.G + pixel.B < 90)
                    continue;

                found = true;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (!found)
            return new Rectangle(0, 0, bitmap.Width, bitmap.Height);

        var rect = Rectangle.FromLTRB(
            Math.Max(0, minX - 6),
            Math.Max(0, minY - 6),
            Math.Min(bitmap.Width, maxX + 7),
            Math.Min(bitmap.Height, maxY + 7));

        return rect.Width > 50 && rect.Height > 50
            ? rect
            : new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    }

    private static Rectangle EstimateScheduleBodyRect(Bitmap bitmap, Rectangle pageRect)
    {
        var detectedTop = FindLastStrongHorizontalLine(bitmap, pageRect, 0.18, 0.40);
        var defaultTop = pageRect.Y + (int)(pageRect.Height * 0.27);
        var minTop = pageRect.Y + (int)(pageRect.Height * 0.20);
        var maxTop = pageRect.Y + (int)(pageRect.Height * 0.33);
        var top = Math.Clamp(detectedTop ?? defaultTop, minTop, maxTop);

        var left = pageRect.X + (int)(pageRect.Width * 0.01);
        var width = Math.Max(50, (int)(pageRect.Width * 0.98));
        var bottom = pageRect.Y + (int)(pageRect.Height * 0.965);

        top = Math.Clamp(top, pageRect.Y, pageRect.Bottom - 80);
        bottom = Math.Clamp(bottom, top + 80, pageRect.Bottom);

        return new Rectangle(left, top, Math.Min(width, pageRect.Right - left), bottom - top);
    }

    private static int? FindLastStrongHorizontalLine(Bitmap bitmap, Rectangle rect, double startFraction, double endFraction)
    {
        var startY = rect.Y + (int)(rect.Height * startFraction);
        var endY = rect.Y + (int)(rect.Height * endFraction);
        var candidateRows = new List<int>();

        for (var y = startY; y <= endY && y < bitmap.Height; y++)
        {
            var dark = 0;
            var total = 0;

            for (var x = rect.X + 4; x < rect.Right - 4 && x < bitmap.Width; x += 2)
            {
                total++;
                if (bitmap.GetPixel(x, y).GetBrightness() < 0.45f)
                    dark++;
            }

            if (total > 0 && dark / (double)total >= 0.45)
                candidateRows.Add(y);
        }

        if (candidateRows.Count == 0)
            return null;

        return candidateRows.Last() + 2;
    }

    private static Rectangle GetDayBandRect(Bitmap bitmap, Rectangle bodyRect, int day)
    {
        var boundaries = FindDayBoundaries(bitmap, bodyRect);
        var y1 = boundaries[day - 1];
        var y2 = boundaries[day];

        var dayHeight = Math.Max(1, y2 - y1);
        var topOverlap = Math.Max(6, (int)Math.Round(dayHeight * 0.05));
        var bottomOverlap = Math.Max(2, (int)Math.Round(dayHeight * 0.015));
        var top = day == 1 ? y1 : y1 - topOverlap;
        var bottom = day == 6 ? y2 : y2 + bottomOverlap;

        top = Math.Clamp(top, bodyRect.Y, bodyRect.Bottom - 10);
        bottom = Math.Clamp(bottom, top + 10, bodyRect.Bottom);

        return new Rectangle(bodyRect.X, top, bodyRect.Width, bottom - top);
    }

    private static List<int> FindDayBoundaries(Bitmap bitmap, Rectangle bodyRect)
    {
        var dayHeight = bodyRect.Height / 6.0;
        var lineCandidates = FindStrongHorizontalLineCenters(bitmap, bodyRect);
        var boundaries = new List<int> { bodyRect.Y };

        for (var i = 1; i <= 5; i++)
        {
            var expected = bodyRect.Y + (int)Math.Round(i * dayHeight);
            var tolerance = Math.Max(18, (int)Math.Round(dayHeight * 0.18));

            var snapped = lineCandidates
                .Where(y => Math.Abs(y - expected) <= tolerance)
                .OrderBy(y => Math.Abs(y - expected))
                .FirstOrDefault();

            var boundary = snapped != 0 ? snapped : expected;
            var minBoundary = boundaries[^1] + Math.Max(24, (int)Math.Round(dayHeight * 0.45));
            var maxBoundary = bodyRect.Bottom - Math.Max(24, (int)Math.Round((6 - i) * dayHeight * 0.45));
            boundaries.Add(Math.Clamp(boundary, minBoundary, maxBoundary));
        }

        boundaries.Add(bodyRect.Bottom);
        return boundaries;
    }

    private static List<int> FindStrongHorizontalLineCenters(Bitmap bitmap, Rectangle rect)
    {
        var rows = new List<int>();

        for (var y = rect.Y; y < rect.Bottom && y < bitmap.Height; y++)
        {
            var dark = 0;
            var total = 0;

            for (var x = rect.X + 4; x < rect.Right - 4 && x < bitmap.Width; x += 2)
            {
                total++;
                if (bitmap.GetPixel(x, y).GetBrightness() < 0.55f)
                    dark++;
            }

            if (total > 0 && dark / (double)total >= 0.28)
                rows.Add(y);
        }

        if (rows.Count == 0)
            return [];

        var centers = new List<int>();
        var start = rows[0];
        var prev = rows[0];

        for (var i = 1; i < rows.Count; i++)
        {
            var current = rows[i];
            if (current - prev <= 2)
            {
                prev = current;
                continue;
            }

            centers.Add((start + prev) / 2);
            start = current;
            prev = current;
        }

        centers.Add((start + prev) / 2);
        return centers;
    }

    private static Bitmap CropBitmap(Bitmap source, Rectangle rect)
    {
        var target = new Bitmap(rect.Width, rect.Height);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        return target;
    }

    private static Bitmap ResizeBitmap(Bitmap source, int targetHeight, double maxScale)
    {
        if (source.Height <= 0 || source.Width <= 0)
            return new Bitmap(source);

        var scale = Math.Max(1.0, targetHeight / (double)source.Height);
        scale = Math.Min(scale, maxScale);

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.Clear(Color.White);
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    // Парсинг JSON из ответа модели.

    private List<ScheduleEntry> ExtractSchedule(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Модель вернула пустой ответ");
            return new();
        }

        foreach (var candidate in EnumerateJsonCandidates(rawText))
        {
            try
            {
                var entries = TryParseFlexible(candidate);
                if (entries.Count == 0) continue;

                var normalized = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Subject))
                    .Where(e => e.DayOfWeek is >= 1 and <= 7)
                    .Where(e => e.LessonNumber is >= 1 and <= 4)
                    .Where(e => e.Subject.Length >= 3)
                    .Select(e => new ScheduleEntry
                    {
                        DayOfWeek = e.DayOfWeek,
                        LessonNumber = e.LessonNumber,
                        Subject = e.Subject.Trim(),
                        SubGroup = e.SubGroup is > 0 ? e.SubGroup : null,
                        WeekType = e.WeekType is 1 or 2 ? e.WeekType : null
                    })
                    .GroupBy(e => new { e.DayOfWeek, e.LessonNumber, Subject = e.Subject.ToLowerInvariant(), e.SubGroup, e.WeekType })
                    .Select(g => g.First())
                    .OrderBy(e => e.DayOfWeek)
                    .ThenBy(e => e.LessonNumber)
                    .ThenBy(e => e.SubGroup ?? 0)
                    .ThenBy(e => e.WeekType ?? 0)
                    .ToList();

                normalized = RemoveConflictingDuplicates(normalized);

                _logger.LogInformation("Распознано {Count} записей расписания", normalized.Count);
                return normalized;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Кандидат JSON не удалось распарсить как расписание");
            }
        }

        _logger.LogWarning("Не удалось извлечь валидное расписание из ответа модели. Ответ: {Text}", rawText);
        return new();
    }

    private List<ScheduleEntry> TryParseFlexible(string jsonText)
    {
        var root = JsonNode.Parse(jsonText);
        if (root is null) return new();

        var rows = ExtractRows(root);
        if (rows.Count == 0) return new();

        var result = new List<ScheduleEntry>(rows.Count);
        foreach (var row in rows.OfType<JsonObject>())
        {
            var dayRaw = GetInt(row, "day", "dayOfWeek", "weekday", "dow", "day_num")
                         ?? ParseDayName(GetString(row, "dayName", "day_name", "day", "weekday"));
            var lessonRaw = GetInt(row, "lesson", "lessonNumber", "pair", "class", "number", "lesson_num", "para");
            var subjectRaw = GetString(row, "subject", "name", "discipline", "lessonName", "title", "item");
            var subGroupRaw = GetInt(row, "subGroup", "subgroup", "sub_group", "group", "groupNumber", "subGroupNumber");
            var weekRaw = GetInt(row, "weekType", "week_type", "week")
                          ?? ParseWeekType(GetString(row, "weekType", "week_type", "week"));

            if (dayRaw is null || lessonRaw is null || string.IsNullOrWhiteSpace(subjectRaw))
                continue;

            result.Add(new ScheduleEntry
            {
                DayOfWeek = dayRaw.Value,
                LessonNumber = lessonRaw.Value,
                Subject = NormalizeSubject(subjectRaw),
                SubGroup = subGroupRaw,
                WeekType = weekRaw
            });
        }

        return result;
    }

    private static List<JsonNode?> ExtractRows(JsonNode root)
    {
        if (root is JsonArray directArray) return directArray.ToList();

        if (root is not JsonObject obj) return new();

        foreach (var key in new[] { "schedule", "entries", "lessons", "data", "result", "items" })
        {
            if (obj[key] is JsonArray arr)
                return arr.ToList();
        }

        // Иногда модель отдаёт объект одной пары вместо массива.
        return new List<JsonNode?> { obj };
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string rawText)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yieldReturnList = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var trimmed = candidate.Trim();
            if (trimmed.Length < 2) return;
            if (seen.Add(trimmed)) yieldReturnList.Add(trimmed);
        }

        Add(rawText);

        foreach (Match m in Regex.Matches(rawText, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase))
            Add(m.Groups[1].Value);

        var spans = ExtractBalancedJson(rawText);
        foreach (var span in spans)
            Add(span);

        return yieldReturnList;
    }

    private static IEnumerable<string> ExtractBalancedJson(string text)
    {
        var result = new List<string>();

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('{' or '[')) continue;
            var open = text[i];
            var close = open == '{' ? '}' : ']';
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var j = i; j < text.Length; j++)
            {
                var ch = text[j];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"') { inString = true; continue; }
                if (ch == open) depth++;
                if (ch == close) depth--;

                if (depth == 0)
                {
                    result.Add(text[i..(j + 1)]);
                    break;
                }
            }
        }

        return result;
    }

    private static int? GetInt(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (node is JsonValue val)
            {
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
            }
        }

        return null;
    }

    private static string? GetString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (node is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }

        return null;
    }

    private static int? ParseDayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();

        return v switch
        {
            "1" or "mon" or "monday" or "понедельник" or "пн" => 1,
            "2" or "tue" or "tuesday" or "вторник" or "вт" => 2,
            "3" or "wed" or "wednesday" or "среда" or "среду" or "ср" => 3,
            "4" or "thu" or "thursday" or "четверг" or "чт" => 4,
            "5" or "fri" or "friday" or "пятница" or "пятницу" or "пт" => 5,
            "6" or "sat" or "saturday" or "суббота" or "субботу" or "сб" => 6,
            "7" or "sun" or "sunday" or "воскресенье" or "вс" => 7,
            _ => null
        };
    }

    private static int? ParseWeekType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();

        if (v is "1" or "odd" or "нечет" or "нечёт" or "нечетная" or "нечётная")
            return 1;

        if (v is "2" or "even" or "чет" or "чёт" or "четная" or "чётная")
            return 2;

        return null;
    }

    private static string NormalizeSubject(string subject)
    {
        var normalized = subject.Trim();
        normalized = Regex.Replace(normalized, @"[\r\n]+", " ");
        normalized = Regex.Replace(normalized, @"\((?:лекц|лекция|практ|практика|лаб|лабораторн)[^)]*\)", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\((?:подгр|подгруппа)[^)]*\)", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bподгруппа\s*\d+\b", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bподгр\.?\s*\d+\b", "", RegexOptions.IgnoreCase);
        var commaIndex = normalized.IndexOf(',');
        if (commaIndex >= 0)
            normalized = normalized[..commaIndex];
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = normalized.Trim(' ', ',', ';', ':', '-', '—');
        return normalized.Length == 0 ? string.Empty : normalized;
    }

    private static List<ScheduleEntry> RemoveConflictingDuplicates(List<ScheduleEntry> entries)
    {
        var result = new List<ScheduleEntry>();

        foreach (var lessonGroup in entries.GroupBy(e => new { e.DayOfWeek, e.LessonNumber, e.SubGroup, e.WeekType }))
        {
            var items = lessonGroup.ToList();
            if (items.Count == 1)
            {
                result.Add(items[0]);
                continue;
            }

            // Если модель вернула несколько разных предметов для одной и той же пары/подгруппы/недели,
            // оставляем самый длинный как более вероятный полный вариант.
            result.Add(items
                .OrderByDescending(e => e.Subject.Length)
                .ThenBy(e => e.Subject)
                .First());
        }

        return result
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.LessonNumber)
            .ThenBy(e => e.SubGroup ?? 0)
            .ThenBy(e => e.WeekType ?? 0)
            .ToList();
    }

    private static List<ScheduleEntry> NormalizeSelectedSubGroupEntries(List<ScheduleEntry> entries)
    {
        return RemoveConflictingDuplicates(entries
            .Select(e => new ScheduleEntry
            {
                DayOfWeek = e.DayOfWeek,
                LessonNumber = e.LessonNumber,
                Subject = e.Subject,
                SubGroup = null,
                WeekType = e.WeekType
            })
            .ToList());
    }

    private static int CountStructuredGridLines(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return 0;

        return rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Count(l => Regex.IsMatch(
                l,
                @"^D[1-6]L[1-4]\s*\|\s*A_ODD=.*?\|\s*A_EVEN=.*?\|\s*B_ODD=.*?\|\s*B_EVEN=.*$",
                RegexOptions.IgnoreCase));
    }

    private static int CountDayGridLines(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return 0;

        return rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Count(l => Regex.IsMatch(
                l,
                @"^L[1-4]\s*\|\s*ODD=.*?\|\s*EVEN=.*$",
                RegexOptions.IgnoreCase));
    }

    private static bool IsReasonableDayByDayResult(List<ScheduleEntry> entries)
    {
        if (entries.Count < 8)
            return false;

        var dayCount = entries
            .Select(e => e.DayOfWeek)
            .Distinct()
            .Count();

        var lessonSlots = entries
            .Select(e => (e.DayOfWeek, e.LessonNumber))
            .Distinct()
            .Count();

        return dayCount >= 4 && lessonSlots >= 10;
    }

    private static bool IsReasonableSubgroupResult(List<ScheduleEntry> entries)
        => IsReasonableDayByDayResult(entries);

    private static List<int> GetDaysNeedingRefinement(List<ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return Enumerable.Range(1, 6).ToList();

        var result = new List<int>();

        for (var day = 1; day <= 6; day++)
        {
            var dayEntries = entries
                .Where(e => e.DayOfWeek == day)
                .ToList();

            var daySlots = dayEntries
                .Select(e => e.LessonNumber)
                .Distinct()
                .Count();

            if (daySlots <= 1)
            {
                result.Add(day);
                continue;
            }

            var lessonNumbers = dayEntries
                .Select(e => e.LessonNumber)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            var hasGap = lessonNumbers.Count >= 2 &&
                         lessonNumbers.Last() - lessonNumbers.First() + 1 != lessonNumbers.Count;

            var hasWeekSplit = dayEntries.Any(e => e.WeekType.HasValue);

            if ((daySlots <= 2 && hasGap) || (daySlots <= 3 && hasWeekSplit))
                result.Add(day);
        }

        return result;
    }

    private static List<int> GetDaysNeedingRefinement(List<ScheduleEntry> primary, List<ScheduleEntry> secondary)
    {
        var result = new HashSet<int>(GetDaysNeedingRefinement(primary));

        foreach (var day in GetDaysNeedingRefinement(secondary))
            result.Add(day);

        foreach (var day in GetDaysWithDisagreements(primary, secondary))
            result.Add(day);

        return result.OrderBy(d => d).ToList();
    }

    private static List<int> GetDaysWithDisagreements(List<ScheduleEntry> primary, List<ScheduleEntry> secondary)
    {
        var result = new List<int>();

        for (var day = 1; day <= 6; day++)
        {
            var left = BuildDaySignature(primary, day);
            var right = BuildDaySignature(secondary, day);

            if (left.Count == 0 && right.Count == 0)
                continue;

            if (!left.SetEquals(right))
                result.Add(day);
        }

        return result;
    }

    private static bool NeedsWholeImageRetryForDay(int day, List<ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return true;

        var lessons = entries
            .Select(e => e.LessonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        var hasGap = lessons.Count >= 2 &&
                     lessons.Last() - lessons.First() + 1 != lessons.Count;

        var hasWeekSplit = entries.Any(e => e.WeekType.HasValue);

        if (!lessons.Contains(1))
            return true;

        if (day == 6 && lessons.Count <= 1)
            return true;

        if (hasGap)
            return true;

        if (hasWeekSplit && lessons.Count <= 3)
            return true;

        return false;
    }

    private static List<ScheduleEntry> ChooseBetterDayResult(List<ScheduleEntry> primary, List<ScheduleEntry> fallback)
    {
        var primaryScore = ScoreDayResult(primary);
        var fallbackScore = ScoreDayResult(fallback);

        if (fallbackScore > primaryScore)
            return fallback;

        if (fallbackScore == primaryScore && fallback.Count > primary.Count)
            return fallback;

        return primary;
    }

    private static int ScoreDayResult(List<ScheduleEntry> entries)
    {
        if (entries.Count == 0)
            return 0;

        var lessons = entries
            .Select(e => e.LessonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        var score = lessons.Count * 10;

        if (lessons.Contains(1))
            score += 6;

        if (lessons.Contains(4))
            score += 2;

        var hasGap = lessons.Count >= 2 &&
                     lessons.Last() - lessons.First() + 1 != lessons.Count;
        if (hasGap)
            score -= 6;

        var hasWeekSplit = entries.Any(e => e.WeekType.HasValue);
        if (hasWeekSplit && lessons.Count <= 3)
            score -= 4;

        return score;
    }

    private static HashSet<string> BuildDaySignature(List<ScheduleEntry> entries, int day)
    {
        return entries
            .Where(e => e.DayOfWeek == day)
            .Select(e => $"{e.LessonNumber}|{e.WeekType ?? 0}|{NormalizeComparableSubject(e.Subject)}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableSubject(string subject)
        => NormalizeSubject(subject).ToLowerInvariant();

    private static List<int> ExtractSubGroups(string rawText)
    {
        var parsed = new List<int>();

        foreach (var candidate in EnumerateJsonCandidates(rawText))
        {
            try
            {
                var root = JsonNode.Parse(candidate)?.AsObject();
                var values = root?["subGroups"]?.AsArray()
                    .Select(node => node?.GetValue<int>() ?? 0)
                    .Where(v => v > 0)
                    .Distinct()
                    .Take(2)
                    .ToList();

                if (values is { Count: >= 2 })
                    return values;
            }
            catch
            {
                // ignored
            }
        }

        foreach (Match match in Regex.Matches(rawText, @"подгрупп[аы]?\s*(\d+)", RegexOptions.IgnoreCase))
        {
            if (!int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || parsed.Contains(value))
                continue;

            parsed.Add(value);
            if (parsed.Count == 2)
                return parsed;
        }

        foreach (Match match in Regex.Matches(rawText, @"\b(\d+)\b"))
        {
            if (!int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || parsed.Contains(value))
                continue;

            parsed.Add(value);
            if (parsed.Count == 2)
                return parsed;
        }

        return [1, 2];
    }

    private static List<int> ResolveSubGroupColumns(IReadOnlyList<int> availableSubGroups, int selectedSubGroup)
    {
        var values = (availableSubGroups ?? Array.Empty<int>())
            .Where(v => v > 0)
            .Distinct()
            .Take(2)
            .ToList();

        if (values.Count == 0)
            values.AddRange([selectedSubGroup, selectedSubGroup == 1 ? 2 : 1]);
        else if (values.Count == 1)
            values.Add(values[0] == selectedSubGroup ? (selectedSubGroup == 1 ? 2 : 1) : selectedSubGroup);

        if (!values.Contains(selectedSubGroup))
            throw new InvalidOperationException("Выбранная подгруппа не найдена в шапке расписания.");

        return values;
    }

    private static bool IsRecoverableGroqFailure(Exception ex)
    {
        if (ex is HttpRequestException)
            return true;

        if (ex is not InvalidOperationException invalidOp)
            return false;

        return invalidOp.Message.Contains("Groq", StringComparison.OrdinalIgnoreCase)
               || invalidOp.Message.Contains("подключиться", StringComparison.OrdinalIgnoreCase)
               || invalidOp.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
               || invalidOp.Message.Contains("не ответил", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsDayGridReview(string rawText, List<ScheduleEntry> firstPass)
    {
        if (CountDayGridLines(rawText) < 4)
            return true;

        if (firstPass.Count == 0)
            return true;

        var lessonCount = firstPass
            .Select(e => e.LessonNumber)
            .Distinct()
            .Count();

        return lessonCount <= 1;
    }

    private static TimeSpan GetGroqRetryDelay(HttpResponseMessage response, string errorBody, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            return delta + TimeSpan.FromMilliseconds(300);

        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            var fromDate = retryDate - DateTimeOffset.UtcNow;
            if (fromDate > TimeSpan.Zero)
                return fromDate + TimeSpan.FromMilliseconds(300);
        }

        var match = Regex.Match(errorBody, @"try again in\s+([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase);
        if (match.Success &&
            double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(300);
        }

        return TimeSpan.FromSeconds(Math.Min(4 * attempt, 12));
    }

    private static TimeSpan GetGroqTransientRetryDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(2 * attempt, 8));

    private static string GetRussianDayName(int day) => day switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        7 => "Воскресенье",
        _ => $"День {day}"
    };

    private List<ScheduleEntry> ParseDayGridScheduleText(string rawText, int day)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new();

        var result = new List<ScheduleEntry>();
        var lines = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^L([1-4])\s*\|\s*ODD=(.*?)\s*\|\s*EVEN=(.*?)$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            var lesson = int.Parse(match.Groups[1].Value);
            var oddRaw = match.Groups[2].Value;
            var evenRaw = match.Groups[3].Value;
            var oddSubject = NormalizeGridSubject(oddRaw);
            var evenSubject = NormalizeGridSubject(evenRaw);

            if (oddSubject is null && evenSubject is null)
                continue;

            if (ShouldCollapseWeekSplit(oddRaw, evenRaw, oddSubject, evenSubject))
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = oddSubject!,
                    SubGroup = null,
                    WeekType = null
                });
                continue;
            }

            if (oddSubject is not null)
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = oddSubject,
                    SubGroup = null,
                    WeekType = 1
                });
            }

            if (evenSubject is not null)
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = evenSubject,
                    SubGroup = null,
                    WeekType = 2
                });
            }
        }

        return RemoveConflictingDuplicates(result);
    }

    private List<ScheduleEntry> ParseStructuredGridScheduleText(
        string rawText,
        int selectedSubGroup,
        IReadOnlyList<int> subgroupColumns)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new();

        var selectedColumnIndex = subgroupColumns[0] == selectedSubGroup ? 0 : 1;

        var result = new List<ScheduleEntry>();
        var lines = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^D([1-6])L([1-4])\s*\|\s*A_ODD=(.*?)\s*\|\s*A_EVEN=(.*?)\s*\|\s*B_ODD=(.*?)\s*\|\s*B_EVEN=(.*?)$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            var day = int.Parse(match.Groups[1].Value);
            var lesson = int.Parse(match.Groups[2].Value);
            var oddRaw = selectedColumnIndex == 0 ? match.Groups[3].Value : match.Groups[5].Value;
            var evenRaw = selectedColumnIndex == 0 ? match.Groups[4].Value : match.Groups[6].Value;
            var oddSubject = NormalizeGridSubject(oddRaw);
            var evenSubject = NormalizeGridSubject(evenRaw);

            if (oddSubject is null && evenSubject is null)
                continue;

            if (ShouldCollapseWeekSplit(oddRaw, evenRaw, oddSubject, evenSubject))
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = oddSubject!,
                    SubGroup = null,
                    WeekType = null
                });
                continue;
            }

            if (oddSubject is not null)
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = oddSubject,
                    SubGroup = null,
                    WeekType = 1
                });
            }

            if (evenSubject is not null)
            {
                result.Add(new ScheduleEntry
                {
                    DayOfWeek = day,
                    LessonNumber = lesson,
                    Subject = evenSubject,
                    SubGroup = null,
                    WeekType = 2
                });
            }
        }

        return RemoveConflictingDuplicates(result);
    }

    private static string? NormalizeGridSubject(string rawValue)
    {
        var cleaned = NormalizeSubject(rawValue);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        return cleaned switch
        {
            "-" => null,
            "—" => null,
            "..." => null,
            "…" => null,
            "нет" => null,
            "пары нет" => null,
            _ => cleaned
        };
    }

    private static bool ShouldCollapseWeekSplit(string oddRaw, string evenRaw, string? oddSubject, string? evenSubject)
    {
        if (oddSubject is null || evenSubject is null)
            return false;

        if (!string.Equals(oddSubject, evenSubject, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(
            NormalizeWeekIdentity(oddRaw),
            NormalizeWeekIdentity(evenRaw),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWeekIdentity(string rawValue)
    {
        var normalized = rawValue.Trim();
        normalized = Regex.Replace(normalized, @"[\r\n]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = normalized.Trim(' ', '|');
        return normalized;
    }

    // Форматирование расписания в читаемый текст.

    /// <summary>
    /// Построить читаемый текст расписания, сгруппированный по дням.
    /// </summary>
    public static string FormatSchedule(List<ScheduleEntry> entries, int? currentWeekType = null)
    {
        if (entries.Count == 0) return "Расписание пусто.";

        var sb = new StringBuilder();

        var byDay = entries
            .GroupBy(e => e.DayOfWeek)
            .OrderBy(g => g.Key);

        foreach (var day in byDay)
        {
            sb.AppendLine($"\n<b>{day.First().DayName}:</b>");

            var byLesson = day
                .GroupBy(e => e.LessonNumber)
                .OrderBy(g => g.Key);

            foreach (var lesson in byLesson)
            {
                var lessonEntries = lesson
                    .OrderBy(e => e.WeekType ?? 0)
                    .ThenBy(e => e.SubGroup ?? 0)
                    .ToList();

                var hasWeekSplit = lessonEntries.Any(e => e.WeekType.HasValue);
                if (hasWeekSplit)
                {
                    var firstWeek = lessonEntries.Where(e => e.WeekType == 1).ToList();
                    var secondWeek = lessonEntries.Where(e => e.WeekType == 2).ToList();

                    sb.AppendLine($"  {lesson.Key}. первая неделя: {FormatWeekLesson(firstWeek, currentWeekType == 1)}");
                    sb.AppendLine($"     вторая неделя: {FormatWeekLesson(secondWeek, currentWeekType == 2)}");
                    continue;
                }

                foreach (var entry in lessonEntries)
                {
                    sb.AppendLine($"  {entry.LessonNumber}. {entry.Subject}{entry.SubGroupLabel}");
                }
            }
        }

        return sb.ToString().TrimStart('\n');
    }

    private static string FormatWeekLesson(List<ScheduleEntry> entries, bool isActiveWeek)
    {
        if (entries.Count == 0)
            return "пары нет";

        var joined = string.Join("; ", entries
            .OrderBy(e => e.SubGroup ?? 0)
            .Select(e => $"{e.Subject}{e.SubGroupLabel}"));

        return isActiveWeek ? joined + " ◀" : joined;
    }

    // Внутренние DTO для API.

        private record OllamaRequest(
        [property: JsonPropertyName("model")]   string   Model,
        [property: JsonPropertyName("prompt")]  string   Prompt,
        [property: JsonPropertyName("images")]  string[] Images,
        [property: JsonPropertyName("stream")]  bool     Stream,
        [property: JsonPropertyName("format")]  string?  Format,
        [property: JsonPropertyName("options")] OllamaOptions Options
    );
    private record OllamaOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("top_p")] double TopP
    );
    private record OllamaResponse(
        [property: JsonPropertyName("response")] string Response
    );

    private record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig
    );

    private record GeminiContent(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts
    );

    private record GeminiPart(
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("inline_data")] GeminiInlineData? InlineData = null
    );

    private record GeminiInlineData(
        [property: JsonPropertyName("mime_type")] string MimeType,
        [property: JsonPropertyName("data")] string Data
    );

    private record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("responseMimeType")] string? ResponseMimeType = null
    );

    private record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates
    );

    private record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content
    );

    private record GroqChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] GroqChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens,
        [property: JsonPropertyName("response_format")] GroqResponseFormat? ResponseFormat
    );

    private record GroqChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] GroqContentPart[] Content
    );

    private record GroqContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("image_url")] GroqImageUrl? ImageUrl = null
    );

    private record GroqImageUrl(
        [property: JsonPropertyName("url")] string Url
    );

    private record GroqResponseFormat(
        [property: JsonPropertyName("type")] string Type
    );

    private record GroqChatResponse(
        [property: JsonPropertyName("choices")] GroqChoice[]? Choices
    );

    private record GroqChoice(
        [property: JsonPropertyName("message")] GroqMessage? Message
    );

    private record GroqMessage(
        [property: JsonPropertyName("content")] string? Content
    );

    // Поддерживаем имя поля "schedule" для основного JSON-ответа.
    private record ScheduleJsonRoot(
        [property: JsonPropertyName("schedule")] List<ScheduleEntry> Schedule
    );

    // Оставлено для обратной совместимости с ранее сохранёнными данными.
    private static List<ScheduleEntry> NormalizeEntries(List<ScheduleEntry> entries)
    {
        // Сейчас дополнительная нормализация не требуется.
        return entries;
    }
}

