using System.Runtime.InteropServices;

namespace FilesystemMcp.Tests.Infrastructure;

public sealed class UnixWritePermissionFactAttribute : FactAttribute
{
    public UnixWritePermissionFactAttribute()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            Skip = "Requires an unprivileged Linux/macOS account for effective file write permission checks.";
        }
        else if (GetEffectiveUid() == 0)
        {
            Skip = "Root bypasses discretionary write permissions; run an unprivileged Unix lane.";
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
