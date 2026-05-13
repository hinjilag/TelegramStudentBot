namespace TelegramStudentBot.Models;

public class GroupReminderSettings
{
    public long ChatId { get; set; }

    public string ChatTitle { get; set; } = "Группа";

    public bool IsEnabled { get; set; }

    public ReminderScheduleMode Frequency { get; set; } = ReminderScheduleMode.Daily;

    public int Hour { get; set; } = 20;

    public int Minute { get; set; }

    public List<int> SelectedDays { get; set; } = new();

    public List<long> SelectedParticipantUserIds { get; set; } = new();

    public int? PinnedReminderMessageId { get; set; }

    public DateTime? LastNotificationDate { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string TimeText => $"{Hour:00}:{Minute:00}";

    public string FrequencyText => Frequency switch
    {
        ReminderScheduleMode.Weekdays => "РїРѕ Р±СѓРґРЅСЏРј",
        ReminderScheduleMode.CustomDays when SelectedDays.Count > 0 => "РІ РІС‹Р±СЂР°РЅРЅС‹Рµ РґРЅРё",
        ReminderScheduleMode.CustomDays => "РїРѕ РІС‹Р±СЂР°РЅРЅС‹Рј РґРЅСЏРј",
        _ => "РєР°Р¶РґС‹Р№ РґРµРЅСЊ"
    };
}
