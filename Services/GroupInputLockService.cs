namespace TelegramStudentBot.Services;

public class GroupInputLockService
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(20);

    private readonly Lock _lock = new();
    private readonly Dictionary<long, GroupInputLock> _locksByChat = new();

    public GroupInputLock? Get(long chatId)
    {
        lock (_lock)
        {
            return GetActiveLock(chatId);
        }
    }

    public bool TryAcquire(long chatId, long userId, string ownerDisplayName, string purpose, out GroupInputLock? activeLock)
    {
        lock (_lock)
        {
            activeLock = GetActiveLock(chatId);
            if (activeLock is not null && activeLock.UserId != userId)
                return false;

            activeLock = new GroupInputLock
            {
                ChatId = chatId,
                UserId = userId,
                OwnerDisplayName = string.IsNullOrWhiteSpace(ownerDisplayName) ? "Участник" : ownerDisplayName,
                Purpose = purpose,
                UpdatedAt = DateTime.UtcNow
            };

            _locksByChat[chatId] = activeLock;
            return true;
        }
    }

    public bool IsOwnedBy(long chatId, long userId, string? purpose = null)
    {
        lock (_lock)
        {
            var activeLock = GetActiveLock(chatId);
            if (activeLock is null || activeLock.UserId != userId)
                return false;

            return string.IsNullOrWhiteSpace(purpose) ||
                   string.Equals(activeLock.Purpose, purpose, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Release(long chatId, long? userId = null)
    {
        lock (_lock)
        {
            if (!_locksByChat.TryGetValue(chatId, out var activeLock))
                return;

            if (userId.HasValue && activeLock.UserId != userId.Value)
                return;

            _locksByChat.Remove(chatId);
        }
    }

    private GroupInputLock? GetActiveLock(long chatId)
    {
        if (!_locksByChat.TryGetValue(chatId, out var activeLock))
            return null;

        if (DateTime.UtcNow - activeLock.UpdatedAt <= LockTtl)
            return activeLock;

        _locksByChat.Remove(chatId);
        return null;
    }
}

public class GroupInputLock
{
    public long ChatId { get; set; }

    public long UserId { get; set; }

    public string OwnerDisplayName { get; set; } = "Участник";

    public string Purpose { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
