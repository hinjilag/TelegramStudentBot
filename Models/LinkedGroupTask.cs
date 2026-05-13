namespace TelegramStudentBot.Models;

public class LinkedGroupTask
{
    public long ChatId { get; set; }

    public string ChatTitle { get; set; } = "Группа";

    public StudyTask Task { get; set; } = new();
}
