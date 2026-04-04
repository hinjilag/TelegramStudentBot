using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TelegramStudentBot.Services;

/// <summary>
/// Сервис распознавания изображений через OpenRouter API (llama-3.2-11b-vision).
/// Бесплатный tier: доступен из России.
///
/// Как получить бесплатный API ключ:
/// 1. Зарегистрируйся на https://openrouter.ai
/// 2. Keys → Create Key
/// 3. Добавь в appsettings.json: "OpenRouterApiKey": "sk-or-v1-..."
/// </summary>
public class LlamaVisionService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly ILogger<LlamaVisionService> _logger;

    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string Model  = "meta-llama/llama-3.2-11b-vision-instruct:free";

    public LlamaVisionService(IConfiguration config, ILogger<LlamaVisionService> logger)
    {
        _apiKey = config["OpenRouterApiKey"];
        _logger = logger;
        _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Настроен ли API ключ</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Анализирует изображение и возвращает текстовое описание.
    /// Поддерживаемые форматы: JPEG, PNG, WEBP, GIF.
    /// </summary>
    /// <param name="imageBytes">Байты изображения</param>
    /// <param name="mimeType">MIME-тип (image/jpeg, image/png, ...)</param>
    /// <param name="userPrompt">Вопрос пользователя (caption). Если null — общий анализ.</param>
    public async Task<string> AnalyzeImageAsync(
        byte[] imageBytes, string mimeType, string? userPrompt, CancellationToken ct)
    {
        if (!IsConfigured)
            return NotConfiguredMessage();

        var prompt = string.IsNullOrWhiteSpace(userPrompt)
            ? "Проанализируй это изображение. Опиши всё, что видишь. " +
              "Если есть текст, таблицы, формулы или расписание — выпиши их полностью и точно. " +
              "Отвечай на русском языке."
            : userPrompt;

        var base64     = Convert.ToBase64String(imageBytes);
        var dataUri    = $"data:{mimeType};base64,{base64}";

        var requestBody = new
        {
            model    = Model,
            messages = new[]
            {
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new { type = "text",      text      = prompt },
                        new { type = "image_url", image_url = new { url = dataUri } }
                    }
                }
            },
            max_tokens  = 2048,
            temperature = 0.4
        };

        try
        {
            var json    = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Add("HTTP-Referer", "https://t.me/studentbot");
            request.Headers.Add("X-Title", "TelegramStudentBot");

            var response     = await _http.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Groq/Llama Vision {Status}: {Body}", response.StatusCode, responseText);
                return "❌ Ошибка при распознавании изображения. Проверь GroqApiKey или попробуй позже.";
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
            return "⏳ Запрос к Llama Vision превысил время ожидания. Попробуй снова.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка LlamaVisionService");
            return "❌ Произошла ошибка при анализе изображения.";
        }
    }

    private static string NotConfiguredMessage() =>
        "⚙️ Llama Vision не настроен.\n\n" +
        "Как получить бесплатный ключ (OpenRouter):\n" +
        "1. Открой https://openrouter.ai\n" +
        "2. Keys → Create Key\n" +
        "3. Добавь в appsettings.json:\n" +
        "   \"OpenRouterApiKey\": \"sk-or-v1-...\"";
}
