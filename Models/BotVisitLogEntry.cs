namespace TelegramStudentBot.Models;

public class BotVisitLogEntry
{
    public long UserId { get; set; }

    public string Nickname { get; set; } = "Студент";

    public string? Username { get; set; }

    public DateTime VisitedAt { get; set; } = DateTime.Now;
}
