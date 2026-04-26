using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TelegramStudentBot.Handlers;
using TelegramStudentBot.MiniApp;
using TelegramStudentBot.Services;

ClearBrokenProxyEnvironment();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddDebug();

var webPort = builder.Configuration.GetValue<int?>("PORT")
    ?? builder.Configuration.GetValue<int?>("WebAppPort")
    ?? 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");

var rawToken = builder.Configuration["BotToken"]
    ?? throw new InvalidOperationException(
        "Токен бота не найден. Укажи BotToken в appsettings.json");

var token = string.Concat(rawToken.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));
Console.WriteLine($"[DEBUG] Токен считан. Длина: {token.Length} символов. Начало: {token[..Math.Min(10, token.Length)]}...");

builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));

builder.Services.AddSingleton<StudyTaskStorageService>();
builder.Services.AddSingleton<ReminderSettingsService>();
builder.Services.AddSingleton<HomeworkSubjectPreferencesService>();
builder.Services.AddSingleton<UserProfileStorageService>();
builder.Services.AddSingleton<UserFeatureIntroService>();
builder.Services.AddSingleton<BotVisitLogService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<TimerService>();
builder.Services.AddSingleton<ScheduleCatalogService>();
builder.Services.AddSingleton<UserScheduleSelectionService>();

builder.Services.AddSingleton<MiniAppAuthService>();
builder.Services.AddSingleton<MiniAppChatSyncService>();
builder.Services.AddSingleton<MiniAppService>();

builder.Services.AddSingleton<CommandHandler>();
builder.Services.AddSingleton<TextHandler>();
builder.Services.AddSingleton<CallbackHandler>();
builder.Services.AddSingleton<UpdateRouter>();
builder.Services.AddHostedService<BotService>();
builder.Services.AddHostedService<DeadlineReminderService>();

var app = builder.Build();

app.UseStaticFiles();

app.MapGet("/", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "miniapp", "index.html"), "text/html"));
app.MapGet("/miniapp", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "miniapp", "index.html"), "text/html"));
app.MapGet("/miniapp/", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "miniapp", "index.html"), "text/html"));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    utc = DateTimeOffset.UtcNow
}));

app.MapMiniAppEndpoints();

try
{
    await app.RunAsync();
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
