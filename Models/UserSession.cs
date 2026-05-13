namespace TelegramStudentBot.Models;

public class UserSession
{
    public long UserId { get; set; }

    public string FirstName { get; set; } = "Студент";

    public UserState State { get; set; } = UserState.Idle;

    public List<StudyTask> Tasks { get; set; } = new();

    public ActiveTimer? ActiveTimer { get; set; }

    public StudyTask? DraftTask { get; set; }

    public DateTime? PendingTaskDeadlineDate { get; set; }

    public long? PendingGroupHomeworkChatId { get; set; }

    public string? PendingGroupHomeworkChatTitle { get; set; }

    public long ReminderTargetChatId { get; set; }

    public string? ReminderTargetChatTitle { get; set; }

    public bool ReminderTargetIsGroup { get; set; }

    public ReminderScheduleMode? PendingReminderMode { get; set; }

    public List<int> PendingReminderSelectedDays { get; set; } = new();

    public long? PendingParticipantChatId { get; set; }

    public string? PendingParticipantChatTitle { get; set; }

    public bool ContinueHomeworkAfterScheduleSelection { get; set; }

    public long? PendingHomeworkScheduleSelectionKey { get; set; }

    public Dictionary<string, string> HomeworkSubjectChoices { get; set; } = new();

    public Dictionary<string, string> HomeworkLessonTypeChoices { get; set; } = new();

    public Dictionary<string, DateTime> HomeworkDeadlineChoices { get; set; } = new();

    public List<ScheduleEntry> Schedule { get; set; } = new();

    public List<ScheduleEntry>? PendingSchedule { get; set; }

    public int? CurrentWeekType { get; set; }

    public int? CurrentSubGroup { get; set; }

    public int ReviewSlotIndex { get; set; }

    public int PendingTasksCount => Tasks.Count(t => !t.IsCompleted);
}
