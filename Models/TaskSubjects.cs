namespace TelegramStudentBot.Models;

public static class TaskSubjects
{
    public const string Personal = "Личное";

    public static bool IsPersonal(string? subject)
        => string.Equals(subject, Personal, StringComparison.OrdinalIgnoreCase);
}
