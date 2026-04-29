using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TelegramStudentBot.MiniApp;

public class GroupMiniAppAccessService
{
    private readonly byte[] _secretKey;
    private readonly bool _allowDebugAuth;

    public GroupMiniAppAccessService(IConfiguration configuration)
    {
        var rawBotToken = configuration["BotToken"]
            ?? throw new InvalidOperationException("BotToken is required for group mini app access.");

        var botToken = string.Concat(rawBotToken.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));
        _secretKey = Encoding.UTF8.GetBytes(botToken);
        _allowDebugAuth = configuration.GetValue<bool>("MiniApp:AllowDebugAuth");
    }

    public string CreateToken(long chatId)
        => Convert.ToHexString(HMACSHA256.HashData(
                _secretKey,
                Encoding.UTF8.GetBytes($"group:{chatId.ToString(CultureInfo.InvariantCulture)}")))
            .ToLowerInvariant();

    public bool TryResolveChatAccess(
        HttpContext httpContext,
        MiniAppIdentity identity,
        out long chatId,
        out string? error)
    {
        chatId = 0;

        var chatIdValue = httpContext.Request.Headers["X-Group-Chat-Id"].ToString();
        if (string.IsNullOrWhiteSpace(chatIdValue))
            chatIdValue = httpContext.Request.Query["chatId"].ToString();

        if (!long.TryParse(chatIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId))
        {
            error = "Missing group chatId.";
            return false;
        }

        var token = httpContext.Request.Headers["X-Group-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
            token = httpContext.Request.Query["groupToken"].ToString();

        if (_allowDebugAuth && identity.IsDebug && string.IsNullOrWhiteSpace(token))
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Missing group mini app token.";
            return false;
        }

        var expectedToken = CreateToken(chatId);
        if (!FixedTimeEquals(expectedToken, token.Trim().ToLowerInvariant()))
        {
            error = "Invalid group mini app token.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        if (expected.Length != provided.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}
