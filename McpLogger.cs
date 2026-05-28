namespace FilesystemMcp;

internal static class McpLogger
{
    public static void LogInfo(string message)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        Console.Error.WriteLine($"[{timestamp}] [INFO] {message}");
    }

    public static void LogError(string message, Exception? ex = null)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        if (ex is null)
        {
            Console.Error.WriteLine($"[{timestamp}] [ERROR] {message}");
            return;
        }

        Console.Error.WriteLine($"[{timestamp}] [ERROR] {message}{Environment.NewLine}{ex}");
    }
}
