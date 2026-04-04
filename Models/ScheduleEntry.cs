namespace TelegramStudentBot.Models;

/// <summary>Одно занятие в расписании студента</summary>
public class ScheduleEntry
{
    /// <summary>День недели (Понедельник, Вторник, ...)</summary>
    public string Day { get; set; } = "";

    /// <summary>Название предмета</summary>
    public string Subject { get; set; } = "";

    /// <summary>Время проведения, например "09:00–10:30"</summary>
    public string Time { get; set; } = "";

    /// <summary>Аудитория / место проведения (необязательно)</summary>
    public string? Room { get; set; }
}
