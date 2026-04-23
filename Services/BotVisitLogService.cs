using System.Text.Json;
using Telegram.Bot.Types;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class BotVisitLogService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly List<BotVisitLogEntry> _entries;

    public BotVisitLogService()
    {
        _path = UserDataPath.ResolveFile("bot-visits.json");
        _entries = LoadEntries(_path);
    }

    public void RecordVisit(User user)
    {
        var entry = new BotVisitLogEntry
        {
            UserId = user.Id,
            Nickname = BuildNickname(user),
            Username = BuildUsername(user.Username),
            VisitedAt = DateTime.Now
        };

        lock (_lock)
        {
            _entries.Add(entry);
            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static List<BotVisitLogEntry> LoadEntries(string path)
    {
        if (!File.Exists(path))
            return new List<BotVisitLogEntry>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<BotVisitLogEntry>>(json, JsonOptions)
            ?? new List<BotVisitLogEntry>();
    }

    private static string BuildNickname(User user)
    {
        var parts = new[] { user.FirstName, user.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
            return string.Join(" ", parts);

        return BuildUsername(user.Username) ?? "Студент";
    }

    private static string? BuildUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        return username.StartsWith('@') ? username : $"@{username}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
