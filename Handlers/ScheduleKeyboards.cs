using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramStudentBot.Handlers;

internal static class ScheduleKeyboards
{
    public static readonly InlineKeyboardMarkup ScheduleMenu = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Сегодня", "sched_today"),
            InlineKeyboardButton.WithCallbackData("Вся неделя", "sched_week"),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Изменить группу", "sched_change"),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Удалить расписание", "sched_delete"),
        }
    });

    public static readonly InlineKeyboardMarkup DeleteConfirmation = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Да, удалить", "sched_delete_yes"),
            InlineKeyboardButton.WithCallbackData("Отмена", "sched_delete_no"),
        }
    });

    public static readonly InlineKeyboardMarkup Confirmation = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Подтвердить", "sched_confirm"),
            InlineKeyboardButton.WithCallbackData("Исправить", "sched_edit"),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Проверить по парам", "sched_review"),
        }
    });

    public static readonly InlineKeyboardMarkup WeekChoice = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Нечётная (1-я)", "week_1"),
            InlineKeyboardButton.WithCallbackData("Чётная (2-я)", "week_2"),
        }
    });

    public static readonly InlineKeyboardMarkup ReviewSlotChoice = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("Верно", "review_ok"),
            InlineKeyboardButton.WithCallbackData("Изменить", "review_edit"),
        }
    });

    public static InlineKeyboardMarkup SingleColumn(
        IEnumerable<(string Text, string CallbackData)> buttons)
    {
        return new InlineKeyboardMarkup(
            buttons.Select(button => new[]
            {
                InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData)
            }));
    }
}
