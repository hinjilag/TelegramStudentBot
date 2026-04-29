namespace TelegramStudentBot.Models;

public class GroupParticipant
{
    public long UserId { get; set; }

    public string Nickname { get; set; } = "Участник";

    public string? Username { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.Now;
}

public class StoredGroupParticipants
{
    public long ChatId { get; set; }

    public string ChatTitle { get; set; } = "Группа";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<GroupParticipant> Participants { get; set; } = new();
}
