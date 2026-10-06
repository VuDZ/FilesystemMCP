namespace FilesystemMcp;

internal sealed record ServerOptions(bool AllowSymLinks = true, string? LogDirectory = null)
{
    private const string AllowSymLinksPrefix = "--allowSymLinks=";
    private const string LogDirectoryPrefix = "--logDirectory=";

    /// <summary>
    /// FS-07 budgets. Immutable for the process lifetime and validated here, so a
    /// nonsensical limit is a startup failure in stderr with a nonzero exit code
    /// rather than a surprise refusal in the middle of a session.
    /// </summary>
    public ResourceBudget Budget { get; init; } = ResourceBudget.Default;

    public static (string Workspace, ServerOptions Options) Parse(string[] args) =>
        Parse(args, Directory.GetCurrentDirectory());

    public static (string Workspace, ServerOptions Options) Parse(string[] args, string clientDirectory)
    {
        var (workspace, optionArgs) = SelectWorkspace(args, clientDirectory);
        var allow = true;
        string? logDirectory = null;
        var allowSeen = false;
        var logDirectorySeen = false;
        var budget = ResourceBudget.Default;
        var budgetSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in optionArgs)
        {
            if (arg.StartsWith(AllowSymLinksPrefix, StringComparison.Ordinal))
            {
                if (allowSeen)
                {
                    throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
                }

                var value = arg[AllowSymLinksPrefix.Length..];
                if (value != "true" && value != "false")
                {
                    throw new ArgumentException("allowSymLinks must be true or false.");
                }

                allow = value == "true";
                allowSeen = true;
                continue;
            }

            if (arg.StartsWith(LogDirectoryPrefix, StringComparison.Ordinal))
            {
                if (logDirectorySeen)
                {
                    throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
                }

                var value = arg[LogDirectoryPrefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("logDirectory requires a non-empty path value.");
                }

                logDirectory = value;
                logDirectorySeen = true;
                continue;
            }

            if (TryParseBudgetOption(arg, budgetSeen, ref budget))
            {
                continue;
            }

            throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
        }

        budget.Validate();
        return (workspace, new ServerOptions(allow, logDirectory) { Budget = budget });
    }

    /// <summary>
    /// An explicit first argument is the workspace and wins. When it is omitted, the
    /// workspace is <paramref name="clientDirectory"/>: the working directory the MCP
    /// client set while starting this process. OpenCode sets that to the session
    /// directory (<c>cwd</c> of the stdio child). It is not the directory that contains
    /// the executable — the binary may be installed anywhere, and using that folder
    /// would jail every session to the install location.
    /// </summary>
    private static (string Workspace, IEnumerable<string> Options) SelectWorkspace(string[] args, string clientDirectory)
    {
        if (args.Length > 0 && string.IsNullOrWhiteSpace(args[0]))
        {
            throw new ArgumentException("Workspace argument is required.");
        }

        var hasWorkspaceArgument = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
        if (hasWorkspaceArgument)
        {
            return (args[0], args.Skip(1));
        }

        if (string.IsNullOrWhiteSpace(clientDirectory))
        {
            throw new ArgumentException("Workspace argument is required.");
        }

        return (clientDirectory, args);
    }

    /// <summary>
    /// One shape for every numeric budget: <c>--name=value</c>, decimal digits only, no
    /// sign, underscore or culture-specific formatting. Duplicates are rejected for the
    /// same reason as the other options — a later value silently winning would make the
    /// effective budget depend on argument order.
    /// </summary>
    private static bool TryParseBudgetOption(string arg, HashSet<string> seen, ref ResourceBudget budget)
    {
        var separator = arg.IndexOf('=');
        if (separator < 0 || !arg.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var name = arg[2..separator];
        if (!IsBudgetName(name))
        {
            return false;
        }

        if (!seen.Add(name))
        {
            throw new ArgumentException("Unknown, missing-value, or duplicate startup option.");
        }

        var value = arg[(separator + 1)..];
        if (value.Length == 0 || !value.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(name + " must be a positive integer.");
        }

        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new ArgumentException(name + " must be a positive integer.");
        }

        budget = name switch
        {
            ResourceBudgetNames.MaxRequestBytes => budget with { MaxRequestBytes = parsed },
            ResourceBudgetNames.MaxFileBytes => budget with { MaxFileBytes = parsed },
            ResourceBudgetNames.MaxLineChars => budget with { MaxLineChars = CheckedInt(parsed, name) },
            ResourceBudgetNames.MaxResponseChars => budget with { MaxResponseChars = parsed },
            ResourceBudgetNames.SearchMaxFiles => budget with { SearchMaxFiles = parsed },
            ResourceBudgetNames.SearchMaxDirectories => budget with { SearchMaxDirectories = parsed },
            ResourceBudgetNames.OperationTimeoutMs => budget with { OperationTimeoutMs = parsed },
            ResourceBudgetNames.MaxConcurrentReads => budget with { MaxConcurrentReads = CheckedInt(parsed, name) },
            _ => budget
        };
        return true;
    }

    private static int CheckedInt(long value, string name) =>
        value > int.MaxValue ? throw new ArgumentException(name + " is too large.") : (int)value;

    private static bool IsBudgetName(string name) => name switch
    {
        ResourceBudgetNames.MaxRequestBytes or ResourceBudgetNames.MaxFileBytes
            or ResourceBudgetNames.MaxLineChars or ResourceBudgetNames.MaxResponseChars
            or ResourceBudgetNames.SearchMaxFiles or ResourceBudgetNames.SearchMaxDirectories
            or ResourceBudgetNames.OperationTimeoutMs or ResourceBudgetNames.MaxConcurrentReads => true,
        _ => false
    };
}
