namespace TelegramStudentBot.Models;

public class MiniAppPinState
{
    public long ChatId { get; set; }

    public int MessageId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
