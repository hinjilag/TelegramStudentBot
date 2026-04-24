using System.Net;
using System.Text;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Handlers;

internal static class PlanListView
{
    private const int ActiveLimit = 10;
    private const int CompletedLimit = 10;

    internal static (string Text, InlineKeyboardMarkup? Keyboard) Build(UserSession session)
    {
        var active = GetActiveTasks(session);
        var completedCount = session.Tasks.Count(t => t.IsCompleted && TaskSubjects.IsPersonal(t.Subject));

        var sb = new StringBuilder();
        sb.AppendLine("📋 <b>Личный план</b>");
        sb.AppendLine($"Активных: <b>{active.Count}</b> | Выполнено: <b>{completedCount}</b>");

        if (active.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Активных дел нет.");
        }
        else
        {
            sb.AppendLine();

            for (var i = 0; i < Math.Min(active.Count, ActiveLimit); i++)
            {
                var task = active[i];
                sb.AppendLine($"{i + 1}. 📌 <b>{Escape(task.Title)}</b>{FormatTaskUrgency(task)}");
                sb.AppendLine($"   📅 {FormatDeadline(task.Deadline)}");

                if (i < Math.Min(active.Count, ActiveLimit) - 1)
                    sb.AppendLine();
            }

            if (active.Count > ActiveLimit)
            {
                sb.AppendLine();
                sb.AppendLine($"... и ещё {active.Count - ActiveLimit} дел(а).");
            }
        }

        return (sb.ToString().TrimEnd(), BuildKeyboard(active.Count, completedCount));
    }

    internal static (string Text, InlineKeyboardMarkup Keyboard) BuildCompleted(UserSession session)
    {
        var completed = session.Tasks
            .Where(t => t.IsCompleted && TaskSubjects.IsPersonal(t.Subject))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("✅ <b>Выполненные дела</b>");
        sb.AppendLine($"Всего: <b>{completed.Count}</b>");

        if (completed.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Выполненных дел пока нет.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Показываю последние 10 выполненных дел.");
            sb.AppendLine();

            foreach (var task in completed.Take(CompletedLimit))
                sb.AppendLine($"✅ <b>{Escape(task.Title)}</b>\n   📅 {FormatDeadline(task.Deadline)}");

            if (completed.Count > CompletedLimit)
            {
                sb.AppendLine();
                sb.AppendLine($"Показаны первые {CompletedLimit}. Ещё: {completed.Count - CompletedLimit}.");
            }
        }

        return (
            sb.ToString().TrimEnd(),
            new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "plan_back")
                }
            }));
    }

    internal static (string Text, InlineKeyboardMarkup? Keyboard) BuildTaskChoice(UserSession session, string action)
    {
        var active = GetActiveTasks(session);
        if (active.Count == 0)
            return (Build(session).Text, null);

        var actionText = action == "del" ? "удалить" : "отметить выполненным";
        var buttonPrefix = action == "del" ? "🗑" : "✅";
        var callbackAction = action == "del" ? "del" : "done";
        var rows = active
            .Take(ActiveLimit)
            .Select((task, index) => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{buttonPrefix} {index + 1}. {TrimButtonText(task.Title)}",
                    $"plan_{callbackAction}_{task.ShortId}")
            })
            .Append(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Назад", "plan_back")
            });

        var text = new StringBuilder();
        text.AppendLine($"Выбери дело, которое нужно {actionText}:");
        text.AppendLine();

        for (var i = 0; i < Math.Min(active.Count, ActiveLimit); i++)
            text.AppendLine($"{i + 1}. <b>{Escape(active[i].Title)}</b>");

        if (active.Count > ActiveLimit)
        {
            text.AppendLine();
            text.AppendLine($"Показаны первые {ActiveLimit}. Ещё: {active.Count - ActiveLimit}.");
        }

        return (text.ToString().TrimEnd(), new InlineKeyboardMarkup(rows));
    }

    internal static (string Text, InlineKeyboardMarkup Keyboard) BuildDeleteConfirmation(StudyTask task)
    {
        return (
            $"Точно удалить дело?\n\n📌 <b>{Escape(task.Title)}</b>",
            new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🗑 Да, удалить", $"plan_confirmdel_{task.ShortId}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "plan_choose_del")
                }
            }));
    }

    private static InlineKeyboardMarkup? BuildKeyboard(int activeCount, int completedCount)
    {
        if (activeCount == 0 && completedCount == 0)
            return null;

        var rows = new List<InlineKeyboardButton[]>();

        if (activeCount > 0)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполнить дело", "plan_choose_done")
            });
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить дело", "plan_choose_del")
            });
        }

        if (completedCount > 0)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполненные", "plan_completed")
            });
        }

        return new InlineKeyboardMarkup(rows);
    }

    private static List<StudyTask> GetActiveTasks(UserSession session)
        => session.Tasks
            .Where(t => !t.IsCompleted && TaskSubjects.IsPersonal(t.Subject))
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

    private static string FormatTaskUrgency(StudyTask task)
    {
        if (!task.Deadline.HasValue)
            return string.Empty;

        var days = (task.Deadline.Value.Date - DateTime.Today).Days;
        return days switch
        {
            < 0 => " 🔴 <b>Просрочено!</b>",
            0 => " 🟡 <b>Сегодня</b>",
            1 => " 🟡 Завтра",
            <= 3 => $" 🟠 Через {days} дня",
            _ => $" ✅ Через {days} дней"
        };
    }

    private static string FormatDeadline(DateTime? deadline)
    {
        if (!deadline.HasValue)
            return "без дедлайна";

        return deadline.Value.TimeOfDay == TimeSpan.Zero
            ? deadline.Value.ToString("dd.MM.yyyy")
            : deadline.Value.ToString("dd.MM.yyyy HH:mm");
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string TrimButtonText(string text)
        => text.Length <= 30 ? text : text[..27] + "...";
}
