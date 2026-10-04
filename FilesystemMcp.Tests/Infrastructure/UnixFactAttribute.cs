namespace FilesystemMcp.Tests.Infrastructure;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "Requires Linux/macOS directory identity and rename semantics; run the Unix test lane.";
        }
    }
}
