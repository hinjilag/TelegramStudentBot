using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserGroupTaskBridgeService
{
    private readonly GroupParticipantStorageService _groupParticipants;
    private readonly GroupStudyTaskStorageService _groupTasks;

    public UserGroupTaskBridgeService(
        GroupParticipantStorageService groupParticipants,
        GroupStudyTaskStorageService groupTasks)
    {
        _groupParticipants = groupParticipants;
        _groupTasks = groupTasks;
    }

    public IReadOnlyList<StoredGroupTasks> GetLinkedGroupTaskFeeds(long userId)
    {
        var memberships = _groupParticipants.GetChatsForUser(userId);
        if (memberships.Count == 0)
            return Array.Empty<StoredGroupTasks>();

        var allTasks = _groupTasks.GetAll();
        return memberships
            .Select(membership =>
            {
                var tasks = allTasks.TryGetValue(membership.ChatId, out var existing)
                    ? existing
                    : new List<StudyTask>();

                return new StoredGroupTasks
                {
                    ChatId = membership.ChatId,
                    ChatTitle = string.IsNullOrWhiteSpace(membership.ChatTitle) ? "Группа" : membership.ChatTitle,
                    Tasks = tasks
                };
            })
            .ToList();
    }

    public IReadOnlyList<LinkedGroupTask> GetLinkedHomeworkTasks(long userId, bool includeCompleted = true)
    {
        return GetLinkedGroupTaskFeeds(userId)
            .SelectMany(feed => feed.Tasks
                .Where(task => !TaskSubjects.IsPersonal(task.Subject))
                .Where(task => includeCompleted || !task.IsCompleted)
                .Select(task => new LinkedGroupTask
                {
                    ChatId = feed.ChatId,
                    ChatTitle = feed.ChatTitle,
                    Task = CloneTask(task)
                }))
            .OrderBy(item => item.Task.IsCompleted)
            .ThenBy(item => item.Task.Deadline ?? DateTime.MaxValue)
            .ThenBy(item => item.ChatTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static StudyTask CloneTask(StudyTask task)
    {
        return new StudyTask
        {
            Id = task.Id,
            Title = task.Title,
            Subject = task.Subject,
            Deadline = task.Deadline,
            IsCompleted = task.IsCompleted,
            CreatedAt = task.CreatedAt,
            CreatedByName = task.CreatedByName,
            CreatedByUserId = task.CreatedByUserId
        };
    }
}
