using System.Text.Json;

namespace FilesystemMcp;

internal static class McpLogger
{
    // FS-02 prerequisite: diagnostics cannot turn a committed mutation into failure.
    public static void LogInfo(string message) { try { LogInfoCore(message); } catch { } }
    public static void LogError(string message, Exception? ex = null) { try { LogErrorCore(message, ex); } catch { } }
    public static void LogToolInvoke(string name, JsonElement arguments) { try { LogToolInvokeCore(name, arguments); } catch { } }
    public static void LogToolComplete(string name, TimeSpan elapsed, bool success, string? detail = null)
    { try { LogToolCompleteCore(name, elapsed, success, detail); } catch { } }
    private static void LogInfoCore(string message)
    {
        var formattedMsg = $"[{DateTime.UtcNow:s}] [INFO] {message}";

        Console.Error.WriteLine(formattedMsg);
        
        AppendToLogFile(GetLogFilename(), formattedMsg);
    }

    private static string GetLogFilename()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        return Path.Combine(logDir, $"mcplog-{DateTime.Now:dd-MM-yyyy}-pid{Environment.ProcessId}.log");
    }

    private static void AppendToLogFile(string filename, string message)
    {
        try
        {
            File.AppendAllText(filename, message + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log write failed (non-fatal): {ex.Message}");
        }
    }

    private static void LogToolInvokeCore(string toolName, JsonElement arguments)
    {
        var sanitizedArgs = LogSanitizer.SanitizeForLog(arguments);
        LogInfo($"Tool invoke: {toolName} args={sanitizedArgs}");
    }

    private static void LogToolCompleteCore(string toolName, TimeSpan elapsed, bool success, string? detail = null)
    {
        var status = success ? "ok" : "failed";
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        LogInfo($"Tool done: {toolName} status={status} elapsedMs={elapsed.TotalMilliseconds:F0}{suffix}");
    }

    private static void LogErrorCore(string message, Exception? ex = null)
    {
        var formattedMsg = $"[{DateTime.Now:s}] [ERROR] {message}";
        if (ex is not null)
        {
            formattedMsg += $"{Environment.NewLine}{ex}";
        }
        
        Console.Error.WriteLine(formattedMsg);
        AppendToLogFile(GetLogFilename(), formattedMsg);
    }
}
