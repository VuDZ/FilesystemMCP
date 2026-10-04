using System.Runtime.InteropServices;
using System.Text;

namespace FilesystemMcp.Tests.Infrastructure;

public sealed class ShortNameFactAttribute : FactAttribute
{
    public ShortNameFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows 8.3 directory aliases; run the Windows test lane.";
            return;
        }

        using var sandbox = new Sandbox();
        if (GetShortPath(sandbox.Workspace) is not { } alias
            || string.Equals(alias, sandbox.Workspace, StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Windows 8.3 aliases are unavailable on this temp volume; run an 8.3-enabled Windows lane.";
        }
    }

    internal static string? GetShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var count = GetShortPathName(path, buffer, buffer.Capacity);
        return count == 0 || count >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder buffer, int capacity);
}
