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
            .GroupBy(GetDayNumber)
            .OrderBy(g => g.Key);

        foreach (var day in byDay)
        {
            sb.AppendLine($"\n<b>{GetDayName(day.Key)}:</b>");

            var byLesson = day
                .GroupBy(GetLessonNumber)
                .OrderBy(g => g.Key);

            foreach (var lesson in byLesson)
            {
                var lessonEntries = lesson
                    .OrderBy(e => e.WeekTypeCode ?? 0)
                    .ThenBy(e => e.SubGroup ?? 0)
                    .ThenBy(e => e.Subject)
                    .ToList();

                var lessonLabel = FormatLessonLabel(
                    lesson.Key,
                    lessonEntries.FirstOrDefault()?.Time);

                var hasWeekSplit = lessonEntries.Any(e => e.WeekTypeCode.HasValue);
                if (hasWeekSplit)
                {
                    var firstWeek = lessonEntries.Where(e => e.WeekTypeCode == 1).ToList();
                    var secondWeek = lessonEntries.Where(e => e.WeekTypeCode == 2).ToList();

                    sb.AppendLine($"  {lessonLabel}: первая неделя: {FormatWeekLesson(firstWeek, currentWeekType == 1)}");
                    sb.AppendLine($"     вторая неделя: {FormatWeekLesson(secondWeek, currentWeekType == 2)}");
                    continue;
                }

                foreach (var entry in lessonEntries)
                    sb.AppendLine($"  {lessonLabel}: {Escape(entry.Subject)}{FormatSubGroup(entry.SubGroup)}");
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
            .Select(e => $"{Escape(e.Subject)}{FormatSubGroup(e.SubGroup)}"));

        return isActiveWeek ? joined + " <" : joined;
    }

    private static string FormatSubGroup(int? subGroup)
        => subGroup.HasValue ? $" (подгр. {subGroup.Value})" : string.Empty;

    private static string FormatLessonLabel(int lesson, string? time)
        => string.IsNullOrWhiteSpace(time)
            ? $"{lesson} пара"
            : $"{lesson} пара ({time})";

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static int GetDayNumber(ScheduleEntry entry)
    {
        if (entry.DayOfWeek is >= 1 and <= 7)
            return entry.DayOfWeek;

        return entry.Day switch
        {
            "Понедельник" => 1,
            "Вторник" => 2,
            "Среда" => 3,
            "Четверг" => 4,
            "Пятница" => 5,
            "Суббота" => 6,
            "Воскресенье" => 7,
            _ => 0
        };
    }

    private static int GetLessonNumber(ScheduleEntry entry)
    {
        if (entry.LessonNumber > 0)
            return entry.LessonNumber;

        return 0;
    }
}
