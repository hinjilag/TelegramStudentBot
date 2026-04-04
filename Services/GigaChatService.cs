using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Сервис для работы с GigaChat API (Сбербанк).
/// Поддерживает текстовые вопросы и анализ изображений.
///
/// Как получить ключ (бесплатно):
/// 1. Зарегистрируйся на https://developers.sber.ru/studio
/// 2. Создай проект → выбери GigaChat API
/// 3. Скопируй "Авторизационные данные" (это и есть GigaChatKey)
/// </summary>
public class GigaChatService
{
    private readonly HttpClient _http;
    private readonly string? _authKey;
    private readonly ILogger<GigaChatService> _logger;

    // Кэш токена (живёт 30 минут)
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private const string TokenUrl   = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string BaseUrl    = "https://gigachat.devices.sberbank.ru/api/v1";
    private const string ModelVision = "GigaChat-Pro"; // поддерживает изображения
    private const string ModelText   = "GigaChat";     // только текст (быстрее)

    public GigaChatService(IConfiguration config, ILogger<GigaChatService> logger)
    {
        _authKey = config["GigaChatKey"];
        _logger  = logger;

        // Sberbank использует собственный CA — обходим проверку сертификата
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Настроен ли ключ</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_authKey);

    // ══════════════════════════════════════════════════════════
    //  Анализ изображения
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Загружает изображение в GigaChat и анализирует его.
    /// Поддерживаются: JPEG, PNG, GIF, WEBP.
    /// </summary>
    public async Task<string> AnalyzeMediaAsync(
        byte[] fileBytes, string mimeType, string? userPrompt, CancellationToken ct)
    {
        if (!IsConfigured)
            return NotConfiguredMessage();

        // PDF GigaChat не поддерживает как встроенный файл для vision
        if (mimeType == "application/pdf")
            return "⚠️ GigaChat не поддерживает анализ PDF-файлов.\n" +
                   "Отправь страницы как изображения (фото или JPG/PNG файлом).";

        var token = await GetTokenAsync(ct);
        if (token is null)
            return "❌ Не удалось получить токен GigaChat. Проверь ключ.";

        try
        {
            // Шаг 1: загружаем файл
            var fileId = await UploadFileAsync(token, fileBytes, mimeType, ct);
            if (fileId is null)
                return "❌ Не удалось загрузить изображение в GigaChat.";

            // Шаг 2: отправляем запрос с file_id
            var prompt = string.IsNullOrWhiteSpace(userPrompt)
                ? "Проанализируй это изображение. Опиши что на нём. " +
                  "Если есть текст, таблицы, формулы — выпиши их полностью и точно. " +
                  "Отвечай на русском языке."
                : userPrompt;

            var requestBody = new
            {
                model = ModelVision,
                messages = new[]
                {
                    new
                    {
                        role    = "user",
                        content = new object[]
                        {
                            new { type = "text",      text = prompt },
                            new { type = "image_url", image_url = new { url = fileId } }
                        }
                    }
                },
                temperature    = 0.5,
                max_tokens     = 2048,
                stream         = false
            };

            return await SendChatRequestAsync(token, requestBody, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при анализе изображения GigaChat");
            return "❌ Ошибка при обращении к GigaChat. Попробуй позже.";
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Построение учебного плана
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Анализирует задачи + расписание занятий и строит оптимальный план учёбы.
    /// </summary>
    public async Task<string> BuildStudyPlanAsync(
        IEnumerable<StudyTask> tasks,
        IEnumerable<ScheduleEntry> schedule,
        CancellationToken ct)
    {
        if (!IsConfigured)
            return NotConfiguredMessage();

        var token = await GetTokenAsync(ct);
        if (token is null)
            return "❌ Не удалось получить токен GigaChat. Проверь ключ.";

        var pendingTasks = tasks.Where(t => !t.IsCompleted).ToList();
        var scheduleList = schedule.ToList();

        if (pendingTasks.Count == 0)
            return "📋 Нет активных задач для планирования. Добавь задачи через /plan.";

        var today      = DateTime.Today;
        var dayOfWeek  = today.ToString("dddd", new System.Globalization.CultureInfo("ru-RU"));
        var dateStr    = today.ToString("dd.MM.yyyy");

        // Формируем текст задач
        var tasksText = string.Join("\n", pendingTasks.Select((t, i) =>
        {
            var deadline = t.Deadline.HasValue
                ? $"дедлайн {t.Deadline.Value:dd.MM.yyyy} ({(t.Deadline.Value.Date - today).Days} дн.)"
                : "без дедлайна";
            return $"{i + 1}. [{t.Subject}] {t.Title} — {deadline}";
        }));

        // Формируем текст расписания
        var scheduleText = scheduleList.Count > 0
            ? string.Join("\n", scheduleList
                .GroupBy(s => s.Day)
                .OrderBy(g => DayOrder(g.Key))
                .Select(g =>
                {
                    var entries = string.Join(", ", g.Select(e =>
                        string.IsNullOrWhiteSpace(e.Room)
                            ? $"{e.Time} {e.Subject}"
                            : $"{e.Time} {e.Subject} ({e.Room})"));
                    return $"{g.Key}: {entries}";
                }))
            : "Расписание не добавлено (учти это при планировании).";

        var prompt =
            $"Ты — учебный ассистент студента. Твоя задача — составить конкретный план учёбы.\n\n" +
            $"Сегодня: {dateStr}, {dayOfWeek}.\n\n" +
            $"Задачи студента:\n{tasksText}\n\n" +
            $"Расписание занятий (регулярное):\n{scheduleText}\n\n" +
            $"Составь детальный план учёбы на ближайшие 7–14 дней:\n" +
            $"— Распредели задачи по конкретным дням\n" +
            $"— В дни с большим количеством занятий давай меньше домашней работы\n" +
            $"— Ближайшие дедлайны — приоритет\n" +
            $"— Укажи примерное время на каждую задачу\n" +
            $"— Оформи ответ по дням: «Понедельник 07.04: ..., Вторник 08.04: ...»\n\n" +
            $"Отвечай кратко и структурированно на русском языке.";

        var requestBody = new
        {
            model    = ModelText,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0.6,
            max_tokens  = 2048,
            stream      = false
        };

        return await SendChatRequestAsync(token, requestBody, ct);
    }

    private static int DayOrder(string day) => day switch
    {
        "Понедельник" => 1,
        "Вторник"     => 2,
        "Среда"       => 3,
        "Четверг"     => 4,
        "Пятница"     => 5,
        "Суббота"     => 6,
        "Воскресенье" => 7,
        _             => 8
    };

    // ══════════════════════════════════════════════════════════
    //  Приватные методы
    // ══════════════════════════════════════════════════════════

    /// <summary>Получить (или обновить) OAuth-токен</summary>
    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        // Проверяем кэш
        if (_accessToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _accessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Повторная проверка после получения блокировки
            if (_accessToken is not null && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            request.Headers.Add("Authorization", $"Basic {_authKey}");
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("scope", "GIGACHAT_API_PERS")
            });

            var response = await _http.SendAsync(request, ct);
            var json     = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GigaChat token error {Status}: {Body}", response.StatusCode, json);
                return null;
            }

            using var doc   = JsonDocument.Parse(json);
            _accessToken    = doc.RootElement.GetProperty("access_token").GetString();
            var expiresAt   = doc.RootElement.GetProperty("expires_at").GetInt64();
            // expires_at — миллисекунды Unix, вычитаем 1 минуту для запаса
            _tokenExpiry    = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
                                            .UtcDateTime
                                            .AddMinutes(-1);

            _logger.LogInformation("GigaChat токен получен, истекает в {Expiry}", _tokenExpiry);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Загрузить файл в GigaChat Files API, вернуть file_id</summary>
    private async Task<string?> UploadFileAsync(
        string token, byte[] bytes, string mimeType, CancellationToken ct)
    {
        using var form    = new MultipartFormDataContent();
        var fileContent   = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "file", $"image.{MimeToExtension(mimeType)}");
        form.Add(new StringContent("general"), "purpose");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/files")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(request, ct);
        var json     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GigaChat file upload error {Status}: {Body}", response.StatusCode, json);
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString();
    }

    /// <summary>Отправить запрос к /chat/completions и вернуть текст ответа</summary>
    private async Task<string> SendChatRequestAsync(
        string token, object requestBody, CancellationToken ct)
    {
        try
        {
            var json    = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response     = await _http.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GigaChat API {Status}: {Body}", response.StatusCode, responseText);
                return "❌ Ошибка GigaChat API. Попробуй позже.";
            }

            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "Не удалось получить ответ.";
        }
        catch (TaskCanceledException)
        {
            return "⏳ GigaChat не ответил вовремя. Попробуй снова.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при запросе к GigaChat");
            return "❌ Произошла ошибка при обращении к GigaChat.";
        }
    }

    private static string MimeToExtension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png"  => "png",
        "image/gif"  => "gif",
        "image/webp" => "webp",
        _            => "bin"
    };

    private static string NotConfiguredMessage() =>
        "⚙️ GigaChat не настроен.\n\n" +
        "Как получить бесплатный ключ:\n" +
        "1. Зарегистрируйся на developers.sber.ru/studio\n" +
        "2. Создай проект → подключи GigaChat API\n" +
        "3. Скопируй «Авторизационные данные» (Base64-строка)\n" +
        "4. Вставь в appsettings.json:\n" +
        "   \"GigaChatKey\": \"твой_ключ\"";
}
