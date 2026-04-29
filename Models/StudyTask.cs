using System.Text.Json.Serialization;

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

    /// <summary>Кто добавил задачу. Используется для общих ДЗ в группах.</summary>
    public string? CreatedByName { get; set; }

    /// <summary>Telegram ID автора задачи, если задача добавлена в группе.</summary>
    public long? CreatedByUserId { get; set; }

    /// <summary>Короткий идентификатор (8 символов) для использования в callback-данных</summary>
    [JsonIgnore]
    public string ShortId => Id.ToString("N")[..8];
}
