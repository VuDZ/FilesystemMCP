using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FilesystemMcp;

// Writes never truncate through a name. Windows pins directory handles against
// write/delete sharing; Unix opens each directory and the leaf relative to its fd.
internal static class NativePath
{
    private const uint Reparse = 0x00200000, Backup = 0x02000000;
    internal readonly record struct DirectoryIdentity(ulong Volume, ulong Low, ulong High);

    internal static DirectoryIdentity GetDirectoryIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsDirectoryForMetadata(path);
            if (!GetFileInformationByHandleEx(handle, 18, out FileIdInformation identity, 24))
                throw PathPolicy.Error("path_changed");
            return new(identity.Volume, identity.Low, identity.High);
        }
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && IntPtr.Size == 8)
        {
            var flags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 : 0x100000 | 0x100 | 0x1000000;
            var fd = Open(path, flags, 0);
            if (fd < 0) throw PathPolicy.Error("path_changed");
            using var handle = new SafeFileHandle((IntPtr)fd, true);
            var identity = UnixIdentity(handle);
            return new(unchecked((ulong)identity.Device), unchecked((ulong)identity.Inode), 0);
        }
        throw PathPolicy.Error("path_changed");
    }

    internal static string GetPhysicalDirectoryPath(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var handle = OpenWindowsDirectoryForMetadata(path, followLinks: true);
        return FinalWindowsPath(handle);
    }

    private static SafeFileHandle OpenWindowsDirectoryForMetadata(string path, bool followLinks = false)
    {
        var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, Backup | (followLinks ? 0 : Reparse), IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)
            || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
        { handle.Dispose(); throw PathPolicy.Error("path_changed"); }
        return handle;
    }
    internal static void ValidateReparseTag(uint tag)
    {
        if (tag != 0xA000000C && tag != 0xA0000003) throw PathPolicy.Error("unsupported_reparse_point");
    }

    public static void CheckReparseType(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, Reparse | Backup, IntPtr.Zero);
        var data = new byte[16384];
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, data, data.Length, out _, IntPtr.Zero))
            throw PathPolicy.Error("unsupported_reparse_point");
        ValidateReparseTag(BitConverter.ToUInt32(data));
    }

    public static async Task WriteAsync(PathPolicy policy, string requested, string expected, string content, bool create,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A directory/root has no file leaf. In particular, '.' must never turn
        // into a child named after the workspace basename.
        var leafName = Path.GetFileName(expected);
        if (PathPolicy.Comparer.Equals(expected, policy.Root) || Directory.Exists(expected)
            || string.IsNullOrEmpty(leafName) || leafName is "." or "..")
            throw PathPolicy.Error("path_is_directory");
        policy.BeforeWriteCommit?.Invoke();
        if (!PathPolicy.Comparer.Equals(policy.Resolve(requested), expected)) throw PathPolicy.Error("path_changed");
        var handles = new List<SafeFileHandle>();
        try
        {
            SafeFileHandle leaf;
            if (OperatingSystem.IsWindows())
            {
                var anchor = policy.Root;
                if (policy.Options.AllowSymLinks && !PathPolicy.Contains(anchor, expected))
                {
                    anchor = Path.GetDirectoryName(expected)!;
                    while (!Directory.Exists(anchor))
                        anchor = Path.GetDirectoryName(anchor) ?? throw PathPolicy.Error("path_changed");
                }
                var parent = PinWindowsDirectory(anchor);
                handles.Add(parent);
                if (!PathPolicy.Comparer.Equals(FinalWindowsPath(parent), anchor)) throw PathPolicy.Error("path_changed");
                var relative = Path.GetRelativePath(anchor, expected);
                if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw PathPolicy.Error("path_changed");
                foreach (var part in (Path.GetDirectoryName(relative) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    parent = OpenWindowsRelative(parent, part, directory: true, create);
                    handles.Add(parent);
                    EnsureNotReparse(parent);
                }
                policy.AfterWriteParentsPinned?.Invoke();
                if (!PathPolicy.Comparer.Equals(policy.Resolve(requested), expected)) throw PathPolicy.Error("path_changed");
                leaf = OpenWindowsRelative(parent, Path.GetFileName(expected), directory: false, create);
                EnsureNotReparse(leaf);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (IntPtr.Size != 8) throw PathPolicy.Error("unsupported_safe_write");
                var identities = new List<(SafeFileHandle Handle, string Path)>();
                var directoryFlags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 : 0x100000 | 0x100 | 0x1000000;
                var fd = Open("/", directoryFlags, 0);
                if (fd < 0) throw PathPolicy.Error("path_changed");
                var parent = new SafeFileHandle((IntPtr)fd, true);
                handles.Add(parent);
                identities.Add((parent, "/"));
                var current = "/";
                foreach (var part in Path.GetDirectoryName(expected)!.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    fd = OpenAt(parent, part, directoryFlags, 0);
                    if (fd < 0 && create && Marshal.GetLastPInvokeError() == 2)
                    {
                        if (MkdirAt(parent, part, 0x1ED) != 0 && Marshal.GetLastPInvokeError() != 17) throw PathPolicy.Error("path_changed");
                        fd = OpenAt(parent, part, directoryFlags, 0);
                    }
                    if (fd < 0) throw PathPolicy.Error("path_changed");
                    parent = new SafeFileHandle((IntPtr)fd, true);
                    handles.Add(parent);
                    identities.Add((parent, current));
                }
                policy.AfterWriteParentsPinned?.Invoke();
                if (!PathPolicy.Comparer.Equals(policy.Resolve(requested), expected)) throw PathPolicy.Error("path_changed");
                VerifyUnixIdentities(identities, directoryFlags);
                var flags = 1 | (OperatingSystem.IsLinux() ? 0x20000 | 0x80000 : 0x100 | 0x1000000);
                if (create) flags |= OperatingSystem.IsLinux() ? 0x40 | 0x80 : 0x200 | 0x800;
                fd = OpenAt(parent, Path.GetFileName(expected), flags, 0x1A4);
                if (fd < 0) throw PathPolicy.Error("path_changed");
                leaf = new SafeFileHandle((IntPtr)fd, true);
                try { VerifyUnixIdentities(identities, directoryFlags); }
                catch { leaf.Dispose(); throw; }
            }
            else throw PathPolicy.Error("unsupported_safe_write");

            await using var stream = new FileStream(leaf, FileAccess.Write);
            // No name lookup is performed after this point, including truncation.
            cancellationToken.ThrowIfCancellationRequested();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    private static void VerifyUnixIdentities(IEnumerable<(SafeFileHandle Handle, string Path)> directories, int flags)
    {
        foreach (var (pinned, path) in directories)
        {
            var fd = Open(path, flags, 0);
            if (fd < 0) throw PathPolicy.Error("path_changed");
            using var live = new SafeFileHandle((IntPtr)fd, true);
            if (UnixIdentity(pinned) != UnixIdentity(live)) throw PathPolicy.Error("path_changed");
        }
    }

    private static (long Device, long Inode) UnixIdentity(SafeFileHandle handle)
    {
        // Both supported ABIs put the inode at offset 8. Darwin's dev_t is
        // 32 bits; Linux's is 64 bits. The buffer exceeds either stat layout.
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            // Darwin x64 retains the legacy symbol; arm64 only has 64-bit
            // inode ABI and uses the unsuffixed fstat symbol (sys/cdefs.h).
            var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FstatDarwin(handle, buffer) : Fstat(handle, buffer);
            if (result != 0) throw PathPolicy.Error("path_changed");
            return (OperatingSystem.IsLinux() ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer), Marshal.ReadInt64(buffer, 8));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle PinWindowsDirectory(string path)
    {
        var handle = CreateFile(path, 0xA0, 1, IntPtr.Zero, 3, Reparse | Backup, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw PathPolicy.Error("path_changed"); }
        EnsureNotReparse(handle);
        return handle;
    }

    private static void EnsureNotReparse(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8) || (info.Attributes & 0x400) != 0)
        { handle.Dispose(); throw PathPolicy.Error("path_changed"); }
    }

    private static string FinalWindowsPath(SafeFileHandle handle)
    {
        var buffer = new System.Text.StringBuilder(32768);
        var size = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity) throw PathPolicy.Error("path_changed");
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static SafeFileHandle OpenWindowsRelative(SafeFileHandle parent, string name, bool directory, bool create)
    {
        var chars = Marshal.StringToHGlobalUni(name);
        var stringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = chars }, stringPointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = stringPointer, Attributes = 0x40 };
            var status = NtCreateFile(out var handle, directory ? 0x1000A0u : 0x40100080u, ref attributes, out _, IntPtr.Zero,
                0x80, 1, create ? (directory ? 3u : 2u) : 1u, 0x200000u | (directory ? 0x21u : 0x20u | 0x40u), IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0) { handle?.Dispose(); throw PathPolicy.Error("path_changed"); }
            return handle;
        }
        finally { Marshal.FreeHGlobal(stringPointer); Marshal.FreeHGlobal(chars); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, System.Text.StringBuilder path, int capacity, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInformation { public ulong Volume; public ulong Low; public ulong High; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInformation info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("libc", SetLastError = true, EntryPoint = "open")] private static extern int Open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "openat")] private static extern int OpenAt(SafeFileHandle directory, string path, int flags, int mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")] private static extern int MkdirAt(SafeFileHandle directory, string path, int mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")] private static extern int Fstat(SafeFileHandle handle, IntPtr stat);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat$INODE64")] private static extern int FstatDarwin(SafeFileHandle handle, IntPtr stat);
}
