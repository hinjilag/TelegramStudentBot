using System.Net;
using System.Text;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public static class ScheduleService
{
    public static string FormatSchedule(List<ScheduleEntry> entries, int? currentWeekType = null)
    {
        if (entries.Count == 0)
            return "Расписание пустое.";

        var sb = new StringBuilder();

        var byDay = entries
            .GroupBy(e => e.DayOfWeek)
            .OrderBy(g => g.Key);

        foreach (var day in byDay)
        {
            sb.AppendLine($"\n<b>{GetDayEmoji(day.Key)} {GetDayName(day.Key)}</b>");

            var byLesson = day
                .GroupBy(e => e.LessonNumber)
                .OrderBy(g => g.Key);

            foreach (var lesson in byLesson)
            {
                var lessonEntries = lesson
                    .OrderBy(e => e.WeekType ?? 0)
                    .ThenBy(e => e.SubGroup ?? 0)
                    .ThenBy(e => e.Subject)
                    .ToList();

                var lessonLabel = FormatLessonLabel(lesson.Key);

                var hasWeekSplit = lessonEntries.Any(e => e.WeekType.HasValue);
                if (hasWeekSplit)
                {
                    var firstWeek = lessonEntries.Where(e => e.WeekType == 1).ToList();
                    var secondWeek = lessonEntries.Where(e => e.WeekType == 2).ToList();

                    sb.AppendLine($"{lessonLabel} | первая неделя | {FormatWeekLesson(firstWeek, currentWeekType == 1)}");
                    sb.AppendLine($"{lessonLabel} | вторая неделя | {FormatWeekLesson(secondWeek, currentWeekType == 2)}");
                    continue;
                }

                foreach (var entry in lessonEntries)
                    sb.AppendLine($"{lessonLabel} | {FormatLessonEntry(entry)}");
            }
        }

        return sb.ToString().TrimStart('\n');
    }

    public static string GetDayName(int day) => day switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        7 => "Воскресенье",
        _ => $"День {day}"
    };

    private static string FormatWeekLesson(List<ScheduleEntry> entries, bool isActiveWeek)
    {
        if (entries.Count == 0)
            return "пар нет";

        var joined = string.Join("; ", entries
            .OrderBy(e => e.SubGroup ?? 0)
            .ThenBy(e => e.Subject)
            .Select(FormatLessonEntry));

        return isActiveWeek ? joined + " <" : joined;
    }

    private static string FormatLessonEntry(ScheduleEntry entry)
    {
        var (subject, lessonType) = SplitSubjectAndLessonType(entry.Subject);
        var formatted = $"{Escape(subject)}{FormatSubGroup(entry.SubGroup)}";

        return string.IsNullOrWhiteSpace(lessonType)
            ? formatted
            : $"{formatted} | {Escape(lessonType)}";
    }

    private static (string Subject, string LessonType) SplitSubjectAndLessonType(string subject)
    {
        var trimmed = subject.Trim();
        var openIndex = trimmed.LastIndexOf(" (", StringComparison.Ordinal);

        if (openIndex < 0 || !trimmed.EndsWith(')'))
            return (trimmed, string.Empty);

        var lessonType = trimmed[(openIndex + 2)..^1].Trim();
        var subjectOnly = trimmed[..openIndex].Trim();

        return string.IsNullOrWhiteSpace(subjectOnly) || string.IsNullOrWhiteSpace(lessonType)
            ? (trimmed, string.Empty)
            : (subjectOnly, lessonType);
    }

    private static string FormatSubGroup(int? subGroup)
        => subGroup.HasValue ? $" (подгр. {subGroup.Value})" : string.Empty;

    private static string FormatLessonLabel(int lesson)
        => $"{lesson} пара";

    private static string GetDayEmoji(int day) => day switch
    {
        1 => "🔵",
        2 => "🟢",
        3 => "🟡",
        4 => "🟣",
        5 => "🟠",
        6 => "⚪",
        7 => "🔴",
        _ => "📌"
    };

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);
}
