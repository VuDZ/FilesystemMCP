using System.Text;

namespace FilesystemMcp;

/// <summary>
/// Sink of fully formatted diagnostic lines. Implementations are called from the single
/// logger writer thread only, and are required to be best effort: a failure must never
/// escape into a tool or into the transport.
/// </summary>
internal interface ILogSink
{
    void Write(string line);
    void Flush();

    /// <summary>
    /// True once the sink can no longer accept records. The writer uses this to fall back
    /// to stderr and to report the loss exactly once instead of dropping diagnostics.
    /// </summary>
    bool IsFailed { get; }
}

/// <summary>One diagnostic record before it is turned into a line.</summary>
internal readonly record struct LogEntry(DateTimeOffset TimestampUtc, string Level, string Message, string? CorrelationId);

/// <summary>
/// Path selection and line formatting for diagnostics. Kept separate from
/// <see cref="McpLogger"/> so that the "user directory, never the binary directory"
/// rule and the single Info/Error format are testable without touching global state.
/// </summary>
internal static class Logging
{
    internal const string InfoLevel = "INFO";
    internal const string ErrorLevel = "ERROR";
    internal const int MaxLineChars = 8192;

    /// <summary>
    /// Default log directory: a per-user location (never the binary directory), with a
    /// temp-directory fallback when the platform gives us no user path.
    /// </summary>
    internal static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "FilesystemMCP", "logs");
        }

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(stateHome))
        {
            return Path.Combine(stateHome, "FilesystemMCP", "logs");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".local", "state", "FilesystemMCP", "logs");
        }

        return Path.Combine(Path.GetTempPath(), "FilesystemMCP-logs");
    }

    /// <summary>
    /// Fully qualified directory: an explicit option wins, otherwise the user directory.
    /// Never throws: an unusable path is reported by the caller as a disabled file sink.
    /// </summary>
    internal static string ResolveDirectory(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultDirectory() : configured;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Creates the directory; returns false (with a safe reason) when unavailable.</summary>
    internal static bool TryPrepareDirectory(string directory, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (Directory.Exists(directory))
            {
                return true;
            }

            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>Session file name; the UTC date is part of the "UTC timestamp" contract.</summary>
    internal static string SessionFileName(DateTimeOffset nowUtc, int processId) =>
        $"mcplog-{nowUtc.UtcDateTime:yyyyMMdd}-pid{processId}.log";

    /// <summary>
    /// The single INFO/ERROR line format: UTC timestamp, one level token, optional request
    /// correlation, then the message. Because both levels share this formatter, the
    /// timestamp and correlation of Info and Error cannot drift apart.
    /// </summary>
    internal static string Format(LogEntry entry)
    {
        var builder = new StringBuilder(128);
        builder.Append('[').Append(entry.TimestampUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append(']');
        builder.Append(" [").Append(entry.Level).Append(']');
        if (!string.IsNullOrEmpty(entry.CorrelationId))
        {
            builder.Append(" [req=").Append(entry.CorrelationId).Append(']');
        }

        builder.Append(' ').Append(entry.Message);

        // One record stays one line and one bounded buffer entry.
        if (builder.Length > MaxLineChars)
        {
            builder.Length = MaxLineChars;
            builder.Append("…[truncated, ").Append(entry.Message.Length).Append(" chars]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats an exception without its platform message: the type chain, HResult and the
    /// stack trace are diagnostics, while a framework message can embed an absolute path
    /// or file content this product does not own.
    /// </summary>
    internal static string DescribeException(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(512);
        var depth = 0;
        for (var current = exception; current is not null && depth < 5; current = current.InnerException, depth++)
        {
            if (depth > 0)
            {
                builder.Append(" --> ");
            }

            var type = current.GetType();
            builder.Append(type.FullName ?? type.Name);
            builder.Append(" hresult=0x").Append(current.HResult.ToString("x8"));
        }

        if (exception.StackTrace is { Length: > 0 } stack)
        {
            builder.Append(Environment.NewLine).Append(stack);
        }

        return builder.ToString();
    }
}
