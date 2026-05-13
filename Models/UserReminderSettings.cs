namespace TelegramStudentBot.Models;

public class UserReminderSettings
{
    public long ChatId { get; set; }

    public string Nickname { get; set; } = "Студент";

    public string? Username { get; set; }

    public bool IsEnabled { get; set; }

    public bool PromptAnswered { get; set; }

    public ReminderScheduleMode Frequency { get; set; } = ReminderScheduleMode.Daily;

    public int Hour { get; set; } = 20;

    public int Minute { get; set; }

    public List<int> SelectedDays { get; set; } = new();

    public DateTime? LastNotificationDate { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string TimeText => $"{Hour:00}:{Minute:00}";

    public string FrequencyText => Frequency switch
    {
        ReminderScheduleMode.Weekdays => "по будням",
        ReminderScheduleMode.CustomDays when SelectedDays.Count > 0 => "в выбранные дни",
        ReminderScheduleMode.CustomDays => "по выбранным дням",
        _ => "каждый день"
    };
}
