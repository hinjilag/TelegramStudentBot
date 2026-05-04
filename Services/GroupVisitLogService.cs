using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupVisitLogService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, GroupVisitLogEntry> _entries;

    public GroupVisitLogService()
    {
        _path = UserDataPath.ResolveFile("group-visits.json");
        _entries = LoadAll(_path);
    }

    public bool RegisterVisit(long chatId, string? chatTitle)
    {
        lock (_lock)
        {
            var isFirstVisit = !_entries.TryGetValue(chatId, out var entry);
            entry ??= new GroupVisitLogEntry
            {
                ChatId = chatId,
                FirstVisitedAt = DateTime.Now
            };

            entry.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? entry.ChatTitle : chatTitle.Trim();
            entry.LastVisitedAt = DateTime.Now;

            _entries[chatId] = entry;
            SaveAll();
            return isFirstVisit;
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

    private static Dictionary<long, GroupVisitLogEntry> LoadAll(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, GroupVisitLogEntry>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, GroupVisitLogEntry>>(json, JsonOptions)
            ?? new Dictionary<long, GroupVisitLogEntry>();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
