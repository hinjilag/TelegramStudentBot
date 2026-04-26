using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.MiniApp;

public class MiniAppService
{
    private readonly SessionService _sessions;
    private readonly StudyTaskStorageService _taskStorage;
    private readonly UserProfileStorageService _userProfiles;
    private readonly ReminderSettingsService _reminders;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly TimerService _timers;
    private readonly MiniAppChatSyncService _chatSync;

    public MiniAppService(
        SessionService sessions,
        StudyTaskStorageService taskStorage,
        UserProfileStorageService userProfiles,
        ReminderSettingsService reminders,
        HomeworkSubjectPreferencesService homeworkSubjects,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        TimerService timers,
        MiniAppChatSyncService chatSync)
    {
        _sessions = sessions;
        _taskStorage = taskStorage;
        _userProfiles = userProfiles;
        _reminders = reminders;
        _homeworkSubjects = homeworkSubjects;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _timers = timers;
        _chatSync = chatSync;
    }

    public MiniAppStateDto GetState(MiniAppIdentity identity)
    {
        var session = GetOrCreateSession(identity);
        var currentWeekType = _scheduleCatalog.GetCurrentWeekType();
        var currentWeekLabel = _scheduleCatalog.GetCurrentWeekLabel();

        var selection = _scheduleSelections.Get(identity.UserId);
        var group = selection is null ? null : _scheduleCatalog.GetGroup(selection.ScheduleId);
        var selectedDirectionCode = group?.DirectionCode;

        var directions = _scheduleCatalog.GetDirections()
            .Select(direction => new MiniAppDirectionDto(
                direction.DirectionCode,
                direction.DirectionName,
                direction.ShortTitle))
            .ToList();

        var effectiveDirectionCode = selectedDirectionCode ?? directions.FirstOrDefault()?.DirectionCode;
        var availableGroups = string.IsNullOrWhiteSpace(effectiveDirectionCode)
            ? new List<MiniAppGroupDto>()
            : BuildGroups(effectiveDirectionCode);

        var weekEntries = new List<ScheduleEntry>();
        var allScheduleEntries = new List<ScheduleEntry>();
        MiniAppSelectedScheduleDto? selectedSchedule = null;

        if (group is not null)
        {
            weekEntries = _scheduleCatalog.GetEntriesForSelection(group, selection!.SubGroup, currentWeekType);
            allScheduleEntries = _scheduleCatalog.GetAllEntriesForSelection(group, selection.SubGroup);

            session.CurrentWeekType = currentWeekType;
            session.CurrentSubGroup = selection.SubGroup;
            session.Schedule = weekEntries;

            selectedSchedule = new MiniAppSelectedScheduleDto(
                group.Id,
                group.Title,
                selection.SubGroup,
                group.DirectionCode,
                group.DirectionName,
                group.SubGroups.OrderBy(item => item).ToList());
        }

        var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
        var todayEntries = weekEntries
            .Where(entry => entry.DayOfWeek == today)
            .ToList();

        var reminderSettings = _reminders.Get(identity.UserId);
        var favoriteSubjects = _homeworkSubjects.Get(identity.UserId);

        var homeworkTasks = session.Tasks
            .Where(task => !TaskSubjects.IsPersonal(task.Subject))
            .OrderBy(task => task.IsCompleted)
            .ThenBy(task => task.Deadline ?? DateTime.MaxValue)
            .ThenByDescending(task => task.CreatedAt)
            .Select(ToTaskDto)
            .ToList();

        var personalTasks = session.Tasks
            .Where(task => TaskSubjects.IsPersonal(task.Subject))
            .OrderBy(task => task.IsCompleted)
            .ThenBy(task => task.Deadline ?? DateTime.MaxValue)
            .ThenByDescending(task => task.CreatedAt)
            .Select(ToTaskDto)
            .ToList();

        var profile = _userProfiles.Get(identity.UserId);
        var displayName = profile?.Nickname
            ?? string.Join(" ", new[] { identity.FirstName, identity.LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "Студент";

        var activeTimer = session.ActiveTimer;
        var completedTasks = session.Tasks.Count(task => task.IsCompleted);

        return new MiniAppStateDto(
            new MiniAppUserDto(identity.UserId, displayName, profile?.Username ?? identity.Username),
            new MiniAppStatsDto(
                HomeworkPending: homeworkTasks.Count(task => !task.IsCompleted),
                PersonalPending: personalTasks.Count(task => !task.IsCompleted),
                CompletedTasks: completedTasks,
                HasSchedule: selectedSchedule is not null,
                RemindersEnabled: reminderSettings.IsEnabled),
            new MiniAppScheduleStateDto(
                _scheduleCatalog.Semester,
                currentWeekType,
                currentWeekLabel,
                directions,
                selectedDirectionCode,
                availableGroups,
                selectedSchedule,
                todayEntries.Select(ToScheduleEntryDto).ToList(),
                weekEntries.Select(ToScheduleEntryDto).ToList()),
            new MiniAppReminderDto(
                reminderSettings.IsEnabled,
                reminderSettings.TimeText,
                reminderSettings.Hour,
                reminderSettings.Minute),
            new MiniAppTimerDto(
                activeTimer is not null,
                activeTimer?.Type.ToString().ToLowerInvariant(),
                activeTimer?.DurationMinutes,
                activeTimer?.EndsAt.ToString("O"),
                activeTimer is null ? null : FormatRemaining(activeTimer.Remaining)),
            new MiniAppTasksDto(homeworkTasks, personalTasks),
            BuildHomeworkSubjects(allScheduleEntries, favoriteSubjects));
    }

    public IReadOnlyList<MiniAppGroupDto> GetGroups(string directionCode)
        => BuildGroups(directionCode);

    public async Task SaveScheduleSelectionAsync(
        MiniAppIdentity identity,
        MiniAppScheduleSelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ScheduleId))
            throw new InvalidOperationException("Не выбрана группа расписания.");

        var group = _scheduleCatalog.GetGroup(request.ScheduleId)
            ?? throw new InvalidOperationException("Выбранное расписание не найдено.");

        if (group.SubGroups.Count > 0 &&
            request.SubGroup.HasValue &&
            !group.SubGroups.Contains(request.SubGroup.Value))
        {
            throw new InvalidOperationException("Выбрана неверная подгруппа.");
        }

        _scheduleSelections.Save(identity.UserId, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = request.SubGroup
        });

        var session = GetOrCreateSession(identity);
        session.CurrentWeekType = _scheduleCatalog.GetCurrentWeekType();
        session.CurrentSubGroup = request.SubGroup;
        session.Schedule = _scheduleCatalog.GetEntriesForSelection(group, request.SubGroup, session.CurrentWeekType.Value);

        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Расписание обновлено: <b>{MiniAppChatSyncService.Escape(group.Title)}</b>" +
            (request.SubGroup.HasValue ? $" (подгруппа {request.SubGroup.Value})." : "."),
            cancellationToken);
    }

    public async Task ClearScheduleSelectionAsync(MiniAppIdentity identity, CancellationToken cancellationToken)
    {
        _scheduleSelections.Delete(identity.UserId);
        var session = GetOrCreateSession(identity);
        session.Schedule.Clear();
        session.CurrentSubGroup = null;
        session.CurrentWeekType = null;

        await _chatSync.NotifyAsync(identity.UserId, "Привязка расписания удалена из mini app.", cancellationToken);
    }

    public async Task ToggleFavoriteSubjectAsync(
        MiniAppIdentity identity,
        MiniAppFavoriteSubjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SubjectTitle))
            throw new InvalidOperationException("Предмет не выбран.");

        var (_, allEntries) = GetScheduleEntries(identity.UserId);
        var allowedTitles = allEntries
            .Select(entry => ScheduleCatalogService.GetHomeworkSubjectTitle(entry.Subject))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!allowedTitles.Contains(request.SubjectTitle, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Этот предмет недоступен для ДЗ.");

        _homeworkSubjects.ToggleFavoriteSubject(identity.UserId, request.SubjectTitle);
    }

    public async Task CreateHomeworkAsync(
        MiniAppIdentity identity,
        MiniAppHomeworkCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Введите текст домашнего задания.");

        if (string.IsNullOrWhiteSpace(request.Subject))
            throw new InvalidOperationException("Выберите предмет.");

        var (group, allEntries) = GetScheduleEntries(identity.UserId);
        if (group is null || allEntries.Count == 0)
            throw new InvalidOperationException("Сначала выберите расписание.");

        if (!allEntries.Any(entry => string.Equals(entry.Subject, request.Subject, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Предмет отсутствует в выбранном расписании.");

        var deadline = _scheduleCatalog.FindNextLessonDate(allEntries, request.Subject)
            ?? throw new InvalidOperationException("Не удалось определить ближайшую пару для этого предмета.");

        var session = GetOrCreateSession(identity);
        session.Tasks.Add(new StudyTask
        {
            Title = request.Title.Trim(),
            Subject = request.Subject.Trim(),
            Deadline = deadline
        });

        _sessions.SaveTasks(session);

        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Добавлено ДЗ: <b>{MiniAppChatSyncService.Escape(request.Title.Trim())}</b>\n" +
            $"Предмет: <b>{MiniAppChatSyncService.Escape(request.Subject.Trim())}</b>\n" +
            $"Дедлайн: <b>{deadline:dd.MM.yyyy}</b>",
            cancellationToken);
    }

    public async Task CreatePersonalTaskAsync(
        MiniAppIdentity identity,
        MiniAppPersonalTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Введите название дела.");

        if (request.Deadline.HasValue && request.Deadline.Value.Date < DateTime.Today)
            throw new InvalidOperationException("Нельзя указать дату раньше сегодняшней.");

        var session = GetOrCreateSession(identity);
        session.Tasks.Add(new StudyTask
        {
            Title = request.Title.Trim(),
            Subject = TaskSubjects.Personal,
            Deadline = request.Deadline
        });

        _sessions.SaveTasks(session);

        var deadlineText = request.Deadline.HasValue
            ? request.Deadline.Value.ToString("dd.MM.yyyy HH:mm")
            : "без дедлайна";

        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Добавлено личное дело: <b>{MiniAppChatSyncService.Escape(request.Title.Trim())}</b>\n" +
            $"Срок: <b>{MiniAppChatSyncService.Escape(deadlineText)}</b>",
            cancellationToken);
    }

    public async Task SetTaskCompletionAsync(
        MiniAppIdentity identity,
        string taskId,
        MiniAppTaskCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(identity);
        var task = FindTask(session, taskId);
        task.IsCompleted = request.IsCompleted;
        _sessions.SaveTasks(session);

        var action = request.IsCompleted ? "выполнена" : "возвращена в активные";
        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Задача <b>{MiniAppChatSyncService.Escape(task.Title)}</b> {action}.",
            cancellationToken);
    }

    public async Task DeleteTaskAsync(MiniAppIdentity identity, string taskId, CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(identity);
        var task = FindTask(session, taskId);
        session.Tasks.Remove(task);
        _sessions.SaveTasks(session);

        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Удалена задача: <b>{MiniAppChatSyncService.Escape(task.Title)}</b>.",
            cancellationToken);
    }

    public async Task UpdateReminderAsync(
        MiniAppIdentity identity,
        MiniAppReminderUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.IsEnabled)
        {
            _reminders.Disable(identity.UserId, identity.UserId);
            await _chatSync.NotifyAsync(identity.UserId, "Напоминания отключены в mini app.", cancellationToken);
            return;
        }

        if (!request.Hour.HasValue || !request.Minute.HasValue ||
            request.Hour is < 0 or > 23 ||
            request.Minute is < 0 or > 59)
        {
            throw new InvalidOperationException("Укажите корректное время напоминаний.");
        }

        _reminders.Enable(identity.UserId, identity.UserId, request.Hour.Value, request.Minute.Value);
        await _chatSync.NotifyAsync(
            identity.UserId,
            $"Напоминания включены: каждый день в <b>{request.Hour:00}:{request.Minute:00}</b> по МСК.",
            cancellationToken);
    }

    public async Task StartTimerAsync(
        MiniAppIdentity identity,
        MiniAppTimerStartRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Minutes is < 1 or > 300)
            throw new InvalidOperationException("Таймер должен быть от 1 до 300 минут.");

        if (string.Equals(request.Type, "work", StringComparison.OrdinalIgnoreCase))
        {
            await _timers.StartWorkTimerAsync(identity.UserId, identity.UserId, request.Minutes);
            return;
        }

        if (string.Equals(request.Type, "rest", StringComparison.OrdinalIgnoreCase))
        {
            await _timers.StartRestTimerAsync(identity.UserId, identity.UserId, request.Minutes);
            return;
        }

        throw new InvalidOperationException("Неизвестный тип таймера.");
    }

    public async Task StopTimerAsync(MiniAppIdentity identity, CancellationToken cancellationToken)
    {
        var stopped = _timers.StopTimer(identity.UserId);
        if (!stopped)
            throw new InvalidOperationException("Сейчас нет активного таймера.");

        await _chatSync.NotifyAsync(identity.UserId, "Активный таймер остановлен из mini app.", cancellationToken);
    }

    private UserSession GetOrCreateSession(MiniAppIdentity identity)
    {
        _userProfiles.Upsert(identity.UserId, identity.FirstName, identity.LastName, identity.Username);
        _taskStorage.SyncUserMetadata(identity.UserId);
        _reminders.SyncUserMetadata(identity.UserId);
        _homeworkSubjects.SyncUserMetadata(identity.UserId);
        _scheduleSelections.SyncUserMetadata(identity.UserId);

        return _sessions.GetOrCreate(identity.UserId, identity.FirstName);
    }

    private (ScheduleGroup? Group, List<ScheduleEntry> Entries) GetScheduleEntries(long userId)
    {
        var selection = _scheduleSelections.Get(userId);
        if (selection is null)
            return (null, new List<ScheduleEntry>());

        var group = _scheduleCatalog.GetGroup(selection.ScheduleId);
        if (group is null)
            return (null, new List<ScheduleEntry>());

        return (group, _scheduleCatalog.GetAllEntriesForSelection(group, selection.SubGroup));
    }

    private List<MiniAppGroupDto> BuildGroups(string directionCode)
    {
        return _scheduleCatalog.GetGroupsByDirection(directionCode)
            .Select(group => new MiniAppGroupDto(
                group.Id,
                group.Title,
                group.Course,
                group.DirectionCode,
                group.DirectionName,
                group.SubGroups.OrderBy(item => item).ToList()))
            .ToList();
    }

    private IReadOnlyList<MiniAppHomeworkSubjectGroupDto> BuildHomeworkSubjects(
        List<ScheduleEntry> allEntries,
        UserHomeworkSubjectPreferences preferences)
    {
        if (allEntries.Count == 0)
            return Array.Empty<MiniAppHomeworkSubjectGroupDto>();

        var groups = allEntries
            .GroupBy(entry => ScheduleCatalogService.GetHomeworkSubjectTitle(entry.Subject), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var favoriteOrder = preferences.FavoriteSubjects.FindIndex(item =>
                    string.Equals(item, group.Key, StringComparison.OrdinalIgnoreCase));

                var options = group
                    .Select(entry => entry.Subject)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ScheduleCatalogService.GetHomeworkLessonTypeLabel)
                    .Select(subject =>
                    {
                        var nextLesson = _scheduleCatalog.FindNextLessonDate(allEntries, subject);
                        return new MiniAppHomeworkSubjectOptionDto(
                            subject,
                            ScheduleCatalogService.GetHomeworkLessonTypeLabel(subject),
                            nextLesson?.ToString("O"),
                            nextLesson?.ToString("dd.MM.yyyy"));
                    })
                    .ToList();

                return new MiniAppHomeworkSubjectGroupDto(
                    group.Key,
                    favoriteOrder >= 0,
                    favoriteOrder >= 0 ? favoriteOrder + 1 : null,
                    options);
            })
            .OrderBy(item => item.IsFavorite ? 0 : 1)
            .ThenBy(item => item.FavoriteOrder ?? int.MaxValue)
            .ThenBy(item => item.Title)
            .ToList();

        return groups;
    }

    private static MiniAppTaskDto ToTaskDto(StudyTask task)
    {
        var lessonType = ScheduleCatalogService.GetHomeworkLessonTypeLabel(task.Subject);
        var isPersonal = TaskSubjects.IsPersonal(task.Subject);
        var deadlineText = task.Deadline.HasValue
            ? task.Deadline.Value.TimeOfDay == TimeSpan.Zero
                ? task.Deadline.Value.ToString("dd.MM.yyyy")
                : task.Deadline.Value.ToString("dd.MM.yyyy HH:mm")
            : null;

        return new MiniAppTaskDto(
            task.Id.ToString("D"),
            task.Title,
            task.Subject,
            isPersonal ? task.Subject : ScheduleCatalogService.GetHomeworkSubjectTitle(task.Subject),
            isPersonal ? null : lessonType,
            task.IsCompleted,
            task.Deadline?.ToString("O"),
            deadlineText,
            task.CreatedAt.ToString("O"));
    }

    private static MiniAppScheduleEntryDto ToScheduleEntryDto(ScheduleEntry entry)
    {
        return new MiniAppScheduleEntryDto(
            entry.DayOfWeek,
            ScheduleService.GetDayName(entry.DayOfWeek),
            entry.LessonNumber,
            entry.Time,
            entry.Subject);
    }

    private static string? FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "00:00";

        var totalHours = (int)remaining.TotalHours;
        return totalHours > 0
            ? $"{totalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static StudyTask FindTask(UserSession session, string taskId)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
            throw new InvalidOperationException("Некорректный идентификатор задачи.");

        return session.Tasks.FirstOrDefault(task => task.Id == taskGuid)
            ?? throw new InvalidOperationException("Задача не найдена.");
    }
}

