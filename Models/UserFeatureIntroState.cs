namespace TelegramStudentBot.Models;

public class UserFeatureIntroState
{
    public bool HasSeenPlanIntro { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
