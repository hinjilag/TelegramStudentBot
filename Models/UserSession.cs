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

    /// <summary>
    /// Уровень усталости от 0 до 100.
    /// 0–30 — свежий, 31–60 — умеренная, 61–85 — высокая, 86–100 — истощение.
    /// </summary>
    public int FatigueLevel { get; set; } = 0;

    /// <summary>
    /// Количество рабочих сессий подряд без отдыха.
    /// Сбрасывается при каждом перерыве.
    /// </summary>
    public int WorkSessionsWithoutRest { get; set; } = 0;

    /// <summary>Список учебных задач пользователя</summary>
    public List<StudyTask> Tasks { get; set; } = new();

    /// <summary>Активный таймер (null, если таймер не запущен)</summary>
    public ActiveTimer? ActiveTimer { get; set; }

    /// <summary>Черновик задачи при пошаговом добавлении</summary>
    public StudyTask? DraftTask { get; set; }

    /// <summary>Сохранённое расписание пар (загружается через /schedule + фото)</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();

    /// <summary>
    /// Временное хранилище расписания во время диалога выбора типа недели.
    /// Очищается после подтверждения пользователем.
    /// </summary>
    public List<ScheduleEntry>? PendingSchedule { get; set; }

    /// <summary>Исходное изображение расписания для повторного разбора после выбора подгруппы</summary>
    public byte[]? PendingScheduleImage { get; set; }

    /// <summary>Найденные на изображении номера подгрупп слева направо</summary>
    public List<int> AvailableSubGroups { get; set; } = new();

    /// <summary>
    /// Текущий тип недели: 1 = нечётная, 2 = чётная, null = не задан.
    /// Используется для показа/напоминаний когда пара только на одной неделе.
    /// </summary>
    public int? CurrentWeekType { get; set; }

    /// <summary>Выбранная пользователем подгруппа (например 3 или 4)</summary>
    public int? CurrentSubGroup { get; set; }

    /// <summary>Текущий индекс слота при пошаговой проверке расписания: 0..23</summary>
    public int ReviewSlotIndex { get; set; }

    /// <summary>Человекочитаемое описание уровня усталости</summary>
    public string FatigueDescription => FatigueLevel switch
    {
        <= 30 => "😊 Свежий",
        <= 60 => "😐 Умеренная усталость",
        <= 85 => "😩 Высокая усталость",
        _      => "🥵 Истощение — срочно отдохни!"
    };

    /// <summary>Нужно ли рекомендовать отдых (высокая усталость или много сессий подряд)</summary>
    public bool NeedsRest => FatigueLevel >= 60 || WorkSessionsWithoutRest >= 4;

    /// <summary>Количество невыполненных задач</summary>
    public int PendingTasksCount => Tasks.Count(t => !t.IsCompleted);
}
