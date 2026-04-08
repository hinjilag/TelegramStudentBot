using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramStudentBot.Handlers;

/// <summary>
/// Статические клавиатуры для диалога загрузки расписания.
/// Используются как в TextHandler, так и в CallbackHandler.
/// </summary>
internal static class ScheduleKeyboards
{
    /// <summary>Кнопки "Подтвердить" / "Исправить" после показа распознанного расписания</summary>
    public static readonly InlineKeyboardMarkup Confirmation = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "sched_confirm"),
            InlineKeyboardButton.WithCallbackData("✏️ Исправить",  "sched_edit"),
        }
    });

    /// <summary>Кнопки выбора текущей недели (после подтверждения расписания с двойными парами)</summary>
    public static readonly InlineKeyboardMarkup WeekChoice = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("1️⃣ Нечётная (1-я)", "week_1"),
            InlineKeyboardButton.WithCallbackData("2️⃣ Чётная (2-я)",   "week_2"),
        }
    });
}
