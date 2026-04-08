using System.Text.Json.Serialization;

namespace TelegramStudentBot.Models;

/// <summary>Одна запись расписания: конкретная пара в конкретный день</summary>
public class ScheduleEntry
{
    /// <summary>День недели: 1=Пн, 2=Вт, 3=Ср, 4=Чт, 5=Пт, 6=Сб, 7=Вс</summary>
    [JsonPropertyName("day")]
    public int DayOfWeek { get; set; }

    /// <summary>Номер пары (1, 2, 3, ...)</summary>
    [JsonPropertyName("lesson")]
    public int LessonNumber { get; set; }

    /// <summary>Название предмета (без преподавателя и аудитории)</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Подгруппа / тип группы:
    ///   null  = для всей группы (ячейка занимает оба столбца)
    ///   1     = подгруппа 1 (левый столбец) / нечётная неделя
    ///   2     = подгруппа 2 (правый столбец) / чётная неделя
    /// </summary>
    [JsonPropertyName("subGroup")]
    public int? SubGroup { get; set; }

    /// <summary>Русское название дня недели</summary>
    [JsonIgnore]
    public string DayName => DayOfWeek switch
    {
        1 => "Понедельник",
        2 => "Вторник",
        3 => "Среда",
        4 => "Четверг",
        5 => "Пятница",
        6 => "Суббота",
        7 => "Воскресенье",
        _ => $"День {DayOfWeek}"
    };

    /// <summary>Метка подгруппы для отображения</summary>
    [JsonIgnore]
    public string SubGroupLabel => SubGroup switch
    {
        1 => " (подгр. 1)",
        2 => " (подгр. 2)",
        _ => string.Empty
    };

    // Обратная совместимость — старый код использует WeekType
    [JsonIgnore]
    public int? WeekType
    {
        get => SubGroup;
        set => SubGroup = value;
    }

    [JsonIgnore]
    public string WeekTypeLabel => SubGroupLabel;
}
