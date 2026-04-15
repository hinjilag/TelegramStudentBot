namespace TelegramStudentBot.Models;

/// <summary>Одно занятие в расписании студента</summary>
public class ScheduleEntry
{
    /// <summary>Уникальный идентификатор строки расписания</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>День недели (Понедельник, Вторник, ...)</summary>
    public string Day { get; set; } = "";

    /// <summary>Название предмета</summary>
    public string Subject { get; set; } = "";

    /// <summary>Время проведения, например "09:00–10:30"</summary>
    public string Time { get; set; } = "";

    /// <summary>Тип недели: every, even, odd</summary>
    public string WeekType { get; set; } = "every";

    /// <summary>Приоритетный ли предмет</summary>
    public bool IsPriority { get; set; }

    /// <summary>Короткий идентификатор для интерфейса</summary>
    public string ShortId => Id.ToString("N")[..8];
}
