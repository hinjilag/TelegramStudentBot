namespace TelegramStudentBot.MiniApp;

public sealed record GroupMiniAppStateDto(
    GroupMiniAppChatDto Chat,
    GroupMiniAppStatsDto Stats,
    MiniAppScheduleStateDto Schedule,
    GroupMiniAppReminderDto Reminder,
    IReadOnlyList<GroupMiniAppTaskDto> Homework,
    IReadOnlyList<MiniAppHomeworkSubjectGroupDto> HomeworkSubjects);

public sealed record GroupMiniAppChatDto(
    long ChatId,
    string Title,
    string OpenedBy);

public sealed record GroupMiniAppStatsDto(
    int HomeworkPending,
    int HomeworkCompleted,
    bool HasSchedule,
    bool RemindersEnabled);

public sealed record GroupMiniAppReminderDto(
    bool IsEnabled,
    string Frequency,
    string FrequencyText,
    string TimeText,
    int Hour,
    int Minute);

public sealed record GroupMiniAppTaskDto(
    string Id,
    string Title,
    string Subject,
    string SubjectTitle,
    bool IsCompleted,
    string? DeadlineIso,
    string? DeadlineText,
    string CreatedAtIso,
    string? CreatedByName);

public sealed record GroupMiniAppReminderUpdateRequest(
    bool IsEnabled,
    string? Frequency,
    int? Hour,
    int? Minute);
