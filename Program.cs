using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TelegramStudentBot.Handlers;
using TelegramStudentBot.Services;

ClearBrokenProxyEnvironment();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        logging.AddDebug();
    })
    .ConfigureServices((ctx, services) =>
    {
        var rawToken = ctx.Configuration["BotToken"]
            ?? throw new InvalidOperationException(
                "Токен бота не найден. Укажи BotToken в appsettings.json");

        var token = string.Concat(rawToken.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));

        Console.WriteLine($"[DEBUG] Токен считан. Длина: {token.Length} символов. Начало: {token[..Math.Min(10, token.Length)]}...");

        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));

        services.AddSingleton<BotVisitLogService>();
        services.AddSingleton<UserProfileStorageService>();
        services.AddSingleton<StudyTaskStorageService>();
        services.AddSingleton<ReminderSettingsService>();
        services.AddSingleton<HomeworkSubjectPreferencesService>();
        services.AddSingleton<UserFeatureIntroService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<TimerService>();
        services.AddSingleton<ScheduleCatalogService>();
        services.AddSingleton<UserScheduleSelectionService>();

        services.AddSingleton<CommandHandler>();
        services.AddSingleton<TextHandler>();
        services.AddSingleton<CallbackHandler>();
        services.AddSingleton<UpdateRouter>();
        services.AddHostedService<BotService>();
        services.AddHostedService<DeadlineReminderService>();
    })
    .Build();

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Ошибка запуска бота:");
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static void ClearBrokenProxyEnvironment()
{
    foreach (var name in new[]
             {
                 "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                 "http_proxy", "https_proxy", "all_proxy",
                 "GIT_HTTP_PROXY", "GIT_HTTPS_PROXY"
             })
    {
        Environment.SetEnvironmentVariable(name, null);
    }
}
