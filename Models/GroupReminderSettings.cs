namespace TelegramStudentBot.Models;

public enum GroupReminderFrequency
{
    Daily,
    Weekdays
}

public class GroupReminderSettings
{
    public long ChatId { get; set; }

    public string ChatTitle { get; set; } = "Группа";

    public bool IsEnabled { get; set; }

    public GroupReminderFrequency Frequency { get; set; } = GroupReminderFrequency.Daily;

    public int Hour { get; set; } = 20;

    public int Minute { get; set; }

    public DateTime? LastNotificationDate { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string TimeText => $"{Hour:00}:{Minute:00}";

    public string FrequencyText => Frequency switch
    {
        GroupReminderFrequency.Weekdays => "по будням",
        _ => "каждый день"
    };
}
