namespace FilesystemMcp;

internal static class McpLogger
{
    public static void LogInfo(string message)
    {
        var formattedMsg = $"[{DateTime.UtcNow:s}] [INFO] {message}";

        Console.Error.WriteLine(formattedMsg);
        
        File.AppendAllText(GetLogFilename(), formattedMsg + Environment.NewLine);
    }

    private static string GetLogFilename()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        return Path.Combine(logDir, $"mcplog-{DateTime.Now:dd-MM-yyyy}.log");
    }

    public static void LogError(string message, Exception? ex = null)
    {
        var formattedMsg = $"[{DateTime.Now:s}] [ERROR] {message}";
        if (ex is not null)
        {
            formattedMsg += $"{Environment.NewLine}{ex}";
        }
        
        Console.Error.WriteLine(formattedMsg);
        File.AppendAllText(GetLogFilename() , formattedMsg + Environment.NewLine);
    }
}
