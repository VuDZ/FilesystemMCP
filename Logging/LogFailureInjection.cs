namespace FilesystemMcp;

/// <summary>Selects one stage of the next diagnostic record to fail.</summary>
internal sealed class LogFailureInjection
{
    internal int Stage { get; }

    internal Action<int>? Hook { get; }

    internal LogFailureInjection(int stage, Action<int>? hook = null)
    {
        Stage = stage;
        Hook = hook;
    }
}
