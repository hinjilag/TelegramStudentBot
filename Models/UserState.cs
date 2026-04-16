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

    /// <summary>Ожидание фотографии расписания (оставлено только для совместимости старых сессий)</summary>
    WaitingForSchedulePhoto,

    /// <summary>
    /// Расписание распознано, ждём подтверждения от пользователя.
    /// PendingSchedule содержит распознанные записи.
    /// </summary>
    WaitingForScheduleConfirmation,

    /// <summary>
    /// Пользователь нажал "Исправить" — ждём текст с исправлением.
    /// Например: "первой парой в среду у меня не мат анализ, а линейная алгебра"
    /// </summary>
    WaitingForScheduleCorrection,

    /// <summary>Пошаговая проверка расписания: ждём решение по текущему слоту</summary>
    WaitingForScheduleReview,

    /// <summary>Ждём точное исправление текущего слота в пошаговой проверке</summary>
    WaitingForReviewSlotCorrection,

    /// <summary>Ожидание выбора типа недели (нечётная/чётная) — после подтверждения расписания</summary>
    WaitingForWeekChoice,
}
