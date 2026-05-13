using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

public class UserGroupScheduleBridgeService
{
    private readonly GroupParticipantStorageService _groupParticipants;
    private readonly UserScheduleSelectionService _scheduleSelections;
    private readonly ScheduleCatalogService _scheduleCatalog;

    public UserGroupScheduleBridgeService(
        GroupParticipantStorageService groupParticipants,
        UserScheduleSelectionService scheduleSelections,
        ScheduleCatalogService scheduleCatalog)
    {
        _groupParticipants = groupParticipants;
        _scheduleSelections = scheduleSelections;
        _scheduleCatalog = scheduleCatalog;
    }

    public bool TryGetLinkedSchedule(
        long userId,
        out ScheduleGroup? group,
        out int? subGroup,
        out long chatId,
        out string? chatTitle)
    {
        group = null;
        subGroup = null;
        chatId = 0;
        chatTitle = null;

        var memberships = _groupParticipants.GetChatsForUser(userId);
        foreach (var membership in memberships)
        {
            var selection = _scheduleSelections.Get(membership.ChatId);
            if (selection is null)
                continue;

            var linkedGroup = _scheduleCatalog.GetGroup(selection.ScheduleId);
            if (linkedGroup is null)
                continue;

            group = linkedGroup;
            subGroup = selection.SubGroup;
            chatId = membership.ChatId;
            chatTitle = membership.ChatTitle;
            return true;
        }

        return false;
    }
}
