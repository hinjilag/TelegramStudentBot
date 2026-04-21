namespace TelegramStudentBot.Models;

/// <summary>Учебная задача пользователя</summary>
public class StudyTask
{
    /// <summary>Уникальный идентификатор задачи</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Название задачи</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Предмет / дисциплина</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Крайний срок выполнения (null если не задан)</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>Выполнена ли задача</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Время добавления задачи</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Короткий идентификатор (8 символов) для использования в callback-данных</summary>
    public string ShortId => Id.ToString("N")[..8];
}
