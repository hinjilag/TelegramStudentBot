using System.Net;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramStudentBot.MiniApp;

public class MiniAppChatSyncService
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<MiniAppChatSyncService> _logger;

    public MiniAppChatSyncService(ITelegramBotClient bot, ILogger<MiniAppChatSyncService> logger)
    {
        _bot = bot;
        _logger = logger;
    }

    public async Task NotifyAsync(long userId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _bot.SendMessage(
                chatId: userId,
                text: $"📱 <b>Mini App</b>\n{text}",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to send mini app sync message to user {UserId}", userId);
        }
    }

    public static string Escape(string text) => WebUtility.HtmlEncode(text);
}
