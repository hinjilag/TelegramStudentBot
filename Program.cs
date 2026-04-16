using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TelegramStudentBot.Handlers;
using TelegramStudentBot.Services;

// ──────────────────────────────────────────────────────────────
//  Точка входа — настройка DI и запуск хоста
// ──────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = false;
            options.TimestampFormat = "HH:mm:ss ";
        });
    })
    .ConfigureAppConfiguration((ctx, config) =>
    {
        // Локальный файл с секретами: помогает, если IDE не передала DOTNET_ENVIRONMENT=Development.
        config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

        var localSettingsPath = FindFileUpwards("appsettings.Development.json");
        if (localSettingsPath is not null)
        {
            config.AddJsonFile(localSettingsPath, optional: true, reloadOnChange: true);
            Console.WriteLine($"[DEBUG] Локальные настройки найдены: {localSettingsPath}");
        }
        else
        {
            Console.WriteLine("[DEBUG] appsettings.Development.json не найден рядом с папкой запуска.");
        }
    })
    .ConfigureServices((ctx, services) =>
    {
        // Читаем токен из переменной окружения, appsettings.json или локального appsettings.Development.json.
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

        // Синхронизация изменений Mini App с Telegram-чатом
        services.AddSingleton<ChatSyncService>();

        // Обработчики обновлений
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<TextHandler>();
        services.AddSingleton<CallbackHandler>();
        services.AddSingleton<UpdateRouter>();

        // HTTP-сервер для Mini App (timer.html)
        services.AddHostedService<WebAppService>();

        // Фоновый сервис бота (запускается автоматически при старте хоста)
        services.AddHostedService<BotService>();
    })
    .Build();

await host.RunAsync();

static string? FindFileUpwards(string fileName)
{
    foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(startPath);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }
    }

    return null;
}
