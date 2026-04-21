using System.Net;

namespace TelegramStudentBot.Helpers;

public static class TelegramHtml
{
    public static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
