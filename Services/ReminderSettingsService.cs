using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class ReminderSettingsService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly UserProfileStorageService _userProfiles;
    private readonly Dictionary<long, UserReminderSettings> _settingsByUser;

    public ReminderSettingsService(UserProfileStorageService userProfiles)
    {
        _userProfiles = userProfiles;
        _path = ResolveSettingsPath();
        _settingsByUser = LoadSettings(_path);
    }

    public UserReminderSettings Get(long userId)
    {
        lock (_lock)
        {
            return _settingsByUser.TryGetValue(userId, out var settings)
                ? CloneSettings(settings)
                : new UserReminderSettings();
        }
    }

    public IReadOnlyDictionary<long, UserReminderSettings> GetAll()
    {
        lock (_lock)
        {
            return _settingsByUser.ToDictionary(
                item => item.Key,
                item => CloneSettings(item.Value));
        }
    }

    public void Save(long userId, UserReminderSettings settings)
    {
        lock (_lock)
        {
            ApplyUserMetadata(userId, settings);
            settings.UpdatedAt = DateTime.Now;
            _settingsByUser[userId] = CloneSettings(settings);
            SaveAll();
        }
    }

    public void SyncUserMetadata(long userId)
    {
        lock (_lock)
        {
            if (!_settingsByUser.TryGetValue(userId, out var settings))
                return;

            var oldNickname = settings.Nickname;
            var oldUsername = settings.Username;

            ApplyUserMetadata(userId, settings);

            if (string.Equals(oldNickname, settings.Nickname, StringComparison.Ordinal) &&
                string.Equals(oldUsername, settings.Username, StringComparison.Ordinal))
            {
                return;
            }

            SaveAll();
        }
    }

    public void MarkPromptAnswered(long userId, long chatId, bool answered = true)
    {
        lock (_lock)
        {
            var settings = _settingsByUser.TryGetValue(userId, out var existing)
                ? existing
                : new UserReminderSettings();

            settings.ChatId = chatId;
            settings.PromptAnswered = answered;
            ApplyUserMetadata(userId, settings);
            settings.UpdatedAt = DateTime.Now;
            _settingsByUser[userId] = settings;
            SaveAll();
        }
    }

    public void Enable(long userId, long chatId, int hour, int minute)
    {
        lock (_lock)
        {
            var settings = _settingsByUser.TryGetValue(userId, out var existing)
                ? existing
                : new UserReminderSettings();

            settings.ChatId = chatId;
            settings.IsEnabled = true;
            settings.PromptAnswered = true;
            settings.Hour = hour;
            settings.Minute = minute;
            ApplyUserMetadata(userId, settings);
            settings.UpdatedAt = DateTime.Now;
            _settingsByUser[userId] = settings;
            SaveAll();
        }
    }

    public void Disable(long userId, long chatId)
    {
        lock (_lock)
        {
            var settings = _settingsByUser.TryGetValue(userId, out var existing)
                ? existing
                : new UserReminderSettings();

            settings.ChatId = chatId;
            settings.IsEnabled = false;
            settings.PromptAnswered = true;
            ApplyUserMetadata(userId, settings);
            settings.UpdatedAt = DateTime.Now;
            _settingsByUser[userId] = settings;
            SaveAll();
        }
    }

    public void MarkNotificationChecked(long userId, DateTime notificationDate)
    {
        lock (_lock)
        {
            if (!_settingsByUser.TryGetValue(userId, out var settings))
                return;

            settings.LastNotificationDate = notificationDate.Date;
            ApplyUserMetadata(userId, settings);
            settings.UpdatedAt = DateTime.Now;
            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_settingsByUser, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, UserReminderSettings> LoadSettings(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, UserReminderSettings>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, UserReminderSettings>>(json, JsonOptions)
            ?? new Dictionary<long, UserReminderSettings>();
    }

    private static UserReminderSettings CloneSettings(UserReminderSettings settings)
    {
        return new UserReminderSettings
        {
            ChatId = settings.ChatId,
            Nickname = settings.Nickname,
            Username = settings.Username,
            IsEnabled = settings.IsEnabled,
            PromptAnswered = settings.PromptAnswered,
            Hour = settings.Hour,
            Minute = settings.Minute,
            LastNotificationDate = settings.LastNotificationDate,
            UpdatedAt = settings.UpdatedAt
        };
    }

    private void ApplyUserMetadata(long userId, UserReminderSettings settings)
    {
        var profile = _userProfiles.Get(userId);
        if (profile is null)
            return;

        settings.Nickname = profile.Nickname;
        settings.Username = profile.Username;
    }

    private static string ResolveSettingsPath()
    {
        return UserDataPath.ResolveFile("user-reminders.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
