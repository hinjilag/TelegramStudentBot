namespace TelegramStudentBot.Models;

/// <summary>Тип активного таймера</summary>
public enum TimerType
{
    /// <summary>Рабочая сессия (учёба)</summary>
    Work,

    /// <summary>Перерыв / отдых</summary>
    Rest
}

/// <summary>Информация о запущенном таймере</summary>
public class ActiveTimer
{
    /// <summary>Тип таймера: работа или отдых</summary>
    public TimerType Type { get; set; }

    /// <summary>Время запуска таймера</summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>Длительность в минутах</summary>
    public int DurationMinutes { get; set; }

    /// <summary>Расчётное время окончания</summary>
    public DateTime EndsAt => StartedAt.AddMinutes(DurationMinutes);

    /// <summary>Оставшееся время (может быть отрицательным, если давно вышло)</summary>
    public TimeSpan Remaining => EndsAt - DateTime.Now;

    /// <summary>Токен отмены — позволяет досрочно прервать таймер</summary>
    public CancellationTokenSource Cts { get; set; } = new();
}
