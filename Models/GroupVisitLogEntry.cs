namespace TelegramStudentBot.Models;

public class GroupVisitLogEntry
{
    public long ChatId { get; set; }

    public string? ChatTitle { get; set; }

    public DateTime FirstVisitedAt { get; set; } = DateTime.Now;

    public DateTime LastVisitedAt { get; set; } = DateTime.Now;
}
