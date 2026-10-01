namespace FilesystemMcp.Tests.Infrastructure;

/// <summary>Sharing violations and junctions have Windows-specific semantics.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows sharing/junction semantics; run on the Windows test lane.";
    }
}
