using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramStudentBot.Services;

public class InlineMessageCleanupService
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(30);

    private readonly ITelegramBotClient _bot;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _scheduled = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new();

    public InlineMessageCleanupService(ITelegramBotClient bot)
    {
        _bot = bot;
    }

    public void Track(long chatId, int messageId, object? replyMarkup)
    {
        if (replyMarkup is not InlineKeyboardMarkup)
            return;

        var key = BuildKey(chatId, messageId);
        if (_scheduled.TryRemove(key, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        _expiresAt[key] = DateTimeOffset.UtcNow.Add(DefaultDelay);

        var cts = new CancellationTokenSource();
        _scheduled[key] = cts;
        _ = DeleteLaterAsync(chatId, messageId, key, cts);
    }

    public bool IsExpired(long chatId, int messageId, DateTime messageDate)
    {
        var key = BuildKey(chatId, messageId);

        if (_expiresAt.TryGetValue(key, out var expiresAt))
            return DateTimeOffset.UtcNow >= expiresAt;

        return DateTimeOffset.UtcNow >= new DateTimeOffset(messageDate.ToUniversalTime()).Add(DefaultDelay);
    }

    public async Task TryDeleteAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _bot.DeleteMessage(chatId, messageId, ct);
        }
        catch (ApiRequestException)
        {
        }
    }

    private async Task DeleteLaterAsync(long chatId, int messageId, string key, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DefaultDelay, cts.Token);
            await _bot.DeleteMessage(chatId, messageId, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiRequestException)
        {
        }
        finally
        {
            _expiresAt.TryRemove(key, out _);

            if (_scheduled.TryRemove(key, out var current))
                current.Dispose();
        }
    }

    private static string BuildKey(long chatId, int messageId)
        => $"{chatId}:{messageId}";
}
