using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class StudyTaskStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly UserProfileStorageService _userProfiles;
    private readonly Dictionary<long, StoredUserTasks> _tasksByUser;

    public StudyTaskStorageService(UserProfileStorageService userProfiles)
    {
        _userProfiles = userProfiles;
        _path = ResolveTasksPath();
        _tasksByUser = LoadTasks(_path);
    }

    public List<StudyTask> Get(long userId)
    {
        lock (_lock)
        {
            return _tasksByUser.TryGetValue(userId, out var tasks)
                ? tasks.Tasks.Select(CloneTask).ToList()
                : new List<StudyTask>();
        }
    }

    public IReadOnlyDictionary<long, List<StudyTask>> GetAll()
    {
        lock (_lock)
        {
            return _tasksByUser.ToDictionary(
                item => item.Key,
                item => item.Value.Tasks.Select(CloneTask).ToList());
        }
    }

    public void Save(long userId, IEnumerable<StudyTask> tasks)
    {
        lock (_lock)
        {
            var storedTasks = _tasksByUser.TryGetValue(userId, out var existing)
                ? existing
                : new StoredUserTasks();

            storedTasks.Tasks = tasks.Select(CloneTask).ToList();
            storedTasks.UpdatedAt = DateTime.Now;
            ApplyUserMetadata(userId, storedTasks);

            _tasksByUser[userId] = storedTasks;
            SaveAll();
        }
    }

    public void SyncUserMetadata(long userId)
    {
        lock (_lock)
        {
            if (!_tasksByUser.TryGetValue(userId, out var storedTasks))
                return;

            var oldNickname = storedTasks.Nickname;
            var oldUsername = storedTasks.Username;

            ApplyUserMetadata(userId, storedTasks);

            if (string.Equals(oldNickname, storedTasks.Nickname, StringComparison.Ordinal) &&
                string.Equals(oldUsername, storedTasks.Username, StringComparison.Ordinal))
            {
                return;
            }

            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_tasksByUser, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, StoredUserTasks> LoadTasks(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, StoredUserTasks>();

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<long, StoredUserTasks>();

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var item in document.RootElement.EnumerateObject())
        {
            if (!long.TryParse(item.Name, out var userId))
                continue;

            StoredUserTasks? record = null;

            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                var tasks = JsonSerializer.Deserialize<List<StudyTask>>(item.Value.GetRawText(), JsonOptions)
                    ?? new List<StudyTask>();

                record = new StoredUserTasks
                {
                    Tasks = tasks.Select(CloneTask).ToList()
                };
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                record = JsonSerializer.Deserialize<StoredUserTasks>(item.Value.GetRawText(), JsonOptions)
                    ?? new StoredUserTasks();

                record.Tasks = (record.Tasks ?? new List<StudyTask>())
                    .Select(CloneTask)
                    .ToList();
            }

            if (record is not null)
                result[userId] = record;
        }

        return result;
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
            CreatedAt = task.CreatedAt
        };
    }

    private void ApplyUserMetadata(long userId, StoredUserTasks storedTasks)
    {
        var profile = _userProfiles.Get(userId);
        if (profile is null)
            return;

        storedTasks.Nickname = profile.Nickname;
        storedTasks.Username = profile.Username;
    }

    private static string ResolveTasksPath()
    {
        return UserDataPath.ResolveFile("user-tasks.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
