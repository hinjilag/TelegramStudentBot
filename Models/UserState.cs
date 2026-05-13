namespace TelegramStudentBot.Models;

public enum UserState
{
    Idle,
    WaitingForTaskTitle,
    WaitingForTaskSubject,
    WaitingForTaskDeadline,
    WaitingForTaskDeadlineTime,
    WaitingForHomeworkText,
    WaitingForGroupHomeworkEntry,
    WaitingForTimerMinutes,
    WaitingForReminderTime,
    WaitingForGroupParticipantUsernames,
    WaitingForSchedulePhoto,
    WaitingForScheduleConfirmation,
    WaitingForScheduleCorrection,
    WaitingForScheduleReview,
    WaitingForReviewSlotCorrection,
    WaitingForWeekChoice,
}
