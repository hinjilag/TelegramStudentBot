using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик медиа-сообщений: фотографий и изображений-файлов.
/// Скачивает файл из Telegram и отправляет в Llama 3.2 Vision (Groq) для анализа.
///
/// Поддерживаемые форматы: JPG, PNG, GIF, WEBP.
/// Подпись к файлу (caption) используется как вопрос к ИИ.
/// </summary>
public class MediaHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly LlamaVisionService _vision;

    // Groq ограничивает размер изображения: ~4 МБ base64 → ~3 МБ исходник
    private const int MaxFileSizeBytes = 4 * 1024 * 1024;

    public MediaHandler(ITelegramBotClient bot, LlamaVisionService vision)
    {
        _bot    = bot;
        _vision = vision;
    }

    // ══════════════════════════════════════════════════════════
    //  Фото (сжатое Telegram'ом)
    // ══════════════════════════════════════════════════════════

    public async Task HandlePhotoAsync(Message msg, CancellationToken ct)
    {
        var chatId = msg.Chat.Id;
        var photo  = msg.Photo!.OrderByDescending(p => p.Width * p.Height).First();

        await _bot.SendMessage(chatId, "🔍 Анализирую фото...", cancellationToken: ct);

        var bytes = await DownloadFileAsync(photo.FileId, ct);
        if (bytes is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Не удалось скачать фото. Попробуй ещё раз.", cancellationToken: ct);
            return;
        }

        var result = await _vision.AnalyzeImageAsync(bytes, "image/jpeg", msg.Caption?.Trim(), ct);
        await SendResultAsync(chatId, result, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Документ (изображение, отправленное как файл)
    // ══════════════════════════════════════════════════════════

    public async Task HandleDocumentAsync(Message msg, CancellationToken ct)
    {
        var chatId   = msg.Chat.Id;
        var doc      = msg.Document!;
        var mimeType = doc.MimeType ?? DetectMimeByExtension(doc.FileName);

        if (!IsSupportedMime(mimeType))
        {
            await _bot.SendMessage(
                chatId,
                "⚠️ <b>Неподдерживаемый формат.</b>\n\n" +
                "Llama Vision читает изображения: JPG, PNG, GIF, WEBP\n" +
                "PDF не поддерживается — отправь страницы как фото.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(chatId, "🔍 Читаю изображение...", cancellationToken: ct);

        var bytes = await DownloadFileAsync(doc.FileId, ct);
        if (bytes is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Не удалось скачать файл. Попробуй ещё раз.", cancellationToken: ct);
            return;
        }

        if (bytes.Length > MaxFileSizeBytes)
        {
            await _bot.SendMessage(
                chatId,
                "⚠️ Файл слишком большой. Максимальный размер — 4 МБ.\n" +
                "Попробуй отправить фото напрямую (Telegram сожмёт его автоматически).",
                cancellationToken: ct);
            return;
        }

        var result = await _vision.AnalyzeImageAsync(bytes, mimeType, msg.Caption?.Trim(), ct);
        await SendResultAsync(chatId, result, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Вспомогательные методы
    // ══════════════════════════════════════════════════════════

    private async Task<byte[]?> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        try
        {
            var file = await _bot.GetFile(fileId, ct);
            using var ms = new MemoryStream();
            await _bot.DownloadFile(file.FilePath!, ms, ct);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SendResultAsync(long chatId, string result, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId,
            "🦙 <b>Llama Vision:</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        const int chunkSize = 4000;
        for (int i = 0; i < result.Length; i += chunkSize)
        {
            var chunk = result.Substring(i, Math.Min(chunkSize, result.Length - i));
            await _bot.SendMessage(chatId, chunk, cancellationToken: ct);
        }
    }

    private static string DetectMimeByExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "application/octet-stream"
        };
    }

    private static bool IsSupportedMime(string mimeType) => mimeType is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp";
}
