using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramStudentBot.Handlers;

internal static class ScheduleKeyboards
{
    public static readonly InlineKeyboardMarkup Confirmation = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "sched_confirm"),
            InlineKeyboardButton.WithCallbackData("✏️ Исправить", "sched_edit"),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔎 Проверить по парам", "sched_review"),
        }
    });

    public static readonly InlineKeyboardMarkup WeekChoice = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("1️⃣ Нечётная (1-я)", "week_1"),
            InlineKeyboardButton.WithCallbackData("2️⃣ Чётная (2-я)", "week_2"),
        }
    });

    public static InlineKeyboardMarkup CreateSubGroupChoice(IReadOnlyList<int>? subGroups)
    {
        var values = (subGroups ?? Array.Empty<int>())
            .Where(v => v > 0)
            .Distinct()
            .Take(2)
            .ToList();

        if (values.Count == 0)
            values.AddRange([1, 2]);
        else if (values.Count == 1)
            values.Add(values[0] + 1);

        return new InlineKeyboardMarkup(new[]
        {
            values
                .Select(v => InlineKeyboardButton.WithCallbackData($"Подгруппа {v}", $"subgroup_{v}"))
                .ToArray()
        });
    }

    public static readonly InlineKeyboardMarkup ReviewSlotChoice = new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Верно", "review_ok"),
            InlineKeyboardButton.WithCallbackData("✏️ Изменить", "review_edit"),
        }
    });
}
