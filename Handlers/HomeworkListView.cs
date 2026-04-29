using System.Net;
using System.Text;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Handlers;

internal static class HomeworkListView
{
    private const int ActiveLimit = 10;
    private const int CompletedLimit = 10;

    internal static (string Text, InlineKeyboardMarkup? Keyboard) Build(
        UserSession session,
        string? emptyText = null)
    {
        var active = session.Tasks
            .Where(t => !t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var completed = session.Tasks
            .Where(t => t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        if (active.Count == 0 && completed.Count == 0)
        {
            return (
                emptyText ?? "📚 <b>Домашних заданий и задач пока нет.</b>\nДобавить ДЗ можно через /add_homework.",
                null);
        }

        var sb = new StringBuilder();
        BuildTaskSummary(sb, active, completed, "📚 <b>Домашние задания и задачи</b>", includeAuthor: false);
        return (sb.ToString().TrimEnd(), BuildKeyboard(active, completed.Count));
    }

    internal static (string Text, InlineKeyboardMarkup? Keyboard) BuildGroup(
        string chatTitle,
        IReadOnlyCollection<StudyTask> tasks)
    {
        var active = tasks
            .Where(t => !t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var completed = tasks
            .Where(t => t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        if (active.Count == 0 && completed.Count == 0)
        {
            return (
                $"📚 <b>Общие домашние задания</b>\nЧат: <b>{Escape(chatTitle)}</b>\n\nПока пусто. Добавить можно через /add_homework.",
                null);
        }

        var sb = new StringBuilder();
        BuildTaskSummary(sb, active, completed, $"📚 <b>Общие домашние задания</b>\nЧат: <b>{Escape(chatTitle)}</b>", includeAuthor: true);
        return (sb.ToString().TrimEnd(), BuildGroupKeyboard(active.Count));
    }

    internal static (string Text, InlineKeyboardMarkup? Keyboard) BuildGroupDeleteChoice(
        string chatTitle,
        IReadOnlyCollection<StudyTask> tasks)
    {
        var active = tasks
            .Where(t => !t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderBy(t => t.Deadline ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        if (active.Count == 0)
            return BuildGroup(chatTitle, tasks);

        var rows = active
            .Take(ActiveLimit)
            .Select((task, index) => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"🗑 {index + 1}. {TrimButtonText(task.Title)}",
                    $"task_group_del_{task.ShortId}")
            })
            .Append(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Назад", "task_group_back")
            });

        var text = new StringBuilder();
        text.AppendLine("🗑 <b>Удаление общего ДЗ</b>");
        text.AppendLine($"Чат: <b>{Escape(chatTitle)}</b>");
        text.AppendLine();
        text.AppendLine("Выбери задание, которое нужно удалить:");
        text.AppendLine();

        for (var i = 0; i < Math.Min(active.Count, ActiveLimit); i++)
        {
            var task = active[i];
            var deadlineText = task.Deadline.HasValue
                ? task.Deadline.Value.ToString("dd.MM.yyyy")
                : "без дедлайна";

            text.AppendLine($"{i + 1}. <b>{Escape(task.Title)}</b>");
            text.AppendLine($"   📚 {Escape(task.Subject)}");
            text.AppendLine($"   📅 {deadlineText}");

            if (!string.IsNullOrWhiteSpace(task.CreatedByName))
                text.AppendLine($"   👤 {Escape(task.CreatedByName)}");

            if (i < Math.Min(active.Count, ActiveLimit) - 1)
                text.AppendLine();
        }

        if (active.Count > ActiveLimit)
        {
            text.AppendLine();
            text.AppendLine($"Показаны первые {ActiveLimit}. Ещё: {active.Count - ActiveLimit}.");
        }

        return (text.ToString().TrimEnd(), new InlineKeyboardMarkup(rows));
    }

    internal static (string Text, InlineKeyboardMarkup Keyboard) BuildCompleted(UserSession session)
    {
        var completed = session.Tasks
            .Where(t => t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("✅ <b>Выполненные задания</b>");
        sb.AppendLine($"Всего: <b>{completed.Count}</b>");

        if (completed.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Выполненных заданий пока нет.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Показываю последние 10 выполненных задач.");
            sb.AppendLine();

            foreach (var task in completed.Take(CompletedLimit))
                sb.AppendLine($"✅ <b>{Escape(task.Title)}</b>\n   📚 {Escape(task.Subject)}");

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
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "task_back")
                }
            }));
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

    internal static (string Text, InlineKeyboardMarkup Keyboard) BuildGroupDeleteConfirmation(StudyTask task)
    {
        var deadlineText = task.Deadline.HasValue
            ? task.Deadline.Value.ToString("dd.MM.yyyy")
            : "без дедлайна";

        var authorLine = string.IsNullOrWhiteSpace(task.CreatedByName)
            ? string.Empty
            : $"\n👤 {Escape(task.CreatedByName)}";

        return (
            "Точно удалить это общее ДЗ?\n\n" +
            $"<b>{Escape(task.Title)}</b>\n" +
            $"📚 {Escape(task.Subject)}\n" +
            $"📅 {deadlineText}" +
            authorLine,
            new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🗑 Да, удалить", $"task_group_confirmdel_{task.ShortId}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "task_group_choose_del")
                }
            }));
    }

    private static InlineKeyboardMarkup? BuildKeyboard(List<StudyTask> active, int completedCount)
    {
        if (active.Count == 0 && completedCount == 0)
            return null;

        var rows = new List<InlineKeyboardButton[]>();

        if (active.Count > 0)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполнить задачу", "task_choose_done")
            });
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить задачу", "task_choose_del")
            });
        }

        if (completedCount > 0)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Выполненные", "task_completed")
            });
        }

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup? BuildGroupKeyboard(int activeCount)
    {
        if (activeCount == 0)
            return null;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить ДЗ", "task_group_choose_del")
            }
        });
    }

    private static List<StudyTask> GetActiveTasks(UserSession session)
    {
        return session.Tasks
            .Where(t => !t.IsCompleted && !TaskSubjects.IsPersonal(t.Subject))
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

    private static void BuildTaskSummary(
        StringBuilder sb,
        List<StudyTask> active,
        List<StudyTask> completed,
        string title,
        bool includeAuthor)
    {
        sb.AppendLine(title);
        sb.AppendLine($"Активных: <b>{active.Count}</b> | Выполнено: <b>{completed.Count}</b>");

        if (active.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Активных ДЗ и задач нет.");
            return;
        }

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

            if (includeAuthor && !string.IsNullOrWhiteSpace(task.CreatedByName))
                sb.AppendLine($"   👤 {Escape(task.CreatedByName)}");

            if (i < Math.Min(active.Count, ActiveLimit) - 1)
                sb.AppendLine();
        }

        if (active.Count > ActiveLimit)
        {
            sb.AppendLine();
            sb.AppendLine($"... и ещё {active.Count - ActiveLimit} задач(и).");
        }
    }
}
