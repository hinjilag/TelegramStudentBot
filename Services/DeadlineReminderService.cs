using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class DeadlineReminderService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly StudyTaskStorageService _tasks;
    private readonly GroupStudyTaskStorageService _groupTasks;
    private readonly ReminderSettingsService _reminders;
    private readonly UserGroupTaskBridgeService _userGroupTasks;
    private readonly GroupReminderSettingsService _groupReminders;
    private readonly GroupParticipantResolverService _groupParticipantResolver;
    private readonly ILogger<DeadlineReminderService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;
    private DateTime? _lastGroupCleanupDate;

    public DeadlineReminderService(
        ITelegramBotClient bot,
        StudyTaskStorageService tasks,
        ReminderSettingsService reminders,
        UserGroupTaskBridgeService userGroupTasks,
        GroupStudyTaskStorageService groupTasks,
        GroupReminderSettingsService groupReminders,
        GroupParticipantResolverService groupParticipantResolver,
        ILogger<DeadlineReminderService> logger)
    {
        _bot = bot;
        _tasks = tasks;
        _reminders = reminders;
        _userGroupTasks = userGroupTasks;
        _groupTasks = groupTasks;
        _groupReminders = groupReminders;
        _groupParticipantResolver = groupParticipantResolver;
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
                !IsReminderDayEnabled(settings.Frequency, settings.SelectedDays, today) ||
                !IsScheduledTimeReached(now, settings.Hour, settings.Minute) ||
                HasAlreadySentScheduledReminder(settings.LastNotificationDate, today, settings.Hour, settings.Minute))
            {
                continue;
            }

            var ownTasks = allTasks.TryGetValue(userId, out var userTasks)
                ? userTasks
                    .Where(task => !task.IsCompleted)
                    .OrderBy(task => TaskSubjects.IsPersonal(task.Subject) ? 1 : 0)
                    .ThenBy(task => task.Deadline.HasValue ? 0 : 1)
                    .ThenBy(task => task.Deadline ?? DateTime.MaxValue)
                    .ThenBy(task => task.Subject)
                    .ThenBy(task => task.Title)
                    .ToList()
                : new List<StudyTask>();
            var groupFeeds = _userGroupTasks.GetLinkedGroupTaskFeeds(userId)
                .Select(feed => new StoredGroupTasks
                {
                    ChatId = feed.ChatId,
                    ChatTitle = feed.ChatTitle,
                    Tasks = feed.Tasks
                        .Where(task => !task.IsCompleted && (!task.Deadline.HasValue || task.Deadline.Value.Date >= today))
                        .OrderBy(task => task.Deadline ?? DateTime.MaxValue)
                        .ThenBy(task => task.Subject)
                        .ThenBy(task => task.Title)
                        .ToList()
                })
                .Where(feed => feed.Tasks.Count > 0)
                .ToList();

            if (ownTasks.Count == 0 && groupFeeds.Count == 0)
                continue;

            try
            {
                await _bot.SendMessage(
                    chatId: settings.ChatId,
                    text: BuildPersonalReminderText(ownTasks, groupFeeds, today),
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                _reminders.MarkNotificationChecked(userId, now);

                _logger.LogInformation(
                    "Отправлено личное напоминание о {Count} задачах пользователю {UserId}",
                    ownTasks.Count + groupFeeds.Sum(feed => feed.Tasks.Count),
                    userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Не удалось отправить личное напоминание пользователю {UserId} в чат {ChatId}",
                    userId,
                    settings.ChatId);
            }
        }

        foreach (var (chatId, settings) in allGroupSettings)
        {
            if (!settings.IsEnabled ||
                !IsReminderDayEnabled(settings.Frequency, settings.SelectedDays, today) ||
                !IsScheduledTimeReached(now, settings.Hour, settings.Minute) ||
                HasAlreadySentScheduledReminder(settings.LastNotificationDate, today, settings.Hour, settings.Minute))
            {
                continue;
            }

            var activeHomework = allGroupTasks.TryGetValue(chatId, out var groupTasks)
                ? groupTasks
                    .Where(task => !task.IsCompleted && (!task.Deadline.HasValue || task.Deadline.Value.Date >= today))
                    .OrderBy(task => task.Deadline ?? DateTime.MaxValue)
                    .ThenBy(task => task.Subject)
                    .ThenBy(task => task.Title)
                    .ToList()
                : new List<StudyTask>();

            if (activeHomework.Count == 0)
                continue;

            var activeParticipants = await _groupParticipantResolver.ResolveReminderParticipantsAsync(
                chatId,
                settings.ChatTitle,
                ct);
            var participantsToMention = FilterSelectedParticipants(activeParticipants, settings);

            try
            {
                var mentionBatches = _groupParticipantResolver.BuildMentionBatches(participantsToMention);
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

                var reminderMessage = await _bot.SendMessage(
                    chatId: settings.ChatId,
                    text: BuildGroupReminderText(activeHomework, today, participantsToMention),
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                await ReplacePinnedReminderAsync(settings, reminderMessage, ct);

                _groupReminders.MarkNotificationChecked(chatId, now);

                _logger.LogInformation(
                    "Отправлено групповое напоминание о {Count} заданиях в чат {ChatId}",
                    activeHomework.Count,
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

    private void RunGroupHomeworkCleanupIfNeeded(
        DateTime now,
        DateTime today,
        IReadOnlyDictionary<long, GroupReminderSettings> allGroupSettings)
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

    private static string BuildPersonalReminderText(
        IEnumerable<StudyTask> tasks,
        IReadOnlyCollection<StoredGroupTasks> linkedGroups,
        DateTime today)
    {
        var homeworkTasks = tasks
            .Where(task => !TaskSubjects.IsPersonal(task.Subject))
            .ToList();
        var personalTasks = tasks
            .Where(task => TaskSubjects.IsPersonal(task.Subject))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"⏰ <b>Напоминание на {today:dd.MM.yyyy}</b>");
        sb.AppendLine();

        if (homeworkTasks.Count > 0)
        {
            sb.AppendLine("📚 <b>Домашние задания</b>");
            sb.AppendLine();

            foreach (var task in homeworkTasks)
            {
                sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
                sb.AppendLine($"📘 {Escape(task.Subject)}");
                sb.AppendLine($"🗓 {FormatDeadline(task.Deadline)}");
                sb.AppendLine();
            }
        }

        if (personalTasks.Count > 0)
        {
            sb.AppendLine("🗂 <b>Личные дела</b>");
            sb.AppendLine();

            foreach (var task in personalTasks)
            {
                sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
                sb.AppendLine($"🗓 {FormatDeadline(task.Deadline)}");
                sb.AppendLine();
            }
        }

        foreach (var group in linkedGroups)
        {
            if (group.Tasks.Count == 0)
                continue;

            sb.AppendLine($"👥 <b>{Escape(group.ChatTitle)}</b>");
            sb.AppendLine();

            foreach (var task in group.Tasks)
            {
                sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
                sb.AppendLine($"📘 {Escape(task.Subject)}");
                sb.AppendLine($"🗓 {FormatDeadline(task.Deadline)}");

                if (!string.IsNullOrWhiteSpace(task.CreatedByName))
                    sb.AppendLine($"👤 {Escape(task.CreatedByName)}");

                sb.AppendLine();
            }
        }

        sb.Append("Открыть списки: /homework и /plan");
        return sb.ToString();
    }

    private static string BuildGroupReminderText(
        IEnumerable<StudyTask> tasks,
        DateTime today,
        IReadOnlyCollection<GroupParticipant> participants)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"⏰ <b>Актуальные ДЗ на {today:dd.MM.yyyy}</b>");
        sb.AppendLine();

        foreach (var task in tasks)
        {
            sb.AppendLine($"📌 <b>{Escape(task.Title)}</b>");
            sb.AppendLine($"📚 {Escape(task.Subject)}");
            sb.AppendLine($"🗓 {FormatDeadline(task.Deadline)}");

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

    private static string FormatDeadline(DateTime? deadline)
    {
        if (!deadline.HasValue)
            return "без дедлайна";

        return deadline.Value.TimeOfDay == TimeSpan.Zero
            ? deadline.Value.ToString("dd.MM.yyyy")
            : deadline.Value.ToString("dd.MM.yyyy HH:mm");
    }

    private static bool IsReminderDayEnabled(
        ReminderScheduleMode mode,
        IReadOnlyCollection<int> selectedDays,
        DateTime date)
    {
        return mode switch
        {
            ReminderScheduleMode.Weekdays => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
            ReminderScheduleMode.CustomDays => selectedDays.Contains(ScheduleCatalogService.GetDayNumber(date)),
            _ => true
        };
    }

    private static bool IsScheduledTimeReached(DateTime now, int hour, int minute)
    {
        if (now.Hour > hour)
            return true;

        return now.Hour == hour && now.Minute >= minute;
    }

    private static bool HasAlreadySentScheduledReminder(
        DateTime? lastNotificationAt,
        DateTime today,
        int hour,
        int minute)
    {
        if (!lastNotificationAt.HasValue || lastNotificationAt.Value.Date != today)
            return false;

        var scheduledAt = today.AddHours(hour).AddMinutes(minute);
        return lastNotificationAt.Value >= scheduledAt || lastNotificationAt.Value.TimeOfDay == TimeSpan.Zero;
    }

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);

    private async Task ReplacePinnedReminderAsync(
        GroupReminderSettings settings,
        Message reminderMessage,
        CancellationToken ct)
    {
        if (settings.PinnedReminderMessageId.HasValue)
        {
            try
            {
                await _bot.UnpinChatMessage(
                    chatId: settings.ChatId,
                    messageId: settings.PinnedReminderMessageId.Value,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Не удалось снять прошлый закреп напоминания {MessageId} в чате {ChatId}",
                    settings.PinnedReminderMessageId.Value,
                    settings.ChatId);
            }
        }

        try
        {
            await _bot.PinChatMessage(
                chatId: settings.ChatId,
                messageId: reminderMessage.MessageId,
                disableNotification: false,
                cancellationToken: ct);

            _groupReminders.SetPinnedReminderMessageId(settings.ChatId, reminderMessage.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Не удалось закрепить напоминание {MessageId} в чате {ChatId}",
                reminderMessage.MessageId,
                settings.ChatId);
        }
    }

    private static IReadOnlyList<GroupParticipant> FilterSelectedParticipants(
        IReadOnlyList<GroupParticipant> participants,
        GroupReminderSettings settings)
    {
        if (settings.SelectedParticipantUserIds.Count == 0)
            return participants;

        var selectedIds = settings.SelectedParticipantUserIds.ToHashSet();
        return participants
            .Where(participant => selectedIds.Contains(participant.UserId))
            .ToList();
    }

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
