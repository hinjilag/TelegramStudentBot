using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramStudentBot.Models;

namespace TelegramStudentBot.Services;

/// <summary>
/// Сервис управления таймерами учёбы и отдыха.
/// Запускает фоновые задачи, которые по истечении времени
/// отправляют пользователю уведомление и обновляют усталость.
/// </summary>
public class TimerService
{
    private readonly ITelegramBotClient _bot;
    private readonly SessionService _sessions;
    private readonly ILogger<TimerService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string? _webAppUrl;
    private readonly string? _webAppStopUrl;

    public TimerService(
        ITelegramBotClient bot,
        SessionService sessions,
        ILogger<TimerService> logger,
        IHostApplicationLifetime lifetime,
        IConfiguration config)
    {
        _bot       = bot;
        _sessions  = sessions;
        _logger    = logger;
        _lifetime  = lifetime;
        _webAppUrl = config["WebAppUrl"]?.TrimEnd('/');
        _webAppStopUrl = config["WebAppStopUrl"]?.TrimEnd('/');
    }

    // ──────────────────────────────────────────────
    //  Публичные методы
    // ──────────────────────────────────────────────

    /// <summary>Запустить рабочий таймер для пользователя</summary>
    /// <param name="chatId">ID чата для отправки уведомлений</param>
    /// <param name="userId">ID пользователя</param>
    /// <param name="minutes">Длительность в минутах</param>
    public async Task StartWorkTimerAsync(long chatId, long userId, int minutes)
    {
        var session = _sessions.GetOrCreate(userId);

        // Если уже идёт таймер — отменяем его без уведомления
        CancelActiveTimer(session);

        var timer = new ActiveTimer
        {
            Type            = TimerType.Work,
            DurationMinutes = minutes
        };
        session.ActiveTimer = timer;

        var endTime = timer.EndsAt.ToString("HH:mm");
        await _bot.SendMessage(
            chatId:           chatId,
            text:             $"⏱ <b>Рабочий таймер запущен!</b>\n" +
                              $"Длительность: <b>{minutes} мин</b>\n" +
                              $"Завершится в: <b>{endTime}</b>\n\n" +
                              $"Сосредоточься и не отвлекайся 💪",
            parseMode:        ParseMode.Html,
            replyMarkup:      BuildWebAppButton(timer, chatId, userId),
            cancellationToken: CancellationToken.None);

        // Запускаем фоновое ожидание — не блокируем вызывающий поток
        _ = RunTimerAsync(chatId, userId, timer);

        _logger.LogInformation("Запущен рабочий таймер {Minutes} мин для пользователя {UserId}", minutes, userId);
    }

    /// <summary>Запустить таймер отдыха для пользователя</summary>
    /// <param name="chatId">ID чата для отправки уведомлений</param>
    /// <param name="userId">ID пользователя</param>
    /// <param name="minutes">Длительность отдыха в минутах</param>
    public async Task StartRestTimerAsync(long chatId, long userId, int minutes)
    {
        var session = _sessions.GetOrCreate(userId);

        // Если уже идёт таймер — отменяем без уведомления
        CancelActiveTimer(session);

        var timer = new ActiveTimer
        {
            Type            = TimerType.Rest,
            DurationMinutes = minutes
        };
        session.ActiveTimer = timer;

        var endTime = timer.EndsAt.ToString("HH:mm");
        await _bot.SendMessage(
            chatId:           chatId,
            text:             $"☕ <b>Время отдыха!</b>\n" +
                              $"Длительность: <b>{minutes} мин</b>\n" +
                              $"Завершится в: <b>{endTime}</b>\n\n" +
                              $"Расслабься, отдохни от экрана 🧘",
            parseMode:        ParseMode.Html,
            replyMarkup:      BuildWebAppButton(timer, chatId, userId),
            cancellationToken: CancellationToken.None);

        _ = RunTimerAsync(chatId, userId, timer);

        _logger.LogInformation("Запущен таймер отдыха {Minutes} мин для пользователя {UserId}", minutes, userId);
    }

    /// <summary>
    /// Досрочно остановить активный таймер пользователя.
    /// Возвращает true, если таймер был активен и успешно остановлен.
    /// </summary>
    public bool StopTimer(long userId)
    {
        var session = _sessions.Get(userId);
        if (session?.ActiveTimer is null)
            return false;

        CancelActiveTimer(session);
        return true;
    }

    /// <summary>Остановить таймер, только если ID запуска совпадает с текущим активным таймером.</summary>
    public bool StopTimer(long userId, Guid timerId)
    {
        var session = _sessions.Get(userId);
        if (session?.ActiveTimer is null || session.ActiveTimer.Id != timerId)
            return false;

        CancelActiveTimer(session);
        return true;
    }

    /// <summary>Проверить, активен ли конкретный запуск таймера.</summary>
    public bool IsTimerActive(long userId, Guid timerId)
    {
        var session = _sessions.Get(userId);
        return session?.ActiveTimer?.Id == timerId;
    }

    // ──────────────────────────────────────────────
    //  Внутренняя логика
    // ──────────────────────────────────────────────

    /// <summary>
    /// Строит инлайн-кнопку для открытия Mini App.
    /// Если WebAppUrl не задан — возвращает null (кнопка не отображается).
    /// </summary>
    private InlineKeyboardMarkup? BuildWebAppButton(ActiveTimer timer, long chatId, long userId)
    {
        if (string.IsNullOrWhiteSpace(_webAppUrl))
            return null;

        var type     = timer.Type == TimerType.Work ? "work" : "rest";
        var duration = timer.DurationMinutes * 60;
        var started  = new DateTimeOffset(timer.StartedAt).ToUnixTimeMilliseconds();
        // Поддерживаем оба варианта: GitHub Pages (/timer.html) и встроенный сервер (/app)
        var isStaticPage = _webAppUrl!.Contains("github.io", StringComparison.OrdinalIgnoreCase);
        var timerPath = isStaticPage ? "/timer.html" : "/app";
        var url       = $"{_webAppUrl}{timerPath}?view=timer&type={type}&duration={duration}&started={started}" +
                        $"&userId={userId}&chatId={chatId}&timerId={timer.Id:N}";

        var stopBaseUrl = isStaticPage ? _webAppStopUrl : _webAppUrl;
        if (!string.IsNullOrWhiteSpace(stopBaseUrl))
        {
            var stopUrl = Uri.EscapeDataString($"{stopBaseUrl}/timer/stop");
            var statusUrl = Uri.EscapeDataString($"{stopBaseUrl}/timer/status");
            var apiBase = Uri.EscapeDataString(stopBaseUrl);
            url += $"&stopUrl={stopUrl}&statusUrl={statusUrl}&apiBase={apiBase}";
        }

        var label = timer.Type == TimerType.Work ? "📚 Открыть таймер" : "☕ Открыть таймер";

        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithWebApp(label, new WebAppInfo { Url = url }) }
        });
    }

    /// <summary>Отменить токен активного таймера и обнулить ссылку</summary>
    private static void CancelActiveTimer(UserSession session)
    {
        if (session.ActiveTimer is not null)
        {
            session.ActiveTimer.Cts.Cancel();
            session.ActiveTimer.Cts.Dispose();
            session.ActiveTimer = null;
        }
    }

    /// <summary>
    /// Фоновое ожидание окончания таймера.
    /// После завершения — обновляет усталость и уведомляет пользователя.
    /// </summary>
    private async Task RunTimerAsync(long chatId, long userId, ActiveTimer timer)
    {
        // Создаём связанный токен: сработает при отмене таймера или остановке приложения
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timer.Cts.Token,
            _lifetime.ApplicationStopping);

        try
        {
            // Ждём окончания таймера
            await Task.Delay(TimeSpan.FromMinutes(timer.DurationMinutes), linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Таймер отменён вручную или приложение завершается — не уведомляем
            return;
        }

        // Таймер сработал — обрабатываем результат
        var session = _sessions.Get(userId);
        if (session is null) return;

        // Очищаем ссылку (таймер уже завершён)
        session.ActiveTimer = null;

        if (timer.Type == TimerType.Work)
        {
            await OnWorkTimerFinishedAsync(chatId, session, timer.DurationMinutes);
        }
        else
        {
            await OnRestTimerFinishedAsync(chatId, session, timer.DurationMinutes);
        }
    }

    /// <summary>Обработка завершения рабочего таймера</summary>
    private async Task OnWorkTimerFinishedAsync(long chatId, UserSession session, int minutes)
    {
        // Увеличиваем усталость за рабочую сессию (+20, максимум 100)
        session.FatigueLevel           = Math.Min(100, session.FatigueLevel + 20);
        session.WorkSessionsWithoutRest++;

        // Формируем предупреждение об усталости
        string fatigueNote;
        if (session.FatigueLevel >= 86)
        {
            fatigueNote = $"\n\n🚨 <b>Критическая усталость {session.FatigueLevel}%!</b>\n" +
                          $"Ты работаешь уже {session.WorkSessionsWithoutRest} сессий подряд.\n" +
                          $"<b>Обязательно сделай перерыв!</b> → /rest";
        }
        else if (session.NeedsRest)
        {
            fatigueNote = $"\n\n⚠️ Рекомендую отдохнуть!\n" +
                          $"Усталость: {session.FatigueLevel}% ({session.FatigueDescription})\n" +
                          $"Используй /rest для перерыва.";
        }
        else
        {
            fatigueNote = $"\n\nУсталость: {session.FatigueLevel}% ({session.FatigueDescription})";
        }

        await _bot.SendMessage(
            chatId:           chatId,
            text:             $"✅ <b>Рабочая сессия завершена!</b>\n" +
                              $"Ты работал <b>{minutes} мин</b>. Отличная работа!{fatigueNote}",
            parseMode:        ParseMode.Html,
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Рабочий таймер завершён. Пользователь {UserId}, усталость {Fatigue}%",
            session.UserId, session.FatigueLevel);
    }

    /// <summary>Обработка завершения таймера отдыха</summary>
    private async Task OnRestTimerFinishedAsync(long chatId, UserSession session, int minutes)
    {
        // Снижаем усталость в зависимости от длины перерыва
        int reduction = minutes switch
        {
            <= 5  => 15,   // Короткий перерыв
            <= 15 => 25,   // Средний перерыв
            _     => 35    // Длинный перерыв
        };

        session.FatigueLevel           = Math.Max(0, session.FatigueLevel - reduction);
        session.WorkSessionsWithoutRest = 0; // Сбрасываем счётчик сессий без отдыха

        await _bot.SendMessage(
            chatId:           chatId,
            text:             $"⏰ <b>Перерыв завершён!</b>\n" +
                              $"Ты отдохнул <b>{minutes} мин</b>. Отлично!\n\n" +
                              $"Усталость: {session.FatigueLevel}% ({session.FatigueDescription})\n\n" +
                              $"Готов к новой сессии? Запускай таймер → /timer 💪",
            parseMode:        ParseMode.Html,
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Таймер отдыха завершён. Пользователь {UserId}, усталость {Fatigue}%",
            session.UserId, session.FatigueLevel);
    }
}
