using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupReminderSettingsService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, GroupReminderSettings> _settingsByChat;

    public GroupReminderSettingsService()
    {
        _path = UserDataPath.ResolveFile("group-reminders.json");
        _settingsByChat = LoadSettings(_path);
    }

    public GroupReminderSettings Get(long chatId)
    {
        lock (_lock)
        {
            return _settingsByChat.TryGetValue(chatId, out var settings)
                ? CloneSettings(settings)
                : new GroupReminderSettings { ChatId = chatId };
        }
    }

    public IReadOnlyDictionary<long, GroupReminderSettings> GetAll()
    {
        lock (_lock)
        {
            return _settingsByChat.ToDictionary(
                item => item.Key,
                item => CloneSettings(item.Value));
        }
    }

    public void Enable(long chatId, string? chatTitle, int hour, int minute)
    {
        lock (_lock)
        {
            var settings = _settingsByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new GroupReminderSettings();

            settings.ChatId = chatId;
            settings.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? "Группа" : chatTitle.Trim();
            settings.IsEnabled = true;
            settings.Hour = hour;
            settings.Minute = minute;
            settings.UpdatedAt = DateTime.Now;

            _settingsByChat[chatId] = CloneSettings(settings);
            SaveAll();
        }
    }

    public void Disable(long chatId, string? chatTitle)
    {
        lock (_lock)
        {
            var settings = _settingsByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new GroupReminderSettings();

            settings.ChatId = chatId;
            settings.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? "Группа" : chatTitle.Trim();
            settings.IsEnabled = false;
            settings.UpdatedAt = DateTime.Now;

            _settingsByChat[chatId] = CloneSettings(settings);
            SaveAll();
        }
    }

    public void MarkNotificationChecked(long chatId, DateTime notificationDate)
    {
        lock (_lock)
        {
            if (!_settingsByChat.TryGetValue(chatId, out var settings))
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

        var json = JsonSerializer.Serialize(_settingsByChat, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, GroupReminderSettings> LoadSettings(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, GroupReminderSettings>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, GroupReminderSettings>>(json, JsonOptions)
            ?? new Dictionary<long, GroupReminderSettings>();
    }

    private static GroupReminderSettings CloneSettings(GroupReminderSettings settings)
    {
        return new GroupReminderSettings
        {
            ChatId = settings.ChatId,
            ChatTitle = settings.ChatTitle,
            IsEnabled = settings.IsEnabled,
            Hour = settings.Hour,
            Minute = settings.Minute,
            LastNotificationDate = settings.LastNotificationDate,
            UpdatedAt = settings.UpdatedAt
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
