namespace TelegramStudentBot.Models;

public class GroupHomeworkSubjectPreferences
{
    public string? ChatTitle { get; set; }

    public List<string> FavoriteSubjects { get; set; } = new();

    public bool IsConfigured { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
