using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// РЎРµСЂРІРёСЃ РїР°СЂСЃРёРЅРіР° СЂР°СЃРїРёСЃР°РЅРёСЏ РёР· С„РѕС‚РѕРіСЂР°С„РёРё С‡РµСЂРµР· Ollama (Qwen-VL РёР»Рё СЃРѕРІРјРµСЃС‚РёРјСѓСЋ РјРѕРґРµР»СЊ).
/// Р’С‹Р·С‹РІР°РµС‚ API РЅР°РїСЂСЏРјСѓСЋ РЅР° localhost:11434 вЂ” С‚РѕС‚ Р¶Рµ Р±СЌРєРµРЅРґ, С‡С‚Рѕ РёСЃРїРѕР»СЊР·СѓРµС‚ PDF_pars.
/// </summary>
public class ScheduleService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScheduleService> _logger;
    private readonly string _ollamaUrl;
    private readonly string _model;
    private readonly string? _geminiApiKey;
    private readonly string _geminiModel;
    private readonly string _provider;

    // РџСЂРѕРјРїС‚ СЃРїРµС†РёР°Р»СЊРЅРѕ СЃРѕСЃС‚Р°РІР»РµРЅ РґР»СЏ РёР·РІР»РµС‡РµРЅРёСЏ СЂР°СЃРїРёСЃР°РЅРёСЏ.
    // РЈРєР°Р·С‹РІР°РµРј СЃС‚СЂСѓРєС‚СѓСЂСѓ С‚Р°Р±Р»РёС†С‹, С„РѕСЂРјР°С‚ РґРІРѕР№РЅС‹С… РїР°СЂ Рё СЃС‚СЂРѕРіРёР№ JSON-РІС‹РІРѕРґ.
    private const string SchedulePrompt = """
        РўС‹ Р°РЅР°Р»РёР·РёСЂСѓРµС€СЊ С„РѕС‚РѕРіСЂР°С„РёСЋ СЂР°СЃРїРёСЃР°РЅРёСЏ Р·Р°РЅСЏС‚РёР№ СЂРѕСЃСЃРёР№СЃРєРѕРіРѕ СѓРЅРёРІРµСЂСЃРёС‚РµС‚Р°.

        Р—РђР”РђР§Рђ: РР·РІР»РµРєРё Р’РЎР• РїР°СЂС‹ Рё РІРµСЂРЅРё РўРћР›Р¬РљРћ JSON. РќРёРєР°РєРёС… РїРѕСЏСЃРЅРµРЅРёР№.

        РЎРўР РЈРљРўРЈР Рђ РўРђР‘Р›РР¦Р«:
        - Р›РµРІС‹Р№ СЃС‚РѕР»Р±РµС†: РґРЅРё РЅРµРґРµР»Рё (РџРѕРЅРµРґРµР»СЊРЅРёРє=1, Р’С‚РѕСЂРЅРёРє=2, РЎСЂРµРґР°=3, Р§РµС‚РІРµСЂРі=4, РџСЏС‚РЅРёС†Р°=5, РЎСѓР±Р±РѕС‚Р°=6)
        - Р’С‚РѕСЂРѕР№ СЃС‚РѕР»Р±РµС†: РЅРѕРјРµСЂР° РїР°СЂ (1 РїР°СЂР°, 2 РїР°СЂР°, ...) Рё РІСЂРµРјСЏ
        - РћСЃС‚Р°Р»СЊРЅС‹Рµ СЃС‚РѕР»Р±С†С‹: РЅР°Р·РІР°РЅРёСЏ РїСЂРµРґРјРµС‚РѕРІ (РјРѕР¶РµС‚ Р±С‹С‚СЊ 1 РёР»Рё 2 СЃС‚РѕР»Р±С†Р° РїРѕРґРіСЂСѓРїРї)

        РџР РђР’РР›Рђ:
        1. Р•СЃР»Рё СЏС‡РµР№РєР° Р—РђРќРРњРђР•Рў РћР‘Рђ СЃС‚РѕР»Р±С†Р° РїРѕРґРіСЂСѓРїРї (РѕР±СЉРµРґРёРЅС‘РЅРЅР°СЏ) в†’ subGroup: null (РѕР±С‰Р°СЏ РґР»СЏ РІСЃРµС…)
        2. Р•СЃР»Рё РІ Р›Р•Р’РћРњ СЃС‚РѕР»Р±С†Рµ РїРѕРґРіСЂСѓРїРї СЃРІРѕС‘ РЅР°Р·РІР°РЅРёРµ, Р° РІ РџР РђР’РћРњ РґСЂСѓРіРѕРµ в†’ РґРѕР±Р°РІСЊ Р”Р’Р• Р·Р°РїРёСЃРё: subGroup:1 Рё subGroup:2
        3. Р•СЃР»Рё СЏС‡РµР№РєР° РїСѓСЃС‚Р°СЏ в†’ РїСЂРѕРїСѓСЃС‚Рё РµС‘ (РЅРµ РґРѕР±Р°РІР»СЏР№ Р·Р°РїРёСЃСЊ)
        4. РР· С‚РµРєСЃС‚Р° СЏС‡РµР№РєРё Р±РµСЂРё РўРћР›Р¬РљРћ РЅР°Р·РІР°РЅРёРµ РґРёСЃС†РёРїР»РёРЅС‹ (Р±РµР· РїСЂРµРїРѕРґР°РІР°С‚РµР»СЏ, Р±РµР· Р°СѓРґРёС‚РѕСЂРёРё, Р±РµР· С‚РёРїР° "Р»РµРєС†РёСЏ"/"РїСЂР°РєС‚."/"Р»Р°Р±.")

        Р¤РћР РњРђРў (СЃС‚СЂРѕРіРѕ, Р±РµР· markdown):
        {"schedule":[
          {"day":1,"lesson":1,"subject":"РњР°С‚РµРјР°С‚РёС‡РµСЃРєРёР№ Р°РЅР°Р»РёР·","subGroup":null},
          {"day":1,"lesson":3,"subject":"РђР»РіРѕСЂРёС‚РјС‹ Рё СЃС‚СЂСѓРєС‚СѓСЂС‹ РґР°РЅРЅС‹С…","subGroup":1},
          {"day":1,"lesson":3,"subject":"РњР°С‚РµРјР°С‚РёС‡РµСЃРєРёР№ Р°РЅР°Р»РёР·","subGroup":2}
        ]}

        Р’РµСЂРЅРё РўРћР›Р¬РљРћ JSON.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string RetryStrictSuffix = """
        Repeat extraction more strictly.
        Return ONLY a valid JSON object, without markdown and explanations.
        If only part of the table is readable, return only confident rows.
        """;

    public ScheduleService(HttpClient http, IConfiguration config, ILogger<ScheduleService> logger)
    {
        _http      = http;
        _logger    = logger;
        _ollamaUrl = (config["OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _model     = config["OllamaModel"] ?? "qwen2.5vl:7b";
        _geminiApiKey = config["GeminiApiKey"]?.Trim();
        _geminiModel  = config["GeminiModel"] ?? "gemini-2.0-flash";
        _provider     = (config["ScheduleAiProvider"] ?? "auto").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// РћС‚РїСЂР°РІРёС‚СЊ РёР·РѕР±СЂР°Р¶РµРЅРёРµ СЂР°СЃРїРёСЃР°РЅРёСЏ РІ Ollama Рё РїРѕР»СѓС‡РёС‚СЊ СЃРїРёСЃРѕРє РїР°СЂ.
    /// </summary>
    /// <param name="imageBytes">Р‘Р°Р№С‚С‹ РёР·РѕР±СЂР°Р¶РµРЅРёСЏ (JPEG/PNG)</param>
    /// <param name="ct">РўРѕРєРµРЅ РѕС‚РјРµРЅС‹</param>
    /// <returns>РЎРїРёСЃРѕРє Р·Р°РїРёСЃРµР№ СЂР°СЃРїРёСЃР°РЅРёСЏ; РїСѓСЃС‚РѕР№ СЃРїРёСЃРѕРє РµСЃР»Рё РјРѕРґРµР»СЊ РЅРµ СЃРјРѕРіР»Р° СЂР°СЃРїРѕР·РЅР°С‚СЊ</returns>
    /// <exception cref="InvalidOperationException">Ollama РЅРµРґРѕСЃС‚СѓРїРЅР°</exception>
    public async Task<List<ScheduleEntry>> ParseScheduleAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (ShouldUseGemini())
            return await ParseWithGeminiAsync(imageBytes, ct);

        return await ParseWithOllamaAsync(imageBytes, ct);
    }

    private bool ShouldUseGemini()
    {
        var hasGeminiKey = !string.IsNullOrWhiteSpace(_geminiApiKey)
                           && !_geminiApiKey.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase);

        return _provider switch
        {
            "gemini" => hasGeminiKey,
            "ollama" => false,
            _ => hasGeminiKey
        };
    }

    private async Task<List<ScheduleEntry>> ParseWithGeminiAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_geminiApiKey))
            throw new InvalidOperationException("GeminiApiKey РЅРµ Р·Р°РґР°РЅ.");

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

        _logger.LogInformation("Р—Р°РїСЂРѕСЃ Рє Gemini ({Model}), СЂР°Р·РјРµСЂ С„РѕС‚Рѕ: {Kb} РљР‘", _geminiModel, imageBytes.Length / 1024);

        HttpResponseMessage httpResponse;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            httpResponse = await _http.PostAsync(endpoint, content, cts.Token);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Gemini РЅРµ РѕС‚РІРµС‚РёР»Р° Р·Р° 3 РјРёРЅСѓС‚С‹.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "РћС€РёР±РєР° РїРѕРґРєР»СЋС‡РµРЅРёСЏ Рє Gemini API");
            throw new InvalidOperationException("РќРµ СѓРґР°Р»РѕСЃСЊ РїРѕРґРєР»СЋС‡РёС‚СЊСЃСЏ Рє Gemini API.", ex);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gemini РІРµСЂРЅСѓР»Р° {Code}: {Body}", httpResponse.StatusCode, err);
            throw new InvalidOperationException(
                $"РћС€РёР±РєР° Gemini {(int)httpResponse.StatusCode}: {err[..Math.Min(300, err.Length)]}");
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
            _logger.LogWarning("Gemini РІРµСЂРЅСѓР»Р° РїСѓСЃС‚РѕР№ РѕС‚РІРµС‚ РґР»СЏ СЂР°СЃРїРёСЃР°РЅРёСЏ");
            return new();
        }

        _logger.LogInformation("РћС‚РІРµС‚ РѕС‚ Gemini ({Len} СЃРёРјРІРѕР»РѕРІ): {Preview}",
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

    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    //  РџР°СЂСЃРёРЅРі JSON РёР· РѕС‚РІРµС‚Р° РјРѕРґРµР»Рё
    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    private List<ScheduleEntry> ExtractSchedule(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("РњРѕРґРµР»СЊ РІРµСЂРЅСѓР»Р° РїСѓСЃС‚РѕР№ РѕС‚РІРµС‚");
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
                    .Where(e => e.LessonNumber >= 1)
                    .Select(e => new ScheduleEntry
                    {
                        DayOfWeek = e.DayOfWeek,
                        LessonNumber = e.LessonNumber,
                        Subject = e.Subject.Trim(),
                        SubGroup = e.SubGroup is 1 or 2 ? e.SubGroup : null
                    })
                    .GroupBy(e => new { e.DayOfWeek, e.LessonNumber, Subject = e.Subject.ToLowerInvariant(), e.SubGroup })
                    .Select(g => g.First())
                    .OrderBy(e => e.DayOfWeek)
                    .ThenBy(e => e.LessonNumber)
                    .ThenBy(e => e.SubGroup ?? 0)
                    .ToList();

                _logger.LogInformation("Р Р°СЃРїРѕР·РЅР°РЅРѕ {Count} Р·Р°РїРёСЃРµР№ СЂР°СЃРїРёСЃР°РЅРёСЏ", normalized.Count);
                return normalized;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "РљР°РЅРґРёРґР°С‚ JSON РЅРµ СѓРґР°Р»РѕСЃСЊ СЂР°СЃРїР°СЂСЃРёС‚СЊ РєР°Рє СЂР°СЃРїРёСЃР°РЅРёРµ");
            }
        }

        _logger.LogWarning("РќРµ СѓРґР°Р»РѕСЃСЊ РёР·РІР»РµС‡СЊ РІР°Р»РёРґРЅРѕРµ СЂР°СЃРїРёСЃР°РЅРёРµ РёР· РѕС‚РІРµС‚Р° РјРѕРґРµР»Рё. РћС‚РІРµС‚: {Text}", rawText);
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
            var weekRaw = GetInt(row, "subGroup", "subgroup", "weekType", "week_type", "week", "group")
                          ?? ParseWeekType(GetString(row, "subGroup", "subgroup", "weekType", "week", "group"));

            if (dayRaw is null || lessonRaw is null || string.IsNullOrWhiteSpace(subjectRaw))
                continue;

            result.Add(new ScheduleEntry
            {
                DayOfWeek = dayRaw.Value,
                LessonNumber = lessonRaw.Value,
                Subject = NormalizeSubject(subjectRaw),
                SubGroup = weekRaw
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

        // РРЅРѕРіРґР° РјРѕРґРµР»СЊ РѕС‚РґР°РµС‚ РѕР±СЉРµРєС‚ РѕРґРЅРѕР№ РїР°СЂС‹ РІРјРµСЃС‚Рѕ РјР°СЃСЃРёРІР°.
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
            "1" or "mon" or "monday" or "РїРѕРЅРµРґРµР»СЊРЅРёРє" or "РїРЅ" => 1,
            "2" or "tue" or "tuesday" or "РІС‚РѕСЂРЅРёРє" or "РІС‚" => 2,
            "3" or "wed" or "wednesday" or "СЃСЂРµРґР°" or "СЃСЂРµРґСѓ" or "СЃСЂ" => 3,
            "4" or "thu" or "thursday" or "С‡РµС‚РІРµСЂРі" or "С‡С‚" => 4,
            "5" or "fri" or "friday" or "РїСЏС‚РЅРёС†Р°" or "РїСЏС‚РЅРёС†Сѓ" or "РїС‚" => 5,
            "6" or "sat" or "saturday" or "СЃСѓР±Р±РѕС‚Р°" or "СЃСѓР±Р±РѕС‚Сѓ" or "СЃР±" => 6,
            "7" or "sun" or "sunday" or "РІРѕСЃРєСЂРµСЃРµРЅСЊРµ" or "РІСЃ" => 7,
            _ => null
        };
    }

    private static int? ParseWeekType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();

        if (v is "1" or "odd" or "РЅРµС‡РµС‚" or "РЅРµС‡С‘С‚" or "РЅРµС‡РµС‚РЅР°СЏ" or "РЅРµС‡С‘С‚РЅР°СЏ")
            return 1;

        if (v is "2" or "even" or "С‡РµС‚" or "С‡С‘С‚" or "С‡РµС‚РЅР°СЏ" or "С‡С‘С‚РЅР°СЏ")
            return 2;

        return null;
    }

    private static string NormalizeSubject(string subject)
    {
        var normalized = Regex.Replace(subject.Trim(), @"\s+", " ");
        return normalized.Length == 0 ? string.Empty : normalized;
    }

    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    //  Р’СЃРїРѕРјРѕРіР°С‚РµР»СЊРЅС‹Р№ РјРµС‚РѕРґ: С„РѕСЂРјР°С‚РёСЂРѕРІР°РЅРЅС‹Р№ С‚РµРєСЃС‚ СЂР°СЃРїРёСЃР°РЅРёСЏ
    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    /// <summary>
    /// РџРѕСЃС‚СЂРѕРёС‚СЊ С‡РёС‚Р°РµРјС‹Р№ С‚РµРєСЃС‚ СЂР°СЃРїРёСЃР°РЅРёСЏ, СЃРіСЂСѓРїРїРёСЂРѕРІР°РЅРЅС‹Р№ РїРѕ РґРЅСЏРј.
    /// РџСЂРёРЅРёРјР°РµС‚ С‚РёРї С‚РµРєСѓС‰РµР№ РЅРµРґРµР»Рё (1 РёР»Рё 2) РґР»СЏ РїРѕРјРµС‚РєРё Р°РєС‚РёРІРЅС‹С… РїР°СЂ.
    /// </summary>
    public static string FormatSchedule(List<ScheduleEntry> entries, int? currentWeekType = null)
    {
        if (entries.Count == 0) return "Р Р°СЃРїРёСЃР°РЅРёРµ РїСѓСЃС‚Рѕ.";

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
                var lessonEntries = lesson.OrderBy(e => e.WeekType ?? 0).ToList();

                foreach (var entry in lessonEntries)
                {
                    // РџРѕРјРµС‡Р°РµРј Р°РєС‚РёРІРЅСѓСЋ РЅР° С‚РµРєСѓС‰РµР№ РЅРµРґРµР»Рµ РїР°СЂСѓ СЃС‚СЂРµР»РєРѕР№
                    var activeMarker = (currentWeekType.HasValue && entry.WeekType == currentWeekType)
                        ? " в—Ђ"
                        : string.Empty;

                    sb.AppendLine($"  {entry.LessonNumber}. {entry.Subject}{entry.WeekTypeLabel}{activeMarker}");
                }
            }
        }

        return sb.ToString().TrimStart('\n');
    }

    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
    //  Р’РЅСѓС‚СЂРµРЅРЅРёРµ DTO РґР»СЏ Ollama API
    // в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

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

    // РџРѕРґРґРµСЂР¶РёРІР°РµРј РѕР±Р° РёРјРµРЅРё РїРѕР»СЏ: "subGroup" (РЅРѕРІС‹Р№) Рё "weekType" (СЃС‚Р°СЂС‹Р№)
    private record ScheduleJsonRoot(
        [property: JsonPropertyName("schedule")] List<ScheduleEntry> Schedule
    );

    // Р’СЃРїРѕРјРѕРіР°С‚РµР»СЊРЅС‹Р№ РґРµСЃРµСЂРёР°Р»РёР·Р°С‚РѕСЂ РґР»СЏ РѕР±СЂР°С‚РЅРѕР№ СЃРѕРІРјРµСЃС‚РёРјРѕСЃС‚Рё:
    // РµСЃР»Рё РјРѕРґРµР»СЊ РІРµСЂРЅСѓР»Р° "weekType" РІРјРµСЃС‚Рѕ "subGroup" вЂ” РїР°С‚С‡РёРј РїРѕСЃР»Рµ РґРµСЃРµСЂРёР°Р»РёР·Р°С†РёРё
    private static List<ScheduleEntry> NormalizeEntries(List<ScheduleEntry> entries)
    {
        // ScheduleEntry.WeekType СѓР¶Рµ СЏРІР»СЏРµС‚СЃСЏ Р°Р»РёР°СЃРѕРј SubGroup вЂ” РЅРёС‡РµРіРѕ РґРµР»Р°С‚СЊ РЅРµ РЅСѓР¶РЅРѕ
        return entries;
    }
}

