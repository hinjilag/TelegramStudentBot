using System.Net;
using System.Text;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Handlers;

internal static class HomeworkListView
{
    private const int ActiveLimit = 10;
    private const int CompletedLimit = 5;

    internal static (string Text, InlineKeyboardMarkup? Keyboard) Build(
        UserSession session,
        string? emptyText = null)
    {
        var active = session.Tasks
            .Where(t => !t.IsCompleted)
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var completed = session.Tasks
            .Where(t => t.IsCompleted)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        if (session.Tasks.Count == 0)
        {
            return (
                emptyText ?? "📚 <b>Домашних заданий и задач пока нет.</b>\nДобавить ДЗ можно через /add_homework.",
                null);
        }

        var sb = new StringBuilder();
        sb.AppendLine("📚 <b>Домашние задания и задачи</b>");
        sb.AppendLine($"Активных: <b>{active.Count}</b> | Выполнено: <b>{completed.Count}</b>");

        if (active.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Активных ДЗ и задач нет.");
        }
        else
        {
            sb.AppendLine();

            for (var i = 0; i < Math.Min(active.Count, ActiveLimit); i++)
            {
                var task = active[i];
                var deadlineText = task.Deadline.HasValue
                    ? task.Deadline.Value.ToString("dd.MM.yyyy")
                    : "без дедлайна";

                sb.AppendLine($"{i + 1}. 📌 <b>{Escape(task.Title)}</b>{FormatTaskUrgency(task)}");
                sb.AppendLine($"   📚 {Escape(task.Subject)}");
                sb.AppendLine($"   📅 {deadlineText}");

                if (i < Math.Min(active.Count, ActiveLimit) - 1)
                    sb.AppendLine();
            }

            if (active.Count > ActiveLimit)
            {
                sb.AppendLine();
                sb.AppendLine($"... и ещё {active.Count - ActiveLimit} задач(и).");
            }
        }

        if (completed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>Выполнено:</b>");
            foreach (var task in completed.Take(CompletedLimit))
                sb.AppendLine($"✅ {Escape(task.Title)} ({Escape(task.Subject)})");

            if (completed.Count > CompletedLimit)
                sb.AppendLine($"... и ещё {completed.Count - CompletedLimit}");
        }

        return (sb.ToString().TrimEnd(), BuildKeyboard(active));
    }

    internal static (string Text, InlineKeyboardMarkup? Keyboard) BuildTaskChoice(UserSession session, string action)
    {
        var active = GetActiveTasks(session);
        if (active.Count == 0)
            return (Build(session).Text, null);

        var actionText = action == "del" ? "удалить" : "отметить выполненной";
        var buttonPrefix = action == "del" ? "🗑" : "✅";
        var rows = active
            .Take(ActiveLimit)
            .Select((task, index) => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{buttonPrefix} {index + 1}. {TrimButtonText(task.Title)}",
                    $"task_{action}_{task.ShortId}")
            })
            .Append(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Назад", "task_back")
            });

        var text = new StringBuilder();
        text.AppendLine($"Выбери задачу, которую нужно {actionText}:");
        text.AppendLine();

        for (var i = 0; i < Math.Min(active.Count, ActiveLimit); i++)
        {
            var task = active[i];
            text.AppendLine($"{i + 1}. <b>{Escape(task.Title)}</b>");
            text.AppendLine($"   📚 {Escape(task.Subject)}");
        }

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
            $"Точно удалить задачу?\n\n📌 <b>{Escape(task.Title)}</b>\n📚 {Escape(task.Subject)}",
            new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🗑 Да, удалить", $"task_confirmdel_{task.ShortId}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "task_choose_del")
                }
            }));
    }

    private static InlineKeyboardMarkup? BuildKeyboard(List<StudyTask> active)
    {
        if (active.Count == 0)
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполнить задачу", "task_choose_done")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить задачу", "task_choose_del")
            }
        });
    }

    private static List<StudyTask> GetActiveTasks(UserSession session)
    {
        return session.Tasks
            .Where(t => !t.IsCompleted)
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();
    }

    private static string FormatTaskUrgency(StudyTask task)
    {
        if (!task.Deadline.HasValue)
            return string.Empty;

        var days = (task.Deadline.Value.Date - DateTime.Today).Days;
        return days switch
        {
            < 0 => " 🔴 <b>Просрочено!</b>",
            0 => " 🟡 <b>Сдать сегодня!</b>",
            1 => " 🟡 Завтра",
            <= 3 => $" 🟠 Через {days} дня",
            _ => $" ✅ Через {days} дней"
        };
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string TrimButtonText(string text)
        => text.Length <= 30 ? text : text[..27] + "...";
}
