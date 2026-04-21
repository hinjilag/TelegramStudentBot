using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserScheduleSelectionService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, UserScheduleSelection> _selections;

    public UserScheduleSelectionService()
    {
        _path = ResolveSelectionPath();
        _selections = LoadSelections(_path);
    }

    public UserScheduleSelection? Get(long userId)
    {
        lock (_lock)
        {
            return _selections.TryGetValue(userId, out var selection)
                ? new UserScheduleSelection
                {
                    ScheduleId = selection.ScheduleId,
                    SubGroup = selection.SubGroup,
                    UpdatedAt = selection.UpdatedAt
                }
                : null;
        }
    }

    public void Save(long userId, UserScheduleSelection selection)
    {
        lock (_lock)
        {
            selection.UpdatedAt = DateTime.Now;
            _selections[userId] = selection;
            SaveSelections();
        }
    }

    public bool Delete(long userId)
    {
        lock (_lock)
        {
            var removed = _selections.Remove(userId);
            if (removed)
                SaveSelections();

            return removed;
        }
    }

    private void SaveSelections()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_selections, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, UserScheduleSelection> LoadSelections(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, UserScheduleSelection>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, UserScheduleSelection>>(json, JsonOptions)
            ?? new Dictionary<long, UserScheduleSelection>();
    }

    private static string ResolveSelectionPath()
    {
        return UserDataPath.ResolveFile("user-schedule-selections.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
