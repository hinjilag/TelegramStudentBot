using System.Text.Json;
using Telegram.Bot.Types;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserProfileStorageService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, UserProfile> _profiles;

    public UserProfileStorageService()
    {
        _path = ResolveProfilesPath();
        _profiles = LoadProfiles(_path);
    }

    public void Upsert(User? user)
    {
        if (user is null)
            return;

        Upsert(user.Id, user.FirstName, user.LastName, user.Username);
    }

    public void Upsert(long userId, string? firstName, string? lastName, string? username)
    {
        var nickname = BuildNickname(firstName, lastName, username);
        var normalizedUsername = BuildUsername(username);

        lock (_lock)
        {
            if (_profiles.TryGetValue(userId, out var existing) &&
                string.Equals(existing.Nickname, nickname, StringComparison.Ordinal) &&
                string.Equals(existing.Username, normalizedUsername, StringComparison.Ordinal))
            {
                return;
            }

            _profiles[userId] = new UserProfile
            {
                UserId = userId,
                Nickname = nickname,
                Username = normalizedUsername,
                UpdatedAt = DateTime.Now
            };

            SaveAll();
        }
    }

    public UserProfile? Get(long userId)
    {
        lock (_lock)
        {
            return _profiles.TryGetValue(userId, out var profile)
                ? Clone(profile)
                : null;
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_profiles, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, UserProfile> LoadProfiles(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, UserProfile>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, UserProfile>>(json, JsonOptions)
            ?? new Dictionary<long, UserProfile>();
    }

    private static UserProfile Clone(UserProfile profile)
    {
        return new UserProfile
        {
            UserId = profile.UserId,
            Nickname = profile.Nickname,
            Username = profile.Username,
            UpdatedAt = profile.UpdatedAt
        };
    }

    private static string BuildNickname(string? firstName, string? lastName, string? username)
    {
        var parts = new[] { firstName, lastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
            return string.Join(" ", parts);

        return BuildUsername(username) ?? "Студент";
    }

    private static string? BuildUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        return username.StartsWith('@') ? username : $"@{username}";
    }

    private static string ResolveProfilesPath()
    {
        return UserDataPath.ResolveFile("user-profiles.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
