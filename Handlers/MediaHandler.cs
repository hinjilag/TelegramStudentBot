using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Обработчик медиа-сообщений: фотографий и документов.
/// Скачивает файл из Telegram и отправляет в GigaChat AI для анализа.
///
/// Поддерживаемые типы файлов:
///   Изображения — JPG, PNG, GIF, WEBP
///
/// Подпись к файлу (caption) используется как вопрос к ИИ.
/// Если подписи нет — ИИ делает общий анализ содержимого.
/// </summary>
public class MediaHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly GigaChatService _gigaChat;

    // Максимальный размер файла для загрузки в GigaChat (10 МБ)
    private const int MaxFileSizeBytes = 10 * 1024 * 1024;

    public MediaHandler(ITelegramBotClient bot, GigaChatService gigaChat)
    {
        _bot      = bot;
        _gigaChat = gigaChat;
    }

    // ══════════════════════════════════════════════════════════
    //  Обработка фото
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Обрабатывает фотографию.
    /// Telegram присылает несколько размеров — берём самый большой.
    /// </summary>
    public async Task HandlePhotoAsync(Message msg, CancellationToken ct)
    {
        var chatId = msg.Chat.Id;

        // Берём фото с максимальным разрешением
        var photo = msg.Photo!.OrderByDescending(p => p.Width * p.Height).First();

        await _bot.SendMessage(chatId, "🔍 Анализирую фото...", cancellationToken: ct);

        var bytes = await DownloadFileAsync(photo.FileId, ct);
        if (bytes is null)
        {
            await _bot.SendMessage(chatId, "⚠️ Не удалось скачать фото. Попробуй ещё раз.", cancellationToken: ct);
            return;
        }

        // Caption используется как вопрос к ИИ
        var caption = msg.Caption?.Trim();
        var result  = await _gigaChat.AnalyzeMediaAsync(bytes, "image/jpeg", caption, ct);

        await SendResultAsync(chatId, result, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Обработка документов
    // ══════════════════════════════════════════════════════════

    /// <summary>Обрабатывает присланный документ (PDF или изображение).</summary>
    public async Task HandleDocumentAsync(Message msg, CancellationToken ct)
    {
        var chatId = msg.Chat.Id;
        var doc    = msg.Document!;

        // Определяем MIME-тип из метаданных или расширения файла
        var mimeType = doc.MimeType ?? DetectMimeByExtension(doc.FileName);

        if (!IsSupportedMime(mimeType))
        {
            await _bot.SendMessage(
                chatId,
                "⚠️ <b>Неподдерживаемый формат файла.</b>\n\n" +
                "GigaChat умеет читать изображения:\n" +
                "🖼 JPG, PNG, GIF, WEBP\n\n" +
                "PDF не поддерживается — отправь страницы как фото.",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(chatId, "🔍 Читаю документ...", cancellationToken: ct);

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
                "⚠️ Файл слишком большой. Максимальный размер — 20 МБ.",
                cancellationToken: ct);
            return;
        }

        var caption = msg.Caption?.Trim();
        var result  = await _gigaChat.AnalyzeMediaAsync(bytes, mimeType, caption, ct);

        await SendResultAsync(chatId, result, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  Вспомогательные методы
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Скачать файл из Telegram по fileId.
    /// Возвращает null при ошибке.
    /// </summary>
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

    /// <summary>
    /// Отправить ответ ИИ пользователю.
    /// Если текст длиннее 4096 символов — разбиваем на части.
    /// </summary>
    private async Task SendResultAsync(long chatId, string result, CancellationToken ct)
    {
        // Заголовок (HTML)
        await _bot.SendMessage(
            chatId,
            "🤖 <b>Ответ ИИ:</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        // Основной текст (без HTML — чтобы не ломать спецсимволы из ответа Gemini)
        const int chunkSize = 4000;
        for (int i = 0; i < result.Length; i += chunkSize)
        {
            var chunk = result.Substring(i, Math.Min(chunkSize, result.Length - i));
            await _bot.SendMessage(chatId, chunk, cancellationToken: ct);
        }
    }

    /// <summary>Определить MIME-тип по расширению файла</summary>
    private static string DetectMimeByExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".pdf"            => "application/pdf",
            _                 => "application/octet-stream"
        };
    }

    /// <summary>Поддерживается ли данный MIME-тип GigaChat API</summary>
    private static bool IsSupportedMime(string mimeType) => mimeType is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp";
}
