namespace FilesystemMcp;

/// <summary>
/// Typed operational failure that is neither a mutation outcome nor a path-policy
/// decision, for example an exceeded resource limit. Carries the FS-05 machine code.
/// The mutable surface stays closed: only the FS-07 budget refusal derives from it,
/// and that derivation exists so <c>resource_limit</c> has exactly one factory.
/// </summary>
internal class OperationalException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
