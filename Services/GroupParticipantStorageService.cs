using System.Text.Json;
using Telegram.Bot.Types;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class GroupParticipantStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly UserProfileStorageService _userProfiles;
    private readonly Dictionary<long, StoredGroupParticipants> _participantsByChat;

    public GroupParticipantStorageService(UserProfileStorageService userProfiles)
    {
        _userProfiles = userProfiles;
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
            var username = NormalizeUsername(user.Username);

            var participant = stored.Participants.FirstOrDefault(item => item.UserId == user.Id);
            if (participant is null)
            {
                stored.Participants.Add(new GroupParticipant
                {
                    UserId = user.Id,
                    Nickname = nickname,
                    Username = username,
                    IsBot = user.IsBot,
                    IsManual = false,
                    LastSeenAt = DateTime.Now
                });
            }
            else
            {
                participant.Nickname = nickname;
                participant.Username = username;
                participant.IsBot = user.IsBot;
                participant.IsManual = false;
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

    public List<GroupChatMembership> GetChatsForUser(long userId)
    {
        lock (_lock)
        {
            var username = _userProfiles.Get(userId)?.Username;

            return _participantsByChat.Values
                .Where(stored =>
                    stored.Participants.Any(participant => participant.UserId == userId && !participant.IsBot) ||
                    (!string.IsNullOrWhiteSpace(username) &&
                     stored.ManualUsernames.Any(item => string.Equals(item, username, StringComparison.OrdinalIgnoreCase))))
                .Select(stored => new GroupChatMembership
                {
                    ChatId = stored.ChatId,
                    ChatTitle = stored.ChatTitle,
                    UpdatedAt = stored.UpdatedAt
                })
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.ChatTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public List<string> GetManualUsernames(long chatId)
    {
        lock (_lock)
        {
            return _participantsByChat.TryGetValue(chatId, out var stored)
                ? stored.ManualUsernames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
    }

    public void SetManualUsernames(long chatId, string? chatTitle, IEnumerable<string> usernames)
    {
        lock (_lock)
        {
            var stored = _participantsByChat.TryGetValue(chatId, out var existing)
                ? existing
                : new StoredGroupParticipants { ChatId = chatId };

            stored.ChatTitle = string.IsNullOrWhiteSpace(chatTitle) ? stored.ChatTitle : chatTitle.Trim();
            stored.UpdatedAt = DateTime.Now;
            stored.ManualUsernames = usernames
                .Select(NormalizeUsername)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList()!;

            _participantsByChat[chatId] = Clone(stored);
            SaveAll();
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
            Participants = source.Participants.Select(Clone).ToList(),
            ManualUsernames = source.ManualUsernames.ToList()
        };
    }

    private static GroupParticipant Clone(GroupParticipant source)
    {
        return new GroupParticipant
        {
            UserId = source.UserId,
            Nickname = source.Nickname,
            Username = source.Username,
            IsBot = source.IsBot,
            IsManual = source.IsManual,
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
            return NormalizeUsername(user.Username)!;

        return "Участник";
    }

    private static string? NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var trimmed = username.Trim();
        return trimmed.StartsWith('@') ? trimmed : $"@{trimmed}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
