using System.Text.Json;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class MiniAppPinStateService
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Dictionary<long, MiniAppPinState> _statesByChat;

    public MiniAppPinStateService()
    {
        _path = UserDataPath.ResolveFile("miniapp-pins.json");
        _statesByChat = LoadStates(_path);
    }

    public MiniAppPinState? Get(long chatId)
    {
        lock (_lock)
        {
            return _statesByChat.TryGetValue(chatId, out var state)
                ? Clone(state)
                : null;
        }
    }

    public void Save(long chatId, int messageId)
    {
        lock (_lock)
        {
            _statesByChat[chatId] = new MiniAppPinState
            {
                ChatId = chatId,
                MessageId = messageId,
                UpdatedAt = DateTime.Now
            };

            SaveAll();
        }
    }

    public void Delete(long chatId)
    {
        lock (_lock)
        {
            if (!_statesByChat.Remove(chatId))
                return;

            SaveAll();
        }
    }

    private void SaveAll()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_statesByChat, JsonOptions);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<long, MiniAppPinState> LoadStates(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<long, MiniAppPinState>();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<long, MiniAppPinState>>(json, JsonOptions)
            ?? new Dictionary<long, MiniAppPinState>();
    }

    private static MiniAppPinState Clone(MiniAppPinState state)
    {
        return new MiniAppPinState
        {
            ChatId = state.ChatId,
            MessageId = state.MessageId,
            UpdatedAt = state.UpdatedAt
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
