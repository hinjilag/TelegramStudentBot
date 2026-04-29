using System.Text.Json;
using Telegram.Bot.Types;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupParticipantStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, StoredGroupParticipants> _participantsByChat;

    public GroupParticipantStorageService()
    {
        _path = UserDataPath.ResolveFile("group-participants.json");
        _participantsByChat = LoadAll(_path);
    }

    public void Upsert(long chatId, string? chatTitle, User? user)
    {
        if (user is null)
            return;

        lock (_lock)
        {
            var stored = _participantsByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new StoredGroupParticipants { ChatId = chatId };

            stored.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? stored.ChatTitle : chatTitle.Trim();
            stored.UpdatedAt = DateTime.Now;

            var nickname = BuildNickname(user);
            var username = string.IsNullOrWhiteSpace(user.Username)
                ? null
                : (user.Username.StartsWith('@') ? user.Username : $"@{user.Username}");

            var participant = stored.Participants.FirstOrDefault(item => item.UserId == user.Id);
            if (participant is null)
            {
                stored.Participants.Add(new GroupParticipant
                {
                    UserId = user.Id,
                    Nickname = nickname,
                    Username = username,
                    LastSeenAt = DateTime.Now
                });
            }
            else
            {
                participant.Nickname = nickname;
                participant.Username = username;
                participant.LastSeenAt = DateTime.Now;
            }

            _participantsByChat[chatId] = Clone(stored);
            SaveAll();
        }
    }

    public List<GroupParticipant> Get(long chatId)
    {
        lock (_lock)
        {
            return _participantsByChat.TryGetValue(chatId, out var stored)
                ? stored.Participants.Select(Clone).OrderBy(item => item.Nickname).ToList()
                : new List<GroupParticipant>();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_participantsByChat, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, StoredGroupParticipants> LoadAll(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, StoredGroupParticipants>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, StoredGroupParticipants>>(json, JsonOptions)
            ?? new Dictionary<long, StoredGroupParticipants>();
    }

    private static StoredGroupParticipants Clone(StoredGroupParticipants source)
    {
        return new StoredGroupParticipants
        {
            ChatId = source.ChatId,
            ChatTitle = source.ChatTitle,
            UpdatedAt = source.UpdatedAt,
            Participants = source.Participants.Select(Clone).ToList()
        };
    }

    private static GroupParticipant Clone(GroupParticipant source)
    {
        return new GroupParticipant
        {
            UserId = source.UserId,
            Nickname = source.Nickname,
            Username = source.Username,
            LastSeenAt = source.LastSeenAt
        };
    }

    private static string BuildNickname(User user)
    {
        var parts = new[] { user.FirstName, user.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
            return string.Join(" ", parts);

        if (!string.IsNullOrWhiteSpace(user.Username))
            return user.Username.StartsWith('@') ? user.Username : $"@{user.Username}";

        return "Участник";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
