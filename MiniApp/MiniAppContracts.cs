namespace TelegramStudentBot.MiniApp;

public sealed record MiniAppIdentity(
    long UserId,
    string FirstName,
    string? LastName,
    string? Username,
    string? LanguageCode,
    bool IsDebug);

public sealed record MiniAppStateDto(
    MiniAppUserDto User,
    MiniAppStatsDto Stats,
    MiniAppScheduleStateDto Schedule,
    MiniAppReminderDto Reminder,
    MiniAppTimerDto Timer,
    MiniAppTasksDto Tasks,
    IReadOnlyList<MiniAppHomeworkSubjectGroupDto> HomeworkSubjects);

public sealed record MiniAppUserDto(
    long UserId,
    string DisplayName,
    string? Username);

public sealed record MiniAppStatsDto(
    int HomeworkPending,
    int PersonalPending,
    int CompletedTasks,
    bool HasSchedule,
    bool RemindersEnabled);

public sealed record MiniAppScheduleStateDto(
    string Semester,
    int CurrentWeekType,
    string CurrentWeekLabel,
    IReadOnlyList<MiniAppDirectionDto> Directions,
    string? SelectedDirectionCode,
    IReadOnlyList<MiniAppGroupDto> AvailableGroups,
    MiniAppSelectedScheduleDto? Selection,
    IReadOnlyList<MiniAppScheduleEntryDto> TodayEntries,
    IReadOnlyList<MiniAppScheduleEntryDto> WeekEntries);

public sealed record MiniAppDirectionDto(
    string DirectionCode,
    string DirectionName,
    string ShortTitle);

public sealed record MiniAppGroupDto(
    string ScheduleId,
    string Title,
    int Course,
    string DirectionCode,
    string DirectionName,
    IReadOnlyList<int> SubGroups);

public sealed record MiniAppSelectedScheduleDto(
    string ScheduleId,
    string Title,
    int? SubGroup,
    string DirectionCode,
    string DirectionName,
    IReadOnlyList<int> AvailableSubGroups);

public sealed record MiniAppScheduleEntryDto(
    int DayOfWeek,
    string DayName,
    int LessonNumber,
    string? Time,
    string Subject);

public sealed record MiniAppReminderDto(
    bool IsEnabled,
    string TimeText,
    int Hour,
    int Minute);

public sealed record MiniAppTimerDto(
    bool IsActive,
    string? Type,
    int? DurationMinutes,
    string? EndsAtIso,
    string? RemainingText);

public sealed record MiniAppTasksDto(
    IReadOnlyList<MiniAppTaskDto> Homework,
    IReadOnlyList<MiniAppTaskDto> Personal);

public sealed record MiniAppTaskDto(
    string Id,
    string Title,
    string Subject,
    string SubjectTitle,
    string? LessonType,
    bool IsCompleted,
    string? DeadlineIso,
    string? DeadlineText,
    string CreatedAtIso);

public sealed record MiniAppHomeworkSubjectGroupDto(
    string Title,
    bool IsFavorite,
    int? FavoriteOrder,
    IReadOnlyList<MiniAppHomeworkSubjectOptionDto> Options);

public sealed record MiniAppHomeworkSubjectOptionDto(
    string Subject,
    string LessonType,
    string? NextDeadlineIso,
    string? NextDeadlineText);

public sealed record MiniAppScheduleSelectionRequest(
    string ScheduleId,
    int? SubGroup);

public sealed record MiniAppHomeworkCreateRequest(
    string Subject,
    string Title);

public sealed record MiniAppPersonalTaskCreateRequest(
    string Title,
    DateTime? Deadline);

public sealed record MiniAppTaskCompletionRequest(
    bool IsCompleted);

public sealed record MiniAppReminderUpdateRequest(
    bool IsEnabled,
    int? Hour,
    int? Minute);

public sealed record MiniAppTimerStartRequest(
    string Type,
    int Minutes);

public sealed record MiniAppFavoriteSubjectRequest(
    string SubjectTitle);
