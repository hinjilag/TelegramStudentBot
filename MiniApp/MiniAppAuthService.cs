using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace TelegramStudentBot.MiniApp;

public class MiniAppAuthService
{
    private readonly string _botToken;
    private readonly bool _allowDebugAuth;
    private readonly ILogger<MiniAppAuthService> _logger;

    public MiniAppAuthService(IConfiguration configuration, ILogger<MiniAppAuthService> logger)
    {
        var rawBotToken = configuration["BotToken"]
            ?? throw new InvalidOperationException("BotToken is required for mini app authentication.");

        _botToken = string.Concat(rawBotToken.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));
        _allowDebugAuth = configuration.GetValue<bool>("MiniApp:AllowDebugAuth");
        _logger = logger;
    }

    public bool TryAuthenticate(HttpContext httpContext, out MiniAppIdentity? identity, out string? error)
    {
        if (TryAuthenticateDebug(httpContext, out identity, out error))
            return true;

        var initData = httpContext.Request.Headers["X-Telegram-Init-Data"].ToString();
        if (string.IsNullOrWhiteSpace(initData))
            initData = httpContext.Request.Query["initData"].ToString();

        if (string.IsNullOrWhiteSpace(initData))
        {
            error = "Missing Telegram Mini App initData.";
            identity = null;
            return false;
        }

        var parsed = QueryHelpers.ParseQuery(initData);
        if (!parsed.TryGetValue("hash", out var hashValues))
        {
            error = "Telegram initData does not contain hash.";
            identity = null;
            return false;
        }

        if (!parsed.TryGetValue("auth_date", out var authDateValues) ||
            !long.TryParse(authDateValues.ToString(), out var authUnix))
        {
            error = "Telegram initData does not contain a valid auth_date.";
            identity = null;
            return false;
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authUnix);
        if (DateTimeOffset.UtcNow - authDate > TimeSpan.FromHours(24))
        {
            error = "Telegram initData expired.";
            identity = null;
            return false;
        }

        var dataCheckString = string.Join(
            "\n",
            parsed
                .Where(item => !string.Equals(item.Key, "hash", StringComparison.Ordinal) &&
                               !string.Equals(item.Key, "signature", StringComparison.Ordinal))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value.ToString()}"));

        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(_botToken));

        var expectedHash = Convert.ToHexString(
                HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString)))
            .ToLowerInvariant();

        var providedHash = hashValues.ToString().Trim().ToLowerInvariant();
        if (!HashesEqual(expectedHash, providedHash))
        {
            _logger.LogWarning("Mini app auth failed due to hash mismatch.");
            error = "Telegram initData hash mismatch.";
            identity = null;
            return false;
        }

        if (!parsed.TryGetValue("user", out var userValues))
        {
            error = "Telegram initData does not contain user.";
            identity = null;
            return false;
        }

        TelegramMiniAppUser? user;
        try
        {
            user = JsonSerializer.Deserialize<TelegramMiniAppUser>(userValues.ToString(), JsonOptions);
        }
        catch (JsonException)
        {
            user = null;
        }

        if (user is null)
        {
            error = "Telegram initData contains invalid user payload.";
            identity = null;
            return false;
        }

        identity = new MiniAppIdentity(
            user.Id,
            user.FirstName ?? "Студент",
            user.LastName,
            user.Username,
            user.LanguageCode,
            IsDebug: false);

        error = null;
        return true;
    }

    private bool TryAuthenticateDebug(HttpContext httpContext, out MiniAppIdentity? identity, out string? error)
    {
        identity = null;
        error = null;

        if (!_allowDebugAuth)
            return false;

        var debugUserIdValue = httpContext.Request.Headers["X-MiniApp-Debug-UserId"].ToString();
        if (string.IsNullOrWhiteSpace(debugUserIdValue))
            debugUserIdValue = httpContext.Request.Query["devUserId"].ToString();

        if (!long.TryParse(debugUserIdValue, out var debugUserId) || debugUserId <= 0)
            return false;

        identity = new MiniAppIdentity(
            debugUserId,
            httpContext.Request.Query["devFirstName"].ToString() is { Length: > 0 } firstName ? firstName : "Debug User",
            null,
            null,
            "ru",
            IsDebug: true);

        return true;
    }

    private static bool HashesEqual(string expected, string provided)
    {
        if (expected.Length != provided.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }

    private sealed class TelegramMiniAppUser
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("first_name")]
        public string? FirstName { get; set; }

        [JsonPropertyName("last_name")]
        public string? LastName { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("language_code")]
        public string? LanguageCode { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
