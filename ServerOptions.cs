namespace FilesystemMcp;

internal sealed record ServerOptions(bool AllowSymLinks = true)
{
    public static (string Workspace, ServerOptions Options) Parse(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Workspace argument is required.");
        var allow = true;
        var seen = false;
        foreach (var arg in args.Skip(1))
        {
            const string prefix = "--allowSymLinks=";
            if (!arg.StartsWith(prefix, StringComparison.Ordinal) || seen)
                throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
            var value = arg[prefix.Length..];
            if (value != "true" && value != "false")
                throw new ArgumentException("allowSymLinks must be true or false.");
            allow = value == "true";
            seen = true;
        }
        return (args[0], new ServerOptions(allow));
    }
}
