using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class ReminderSettingsService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, UserReminderSettings> _settingsByUser;

    public ReminderSettingsService()
    {
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
            settings.UpdatedAt = DateTime.Now;
            _settingsByUser[userId] = CloneSettings(settings);
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

            var shouldResetNotificationDate =
                !settings.IsEnabled ||
                settings.Hour != hour ||
                settings.Minute != minute;

            settings.ChatId = chatId;
            settings.IsEnabled = true;
            settings.PromptAnswered = true;
            settings.Hour = hour;
            settings.Minute = minute;
            if (shouldResetNotificationDate)
                settings.LastNotificationDate = null;
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
            IsEnabled = settings.IsEnabled,
            PromptAnswered = settings.PromptAnswered,
            Hour = settings.Hour,
            Minute = settings.Minute,
            LastNotificationDate = settings.LastNotificationDate,
            UpdatedAt = settings.UpdatedAt
        };
    }

    private static string ResolveSettingsPath()
    {
        var contentRootData = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        if (Directory.Exists(contentRootData))
            return Path.Combine(contentRootData, "user-reminders.json");

        return Path.Combine(AppContext.BaseDirectory, "Data", "user-reminders.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
