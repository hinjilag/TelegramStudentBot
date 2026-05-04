using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupHomeworkSubjectPreferencesService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, GroupHomeworkSubjectPreferences> _preferencesByChat;

    public GroupHomeworkSubjectPreferencesService()
    {
        _path = UserDataPath.ResolveFile("group-homework-subjects.json");
        _preferencesByChat = LoadPreferences(_path);
    }

    public GroupHomeworkSubjectPreferences Get(long chatId)
    {
        lock (_lock)
        {
            return _preferencesByChat.TryGetValue(chatId, out var preferences)
                ? ClonePreferences(preferences)
                : new GroupHomeworkSubjectPreferences();
        }
    }

    public void ToggleFavoriteSubject(long chatId, string? chatTitle, string subject)
    {
        lock (_lock)
        {
            var preferences = _preferencesByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new GroupHomeworkSubjectPreferences();

            preferences.IsConfigured = true;

            if (!string.IsNullOrWhiteSpace(chatTitle))
                preferences.ChatTitle = chatTitle.Trim();

            var index = preferences.FavoriteSubjects.FindIndex(item =>
                string.Equals(item, subject, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                preferences.FavoriteSubjects.RemoveAt(index);
            else
                preferences.FavoriteSubjects.Add(subject);

            preferences.FavoriteSubjects = preferences.FavoriteSubjects
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            preferences.UpdatedAt = DateTime.Now;
            _preferencesByChat[chatId] = preferences;
            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_preferencesByChat, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, GroupHomeworkSubjectPreferences> LoadPreferences(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, GroupHomeworkSubjectPreferences>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, GroupHomeworkSubjectPreferences>>(json, JsonOptions)
            ?? new Dictionary<long, GroupHomeworkSubjectPreferences>();
    }

    private static GroupHomeworkSubjectPreferences ClonePreferences(GroupHomeworkSubjectPreferences preferences)
    {
        return new GroupHomeworkSubjectPreferences
        {
            ChatTitle = preferences.ChatTitle,
            FavoriteSubjects = preferences.FavoriteSubjects.ToList(),
            IsConfigured = preferences.IsConfigured,
            UpdatedAt = preferences.UpdatedAt
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
