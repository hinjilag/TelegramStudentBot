namespace TelegramStudentBot.Models;

public class UserProfile
{
    public long UserId { get; set; }

    public string Nickname { get; set; } = "Студент";

    public string? Username { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class StoredUserTasks
{
    public string Nickname { get; set; } = "Студент";

    public string? Username { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<StudyTask> Tasks { get; set; } = new();
}
