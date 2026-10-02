namespace FilesystemMcp;

internal sealed record ServerOptions(bool AllowSymLinks = true, string? LogDirectory = null)
{
    private const string AllowSymLinksPrefix = "--allowSymLinks=";
    private const string LogDirectoryPrefix = "--logDirectory=";

    public static (string Workspace, ServerOptions Options) Parse(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Workspace argument is required.");
        var allow = true;
        string? logDirectory = null;
        var allowSeen = false;
        var logDirectorySeen = false;
        foreach (var arg in args.Skip(1))
        {
            if (arg.StartsWith(AllowSymLinksPrefix, StringComparison.Ordinal))
            {
                if (allowSeen)
                    throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
                var value = arg[AllowSymLinksPrefix.Length..];
                if (value != "true" && value != "false")
                    throw new ArgumentException("allowSymLinks must be true or false.");
                allow = value == "true";
                allowSeen = true;
                continue;
            }

            if (arg.StartsWith(LogDirectoryPrefix, StringComparison.Ordinal))
            {
                if (logDirectorySeen)
                    throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
                var value = arg[LogDirectoryPrefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("logDirectory requires a non-empty path value.");
                logDirectory = value;
                logDirectorySeen = true;
                continue;
            }

            throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
        }

        return (args[0], new ServerOptions(allow, logDirectory));
    }
}
