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

    /// <summary>Расписание занятий студента</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();

    /// <summary>Фото расписания, загруженное через Mini App (data URL)</summary>
    public string? SchedulePhotoDataUrl { get; set; }

    /// <summary>Черновик записи расписания при пошаговом добавлении</summary>
    public ScheduleEntry? DraftSchedule { get; set; }

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
