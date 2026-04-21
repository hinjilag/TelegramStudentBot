using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace TelegramStudentBot.Helpers;

public static class TelegramMiniAppInitDataValidator
{
    private static readonly TimeSpan FutureSkewTolerance = TimeSpan.FromMinutes(1);

    public static bool TryValidate(
        string initData,
        string botToken,
        TimeSpan maxAge,
        out TelegramMiniAppInitData authData,
        out string errorCode)
    {
        authData = default;
        errorCode = "invalid_init_data";

        if (string.IsNullOrWhiteSpace(initData) || string.IsNullOrWhiteSpace(botToken))
        {
            errorCode = "init_data_missing";
            return false;
        }

        var parsed = QueryHelpers.ParseQuery(initData);
        if (!parsed.TryGetValue("hash", out var hashValues))
        {
            errorCode = "init_data_hash_missing";
            return false;
        }

        var hash = hashValues.ToString();
        if (string.IsNullOrWhiteSpace(hash))
        {
            errorCode = "init_data_hash_missing";
            return false;
        }

        var dataCheckString = string.Join(
            '\n',
            parsed
                .Where(pair => !string.Equals(pair.Key, "hash", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(botToken));
        var calculatedHash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));

        byte[] providedHash;
        try
        {
            providedHash = Convert.FromHexString(hash);
        }
        catch (FormatException)
        {
            errorCode = "init_data_hash_invalid";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(calculatedHash, providedHash))
        {
            errorCode = "init_data_hash_mismatch";
            return false;
        }

        if (!parsed.TryGetValue("auth_date", out var authDateValues) ||
            !long.TryParse(authDateValues.ToString(), out var authDateUnix))
        {
            errorCode = "init_data_auth_date_missing";
            return false;
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
        var age = DateTimeOffset.UtcNow - authDate;
        if (age < -FutureSkewTolerance || age > maxAge)
        {
            errorCode = "init_data_expired";
            return false;
        }

        if (!parsed.TryGetValue("user", out var userValues))
        {
            errorCode = "init_data_user_missing";
            return false;
        }

        try
        {
            using var userJson = JsonDocument.Parse(userValues.ToString());
            var userId = userJson.RootElement.GetProperty("id").GetInt64();
            authData = new TelegramMiniAppInitData(userId, authDateUnix);
            return true;
        }
        catch (Exception)
        {
            errorCode = "init_data_user_invalid";
            return false;
        }
    }
}

public readonly record struct TelegramMiniAppInitData(long UserId, long AuthDateUnix);
