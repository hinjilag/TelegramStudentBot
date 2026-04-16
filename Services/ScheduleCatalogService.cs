using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class ScheduleCatalogService
{
    private readonly ScheduleCatalog _catalog;

    public ScheduleCatalogService()
    {
        _catalog = LoadCatalog();
    }

    public string Semester => _catalog.Semester;

    public IReadOnlyList<ScheduleDirectionOption> GetDirections()
    {
        return _catalog.Groups
            .GroupBy(g => g.DirectionCode)
            .OrderBy(g => GetDirectionOrder(g.Key))
            .Select(g =>
            {
                var first = g.OrderBy(x => x.Course).First();
                return new ScheduleDirectionOption(
                    first.DirectionCode,
                    first.DirectionName,
                    first.ShortTitle);
            })
            .ToList();
    }

    public IReadOnlyList<ScheduleGroup> GetGroupsByDirection(string directionCode)
    {
        return _catalog.Groups
            .Where(g => g.DirectionCode == directionCode)
            .OrderBy(g => g.Course)
            .ToList();
    }

    public ScheduleGroup? GetGroup(string scheduleId)
    {
        return _catalog.Groups.FirstOrDefault(g => g.Id == scheduleId);
    }

    public ScheduleGroup? GetGroup(string directionCode, int course)
    {
        return _catalog.Groups.FirstOrDefault(g =>
            g.DirectionCode == directionCode && g.Course == course);
    }

    public int GetCurrentWeekType(DateTime? date = null)
    {
        var currentDate = (date ?? DateTime.Today).Date;
        var days = (currentDate - _catalog.WeekReferenceDate.Date).TotalDays;
        var weeks = (int)Math.Floor(days / 7);
        var normalized = ((weeks % 2) + 2) % 2;

        if (_catalog.WeekReferenceType == 1)
            return normalized == 0 ? 1 : 2;

        return normalized == 0 ? 2 : 1;
    }

    public string GetCurrentWeekLabel(DateTime? date = null)
        => GetCurrentWeekType(date) == 1 ? "нечётная" : "чётная";

    public List<ScheduleEntry> GetEntriesForSelection(
        ScheduleGroup group,
        int? subGroup,
        int weekType)
    {
        return group.Entries
            .Where(e => !subGroup.HasValue || e.SubGroup is null || e.SubGroup == subGroup)
            .Where(e => e.WeekType is null || e.WeekType == weekType)
            .Select(e => new ScheduleEntry
            {
                DayOfWeek = e.DayOfWeek,
                LessonNumber = e.LessonNumber,
                Time = e.Time,
                Subject = e.Subject,
                SubGroup = null,
                WeekType = null
            })
            .DistinctBy(e => new
            {
                e.DayOfWeek,
                e.LessonNumber,
                e.Time,
                e.Subject
            })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.LessonNumber)
            .ThenBy(e => e.Subject)
            .ToList();
    }

    public static int GetDayNumber(DateTime date)
    {
        return date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
    }

    private static ScheduleCatalog LoadCatalog()
    {
        var path = ResolveDataPath("schedules.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл расписаний не найден.", path);

        var json = File.ReadAllText(path);
        var catalog = JsonSerializer.Deserialize<ScheduleCatalog>(json, JsonOptions)
            ?? throw new InvalidOperationException("Не удалось прочитать файл расписаний.");

        catalog.Groups = catalog.Groups
            .OrderBy(g => GetDirectionOrder(g.DirectionCode))
            .ThenBy(g => g.Course)
            .ToList();

        return catalog;
    }

    internal static string ResolveDataPath(string fileName)
    {
        var contentRootPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", fileName);
        if (File.Exists(contentRootPath))
            return contentRootPath;

        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }

    private static int GetDirectionOrder(string directionCode) => directionCode switch
    {
        "01.03.02" => 1,
        "09.03.01" => 2,
        "44.03.05" => 3,
        _ => 100
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
