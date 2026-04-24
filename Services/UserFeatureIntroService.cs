using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserFeatureIntroService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, UserFeatureIntroState> _states;

    public UserFeatureIntroService()
    {
        _path = UserDataPath.ResolveFile("user-feature-intros.json");
        _states = LoadStates(_path);
    }

    public bool HasSeenPlanIntro(long userId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(userId, out var state) && state.HasSeenPlanIntro;
        }
    }

    public void MarkPlanIntroSeen(long userId)
    {
        lock (_lock)
        {
            var state = _states.TryGetValue(userId, out var existing)
                ? existing
                : new UserFeatureIntroState();

            state.HasSeenPlanIntro = true;
            state.UpdatedAt = DateTime.Now;
            _states[userId] = state;
            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_states, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, UserFeatureIntroState> LoadStates(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, UserFeatureIntroState>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, UserFeatureIntroState>>(json, JsonOptions)
            ?? new Dictionary<long, UserFeatureIntroState>();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
