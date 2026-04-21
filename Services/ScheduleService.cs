using System.Net;
using System.Text;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public static class ScheduleService
{
    public static string FormatSchedule(List<ScheduleEntry> entries, int? currentWeekType = null)
    {
        if (entries.Count == 0)
            return "Расписание пока пустое.";

        var sb = new StringBuilder();

        var byDay = entries
            .GroupBy(GetDayNumber)
            .OrderBy(group => group.Key);

        foreach (var day in byDay)
        {
            if (day.Key <= 0)
                continue;

            sb.AppendLine($"<b>{GetDayName(day.Key)}</b>");

            var byLesson = day
                .GroupBy(GetLessonNumber)
                .OrderBy(group => group.Key);

            foreach (var lesson in byLesson)
            {
                var lessonEntries = lesson
                    .OrderBy(entry => entry.WeekTypeCode ?? 0)
                    .ThenBy(entry => entry.SubGroup ?? 0)
                    .ThenBy(entry => entry.Subject)
                    .ToList();

                var time = lessonEntries.FirstOrDefault()?.Time;
                var prefix = BuildLessonPrefix(lesson.Key, time);
                var hasWeekSplit = lessonEntries.Any(entry => entry.WeekTypeCode.HasValue);

                if (!hasWeekSplit)
                {
                    foreach (var entry in lessonEntries)
                        sb.AppendLine($"{prefix} {Escape(entry.Subject)}{FormatSubGroup(entry.SubGroup)}");

                    continue;
                }

                var oddWeek = lessonEntries.Where(entry => entry.WeekTypeCode == 1).ToList();
                var evenWeek = lessonEntries.Where(entry => entry.WeekTypeCode == 2).ToList();

                sb.AppendLine(prefix);
                sb.AppendLine($"  Нечётная: {FormatWeekLesson(oddWeek, currentWeekType == 1)}");
                sb.AppendLine($"  Чётная: {FormatWeekLesson(evenWeek, currentWeekType == 2)}");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
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

        var text = string.Join("; ", entries
            .OrderBy(entry => entry.SubGroup ?? 0)
            .ThenBy(entry => entry.Subject)
            .Select(entry => $"{Escape(entry.Subject)}{FormatSubGroup(entry.SubGroup)}"));

        return isActiveWeek ? $"{text} <- текущая" : text;
    }

    private static string FormatSubGroup(int? subGroup)
        => subGroup.HasValue ? $" (подгр. {subGroup.Value})" : string.Empty;

    private static string BuildLessonPrefix(int lesson, string? time)
    {
        if (string.IsNullOrWhiteSpace(time))
            return $"• {lesson} пара:";

        return $"• {lesson} пара ({Escape(time)}):";
    }

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
        => entry.LessonNumber > 0 ? entry.LessonNumber : 0;
}
