using System.Text.Json;

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

    public static void LogToolInvoke(string toolName, JsonElement arguments)
    {
        var sanitizedArgs = LogSanitizer.SanitizeForLog(arguments);
        LogInfo($"Tool invoke: {toolName} args={sanitizedArgs}");
    }

    public static void LogToolComplete(string toolName, TimeSpan elapsed, bool success, string? detail = null)
    {
        var status = success ? "ok" : "failed";
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        LogInfo($"Tool done: {toolName} status={status} elapsedMs={elapsed.TotalMilliseconds:F0}{suffix}");
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
