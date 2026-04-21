using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Helpers;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

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
        TrySendAsync(chatId, "⏹ Таймер остановлен из Mini App.", replyMarkup: null, ct);

    public Task TrySendTaskAddedAsync(long chatId, StudyTask task, CancellationToken ct)
    {
        var deadline = task.Deadline.HasValue
            ? $"\n📅 Дедлайн: <b>{task.Deadline.Value:dd.MM.yyyy}</b>"
            : string.Empty;

        return TrySendAsync(
            chatId,
            "📌 <b>Задача добавлена из Mini App</b>\n\n" +
            $"<b>{TelegramHtml.Escape(task.Title)}</b>\n" +
            $"📚 {TelegramHtml.Escape(task.Subject)}{deadline}",
            BuildTaskKeyboard(task),
            ct);
    }

    public Task TrySendTaskStatusChangedAsync(long chatId, StudyTask task, CancellationToken ct)
    {
        var status = task.IsCompleted ? "отмечена как выполненная" : "возвращена в активные";

        return TrySendAsync(
            chatId,
            $"✅ Задача <b>«{TelegramHtml.Escape(task.Title)}»</b> {status} из Mini App.",
            replyMarkup: null,
            ct);
    }

    public Task TrySendTaskDeletedAsync(long chatId, StudyTask task, CancellationToken ct) =>
        TrySendAsync(
            chatId,
            $"🗑 Задача <b>«{TelegramHtml.Escape(task.Title)}»</b> удалена из Mini App.",
            replyMarkup: null,
            ct);

    public Task TrySendScheduleSavedAsync(long chatId, IReadOnlyCollection<ScheduleEntry> entries, bool hasPhoto, CancellationToken ct)
    {
        var lines = new List<string>
        {
            "🗓 <b>Расписание обновлено</b>"
        };

        if (hasPhoto)
            lines.Add("Фото расписания сохранено.");

        if (entries.Count == 0)
        {
            lines.Add("Расписание пока пустое.");
            return TrySendAsync(chatId, string.Join("\n\n", lines), replyMarkup: null, ct);
        }

        lines.Add(ScheduleService.FormatSchedule(entries.ToList()));

        return TrySendAsync(chatId, string.Join("\n\n", lines), replyMarkup: null, ct);
    }

    public Task TrySendScheduleClearedAsync(long chatId, CancellationToken ct) =>
        TrySendAsync(chatId, "🗑 Расписание очищено.", replyMarkup: null, ct);

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
}
