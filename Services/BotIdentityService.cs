namespace TelegramStudentBot.Services;

public class BotIdentityService
{
    public string? Username { get; private set; }

    public void SetUsername(string? username)
    {
        Username = string.IsNullOrWhiteSpace(username)
            ? null
            : username.Trim().TrimStart('@');
    }
}
