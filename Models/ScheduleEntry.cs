using System.Text.Json.Serialization;

namespace TelegramStudentBot.Models;

public class ScheduleEntry
{
    [JsonPropertyName("day")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("lesson")]
    public int LessonNumber { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("subGroup")]
    public int? SubGroup { get; set; }

    [JsonPropertyName("weekType")]
    public int? WeekType { get; set; }

    [JsonIgnore]
    public string DayName => DayOfWeek switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        7 => "Воскресенье",
        _ => $"День {DayOfWeek}"
    };

    [JsonIgnore]
    public string SubGroupLabel => SubGroup.HasValue ? $" (подгр. {SubGroup.Value})" : string.Empty;

    [JsonIgnore]
    public string WeekTypeLabel => WeekType switch
    {
        1 => " (нечётная)",
        2 => " (чётная)",
        _ => string.Empty
    };
}
