using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Хранилище сессий пользователей в памяти.
/// Синглтон — живёт всё время работы бота.
/// </summary>
public class SessionService
{
    private readonly StudyTaskStorageService _taskStorage;

    // Словарь: Telegram UserId → сессия
    private readonly Dictionary<long, UserSession> _sessions = new();

    // Блокировка для потокобезопасности (несколько пользователей одновременно)
    private readonly Lock _lock = new();

    public SessionService(StudyTaskStorageService taskStorage)
    {
        _taskStorage = taskStorage;
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
                    FirstName = firstName,
                    Tasks     = _taskStorage.Get(userId)
                };
                _sessions[userId] = session;
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

    public void SaveTasks(UserSession session)
    {
        lock (_lock)
        {
            _taskStorage.Save(session.UserId, session.Tasks);
        }
    }
}
