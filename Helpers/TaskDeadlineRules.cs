using System.Globalization;

namespace TelegramStudentBot.Helpers;

public static class TaskDeadlineRules
{
    public static DateTime Today => DateTime.Today;

    public static bool IsInPast(DateTime deadline) => deadline.Date < Today;

    public static string TodayForUser => Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string TodayForInput => Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
