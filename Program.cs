using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using TelegramStudentBot.Handlers;
using TelegramStudentBot.Services;

// ──────────────────────────────────────────────────────────────
//  Точка входа — настройка DI и запуск хоста
// ──────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        // Читаем токен из appsettings.json
        var rawToken = ctx.Configuration["BotToken"];
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new InvalidOperationException(
                "Токен бота не найден. Укажи BotToken в переменной окружения BotToken или в локальном appsettings.Development.json. Пример есть в appsettings.Development.example.json.");
        }

        // Убираем пробелы и невидимые символы (могут попасть при копировании из Telegram)
        var token = string.Concat(rawToken.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Токен бота пустой. Укажи BotToken в переменной окружения BotToken или в локальном appsettings.Development.json. Пример есть в appsettings.Development.example.json.");
        }

        // Диагностика без вывода содержимого токена.
        Console.WriteLine($"[DEBUG] Токен считан. Длина: {token.Length} символов.");

        // Telegram Bot Client — синглтон, переиспользуется во всём приложении
        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));

        // Хранилище сессий пользователей (в памяти)
        services.AddSingleton<SessionService>();

        // Сервис управления таймерами
        services.AddSingleton<TimerService>();

        // Обработчики обновлений
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<TextHandler>();
        services.AddSingleton<CallbackHandler>();
        services.AddSingleton<UpdateRouter>();

        // Фоновый сервис бота (запускается автоматически при старте хоста)
        services.AddHostedService<BotService>();

        // HTTP-сервер для Mini App (timer.html)
        services.AddHostedService<WebAppService>();
    })
    .Build();

await host.RunAsync();
