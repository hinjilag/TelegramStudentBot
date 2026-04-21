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
                return new ScheduleDirectionOption(first.DirectionCode, first.DirectionName, first.ShortTitle);
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
        => _catalog.Groups.FirstOrDefault(g => g.Id == scheduleId);

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

    public List<ScheduleEntry> GetEntriesForSelection(ScheduleGroup group, int? subGroup, int weekType)
    {
        return group.Entries
            .Where(e => !subGroup.HasValue || e.SubGroup is null || e.SubGroup == subGroup)
            .Where(e => e.WeekType is null || e.WeekType == weekType)
            .Select(ToScheduleEntry)
            .DistinctBy(e => new { e.DayOfWeek, e.LessonNumber, e.Time, e.Subject })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.LessonNumber)
            .ThenBy(e => e.Subject)
            .ToList();
    }

    public List<ScheduleEntry> GetAllEntriesForSelection(ScheduleGroup group, int? subGroup)
    {
        return group.Entries
            .Where(e => !subGroup.HasValue || e.SubGroup is null || e.SubGroup == subGroup)
            .Select(ToScheduleEntry)
            .DistinctBy(e => new { e.DayOfWeek, e.LessonNumber, e.Time, e.Subject, e.WeekTypeCode })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.LessonNumber)
            .ThenBy(e => e.Subject)
            .ToList();
    }

    public DateTime? FindNextLessonDate(IEnumerable<ScheduleEntry> entries, string subject, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        DateTime? best = null;

        foreach (var date in Enumerable.Range(0, 120).Select(offset => current.Date.AddDays(offset)))
        {
            var dayNumber = GetDayNumber(date);
            var weekType = GetCurrentWeekType(date);

            foreach (var entry in entries)
            {
                if (entry.DayOfWeek != dayNumber ||
                    !string.Equals(entry.Subject, subject, StringComparison.OrdinalIgnoreCase) ||
                    (entry.WeekTypeCode.HasValue && entry.WeekTypeCode.Value != weekType))
                {
                    continue;
                }

                var occurrence = date.Add(ParseLessonStart(entry.Time));
                if (occurrence <= current)
                    continue;

                if (best is null || occurrence < best.Value)
                    best = occurrence;
            }
        }

        return best?.Date;
    }

    public static string GetHomeworkSubjectTitle(string subject)
    {
        var (title, _) = SplitHomeworkSubject(subject);
        return title;
    }

    public static string GetHomeworkLessonTypeLabel(string subject)
    {
        var (_, lessonType) = SplitHomeworkSubject(subject);
        return lessonType;
    }

    public static int GetHomeworkSubjectSortGroup(string subject)
        => IsPriorityHomeworkSubject(subject) ? 0 : 1;

    public static int GetDayNumber(DateTime date)
        => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    private static bool IsPriorityHomeworkSubject(string subject)
    {
        var normalized = NormalizeForPriorityMatch(subject);

        if (NonPrioritySubjectKeywords.Any(normalized.Contains))
            return false;

        return PrioritySubjectKeywords.Any(normalized.Contains);
    }

    private static (string Title, string LessonType) SplitHomeworkSubject(string subject)
    {
        var trimmed = subject.Trim();
        var close = trimmed.LastIndexOf(')');
        var open = trimmed.LastIndexOf('(');

        if (open >= 0 && close == trimmed.Length - 1 && open < close)
        {
            var rawType = trimmed[(open + 1)..close].Trim(' ', '.');
            var lessonType = NormalizeLessonType(rawType);

            if (!string.IsNullOrWhiteSpace(lessonType))
            {
                var title = trimmed[..open].Trim();
                return (string.IsNullOrWhiteSpace(title) ? trimmed : title, lessonType);
            }
        }

        return (trimmed, "Занятие");
    }

    private static string NormalizeLessonType(string rawType)
    {
        var normalized = rawType.ToLowerInvariant().Replace('ё', 'е');

        if (normalized.Contains("лекц"))
            return "Лекция";
        if (normalized.Contains("практ"))
            return "Практика";
        if (normalized.Contains("лаб"))
            return "Лабораторная";
        if (normalized.Contains("сем"))
            return "Семинар";

        return string.Empty;
    }

    private static string NormalizeForPriorityMatch(string subject)
        => subject.ToLowerInvariant().Replace('ё', 'е');

    private static TimeSpan ParseLessonStart(string? time)
    {
        if (string.IsNullOrWhiteSpace(time))
            return TimeSpan.Zero;

        var startText = time.Split('-', 2)[0].Trim();
        return TimeSpan.TryParse(startText, out var start) ? start : TimeSpan.Zero;
    }

    private static ScheduleEntry ToScheduleEntry(ScheduleCatalogEntry entry)
    {
        return new ScheduleEntry
        {
            Day = ScheduleService.GetDayName(entry.DayOfWeek),
            DayOfWeek = entry.DayOfWeek,
            LessonNumber = entry.LessonNumber,
            Time = entry.Time,
            Subject = entry.Subject,
            SubGroup = entry.SubGroup,
            WeekType = MapWeekType(entry.WeekType)
        };
    }

    private static string MapWeekType(int? weekType) => weekType switch
    {
        1 => "odd",
        2 => "even",
        _ => "every"
    };

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

    private static readonly string[] PrioritySubjectKeywords =
    {
        "1с", "алгеб", "алгоритм", "анализ", "базы данных", "веб", "вероятност",
        "вычисл", "геометр", "данн", "дифференц", "дискрет", "информат", "искусствен",
        "квант", "комплекс", "компьютер", "конструирование по", "криптограф", "логик",
        "математ", "моделирован", "олимпиад", "оптимизац", "программ", "роботот",
        "сети", "систем", "статист", "схемотех", "технолог", "уравнен", "электроник",
        "электротех"
    };

    private static readonly string[] NonPrioritySubjectKeywords =
    {
        "безопасность жизнедеятельности", "вожат", "иностран", "история религ", "история росс",
        "обучение служением", "педагогика", "предпринимател", "правов", "психология", "религи",
        "речев", "русский", "физическ", "философ", "экология", "электив"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
