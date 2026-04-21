using System.Text.Json.Serialization;

namespace TelegramStudentBot.Models;

public class ScheduleCatalog
{
    public string Semester { get; set; } = string.Empty;

    public string Faculty { get; set; } = string.Empty;

    public DateTime WeekReferenceDate { get; set; } = new(2026, 4, 13);

    public int WeekReferenceType { get; set; } = 1;

    public List<ScheduleGroup> Groups { get; set; } = new();
}

public class ScheduleGroup
{
    public string Id { get; set; } = string.Empty;

    public string DirectionCode { get; set; } = string.Empty;

    public string DirectionName { get; set; } = string.Empty;

    public string ShortTitle { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Course { get; set; }

    public List<int> SubGroups { get; set; } = new();

    public List<string> SourceSheets { get; set; } = new();

    public List<ScheduleCatalogEntry> Entries { get; set; } = new();
}

public class UserScheduleSelection
{
    public string ScheduleId { get; set; } = string.Empty;

    public int? SubGroup { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public record ScheduleDirectionOption(
    string DirectionCode,
    string DirectionName,
    string ShortTitle);

public class ScheduleCatalogEntry
{
    [JsonPropertyName("day")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("lesson")]
    public int LessonNumber { get; set; }

    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty;

    [JsonPropertyName("weekType")]
    public int? WeekType { get; set; }

    [JsonPropertyName("subGroup")]
    public int? SubGroup { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;
}
