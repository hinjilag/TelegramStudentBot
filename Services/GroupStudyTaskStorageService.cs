using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupStudyTaskStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, StoredGroupTasks> _tasksByChat;

    public GroupStudyTaskStorageService()
    {
        _path = UserDataPath.ResolveFile("group-tasks.json");
        _tasksByChat = LoadTasks(_path);
    }

    public List<StudyTask> Get(long chatId)
    {
        lock (_lock)
        {
            return _tasksByChat.TryGetValue(chatId, out var tasks)
                ? tasks.Tasks.Select(CloneTask).ToList()
                : new List<StudyTask>();
        }
    }

    public IReadOnlyDictionary<long, List<StudyTask>> GetAll()
    {
        lock (_lock)
        {
            return _tasksByChat.ToDictionary(
                item => item.Key,
                item => item.Value.Tasks.Select(CloneTask).ToList());
        }
    }

    public void Save(long chatId, string? chatTitle, IEnumerable<StudyTask> tasks)
    {
        lock (_lock)
        {
            var storedTasks = _tasksByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new StoredGroupTasks { ChatId = chatId };

            storedTasks.ChatId = chatId;
            storedTasks.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? "Группа" : chatTitle.Trim();
            storedTasks.Tasks = tasks.Select(CloneTask).ToList();
            storedTasks.UpdatedAt = DateTime.Now;

            _tasksByChat[chatId] = storedTasks;
            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_tasksByChat, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, StoredGroupTasks> LoadTasks(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, StoredGroupTasks>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, StoredGroupTasks>>(json, JsonOptions)
            ?? new Dictionary<long, StoredGroupTasks>();
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
