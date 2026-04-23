using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserScheduleSelectionService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly UserProfileStorageService _userProfiles;
    private readonly Dictionary<long, UserScheduleSelection> _selections;

    public UserScheduleSelectionService(UserProfileStorageService userProfiles)
    {
        _userProfiles = userProfiles;
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
                    Nickname = selection.Nickname,
                    Username = selection.Username,
                    UpdatedAt = selection.UpdatedAt
                }
                : null;
        }
    }

    public void Save(long userId, UserScheduleSelection selection)
    {
        lock (_lock)
        {
            ApplyUserMetadata(userId, selection);
            selection.UpdatedAt = DateTime.Now;
            _selections[userId] = selection;
            SaveSelections();
        }
    }

    public void SyncUserMetadata(long userId)
    {
        lock (_lock)
        {
            if (!_selections.TryGetValue(userId, out var selection))
                return;

            var oldNickname = selection.Nickname;
            var oldUsername = selection.Username;

            ApplyUserMetadata(userId, selection);

            if (string.Equals(oldNickname, selection.Nickname, StringComparison.Ordinal) &&
                string.Equals(oldUsername, selection.Username, StringComparison.Ordinal))
            {
                return;
            }

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

    private void ApplyUserMetadata(long userId, UserScheduleSelection selection)
    {
        var profile = _userProfiles.Get(userId);
        if (profile is null)
            return;

        selection.Nickname = profile.Nickname;
        selection.Username = profile.Username;
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
