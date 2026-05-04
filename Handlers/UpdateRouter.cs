using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramStudentBot.Models;
using TelegramStudentBot.Services;

namespace TelegramStudentBot.Handlers;

public class UpdateRouter
{
    private readonly CommandHandler _commands;
    private readonly TextHandler _text;
    private readonly CallbackHandler _callbacks;
    private readonly SessionService _sessions;
    private readonly BotIdentityService _botIdentity;
    private readonly GroupVisitLogService _groupVisits;
    private readonly UserProfileStorageService _userProfiles;
    private readonly StudyTaskStorageService _taskStorage;
    private readonly ReminderSettingsService _reminders;
    private readonly GroupParticipantStorageService _groupParticipants;
    private readonly HomeworkSubjectPreferencesService _homeworkSubjects;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ILogger<UpdateRouter> _logger;

    public UpdateRouter(
        CommandHandler commands,
        TextHandler text,
        CallbackHandler callbacks,
        SessionService sessions,
        BotIdentityService botIdentity,
        GroupVisitLogService groupVisits,
        UserProfileStorageService userProfiles,
        StudyTaskStorageService taskStorage,
        ReminderSettingsService reminders,
        GroupParticipantStorageService groupParticipants,
        HomeworkSubjectPreferencesService homeworkSubjects,
        UserScheduleSelectionService scheduleSelections,
        ILogger<UpdateRouter> logger)
    {
        _commands = commands;
        _text = text;
        _callbacks = callbacks;
        _sessions = sessions;
        _botIdentity = botIdentity;
        _groupVisits = groupVisits;
        _userProfiles = userProfiles;
        _taskStorage = taskStorage;
        _reminders = reminders;
        _groupParticipants = groupParticipants;
        _homeworkSubjects = homeworkSubjects;
        _scheduleSelections = scheduleSelections;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            RememberUser(update);

            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
            {
                await _callbacks.HandleAsync(update.CallbackQuery, ct);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message?.Text is not null)
            {
                await HandleMessageAsync(update.Message, ct);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message?.Photo is not null)
            {
                if (IsGroupChat(update.Message.Chat.Type))
                    return;

                await _text.HandlePhotoAsync(update.Message, ct);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message?.Document is not null)
            {
                if (IsGroupChat(update.Message.Chat.Type))
                    return;

                var mime = update.Message.Document.MimeType ?? string.Empty;
                if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    await _text.HandlePhotoAsync(update.Message, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error while processing update {UpdateType}", update.Type);
        }
    }

    private void RememberUser(Update update)
    {
        var user = update.Message?.From ?? update.CallbackQuery?.From;
        if (user is null)
            return;

        _userProfiles.Upsert(user);
        _taskStorage.SyncUserMetadata(user.Id);
        _reminders.SyncUserMetadata(user.Id);
        _homeworkSubjects.SyncUserMetadata(user.Id);
        _scheduleSelections.SyncUserMetadata(user.Id);

        var chat = update.Message?.Chat ?? update.CallbackQuery?.Message?.Chat;
        if (chat?.Type is ChatType.Group or ChatType.Supergroup)
            _groupParticipants.Upsert(chat.Id, chat.Title, user);
    }

    private async Task HandleMessageAsync(Message msg, CancellationToken ct)
    {
        var text = msg.Text!.Trim();
        if (TryGetCommand(msg, text, out var commandPart))
        {
            if (IsGroupChat(msg.Chat.Type))
            {
                var isFirstGroupVisit = _groupVisits.RegisterVisit(msg.Chat.Id, msg.Chat.Title);
                if (isFirstGroupVisit && commandPart != "/start")
                    await _commands.SendGroupWelcomeAsync(msg.Chat.Id, ct);
            }

            await RouteCommandAsync(msg, commandPart, ct);
            return;
        }

        if (IsGroupChat(msg.Chat.Type) && !ShouldHandleGroupText(msg))
            return;

        await _text.HandleAsync(msg, ct);
    }

    private bool TryGetCommand(Message msg, string text, out string command)
    {
        command = string.Empty;
        if (!text.StartsWith('/'))
            return false;

        var rawCommand = text.Split(' ')[0].Trim();
        if (string.IsNullOrWhiteSpace(rawCommand))
            return false;

        var mentionIndex = rawCommand.IndexOf('@');
        if (mentionIndex < 0)
        {
            command = rawCommand.ToLowerInvariant();
            return true;
        }

        var targetUsername = rawCommand[(mentionIndex + 1)..].TrimStart('@');
        var botUsername = _botIdentity.Username;
        if (string.IsNullOrWhiteSpace(botUsername) ||
            !string.Equals(targetUsername, botUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        command = rawCommand[..mentionIndex].ToLowerInvariant();
        return true;
    }

    private bool ShouldHandleGroupText(Message msg)
    {
        var session = _sessions.GetOrCreate(msg.From!.Id, msg.From.FirstName);

        return session.State switch
        {
            UserState.WaitingForHomeworkText => session.PendingGroupHomeworkChatId == msg.Chat.Id,
            UserState.WaitingForGroupHomeworkEntry => session.PendingGroupHomeworkChatId == msg.Chat.Id,
            UserState.WaitingForReminderTime => session.ReminderTargetIsGroup && session.ReminderTargetChatId == msg.Chat.Id,
            UserState.WaitingForScheduleCorrection => true,
            UserState.WaitingForReviewSlotCorrection => true,
            _ => false
        };
    }

    private static bool IsGroupChat(ChatType chatType)
        => chatType is ChatType.Group or ChatType.Supergroup;

    private async Task RouteCommandAsync(Message msg, string command, CancellationToken ct)
    {
        _logger.LogDebug("Command {Command} from user {UserId}", command, msg.From?.Id);

        switch (command)
        {
            case "/start":
                await _commands.HandleStartAsync(msg, ct);
                break;

            case "/help":
                await _commands.HandleHelpAsync(msg, ct);
                break;

            case "/miniapp":
                await _commands.HandleMiniAppAsync(msg, ct);
                break;

            case "/timer":
                await _commands.HandleTimerAsync(msg, ct);
                break;

            case "/rest":
                await _commands.HandleRestAsync(msg, ct);
                break;

            case "/stop":
                await _commands.HandleStopAsync(msg, ct);
                break;

            case "/plan":
                await _commands.HandlePlanAsync(msg, ct);
                break;

            case "/add_homework":
                await _commands.HandleAddHomeworkAsync(msg, ct);
                break;

            case "/homework_settings":
                await _commands.HandleHomeworkSettingsAsync(msg, ct);
                break;

            case "/homework":
                await _commands.HandleHomeworkAsync(msg, ct);
                break;

            case "/reminders":
                await _commands.HandleRemindersAsync(msg, ct);
                break;

            case "/schedule":
                await _commands.HandleScheduleAsync(msg, ct);
                break;

            case "/add_schedule":
                await _commands.HandleAddScheduleAsync(msg, ct);
                break;

            default:
                await _text.HandleAsync(msg, ct);
                break;
        }
    }

}
