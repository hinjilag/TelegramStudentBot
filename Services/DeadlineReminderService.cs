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
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly ReminderSettingsService _reminders;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly GroupParticipantStorageService _groupParticipants;
    private readonly ILogger<DeadlineReminderService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;
    private DateTime? _lastGroupCleanupDate;

    public DeadlineReminderService(
        ITelegramBotClient bot,
        StudyTaskStorageService tasks,
        ReminderSettingsService reminders,
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        GroupParticipantStorageService groupParticipants,
        ILogger<DeadlineReminderService> logger)
    {
        _bot = bot;
        _tasks = tasks;
        _reminders = reminders;
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _groupParticipants = groupParticipants;
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
        var allGroupSettings = _groupReminders.GetAll();

        RunGroupHomeworkCleanupIfNeeded(now, today, allGroupSettings);

        var allGroupTasks = _groupTasks.GetAll();

        foreach (var (userId, settings) in allSettings)
        {
            if (!settings.IsEnabled ||
                settings.ChatId == 0 ||
                !IsScheduledTimeReached(now, settings.Hour, settings.Minute) ||
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
                try
                {
                    await _bot.SendMessage(
                        chatId: settings.ChatId,
                        text: BuildReminderText(dueTomorrow, tomorrow),
                        parseMode: ParseMode.Html,
                        cancellationToken: ct);

                    _reminders.MarkNotificationChecked(userId, today);

                    _logger.LogInformation(
                        "Отправлено напоминание о {Count} дедлайнах пользователю {UserId}",
                        dueTomorrow.Count,
                        userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Не удалось отправить напоминание пользователю {UserId} в чат {ChatId}",
                        userId,
                        settings.ChatId);
                }
            }
        }

        foreach (var (chatId, settings) in allGroupSettings)
        {
            if (!settings.IsEnabled ||
                (settings.Frequency == Models.GroupReminderFrequency.Weekdays &&
                 (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)) ||
                !IsScheduledTimeReached(now, settings.Hour, settings.Minute) ||
                settings.LastNotificationDate?.Date == today)
            {
                continue;
            }

            var tomorrow = today.AddDays(1);
            var dueTomorrow = allGroupTasks.TryGetValue(chatId, out var groupTasks)
                ? groupTasks
                    .Where(task => !task.IsCompleted &&
                                   task.Deadline.HasValue &&
                                   task.Deadline.Value.Date == tomorrow)
                    .OrderBy(task => task.Subject)
                    .ThenBy(task => task.Title)
                    .ToList()
                : new();

            if (dueTomorrow.Count > 0)
            {
                await SeedKnownParticipantsFromAdminsAsync(chatId, settings.ChatTitle, ct);

                var knownParticipants = _groupParticipants.Get(chatId);
                var activeParticipants = await GetActiveParticipantsAsync(chatId, knownParticipants, ct);

                try
                {
                    var mentionBatches = BuildParticipantMentionBatches(activeParticipants);
                    for (var index = 0; index < mentionBatches.Count; index++)
                    {
                        var prefix = index == 0
                            ? "📣 <b>Напоминание для группы:</b>\n\n"
                            : "📣 <b>Ещё участники:</b>\n\n";

                        await _bot.SendMessage(
                            chatId: settings.ChatId,
                            text: prefix + mentionBatches[index],
                            parseMode: ParseMode.Html,
                            cancellationToken: ct);

                        await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
                    }

                    await _bot.SendMessage(
                        chatId: settings.ChatId,
                        text: BuildGroupReminderText(dueTomorrow, tomorrow, activeParticipants),
                        parseMode: ParseMode.Html,
                        cancellationToken: ct);

                    _groupReminders.MarkNotificationChecked(chatId, today);

                    _logger.LogInformation(
                        "Отправлено групповое напоминание о {Count} дедлайнах в чат {ChatId}",
                        dueTomorrow.Count,
                        chatId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Не удалось отправить групповое напоминание в чат {ChatId}",
                        settings.ChatId);
                }
            }
        }
    }

    private void RunGroupHomeworkCleanupIfNeeded(
        DateTime now,
        DateTime today,
        IReadOnlyDictionary<long, Models.GroupReminderSettings> allGroupSettings)
    {
        if (now.Hour != 0 || now.Minute != 5 || _lastGroupCleanupDate?.Date == today)
            return;

        var allGroupTasks = _groupTasks.GetAll();

        foreach (var (chatId, tasks) in allGroupTasks)
        {
            var activeTasks = tasks
                .Where(task => !(task.Deadline.HasValue && task.Deadline.Value.Date < today))
                .ToList();

            if (activeTasks.Count == tasks.Count)
                continue;

            var chatTitle = allGroupSettings.TryGetValue(chatId, out var settings)
                ? settings.ChatTitle
                : null;

            _groupTasks.Save(chatId, chatTitle, activeTasks);

            _logger.LogInformation(
                "Удалены просроченные групповые ДЗ в чате {ChatId}. Было: {Before}, осталось: {After}",
                chatId,
                tasks.Count,
                activeTasks.Count);
        }

        _lastGroupCleanupDate = today;
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

    private static string BuildGroupReminderText(
        IEnumerable<Models.StudyTask> tasks,
        DateTime deadlineDate,
        IReadOnlyCollection<Models.GroupParticipant> participants)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"⏰ <b>Что нужно сдать завтра — {deadlineDate:dd.MM.yyyy}</b>");
        sb.AppendLine();

        foreach (var task in tasks)
        {
            sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
            sb.AppendLine($"📚 {Escape(task.Subject)}");

            if (!string.IsNullOrWhiteSpace(task.CreatedByName))
                sb.AppendLine($"👤 {Escape(task.CreatedByName)}");

            sb.AppendLine();
        }

        if (participants.Count == 0)
        {
            sb.AppendLine("Чтобы я отмечал людей в таких напоминаниях, участникам нужно хотя бы раз проявиться в чате.");
            sb.AppendLine();
        }

        sb.Append("Открыть общий список: /homework");
        return sb.ToString();
    }

    private static string BuildMention(Models.GroupParticipant participant)
    {
        var label = string.IsNullOrWhiteSpace(participant.Username)
            ? participant.Nickname
            : participant.Username!;

        return $"<a href=\"tg://user?id={participant.UserId}\">{Escape(label)}</a>";
    }

    private async Task SeedKnownParticipantsFromAdminsAsync(long chatId, string? chatTitle, CancellationToken ct)
    {
        try
        {
            var admins = await _bot.GetChatAdministrators(chatId, ct);
            foreach (var admin in admins)
                _groupParticipants.Upsert(chatId, chatTitle, admin.User);
        }
        catch
        {
            // Not critical for reminder delivery.
        }
    }

    private async Task<List<Models.GroupParticipant>> GetActiveParticipantsAsync(
        long chatId,
        IReadOnlyCollection<Models.GroupParticipant> participants,
        CancellationToken ct)
    {
        var activeParticipants = new List<Models.GroupParticipant>();

        foreach (var participant in participants)
        {
            if (participant.IsBot)
                continue;

            try
            {
                var member = await _bot.GetChatMember(chatId, participant.UserId, ct);
                if (member is Telegram.Bot.Types.ChatMemberMember or
                    Telegram.Bot.Types.ChatMemberAdministrator or
                    Telegram.Bot.Types.ChatMemberOwner or
                    Telegram.Bot.Types.ChatMemberRestricted)
                {
                    activeParticipants.Add(participant);
                }
            }
            catch
            {
                // Skip participants that can't be verified anymore.
            }
        }

        return activeParticipants
            .GroupBy(participant => participant.UserId)
            .Select(group => group.OrderByDescending(item => item.LastSeenAt).First())
            .OrderByDescending(participant => participant.LastSeenAt)
            .ToList();
    }

    private static List<string> BuildParticipantMentionBatches(IReadOnlyList<Models.GroupParticipant> participants)
    {
        const int maxBatchLength = 700;
        const int maxMentionsPerBatch = 6;
        var batches = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (var participant in participants)
        {
            var mention = BuildMention(participant);
            var separatorLength = current.Count == 0 ? 0 : 1;
            if (current.Count > 0 &&
                (currentLength + separatorLength + mention.Length > maxBatchLength ||
                 current.Count >= maxMentionsPerBatch))
            {
                batches.Add(string.Join(" ", current));
                current.Clear();
                currentLength = 0;
            }

            current.Add(mention);
            currentLength += (current.Count == 1 ? 0 : 1) + mention.Length;
        }

        if (current.Count > 0)
            batches.Add(string.Join(" ", current));

        return batches;
    }

    private static bool IsScheduledTimeReached(DateTime now, int hour, int minute)
    {
        if (now.Hour > hour)
            return true;

        return now.Hour == hour && now.Minute >= minute;
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
