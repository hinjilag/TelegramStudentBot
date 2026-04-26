using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class HomeworkSubjectPreferencesService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly UserProfileStorageService _userProfiles;
    private readonly Dictionary<long, UserHomeworkSubjectPreferences> _preferencesByUser;

    public HomeworkSubjectPreferencesService(UserProfileStorageService userProfiles)
    {
        _userProfiles = userProfiles;
        _path = ResolvePreferencesPath();
        _preferencesByUser = LoadPreferences(_path);
    }

    public UserHomeworkSubjectPreferences Get(long userId)
    {
        lock (_lock)
        {
            return _preferencesByUser.TryGetValue(userId, out var preferences)
                ? ClonePreferences(preferences)
                : new UserHomeworkSubjectPreferences();
        }
    }

    public void ToggleFavoriteSubject(long userId, string subject)
    {
        lock (_lock)
        {
            var preferences = _preferencesByUser.TryGetValue(userId, out var existing)
                ? existing
                : new UserHomeworkSubjectPreferences();

            preferences.IsConfigured = true;

            var index = preferences.FavoriteSubjects.FindIndex(item =>
                string.Equals(item, subject, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                preferences.FavoriteSubjects.RemoveAt(index);
            else
                preferences.FavoriteSubjects.Add(subject);

            preferences.FavoriteSubjects = preferences.FavoriteSubjects
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyUserMetadata(userId, preferences);
            preferences.UpdatedAt = DateTime.Now;
            _preferencesByUser[userId] = preferences;
            SaveAll();
        }
    }

    public void SyncUserMetadata(long userId)
    {
        lock (_lock)
        {
            if (!_preferencesByUser.TryGetValue(userId, out var preferences))
                return;

            var oldNickname = preferences.Nickname;
            var oldUsername = preferences.Username;

            ApplyUserMetadata(userId, preferences);

            if (string.Equals(oldNickname, preferences.Nickname, StringComparison.Ordinal) &&
                string.Equals(oldUsername, preferences.Username, StringComparison.Ordinal))
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

        var json = JsonSerializer.Serialize(_preferencesByUser, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, UserHomeworkSubjectPreferences> LoadPreferences(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, UserHomeworkSubjectPreferences>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, UserHomeworkSubjectPreferences>>(json, JsonOptions)
            ?? new Dictionary<long, UserHomeworkSubjectPreferences>();
    }

    private static UserHomeworkSubjectPreferences ClonePreferences(UserHomeworkSubjectPreferences preferences)
    {
        return new UserHomeworkSubjectPreferences
        {
            Nickname = preferences.Nickname,
            Username = preferences.Username,
            FavoriteSubjects = preferences.FavoriteSubjects.ToList(),
            IsConfigured = preferences.IsConfigured,
            UpdatedAt = preferences.UpdatedAt
        };
    }

    private void ApplyUserMetadata(long userId, UserHomeworkSubjectPreferences preferences)
    {
        var profile = _userProfiles.Get(userId);
        if (profile is null)
            return;

        preferences.Nickname = profile.Nickname;
        preferences.Username = profile.Username;
    }

    private static string ResolvePreferencesPath()
    {
        return UserDataPath.ResolveFile("user-homework-subjects.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
