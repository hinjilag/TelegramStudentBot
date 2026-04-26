using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class StudyTaskStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, List<StudyTask>> _tasksByUser;

    public StudyTaskStorageService()
    {
        _path = ResolveTasksPath();
        _tasksByUser = LoadTasks(_path);
    }

    public List<StudyTask> Get(long userId)
    {
        lock (_lock)
        {
            return _tasksByUser.TryGetValue(userId, out var tasks)
                ? tasks.Select(CloneTask).ToList()
                : new List<StudyTask>();
        }
    }

    public IReadOnlyDictionary<long, List<StudyTask>> GetAll()
    {
        lock (_lock)
        {
            return _tasksByUser.ToDictionary(
                item => item.Key,
                item => item.Value.Select(CloneTask).ToList());
        }
    }

    public void Save(long userId, IEnumerable<StudyTask> tasks)
    {
        lock (_lock)
        {
            _tasksByUser[userId] = tasks.Select(CloneTask).ToList();
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

    private static Dictionary<long, List<StudyTask>> LoadTasks(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, List<StudyTask>>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, List<StudyTask>>>(json, JsonOptions)
            ?? new Dictionary<long, List<StudyTask>>();
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
