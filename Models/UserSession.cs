namespace TelegramStudentBot.Models;

public class UserSession
{
    public long UserId { get; set; }

    public string FirstName { get; set; } = "Студент";

    public long LastChatId { get; set; }

    public UserState State { get; set; } = UserState.Idle;

    public int FatigueLevel { get; set; } = 0;

    public int WorkSessionsWithoutRest { get; set; } = 0;

    public List<StudyTask> Tasks { get; set; } = new();

    public ActiveTimer? ActiveTimer { get; set; }

    public StudyTask? DraftTask { get; set; }

    public List<ScheduleEntry> Schedule { get; set; } = new();

    public string? SchedulePhotoDataUrl { get; set; }

    public ScheduleEntry? DraftSchedule { get; set; }

    public string FatigueDescription => FatigueLevel switch
    {
        <= 30 => "😊 Свежий",
        <= 60 => "😐 Умеренная усталость",
        <= 85 => "😩 Высокая усталость",
        _ => "🥵 Истощение — срочно отдохни!"
    };

    public bool NeedsRest => FatigueLevel >= 60 || WorkSessionsWithoutRest >= 4;

    public int PendingTasksCount => Tasks.Count(t => !t.IsCompleted);
}
