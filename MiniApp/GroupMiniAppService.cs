using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.MiniApp;

public class GroupMiniAppService
{
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly GroupParticipantResolverService _groupParticipantResolver;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly UserProfileStorageService _userProfiles;

    public GroupMiniAppService(
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        GroupParticipantResolverService groupParticipantResolver,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        UserProfileStorageService userProfiles)
    {
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _groupParticipantResolver = groupParticipantResolver;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _userProfiles = userProfiles;
    }

    public async Task<GroupMiniAppStateDto> GetStateAsync(
        MiniAppIdentity identity,
        long chatId,
        CancellationToken ct)
    {
        var currentWeekType = _scheduleCatalog.GetCurrentWeekType();
        var currentWeekLabel = _scheduleCatalog.GetCurrentWeekLabel();

        var selection = _scheduleSelections.Get(chatId);
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
        var allEntries = new List<ScheduleEntry>();
        MiniAppSelectedScheduleDto? selectedSchedule = null;

        if (group is not null)
        {
            weekEntries = _scheduleCatalog.GetEntriesForSelection(group, selection!.SubGroup, currentWeekType);
            allEntries = _scheduleCatalog.GetAllEntriesForSelection(group, selection.SubGroup);

            selectedSchedule = new MiniAppSelectedScheduleDto(
                group.Id,
                group.Title,
                selection.SubGroup,
                group.DirectionCode,
                group.DirectionName,
                group.SubGroups.OrderBy(item => item).ToList());
        }

        var today = ScheduleCatalogService.GetDayNumber(DateTime.Today);
        var todayEntries = weekEntries.Where(entry => entry.DayOfWeek == today).ToList();

        var reminder = _groupReminders.Get(chatId);
        var reminderParticipants = await BuildReminderParticipantsAsync(
            chatId,
            reminder.ChatTitle,
            reminder.SelectedParticipantUserIds,
            ct);
        var homework = _groupTasks.Get(chatId)
            .OrderBy(task => task.IsCompleted)
            .ThenBy(task => task.Deadline ?? DateTime.MaxValue)
            .ThenByDescending(task => task.CreatedAt)
            .ToList();

        var displayName = GetDisplayName(identity);
        var chatTitle = string.IsNullOrWhiteSpace(reminder.ChatTitle) ? "Группа" : reminder.ChatTitle;

        return new GroupMiniAppStateDto(
            new GroupMiniAppChatDto(chatId, chatTitle, displayName),
            new GroupMiniAppStatsDto(
                HomeworkPending: homework.Count(task => !task.IsCompleted),
                HomeworkCompleted: homework.Count(task => task.IsCompleted),
                HasSchedule: selectedSchedule is not null,
                RemindersEnabled: reminder.IsEnabled),
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
            new GroupMiniAppReminderDto(
                reminder.IsEnabled,
                reminder.Frequency.ToString().ToLowerInvariant(),
                reminder.FrequencyText,
                reminder.TimeText,
                reminder.Hour,
                reminder.Minute,
                reminder.SelectedDays.ToList(),
                reminderParticipants),
            homework.Select(ToTaskDto).ToList(),
            BuildHomeworkSubjects(allEntries));
    }

    public IReadOnlyList<MiniAppGroupDto> GetGroups(string directionCode)
        => BuildGroups(directionCode);

    public void SaveScheduleSelection(long chatId, MiniAppScheduleSelectionRequest request)
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

        _scheduleSelections.Save(chatId, new UserScheduleSelection
        {
            ScheduleId = group.Id,
            SubGroup = request.SubGroup
        });
    }

    public void ClearScheduleSelection(long chatId)
        => _scheduleSelections.Delete(chatId);

    public void CreateHomework(MiniAppIdentity identity, long chatId, MiniAppHomeworkCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Введите текст домашнего задания.");

        if (string.IsNullOrWhiteSpace(request.Subject))
            throw new InvalidOperationException("Выберите предмет.");

        var (group, allEntries) = GetScheduleEntries(chatId);
        if (group is null || allEntries.Count == 0)
            throw new InvalidOperationException("Сначала выберите расписание группы.");

        if (!allEntries.Any(entry => string.Equals(entry.Subject, request.Subject, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Предмет отсутствует в расписании группы.");

        var deadline = ValidateSelectedHomeworkDeadline(allEntries, request.Subject, request.Deadline);

        var tasks = _groupTasks.Get(chatId);
        tasks.Add(new StudyTask
        {
            Title = request.Title.Trim(),
            Subject = request.Subject.Trim(),
            Deadline = deadline,
            CreatedByName = GetDisplayName(identity),
            CreatedByUserId = identity.UserId
        });

        var chatTitle = _groupReminders.Get(chatId).ChatTitle;
        _groupTasks.Save(chatId, chatTitle, tasks);
    }

    public void DeleteHomework(long chatId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Не указано задание для удаления.");

        var tasks = _groupTasks.Get(chatId);
        var removed = tasks.RemoveAll(task => string.Equals(task.ShortId, taskId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            throw new InvalidOperationException("Задание не найдено.");

        var chatTitle = _groupReminders.Get(chatId).ChatTitle;
        _groupTasks.Save(chatId, chatTitle, tasks);
    }

    public void UpdateReminder(long chatId, GroupMiniAppReminderUpdateRequest request)
    {
        var currentSettings = _groupReminders.Get(chatId);

        if (!request.IsEnabled)
        {
            _groupReminders.Disable(chatId, currentSettings.ChatTitle);
            return;
        }

        if (!request.Hour.HasValue || !request.Minute.HasValue)
            throw new InvalidOperationException("Укажи время напоминаний.");

        if (request.Hour < 0 || request.Hour > 23 || request.Minute < 0 || request.Minute > 59)
            throw new InvalidOperationException("Время напоминаний указано неверно.");

        var frequency = ParseFrequency(request.Frequency);
        var selectedDays = NormalizeSelectedDays(request.SelectedDays);
        if (frequency == ReminderScheduleMode.CustomDays && selectedDays.Count == 0)
            throw new InvalidOperationException("Выберите хотя бы один день для напоминаний.");

        _groupReminders.Enable(
            chatId,
            currentSettings.ChatTitle,
            request.Hour.Value,
            request.Minute.Value,
            frequency,
            selectedDays,
            request.SelectedParticipantUserIds);
    }

    private async Task<IReadOnlyList<GroupMiniAppParticipantDto>> BuildReminderParticipantsAsync(
        long chatId,
        string? chatTitle,
        IReadOnlyCollection<long> selectedParticipantUserIds,
        CancellationToken ct)
    {
        var selectedIds = selectedParticipantUserIds.Count == 0
            ? null
            : selectedParticipantUserIds.ToHashSet();

        var participants = await _groupParticipantResolver.ResolveReminderParticipantsAsync(chatId, chatTitle, ct);
        return participants
            .Select(participant => new GroupMiniAppParticipantDto(
                participant.UserId,
                participant.Nickname,
                participant.Username,
                selectedIds?.Contains(participant.UserId) ?? false))
            .ToList();
    }

    private (ScheduleGroup? Group, List<ScheduleEntry> Entries) GetScheduleEntries(long chatId)
    {
        var selection = _scheduleSelections.Get(chatId);
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

    private IReadOnlyList<MiniAppHomeworkSubjectGroupDto> BuildHomeworkSubjects(List<ScheduleEntry> allEntries)
    {
        if (allEntries.Count == 0)
            return Array.Empty<MiniAppHomeworkSubjectGroupDto>();

        return allEntries
            .GroupBy(entry => ScheduleCatalogService.GetHomeworkSubjectTitle(entry.Subject), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .OrderBy(group => ScheduleCatalogService.GetHomeworkSubjectSortGroup(group.Key!))
            .ThenBy(group => group.Key)
            .Select(group =>
            {
                var options = group
                    .Select(entry => entry.Subject)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ScheduleCatalogService.GetHomeworkLessonTypeLabel)
                    .Select(subject =>
                    {
                        var availableDeadlines = _scheduleCatalog.FindUpcomingHomeworkDates(allEntries, subject)
                            .Select(date => new MiniAppHomeworkDeadlineDto(
                                date.ToString("yyyy-MM-dd"),
                                date.ToString("dd.MM.yyyy")))
                            .ToList();
                        var nextDeadline = availableDeadlines.FirstOrDefault();

                        return new MiniAppHomeworkSubjectOptionDto(
                            subject,
                            ScheduleCatalogService.GetHomeworkLessonTypeLabel(subject),
                            nextDeadline?.DateIso,
                            nextDeadline?.Label,
                            availableDeadlines);
                    })
                    .ToList();

                return new MiniAppHomeworkSubjectGroupDto(
                    group.Key!,
                    IsFavorite: false,
                    FavoriteOrder: null,
                    options);
            })
            .ToList();
    }

    private static ReminderScheduleMode ParseFrequency(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "weekdays" => ReminderScheduleMode.Weekdays,
            "customdays" => ReminderScheduleMode.CustomDays,
            "custom" => ReminderScheduleMode.CustomDays,
            _ => ReminderScheduleMode.Daily
        };
    }

    private string GetDisplayName(MiniAppIdentity identity)
    {
        var profile = _userProfiles.Get(identity.UserId);
        var displayName = profile?.Nickname
            ?? string.Join(" ", new[] { identity.FirstName, identity.LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return string.IsNullOrWhiteSpace(displayName) ? "Участник" : displayName;
    }

    private static GroupMiniAppTaskDto ToTaskDto(StudyTask task)
    {
        return new GroupMiniAppTaskDto(
            task.ShortId,
            task.Title,
            task.Subject,
            ScheduleCatalogService.GetHomeworkSubjectTitle(task.Subject),
            task.IsCompleted,
            task.Deadline?.ToString("O"),
            task.Deadline?.ToString("dd.MM.yyyy"),
            task.CreatedAt.ToString("O"),
            task.CreatedByName);
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

    private DateTime ValidateSelectedHomeworkDeadline(
        IReadOnlyList<ScheduleEntry> entries,
        string subject,
        DateTime? requestedDeadline)
    {
        var availableDeadlines = _scheduleCatalog.FindUpcomingHomeworkDates(entries, subject);
        if (availableDeadlines.Count == 0)
            throw new InvalidOperationException("Не удалось определить ближайшие пары для этого предмета.");

        if (!requestedDeadline.HasValue)
            return availableDeadlines[0];

        var deadlineDate = requestedDeadline.Value.Date;
        if (!availableDeadlines.Contains(deadlineDate))
            throw new InvalidOperationException("Выбранная дата больше не подходит для этого предмета.");

        return deadlineDate;
    }

    private static List<int> NormalizeSelectedDays(IReadOnlyList<int>? selectedDays)
    {
        return (selectedDays ?? Array.Empty<int>())
            .Where(day => day is >= 1 and <= 7)
            .Distinct()
            .OrderBy(day => day)
            .ToList();
    }
}
