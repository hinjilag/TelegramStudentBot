using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.MiniApp;

public class GroupMiniAppService
{
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly ScheduleCatalogService _scheduleCatalog;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly UserProfileStorageService _userProfiles;

    public GroupMiniAppService(
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        ScheduleCatalogService scheduleCatalog,
        UserScheduleSelectionService scheduleSelections,
        UserProfileStorageService userProfiles)
    {
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _scheduleCatalog = scheduleCatalog;
        _scheduleSelections = scheduleSelections;
        _userProfiles = userProfiles;
    }

    public GroupMiniAppStateDto GetState(MiniAppIdentity identity, long chatId)
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
                reminder.Minute),
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

        var deadline = _scheduleCatalog.FindNextLessonDate(allEntries, request.Subject)
            ?? throw new InvalidOperationException("Не удалось определить ближайшую пару для этого предмета.");

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

    public void UpdateReminder(long chatId, GroupMiniAppReminderUpdateRequest request)
    {
        if (!request.IsEnabled)
        {
            _groupReminders.Disable(chatId, _groupReminders.Get(chatId).ChatTitle);
            return;
        }

        if (!request.Hour.HasValue || !request.Minute.HasValue)
            throw new InvalidOperationException("Укажи время напоминаний.");

        if (request.Hour < 0 || request.Hour > 23 || request.Minute < 0 || request.Minute > 59)
            throw new InvalidOperationException("Время напоминаний указано неверно.");

        var frequency = ParseFrequency(request.Frequency);
        _groupReminders.Enable(
            chatId,
            _groupReminders.Get(chatId).ChatTitle,
            request.Hour.Value,
            request.Minute.Value,
            frequency);
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
                        var nextDeadline = _scheduleCatalog.FindNextLessonDate(allEntries, subject);
                        return new MiniAppHomeworkSubjectOptionDto(
                            subject,
                            ScheduleCatalogService.GetHomeworkLessonTypeLabel(subject),
                            nextDeadline?.ToString("O"),
                            nextDeadline?.ToString("dd.MM.yyyy"));
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

    private GroupReminderFrequency ParseFrequency(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "weekdays" => GroupReminderFrequency.Weekdays,
            _ => GroupReminderFrequency.Daily
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
}
