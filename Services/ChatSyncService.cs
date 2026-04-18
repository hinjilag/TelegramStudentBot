 using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Sends compact Telegram chat updates for changes made outside the chat UI.
/// </summary>
public class ChatSyncService
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<ChatSyncService> _logger;

    public ChatSyncService(ITelegramBotClient bot, ILogger<ChatSyncService> logger)
    {
        _bot = bot;
        _logger = logger;
    }

    public Task TrySendTimerStoppedAsync(long chatId, CancellationToken ct) =>
        TrySendAsync(
            chatId,
            "⏹ Таймер остановлен из Mini App.",
            replyMarkup: null,
            ct);

    public Task TrySendTaskAddedAsync(long chatId, StudyTask task, CancellationToken ct)
    {
        var deadline = task.Deadline.HasValue
            ? $"\n📅 Дедлайн: <b>{task.Deadline.Value:dd.MM.yyyy}</b>"
            : "";

        return TrySendAsync(
            chatId,
            "📌 <b>ДЗ добавлено из Mini App</b>\n\n" +
            $"<b>{TelegramHtml.Escape(task.Title)}</b>\n" +
            $"📚 {TelegramHtml.Escape(task.Subject)}{deadline}",
            BuildTaskKeyboard(task),
            ct);
    }

    public Task TrySendTaskStatusChangedAsync(long chatId, StudyTask task, CancellationToken ct)
    {
        var status = task.IsCompleted ? "отмечено как выполненное" : "возвращено в активные";

        return TrySendAsync(
            chatId,
            $"✅ ДЗ <b>«{TelegramHtml.Escape(task.Title)}»</b> {status} из Mini App.",
            replyMarkup: null,
            ct);
    }

    public Task TrySendTaskDeletedAsync(long chatId, StudyTask task, CancellationToken ct) =>
        TrySendAsync(
            chatId,
            $"🗑 ДЗ <b>«{TelegramHtml.Escape(task.Title)}»</b> удалено из Mini App.",
            replyMarkup: null,
            ct);

    public Task TrySendScheduleSavedAsync(long chatId, IReadOnlyCollection<ScheduleEntry> entries, bool hasPhoto, CancellationToken ct)
    {
        var lines = new List<string>
        {
            "🗓 <b>Расписание обновлено из Mini App</b>",
            $"Строк: <b>{entries.Count}</b>" + (hasPhoto ? "\nФото расписания сохранено." : "")
        };

        foreach (var entry in entries.Take(8))
        {
            lines.Add(FormatScheduleEntry(entry));
        }

        if (entries.Count > 8)
        {
            lines.Add($"...и еще {entries.Count - 8}");
        }

        return TrySendAsync(chatId, string.Join("\n", lines), replyMarkup: null, ct);
    }

    public Task TrySendScheduleClearedAsync(long chatId, CancellationToken ct) =>
        TrySendAsync(
            chatId,
            "🗑 Расписание очищено из Mini App.",
            replyMarkup: null,
            ct);

    private async Task TrySendAsync(long chatId, string text, InlineKeyboardMarkup? replyMarkup, CancellationToken ct)
    {
        if (chatId == 0)
            return;

        try
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось отправить синхронизацию в чат {ChatId}", chatId);
        }
    }

    private static InlineKeyboardMarkup BuildTaskKeyboard(StudyTask task) =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task_done_{task.ShortId}"),
                InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"task_del_{task.ShortId}")
            }
        });

    private static string FormatScheduleEntry(ScheduleEntry entry)
    {
        var week = entry.WeekType switch
        {
            "even" => " (четная)",
            "odd" => " (нечетная)",
            _ => ""
        };

        var priority = entry.IsPriority ? " 🔴" : "";
        return $"• <b>{TelegramHtml.Escape(entry.Day)}</b> {TelegramHtml.Escape(entry.Time)} - {TelegramHtml.Escape(entry.Subject)}{week}{priority}";
    }
}
