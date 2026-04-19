using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramStudentBot.Services;

public class DeadlineReminderService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly StudyTaskStorageService _tasks;
    private readonly ReminderSettingsService _reminders;
    private readonly ILogger<DeadlineReminderService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;

    public DeadlineReminderService(
        ITelegramBotClient bot,
        StudyTaskStorageService tasks,
        ReminderSettingsService reminders,
        ILogger<DeadlineReminderService> logger)
    {
        _bot = bot;
        _tasks = tasks;
        _reminders = reminders;
        _logger = logger;
        _moscowTimeZone = ResolveMoscowTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке напоминаний о дедлайнах");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task CheckRemindersAsync(CancellationToken ct)
    {
        var now = GetMoscowNow();
        var today = now.Date;
        var allSettings = _reminders.GetAll();
        var allTasks = _tasks.GetAll();

        foreach (var (userId, settings) in allSettings)
        {
            if (!settings.IsEnabled ||
                settings.ChatId == 0 ||
                settings.Hour != now.Hour ||
                settings.Minute != now.Minute ||
                settings.LastNotificationDate?.Date == today)
            {
                continue;
            }

            var tomorrow = today.AddDays(1);
            var dueTomorrow = allTasks.TryGetValue(userId, out var userTasks)
                ? userTasks
                    .Where(task => !task.IsCompleted &&
                                   task.Deadline.HasValue &&
                                   task.Deadline.Value.Date == tomorrow)
                    .OrderBy(task => task.Subject)
                    .ThenBy(task => task.Title)
                    .ToList()
                : new();

            if (dueTomorrow.Count > 0)
            {
                await _bot.SendMessage(
                    chatId: settings.ChatId,
                    text: BuildReminderText(dueTomorrow, tomorrow),
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                _logger.LogInformation(
                    "Отправлено напоминание о {Count} дедлайнах пользователю {UserId}",
                    dueTomorrow.Count,
                    userId);
            }

            _reminders.MarkNotificationChecked(userId, today);
        }
    }

    private DateTime GetMoscowNow()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _moscowTimeZone).DateTime;

    private static string BuildReminderText(IEnumerable<Models.StudyTask> tasks, DateTime deadlineDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"⏰ <b>Дедлайны на завтра ({deadlineDate:dd.MM.yyyy})</b>");
        sb.AppendLine();

        foreach (var task in tasks)
        {
            sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
            sb.AppendLine($"📚 {Escape(task.Subject)}");
            sb.AppendLine();
        }

        sb.Append("Открыть список: /homework");
        return sb.ToString();
    }

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        foreach (var id in new[] { "Russian Standard Time", "Europe/Moscow" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
