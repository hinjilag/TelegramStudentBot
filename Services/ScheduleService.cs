using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Сервис парсинга расписания из фотографии через Ollama (Qwen-VL или совместимую модель).
/// Вызывает API напрямую на localhost:11434 — тот же бэкенд, что использует PDF_pars.
/// </summary>
public class ScheduleService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScheduleService> _logger;
    private readonly string _ollamaUrl;
    private readonly string _model;

    // Промпт специально составлен для извлечения расписания.
    // Указываем структуру таблицы, формат двойных пар и строгий JSON-вывод.
    private const string SchedulePrompt = """
        Ты анализируешь фотографию расписания занятий российского университета.

        ЗАДАЧА: Извлеки ВСЕ пары и верни ТОЛЬКО JSON. Никаких пояснений.

        СТРУКТУРА ТАБЛИЦЫ:
        - Левый столбец: дни недели (Понедельник=1, Вторник=2, Среда=3, Четверг=4, Пятница=5, Суббота=6)
        - Второй столбец: номера пар (1 пара, 2 пара, ...) и время
        - Остальные столбцы: названия предметов (может быть 1 или 2 столбца подгрупп)

        ПРАВИЛА:
        1. Если ячейка ЗАНИМАЕТ ОБА столбца подгрупп (объединённая) → subGroup: null (общая для всех)
        2. Если в ЛЕВОМ столбце подгрупп своё название, а в ПРАВОМ другое → добавь ДВЕ записи: subGroup:1 и subGroup:2
        3. Если ячейка пустая → пропусти её (не добавляй запись)
        4. Из текста ячейки бери ТОЛЬКО название дисциплины (без преподавателя, без аудитории, без типа "лекция"/"практ."/"лаб.")

        ФОРМАТ (строго, без markdown):
        {"schedule":[
          {"day":1,"lesson":1,"subject":"Математический анализ","subGroup":null},
          {"day":1,"lesson":3,"subject":"Алгоритмы и структуры данных","subGroup":1},
          {"day":1,"lesson":3,"subject":"Математический анализ","subGroup":2}
        ]}

        Верни ТОЛЬКО JSON.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ScheduleService(HttpClient http, IConfiguration config, ILogger<ScheduleService> logger)
    {
        _http      = http;
        _logger    = logger;
        _ollamaUrl = (config["OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _model     = config["OllamaModel"] ?? "qwen2.5vl:7b";
    }

    /// <summary>
    /// Отправить изображение расписания в Ollama и получить список пар.
    /// </summary>
    /// <param name="imageBytes">Байты изображения (JPEG/PNG)</param>
    /// <param name="ct">Токен отмены</param>
    /// <returns>Список записей расписания; пустой список если модель не смогла распознать</returns>
    /// <exception cref="InvalidOperationException">Ollama недоступна</exception>
    public async Task<List<ScheduleEntry>> ParseScheduleAsync(byte[] imageBytes, CancellationToken ct)
    {
        var base64Image = Convert.ToBase64String(imageBytes);

        var payload = new OllamaRequest(
            Model:   _model,
            Prompt:  SchedulePrompt,
            Images:  new[] { base64Image },
            Stream:  false,
            Options: new OllamaOptions(Temperature: 0.05)
        );

        var requestJson = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        _logger.LogInformation("Запрос к Ollama ({Model}), размер фото: {Kb} КБ",
            _model, imageBytes.Length / 1024);

        HttpResponseMessage httpResponse;
        try
        {
            // Таймаут 5 минут — VL-модели медленные
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            httpResponse = await _http.PostAsync($"{_ollamaUrl}/api/generate", content, cts.Token);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Ollama не ответила за 5 минут. Модель может быть ещё не загружена.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка подключения к Ollama ({Url})", _ollamaUrl);
            throw new InvalidOperationException(
                $"Не удалось подключиться к Ollama по адресу {_ollamaUrl}.\n" +
                "Убедись, что Ollama запущена: ollama serve", ex);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Ollama вернула {Code}: {Body}", httpResponse.StatusCode, err);
            throw new InvalidOperationException($"Ошибка Ollama {(int)httpResponse.StatusCode}: {err[..Math.Min(200, err.Length)]}");
        }

        var ollamaResult = await httpResponse.Content.ReadFromJsonAsync<OllamaResponse>(JsonOpts, ct);
        var rawText      = ollamaResult?.Response ?? string.Empty;

        _logger.LogInformation("Ответ от модели ({Len} символов): {Preview}",
            rawText.Length, rawText[..Math.Min(200, rawText.Length)]);

        return ExtractSchedule(rawText);
    }

    // ──────────────────────────────────────────────────────────
    //  Парсинг JSON из ответа модели
    // ──────────────────────────────────────────────────────────

    private List<ScheduleEntry> ExtractSchedule(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("Модель вернула пустой ответ");
            return new();
        }

        // Модель может добавить markdown или текст вокруг JSON — ищем первый { и последний }
        var start = rawText.IndexOf('{');
        var end   = rawText.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            _logger.LogWarning("JSON не найден в ответе модели. Ответ: {Text}", rawText);
            return new();
        }

        var jsonSlice = rawText[start..(end + 1)];

        try
        {
            var parsed = JsonSerializer.Deserialize<ScheduleJsonRoot>(jsonSlice, JsonOpts);
            var entries = parsed?.Schedule ?? new();

            // Базовая фильтрация: убираем записи без предмета или с невалидным днём
            entries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Subject) && e.DayOfWeek is >= 1 and <= 7)
                .ToList();

            _logger.LogInformation("Распознано {Count} записей расписания", entries.Count);
            return entries;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Ошибка парсинга JSON расписания: {Json}", jsonSlice[..Math.Min(500, jsonSlice.Length)]);
            return new();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Вспомогательный метод: форматированный текст расписания
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Построить читаемый текст расписания, сгруппированный по дням.
    /// Принимает тип текущей недели (1 или 2) для пометки активных пар.
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
                var lessonEntries = lesson.OrderBy(e => e.WeekType ?? 0).ToList();

                foreach (var entry in lessonEntries)
                {
                    // Помечаем активную на текущей неделе пару стрелкой
                    var activeMarker = (currentWeekType.HasValue && entry.WeekType == currentWeekType)
                        ? " ◀"
                        : string.Empty;

                    sb.AppendLine($"  {entry.LessonNumber}. {entry.Subject}{entry.WeekTypeLabel}{activeMarker}");
                }
            }
        }

        return sb.ToString().TrimStart('\n');
    }

    // ──────────────────────────────────────────────────────────
    //  Внутренние DTO для Ollama API
    // ──────────────────────────────────────────────────────────

    private record OllamaRequest(
        [property: JsonPropertyName("model")]   string   Model,
        [property: JsonPropertyName("prompt")]  string   Prompt,
        [property: JsonPropertyName("images")]  string[] Images,
        [property: JsonPropertyName("stream")]  bool     Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options
    );

    private record OllamaOptions(
        [property: JsonPropertyName("temperature")] double Temperature
    );

    private record OllamaResponse(
        [property: JsonPropertyName("response")] string Response
    );

    // Поддерживаем оба имени поля: "subGroup" (новый) и "weekType" (старый)
    private record ScheduleJsonRoot(
        [property: JsonPropertyName("schedule")] List<ScheduleEntry> Schedule
    );

    // Вспомогательный десериализатор для обратной совместимости:
    // если модель вернула "weekType" вместо "subGroup" — патчим после десериализации
    private static List<ScheduleEntry> NormalizeEntries(List<ScheduleEntry> entries)
    {
        // ScheduleEntry.WeekType уже является алиасом SubGroup — ничего делать не нужно
        return entries;
    }
}
