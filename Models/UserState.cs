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

    /// <summary>Ожидание времени ежедневных напоминаний в формате HH:mm</summary>
    WaitingForReminderTime,
}
