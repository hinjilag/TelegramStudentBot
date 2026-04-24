namespace TelegramStudentBot.Models;

public class UserHomeworkSubjectPreferences
{
    public string? Nickname { get; set; }

    public string? Username { get; set; }

    public List<string> FavoriteSubjects { get; set; } = new();

    public bool IsConfigured { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
