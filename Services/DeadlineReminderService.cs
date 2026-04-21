using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class DeadlineReminderService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly ReminderSettingsService _reminders;
    private readonly ILogger<DeadlineReminderService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;

    public DeadlineReminderService(
        ITelegramBotClient bot,
        SessionService sessions,
        ReminderSettingsService reminders,
        ILogger<DeadlineReminderService> logger)
    {
        _bot = bot;
        _sessions = sessions;
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
        var tomorrow = today.AddDays(1);
        var allSettings = _reminders.GetAll();

        foreach (var (userId, settings) in allSettings)
        {
            if (!IsReminderDue(settings, now, today))
                continue;

            var session = _sessions.Get(userId);
            var activeTasks = session?.Tasks
                .Where(task => !task.IsCompleted && task.Deadline.HasValue)
                .OrderBy(task => task.Deadline)
                .ThenBy(task => task.Subject)
                .ThenBy(task => task.Title)
                .ToList() ?? new List<StudyTask>();

            var dueToday = activeTasks
                .Where(task => task.Deadline!.Value.Date == today)
                .ToList();

            var dueTomorrow = activeTasks
                .Where(task => task.Deadline!.Value.Date == tomorrow)
                .ToList();

            await _bot.SendMessage(
                chatId: settings.ChatId,
                text: BuildReminderText(dueToday, dueTomorrow, today, tomorrow),
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            _logger.LogInformation(
                "Отправлено напоминание пользователю {UserId}. На сегодня: {TodayCount}, на завтра: {TomorrowCount}",
                userId,
                dueToday.Count,
                dueTomorrow.Count);

            _reminders.MarkNotificationChecked(userId, today);
        }
    }

    private static bool IsReminderDue(UserReminderSettings settings, DateTime now, DateTime today)
    {
        if (!settings.IsEnabled || settings.ChatId == 0)
            return false;

        if (settings.LastNotificationDate?.Date == today)
            return false;

        var scheduledToday = today.AddHours(settings.Hour).AddMinutes(settings.Minute);
        return now >= scheduledToday;
    }

    private DateTime GetMoscowNow()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _moscowTimeZone).DateTime;

    private static string BuildReminderText(
        IReadOnlyCollection<StudyTask> dueToday,
        IReadOnlyCollection<StudyTask> dueTomorrow,
        DateTime today,
        DateTime tomorrow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("⏰ <b>Напоминание о дедлайнах</b>");
        sb.AppendLine();

        AppendSection(sb, $"На сегодня ({today:dd.MM.yyyy})", dueToday, "На сегодня дедлайнов нет.");
        sb.AppendLine();
        AppendSection(sb, $"На завтра ({tomorrow:dd.MM.yyyy})", dueTomorrow, "На завтра дедлайнов нет.");

        if (dueToday.Count > 0 || dueTomorrow.Count > 0)
        {
            sb.AppendLine();
            sb.Append("Открыть список задач: /plan");
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyCollection<StudyTask> tasks, string emptyText)
    {
        sb.AppendLine($"<b>{title}</b>");

        if (tasks.Count == 0)
        {
            sb.AppendLine(emptyText);
            return;
        }

        foreach (var task in tasks)
        {
            sb.AppendLine($"• <b>{Escape(task.Title)}</b>");
            sb.AppendLine($"  {Escape(task.Subject)}");
        }
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
