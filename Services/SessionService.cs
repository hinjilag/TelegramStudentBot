using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Хранилище сессий пользователей в памяти.
/// Синглтон — живёт всё время работы бота.
/// </summary>
public class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Словарь: Telegram UserId → сессия
    private readonly Dictionary<long, UserSession> _sessions = new();

    // Блокировка для потокобезопасности (несколько пользователей одновременно)
    private readonly Lock _lock = new();
    private readonly string _storagePath;
    private readonly ILogger<SessionService> _logger;

    public SessionService(IConfiguration config, ILogger<SessionService> logger)
    {
        _logger = logger;
        _storagePath = config["SessionStoragePath"] ??
                       Path.Combine(Directory.GetCurrentDirectory(), "data", "sessions.json");

        Load();
    }

    /// <summary>
    /// Получить сессию пользователя. Если не существует — создать новую.
    /// </summary>
    /// <param name="userId">Telegram ID пользователя</param>
    /// <param name="firstName">Имя (используется только при создании)</param>
    public UserSession GetOrCreate(long userId, string firstName = "Студент")
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(userId, out var session))
            {
                session = new UserSession
                {
                    UserId    = userId,
                    FirstName = firstName
                };
                _sessions[userId] = session;
                SaveLocked();
            }
            else if (!string.IsNullOrWhiteSpace(firstName) &&
                     firstName != "Студент" &&
                     !string.Equals(session.FirstName, firstName, StringComparison.Ordinal))
            {
                session.FirstName = firstName;
                SaveLocked();
            }
            return session;
        }
    }

    /// <summary>
    /// Получить сессию пользователя. Возвращает null, если не существует.
    /// </summary>
    public UserSession? Get(long userId)
    {
        lock (_lock)
        {
            _sessions.TryGetValue(userId, out var session);
            return session;
        }
    }

    /// <summary>Сохранить постоянные данные всех сессий на диск.</summary>
    public void Save()
    {
        lock (_lock)
        {
            SaveLocked();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_storagePath))
                return;

            try
            {
                var json = File.ReadAllText(_storagePath);
                var storage = JsonSerializer.Deserialize<SessionStorage>(json, JsonOptions);
                if (storage?.Sessions is null)
                    return;

                _sessions.Clear();
                foreach (var stored in storage.Sessions.Where(s => s.UserId > 0))
                {
                    _sessions[stored.UserId] = new UserSession
                    {
                        UserId = stored.UserId,
                        FirstName = string.IsNullOrWhiteSpace(stored.FirstName) ? "Студент" : stored.FirstName,
                        FatigueLevel = Math.Clamp(stored.FatigueLevel, 0, 100),
                        WorkSessionsWithoutRest = Math.Max(0, stored.WorkSessionsWithoutRest),
                        Tasks = stored.Tasks ?? new List<StudyTask>(),
                        Schedule = stored.Schedule ?? new List<ScheduleEntry>(),
                        SchedulePhotoDataUrl = stored.SchedulePhotoDataUrl,
                        State = UserState.Idle,
                        ActiveTimer = null,
                        DraftTask = null,
                        DraftSchedule = null
                    };
                }

                _logger.LogInformation("Загружено сессий из {Path}: {Count}", _storagePath, _sessions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось загрузить сессии из {Path}", _storagePath);
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var storage = new SessionStorage
            {
                Sessions = _sessions.Values
                    .OrderBy(s => s.UserId)
                    .Select(s => new StoredSession
                    {
                        UserId = s.UserId,
                        FirstName = s.FirstName,
                        FatigueLevel = s.FatigueLevel,
                        WorkSessionsWithoutRest = s.WorkSessionsWithoutRest,
                        Tasks = s.Tasks,
                        Schedule = s.Schedule,
                        SchedulePhotoDataUrl = s.SchedulePhotoDataUrl
                    })
                    .ToList()
            };

            var json = JsonSerializer.Serialize(storage, JsonOptions);
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить сессии в {Path}", _storagePath);
        }
    }

    private sealed class SessionStorage
    {
        public int Version { get; set; } = 1;
        public List<StoredSession> Sessions { get; set; } = new();
    }

    private sealed class StoredSession
    {
        public long UserId { get; set; }
        public string FirstName { get; set; } = "Студент";
        public int FatigueLevel { get; set; }
        public int WorkSessionsWithoutRest { get; set; }
        public List<StudyTask> Tasks { get; set; } = new();
        public List<ScheduleEntry> Schedule { get; set; } = new();
        public string? SchedulePhotoDataUrl { get; set; }
    }
}
