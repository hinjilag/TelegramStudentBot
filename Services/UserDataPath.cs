namespace TelegramStudentBot.Services;

internal static class UserDataPath
{
    public static string ResolveFile(string fileName)
    {
        var configuredDirectory = GetConfiguredDirectory();
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.Combine(configuredDirectory, fileName);

        var contentRootData = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        if (Directory.Exists(contentRootData))
            return Path.Combine(contentRootData, fileName);

        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }

    private static string? GetConfiguredDirectory()
    {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("USER_DATA_DIR"),
            Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH"));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
