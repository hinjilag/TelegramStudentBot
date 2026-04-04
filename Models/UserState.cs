namespace TelegramStudentBot.Models;

/// <summary>Состояние диалога с пользователем — определяет, что ожидает бот</summary>
public enum UserState
{
    /// <summary>Обычный режим, бот ждёт команду</summary>
    Idle,

    /// <summary>Ожидание названия новой задачи</summary>
    WaitingForTaskTitle,

    /// <summary>Ожидание предмета новой задачи</summary>
    WaitingForTaskSubject,

    /// <summary>Ожидание дедлайна новой задачи (или "пропустить")</summary>
    WaitingForTaskDeadline,

    /// <summary>Ожидание произвольного времени таймера в минутах</summary>
    WaitingForTimerMinutes,

    /// <summary>Ожидание дня недели для новой записи расписания</summary>
    WaitingForScheduleDay,

    /// <summary>Ожидание названия предмета для новой записи расписания</summary>
    WaitingForScheduleSubject,

    /// <summary>Ожидание времени занятия (например, 09:00–10:30)</summary>
    WaitingForScheduleTime,

    /// <summary>Ожидание аудитории / места (можно пропустить)</summary>
    WaitingForScheduleRoom,
}
