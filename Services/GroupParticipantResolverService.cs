using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TL;
using Telegram.Bot;
using TelegramStudentBot.Models;
using WTelegram;

namespace TelegramStudentBot.Services;

public sealed class GroupParticipantResolverService : IDisposable
{
    private const int MaxBatchLength = 700;
    private const int MaxMentionsPerBatch = 6;
    private const long SupergroupBotApiPrefix = -1000000000000L;

    private readonly ITelegramBotClient _bot;
    private readonly GroupParticipantStorageService _storage;
    private readonly ILogger<GroupParticipantResolverService> _logger;
    private readonly SemaphoreSlim _mtProtoLock = new(1, 1);
    private readonly string _botToken;
    private readonly string? _apiId;
    private readonly string? _apiHash;
    private readonly string _sessionPath;
    private readonly bool _useMtProtoParticipants;

    private Client? _mtProtoClient;
    private bool _mtProtoInitializationFailed;
    private bool _missingConfigLogged;
    private bool _mtProtoFailureLogged;
    private bool _disposed;

    public GroupParticipantResolverService(
        ITelegramBotClient bot,
        GroupParticipantStorageService storage,
        ILogger<GroupParticipantResolverService> logger,
        IConfiguration configuration)
    {
        _bot = bot;
        _storage = storage;
        _logger = logger;
        _botToken = Sanitize(configuration["BotToken"]);
        _apiId = FirstNonEmpty(configuration["TelegramApi:ApiId"], configuration["TELEGRAM_API_ID"]);
        _apiHash = FirstNonEmpty(configuration["TelegramApi:ApiHash"], configuration["TELEGRAM_API_HASH"]);
        _sessionPath = ResolveSessionPath(configuration);
        _useMtProtoParticipants = configuration.GetValue<bool?>("TelegramApi:UseMtProtoParticipants") ?? true;
    }

    public async Task<IReadOnlyList<GroupParticipant>> ResolveCallParticipantsAsync(
        long chatId,
        string? chatTitle,
        long? excludeUserId,
        CancellationToken ct)
    {
        return await ResolveParticipantsAsync(chatId, chatTitle, excludeUserId, ct);
    }

    public async Task<IReadOnlyList<GroupParticipant>> ResolveReminderParticipantsAsync(
        long chatId,
        string? chatTitle,
        CancellationToken ct)
    {
        return await ResolveParticipantsAsync(chatId, chatTitle, excludeUserId: null, ct);
    }

    public List<string> BuildMentionBatches(IReadOnlyList<GroupParticipant> participants)
    {
        var batches = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (var participant in participants)
        {
            var mention = BuildMention(participant);
            var separatorLength = current.Count == 0 ? 0 : 1;

            if (current.Count > 0 &&
                (currentLength + separatorLength + mention.Length > MaxBatchLength ||
                 current.Count >= MaxMentionsPerBatch))
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _mtProtoClient?.Dispose();
        _mtProtoLock.Dispose();
    }

    private async Task<IReadOnlyList<GroupParticipant>> ResolveParticipantsAsync(
        long chatId,
        string? chatTitle,
        long? excludeUserId,
        CancellationToken ct)
    {
        var mtProtoParticipants = await TryResolveParticipantsFromMtProtoAsync(chatId, chatTitle, ct);
        if (mtProtoParticipants.Count > 0)
        {
            return FilterAndSort(mtProtoParticipants, excludeUserId);
        }

        await SeedKnownParticipantsFromAdminsAsync(chatId, chatTitle, ct);
        var activeKnownParticipants = await GetActiveKnownParticipantsAsync(chatId, excludeUserId, ct);
        return FilterAndSort(activeKnownParticipants, excludeUserId);
    }

    private async Task<List<GroupParticipant>> TryResolveParticipantsFromMtProtoAsync(
        long chatId,
        string? chatTitle,
        CancellationToken ct)
    {
        var client = await GetMtProtoClientAsync(ct);
        if (client is null)
            return new List<GroupParticipant>();

        try
        {
            var chats = await client.Messages_GetAllChats();

            foreach (var (_, chat) in chats.chats)
            {
                switch (chat)
                {
                    case Channel channel when ToBotApiChatId(channel) == chatId:
                        return await ResolveChannelParticipantsAsync(client, channel, chatId, chatTitle, ct);

                    case Chat basicChat when ToBotApiChatId(basicChat) == chatId:
                        return await ResolveBasicChatParticipantsAsync(client, basicChat, chatId, chatTitle);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_mtProtoFailureLogged)
            {
                _logger.LogWarning(ex, "Не удалось получить участников через MTProto для чата {ChatId}", chatId);
                _mtProtoFailureLogged = true;
            }
        }

        return new List<GroupParticipant>();
    }

    private async Task<List<GroupParticipant>> ResolveChannelParticipantsAsync(
        Client client,
        Channel channel,
        long chatId,
        string? chatTitle,
        CancellationToken ct)
    {
        var channelRef = new InputChannel(channel.id, channel.access_hash);
        var result = await client.Channels_GetAllParticipants(channelRef, cancellationToken: ct);
        var participantIds = result.participants
            .Select(GetChannelParticipantUserId)
            .Where(id => id > 0)
            .ToHashSet();

        return BuildParticipantsFromUsers(
            chatId,
            chatTitle,
            result.users.Values.OfType<User>(),
            participantIds);
    }

    private async Task<List<GroupParticipant>> ResolveBasicChatParticipantsAsync(
        Client client,
        Chat chat,
        long chatId,
        string? chatTitle)
    {
        var result = await client.Messages_GetFullChat(chat.id);
        if (result.full_chat is not ChatFull fullChat ||
            fullChat.participants is not ChatParticipants participants)
        {
            return new List<GroupParticipant>();
        }

        var participantIds = participants.participants
            .Select(GetBasicChatParticipantUserId)
            .Where(id => id > 0)
            .ToHashSet();

        return BuildParticipantsFromUsers(
            chatId,
            chatTitle,
            result.users.Values.OfType<User>(),
            participantIds);
    }

    private List<GroupParticipant> BuildParticipantsFromUsers(
        long chatId,
        string? chatTitle,
        IEnumerable<User> users,
        HashSet<long> participantIds)
    {
        var resolvedAt = DateTime.Now;
        var participants = new List<GroupParticipant>();

        foreach (var user in users)
        {
            if (!participantIds.Contains(user.id))
                continue;

            var participant = new GroupParticipant
            {
                UserId = user.id,
                Nickname = BuildNickname(user),
                Username = NormalizeUsername(user.username),
                IsBot = user.flags.HasFlag(User.Flags.bot),
                LastSeenAt = resolvedAt
            };

            participants.Add(participant);
            _storage.Upsert(chatId, chatTitle, participant);
        }

        return participants;
    }

    private async Task<List<GroupParticipant>> GetActiveKnownParticipantsAsync(
        long chatId,
        long? excludeUserId,
        CancellationToken ct)
    {
        var knownParticipants = _storage.Get(chatId)
            .Where(participant => !participant.IsBot)
            .Where(participant => participant.UserId != excludeUserId)
            .GroupBy(participant => participant.UserId)
            .Select(group => group.OrderByDescending(item => item.LastSeenAt).First())
            .OrderByDescending(participant => participant.LastSeenAt)
            .ToList();

        var activeParticipants = new List<GroupParticipant>();
        foreach (var participant in knownParticipants)
        {
            if (await IsActiveChatMemberAsync(chatId, participant.UserId, ct))
                activeParticipants.Add(participant);
        }

        return activeParticipants;
    }

    private async Task<bool> IsActiveChatMemberAsync(long chatId, long userId, CancellationToken ct)
    {
        try
        {
            var member = await _bot.GetChatMember(chatId, userId, ct);
            return member is Telegram.Bot.Types.ChatMemberMember or
                Telegram.Bot.Types.ChatMemberAdministrator or
                Telegram.Bot.Types.ChatMemberOwner or
                Telegram.Bot.Types.ChatMemberRestricted;
        }
        catch
        {
            return false;
        }
    }

    private async Task SeedKnownParticipantsFromAdminsAsync(long chatId, string? chatTitle, CancellationToken ct)
    {
        try
        {
            var admins = await _bot.GetChatAdministrators(chatId, ct);
            foreach (var admin in admins)
                _storage.Upsert(chatId, chatTitle, admin.User);
        }
        catch
        {
            // Admin list is only a fallback source for mentions.
        }
    }

    private async Task<Client?> GetMtProtoClientAsync(CancellationToken ct)
    {
        if (!_useMtProtoParticipants)
            return null;

        if (string.IsNullOrWhiteSpace(_botToken) ||
            string.IsNullOrWhiteSpace(_apiId) ||
            string.IsNullOrWhiteSpace(_apiHash))
        {
            if (!_missingConfigLogged)
            {
                _logger.LogInformation("MTProto-резолвер участников пропущен: не заданы TelegramApi:ApiId/ApiHash.");
                _missingConfigLogged = true;
            }

            return null;
        }

        if (_mtProtoInitializationFailed)
            return null;

        await _mtProtoLock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            if (_mtProtoClient is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
                _mtProtoClient = new Client(GetMtProtoConfigValue);
            }

            await _mtProtoClient.LoginBotIfNeeded(_botToken);
            return _mtProtoClient;
        }
        catch (Exception ex)
        {
            _mtProtoInitializationFailed = true;

            if (!_mtProtoFailureLogged)
            {
                _logger.LogWarning(ex, "Не удалось инициализировать MTProto-клиент для загрузки участников.");
                _mtProtoFailureLogged = true;
            }

            _mtProtoClient?.Dispose();
            _mtProtoClient = null;
            return null;
        }
        finally
        {
            _mtProtoLock.Release();
        }
    }

    private string? GetMtProtoConfigValue(string key)
    {
        return key switch
        {
            "api_id" => _apiId,
            "api_hash" => _apiHash,
            "bot_token" => _botToken,
            "session_pathname" => _sessionPath,
            _ => null
        };
    }

    private static IReadOnlyList<GroupParticipant> FilterAndSort(
        IEnumerable<GroupParticipant> participants,
        long? excludeUserId)
    {
        return participants
            .Where(participant => !participant.IsBot)
            .Where(participant => participant.UserId != excludeUserId)
            .GroupBy(participant => participant.UserId)
            .Select(group => group.OrderByDescending(item => item.LastSeenAt).First())
            .OrderByDescending(participant => participant.LastSeenAt)
            .ThenBy(participant => participant.Nickname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildMention(GroupParticipant participant)
    {
        var label = string.IsNullOrWhiteSpace(participant.Username)
            ? participant.Nickname
            : participant.Username!;

        return $"<a href=\"tg://user?id={participant.UserId}\">{Escape(label)}</a>";
    }

    private static long GetChannelParticipantUserId(ChannelParticipantBase participant)
    {
        return participant switch
        {
            ChannelParticipant item => item.user_id,
            ChannelParticipantSelf item => item.user_id,
            ChannelParticipantCreator item => item.user_id,
            ChannelParticipantAdmin item => item.user_id,
            _ => 0
        };
    }

    private static long GetBasicChatParticipantUserId(ChatParticipantBase participant)
    {
        return participant switch
        {
            ChatParticipant item => item.user_id,
            _ => 0
        };
    }

    private static long ToBotApiChatId(Channel channel)
        => SupergroupBotApiPrefix - channel.id;

    private static long ToBotApiChatId(Chat chat)
        => -chat.id;

    private static string BuildNickname(User user)
    {
        var parts = new[] { user.first_name, user.last_name }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
            return string.Join(" ", parts);

        if (!string.IsNullOrWhiteSpace(user.username))
            return NormalizeUsername(user.username)!;

        return "Участник";
    }

    private static string? NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        return username.StartsWith('@') ? username : $"@{username}";
    }

    private static string ResolveSessionPath(IConfiguration configuration)
    {
        var configured = FirstNonEmpty(
            configuration["TelegramApi:SessionPathname"],
            configuration["TELEGRAM_API_SESSION"]);

        return string.IsNullOrWhiteSpace(configured)
            ? UserDataPath.ResolveFile("wtelegram-bot.session")
            : configured;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string Escape(string text)
        => WebUtility.HtmlEncode(text);
}
