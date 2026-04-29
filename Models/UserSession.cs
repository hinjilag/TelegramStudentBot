namespace TelegramStudentBot.Models;

/// <summary>Сессия пользователя — хранит всё его состояние в боте</summary>
public class UserSession
{
    /// <summary>Telegram ID пользователя</summary>
    public long UserId { get; set; }

    /// <summary>Имя пользователя (для обращений)</summary>
    public string FirstName { get; set; } = "Студент";

    /// <summary>Текущее состояние диалога</summary>
    public UserState State { get; set; } = UserState.Idle;

    /// <summary>Список учебных задач пользователя</summary>
    public List<StudyTask> Tasks { get; set; } = new();

    /// <summary>Активный таймер (null, если таймер не запущен)</summary>
    public ActiveTimer? ActiveTimer { get; set; }

    /// <summary>Черновик задачи при пошаговом добавлении</summary>
    public StudyTask? DraftTask { get; set; }

    /// <summary>Дата дедлайна личного дела, выбранная быстрой кнопкой.</summary>
    public DateTime? PendingTaskDeadlineDate { get; set; }

    /// <summary>Идентификатор группы, для которой пользователь сейчас вводит общее ДЗ.</summary>
    public long? PendingGroupHomeworkChatId { get; set; }

    /// <summary>Название группы, для которой пользователь сейчас вводит общее ДЗ.</summary>
    public string? PendingGroupHomeworkChatTitle { get; set; }

    /// <summary>Идентификатор чата, для которого пользователь настраивает напоминания.</summary>
    public long ReminderTargetChatId { get; set; }

    /// <summary>Название чата, для которого пользователь настраивает напоминания.</summary>
    public string? ReminderTargetChatTitle { get; set; }

    /// <summary>Признак, что напоминания настраиваются для группового чата.</summary>
    public bool ReminderTargetIsGroup { get; set; }

    /// <summary>Временные варианты предметов для добавления домашнего задания</summary>
    public Dictionary<string, string> HomeworkSubjectChoices { get; set; } = new();

    /// <summary>Временные варианты типов занятия для выбранного предмета</summary>
    public Dictionary<string, string> HomeworkLessonTypeChoices { get; set; } = new();

    /// <summary>Сохранённое расписание пар.</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();

    /// <summary>
    /// Временное хранилище расписания во время диалога выбора типа недели.
    /// Очищается после подтверждения пользователем.
    /// </summary>
    public List<ScheduleEntry>? PendingSchedule { get; set; }

    /// <summary>
    /// Текущий тип недели: 1 = нечётная, 2 = чётная, null = не задан.
    /// Используется для показа/напоминаний когда пара только на одной неделе.
    /// </summary>
    public int? CurrentWeekType { get; set; }

    /// <summary>Выбранная пользователем подгруппа (например 3 или 4)</summary>
    public int? CurrentSubGroup { get; set; }

    /// <summary>Текущий индекс слота при пошаговой проверке расписания: 0..23</summary>
    public int ReviewSlotIndex { get; set; }

    /// <summary>Количество невыполненных задач</summary>
    public int PendingTasksCount => Tasks.Count(t => !t.IsCompleted);
}
