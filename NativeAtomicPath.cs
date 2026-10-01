using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FilesystemMcp;

internal static partial class NativePath
{
    internal static DirectoryIdentity GetFileIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var file = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, Reparse, IntPtr.Zero);
            if (file.IsInvalid) throw new IOException("Cannot inspect file identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
            EnsureNotReparse(file);
            return FileIdentity(file);
        }
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || IntPtr.Size != 8)
            throw PathPolicy.Error("unsupported_safe_write");
        var fd = Open(path, NoFollow, 0);
        if (fd < 0) throw new IOException("Cannot inspect file identity.");
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        return FileIdentity(handle);
    }

    private static int NoFollow => OperatingSystem.IsLinux() ? 0x20000 | 0x80000 : 0x100 | 0x1000000;
    private static DirectoryIdentity FileIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(handle, 18, out FileIdInformation identity, 24)) throw PathPolicy.Error("path_changed");
            return new(identity.Volume, identity.Low, identity.High);
        }
        var unix = UnixIdentity(handle);
        return new(unchecked((ulong)unix.Device), unchecked((ulong)unix.Inode), 0);
    }

    internal sealed class AtomicParent : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        private readonly List<(SafeFileHandle Handle, string Path)> _identities = [];
        private readonly PathPolicy _policy;
        private readonly string _requested, _expected;
        private readonly int _directoryFlags;
        internal SafeFileHandle Parent => _handles[^1];

        internal AtomicParent(PathPolicy policy, string requested, string expected, bool create, CancellationToken token = default)
        {
            _policy = policy; _requested = requested; _expected = expected;
            if (PathPolicy.Comparer.Equals(expected, policy.Root) || Directory.Exists(expected))
                throw PathPolicy.Error("path_is_directory");
            policy.BeforeWriteCommit?.Invoke();
            token.ThrowIfCancellationRequested();
            VerifyPolicy();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var anchor = policy.Root;
                    if (policy.Options.AllowSymLinks && !PathPolicy.Contains(anchor, expected))
                    {
                        anchor = Path.GetDirectoryName(expected)!;
                        while (!Directory.Exists(anchor)) anchor = Path.GetDirectoryName(anchor) ?? throw PathPolicy.Error("path_changed");
                    }
                    var parent = PinWindowsDirectory(anchor); _handles.Add(parent);
                    if (!PathPolicy.Comparer.Equals(FinalWindowsPath(parent), anchor)) throw PathPolicy.Error("path_changed");
                    var relative = Path.GetRelativePath(anchor, expected);
                    if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw PathPolicy.Error("path_changed");
                    foreach (var part in (Path.GetDirectoryName(relative) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    {
                        token.ThrowIfCancellationRequested();
                        parent = OpenWindowsRelative(parent, part, true, create); _handles.Add(parent); EnsureNotReparse(parent);
                    }
                }
                else if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && IntPtr.Size == 8)
                {
                    _directoryFlags = NoFollow | (OperatingSystem.IsLinux() ? 0x10000 : 0x100000);
                    var fd = Open("/", _directoryFlags, 0);
                    if (fd < 0) throw PathPolicy.Error("path_changed");
                    var parent = new SafeFileHandle((IntPtr)fd, true); _handles.Add(parent); _identities.Add((parent, "/"));
                    var current = "/";
                    foreach (var part in Path.GetDirectoryName(expected)!.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        token.ThrowIfCancellationRequested();
                        current = Path.Combine(current, part);
                        fd = OpenAt(parent, part, _directoryFlags, 0);
                        if (fd < 0 && create && Marshal.GetLastPInvokeError() == 2)
                        {
                            if (MkdirAt(parent, part, 0x1ED) != 0 && Marshal.GetLastPInvokeError() != 17) throw PathPolicy.Error("path_changed");
                            fd = OpenAt(parent, part, _directoryFlags, 0);
                        }
                        if (fd < 0) throw PathPolicy.Error("path_changed");
                        parent = new SafeFileHandle((IntPtr)fd, true); _handles.Add(parent); _identities.Add((parent, current));
                    }
                }
                else throw PathPolicy.Error("unsupported_safe_write");
                policy.AfterWriteParentsPinned?.Invoke(); token.ThrowIfCancellationRequested(); Verify();
            }
            catch { Dispose(); throw; }
        }

        private void VerifyPolicy()
        {
            if (!PathPolicy.Comparer.Equals(_policy.Resolve(_requested), _expected)) throw PathPolicy.Error("path_changed");
        }
        internal void Verify()
        {
            VerifyPolicy();
            if (!OperatingSystem.IsWindows()) VerifyUnixIdentities(_identities, _directoryFlags);
            else foreach (var handle in _handles) EnsureNotReparse(handle);
        }

        internal SafeFileHandle OpenLeaf(string name, bool temp)
        {
            if (OperatingSystem.IsWindows()) return OpenAtomicWindowsRelative(Parent, name, temp);
            var flags = NoFollow | (temp ? 2 | (OperatingSystem.IsLinux() ? 0x40 | 0x80 : 0x200 | 0x800) : 0);
            var fd = OpenAt(Parent, name, flags, 0x180); // Temp private until metadata is copied.
            if (fd < 0) throw new IOException("Cannot open atomic write file.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return new SafeFileHandle((IntPtr)fd, true);
        }

        internal void RequireWriteAccess(string leaf, DirectoryIdentity expectedIdentity)
        {
            // Rename permissions on a writable parent must not bypass the
            // original file's data-write ACL/permissions.
            if (OperatingSystem.IsWindows())
            {
                using var writable = OpenAtomicWindowsRelative(Parent, leaf, false, writeAccess: true);
                if (FileIdentity(writable) != expectedIdentity) throw MutationException.Conflict();
            }
            else
            {
                var fd = OpenAt(Parent, leaf, 1 | NoFollow, 0);
                if (fd < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is 1 or 13) throw new MutationException("access_denied", "File write permission is required.");
                    throw new IOException("Cannot check file write access.", new Win32Exception(error));
                }
                using var writable = new SafeFileHandle((IntPtr)fd, true);
                if (FileIdentity(writable) != expectedIdentity) throw MutationException.Conflict();
            }
        }

        internal void Publish(SafeFileHandle temp, string tempName, string leaf, bool create)
        {
            Verify();
            if (OperatingSystem.IsWindows())
            {
                var name = System.Text.Encoding.Unicode.GetBytes(leaf);
                var pointer = Marshal.AllocHGlobal(20 + name.Length);
                try
                {
                    for (var i = 0; i < 20; i++) Marshal.WriteByte(pointer, i, 0);
                    // POSIX semantics let already-open readers retain the old
                    // inode while new opens see the complete replacement.
                    Marshal.WriteInt32(pointer, 0, create ? 0 : 1 | 2);
                    Marshal.WriteIntPtr(pointer, 8, Parent.DangerousGetHandle());
                    Marshal.WriteInt32(pointer, 16, name.Length); Marshal.Copy(name, 0, pointer + 20, name.Length);
                    var status = NtSetInformationFile(temp, out _, pointer, (uint)(20 + name.Length), 65);
                    if (status < 0)
                    {
                        var error = unchecked((int)RtlNtStatusToDosError(status));
                        if (create && error is 80 or 183) throw MutationException.Exists();
                        if (error is 1 or 50 or 87) throw PathPolicy.Error("unsupported_safe_write");
                        throw new IOException("Atomic publication failed.", new Win32Exception(error));
                    }
                }
                finally { Marshal.FreeHGlobal(pointer); GC.KeepAlive(Parent); }
            }
            else
            {
                using (var namedTemp = OpenLeaf(tempName, false))
                    if (FileIdentity(namedTemp) != FileIdentity(temp)) throw PathPolicy.Error("path_changed");
                // linkat is an atomic exclusive create; renameat is atomic replacement.
                var result = create ? LinkAt(Parent, tempName, Parent, leaf, 0) : RenameAt(Parent, tempName, Parent, leaf);
                if (result != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (create && error == 17) throw MutationException.Exists();
                    throw new IOException("Atomic publication failed.", new Win32Exception(error));
                }
            }
        }

        internal void Cleanup(SafeFileHandle temp, string name)
        {
            if (OperatingSystem.IsWindows())
            {
                var flag = Marshal.AllocHGlobal(4);
                try { Marshal.WriteInt32(flag, 1); if (!SetFileInformationByHandle(temp, 4, flag, 4)) throw new IOException("Temp cleanup failed."); }
                finally { Marshal.FreeHGlobal(flag); }
            }
            else
            {
                try
                {
                    using var namedTemp = OpenLeaf(name, false);
                    if (FileIdentity(namedTemp) == FileIdentity(temp) && UnlinkAt(Parent, name, 0) != 0) throw new IOException("Temp cleanup failed.");
                }
                catch (IOException) { /* Absent or inaccessible entries are never recovered/deleted by guessing. */ }
            }
        }

        public void Dispose() { foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    private static SafeFileHandle OpenAtomicWindowsRelative(SafeFileHandle parent, string name, bool temp, bool writeAccess = false)
    {
        if (IntPtr.Size != 8) throw PathPolicy.Error("unsupported_safe_write");
        var chars = Marshal.StringToHGlobalUni(name); var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = chars }, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = pointer, Attributes = 0x40 };
            var access = temp ? 0x401F0180u : writeAccess ? 0x40120080u : 0x80120080u;
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, temp ? 1u : 7u,
                temp ? 2u : 1u, 0x200000u | 0x20u | 0x40u, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                handle?.Dispose(); var error = unchecked((int)RtlNtStatusToDosError(status));
                if (writeAccess && error == 5) throw new MutationException("access_denied", "File write permission is required.");
                throw new IOException("Cannot open atomic write file.", new Win32Exception(error));
            }
            EnsureNotReparse(handle); return handle;
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(chars); }
    }

    internal static void CopyMetadata(SafeFileHandle source, SafeFileHandle temp)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(source, 9, out AttributeTag tag, 8)) throw new IOException("Cannot read file attributes.");
            if ((tag.Attributes & 1) != 0) throw new MutationException("access_denied", "Read-only file cannot be replaced.");
            GetKernelObjectSecurity(source, 7, null, 0, out var needed);
            var security = new byte[needed];
            if (!GetKernelObjectSecurity(source, 7, security, needed, out _)
                || !GetSecurityDescriptorControl(security, out var control, out _))
                throw new IOException("Cannot preserve file security.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var information = 7u | ((control & 0x1000) != 0 ? 0x80000000u : 0x20000000u);
            if ((control & 0x400) == 0)
            {
                if (!SetKernelObjectSecurity(temp, information, security)) throw new IOException("Cannot preserve file security.");
            }
            else
            {
                // SetSecurityInfo retains the modern auto-inheritance control
                // bit; SetKernelObjectSecurity would silently clear it.
                var descriptor = Marshal.AllocHGlobal(security.Length);
                try
                {
                    Marshal.Copy(security, 0, descriptor, security.Length);
                    if (!GetSecurityDescriptorOwner(descriptor, out var owner, out _)
                        || !GetSecurityDescriptorGroup(descriptor, out var group, out _)
                        || !GetSecurityDescriptorDacl(descriptor, out _, out var acl, out _)) throw new IOException("Cannot inspect file security.");
                    var error = SetSecurityInfo(temp, 1, information, owner, group, acl, IntPtr.Zero);
                    if (error != 0) throw new IOException("Cannot preserve file security.", new Win32Exception((int)error));
                }
                finally { Marshal.FreeHGlobal(descriptor); }
            }
            var basic = Marshal.AllocHGlobal(40);
            try
            {
                for (var i = 0; i < 40; i++) Marshal.WriteByte(basic, i, 0);
                Marshal.WriteInt32(basic, 32, unchecked((int)tag.Attributes));
                if (!SetFileInformationByHandle(temp, 0, basic, 40)) throw new IOException("Cannot preserve file attributes.");
            }
            finally { Marshal.FreeHGlobal(basic); }
        }
        else
        {
            var stat = Marshal.AllocHGlobal(512);
            try
            {
                var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? FstatDarwin(source, stat) : Fstat(source, stat);
                if (result != 0) throw new IOException("Cannot read file metadata.");
                if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                    throw PathPolicy.Error("unsupported_safe_write");
                var armLinux = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                var mode = OperatingSystem.IsLinux() ? Marshal.ReadInt32(stat, armLinux ? 16 : 24) : (ushort)Marshal.ReadInt16(stat, 4);
                var uid = Marshal.ReadInt32(stat, OperatingSystem.IsLinux() ? (armLinux ? 24 : 28) : 16);
                var gid = Marshal.ReadInt32(stat, OperatingSystem.IsLinux() ? (armLinux ? 28 : 32) : 20);
                if ((mode & 0x92) == 0) throw new MutationException("access_denied", "Read-only file cannot be replaced.");
                var flags = OperatingSystem.IsLinux() ? LinuxFlags(source) : unchecked((uint)Marshal.ReadInt32(stat, 116));
                if ((flags & (OperatingSystem.IsLinux() ? 0x30u : 0x60006u)) != 0)
                    throw new MutationException("access_denied", "Immutable/append-only file cannot be replaced.");
                if (Fchown(temp, uid, gid) != 0 || Fchmod(temp, mode & 0xFFF) != 0) throw new IOException("Cannot preserve file permissions.");
                CopyExtendedAttributes(source, temp);
                if (OperatingSystem.IsMacOS())
                {
                    var acl = AclGetFd(source);
                    if (acl == IntPtr.Zero) throw new IOException("Cannot read file ACL.");
                    try { if (AclSetFd(temp, acl) != 0) throw new IOException("Cannot preserve file ACL."); }
                    finally { AclFree(acl); }
                }
                if (OperatingSystem.IsMacOS())
                {
                    if (Fchflags(temp, flags) != 0) throw new IOException("Cannot preserve file flags.");
                }
                else if (flags != LinuxFlags(temp))
                {
                    var value = (ulong)flags;
                    if (Ioctl(temp, 0x40086602, ref value) != 0) throw new IOException("Cannot preserve file flags.");
                }
            }
            finally { Marshal.FreeHGlobal(stat); }
        }
    }

    private static void CopyExtendedAttributes(SafeFileHandle source, SafeFileHandle temp)
    {
        foreach (var (name, value) in ExtendedAttributes(source))
        {
            var set = OperatingSystem.IsLinux() ? Fsetxattr(temp, name, value, (nuint)value.Length, 0) : DarwinFsetxattr(temp, name, value, (nuint)value.Length, 0, 0);
            if (set != 0) throw new IOException("Cannot preserve file ACL/extended attribute.");
        }
    }

    private static List<(string Name, byte[] Value)> ExtendedAttributes(SafeFileHandle source)
    {
        var attributes = new List<(string, byte[])>();
        var length = OperatingSystem.IsLinux() ? Flistxattr(source, null, 0) : DarwinFlistxattr(source, null, 0, 0);
        if (length < 0)
        {
            // Filesystems without xattr support also cannot carry an xattr ACL.
            if (Marshal.GetLastPInvokeError() is 95 or 45) return attributes;
            throw new IOException("Cannot enumerate file ACL/extended attributes.");
        }
        var names = new byte[checked((int)length)];
        if (length == 0) return attributes;
        length = OperatingSystem.IsLinux() ? Flistxattr(source, names, (nuint)names.Length) : DarwinFlistxattr(source, names, (nuint)names.Length, 0);
        if (length < 0) throw new IOException("Cannot enumerate file ACL/extended attributes.");
        foreach (var name in System.Text.Encoding.UTF8.GetString(names, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var size = OperatingSystem.IsLinux() ? Fgetxattr(source, name, null, 0) : DarwinFgetxattr(source, name, null, 0, 0, 0);
            if (size < 0) throw new IOException("Cannot read file ACL/extended attribute.");
            var value = new byte[checked((int)size)];
            var read = OperatingSystem.IsLinux() ? Fgetxattr(source, name, value, (nuint)value.Length) : DarwinFgetxattr(source, name, value, (nuint)value.Length, 0, 0);
            if (read < 0) throw new IOException("Cannot read file ACL/extended attribute.");
            attributes.Add((name, value[..checked((int)read)]));
        }
        return attributes;
    }

    private static uint LinuxFlags(SafeFileHandle handle)
    {
        ulong value = 0;
        if (Ioctl(handle, 0x80086601, ref value) == 0) return checked((uint)value);
        if (Marshal.GetLastPInvokeError() is 25 or 95) return 0;
        throw new IOException("Cannot inspect file flags.");
    }

    internal static string MetadataFingerprint(SafeFileHandle file)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        if (OperatingSystem.IsWindows())
        {
            GetKernelObjectSecurity(file, 7, null, 0, out var needed);
            var security = new byte[needed];
            if (!GetKernelObjectSecurity(file, 7, security, needed, out _)) throw new IOException("Cannot inspect file security.");
            writer.Write(security);
        }
        else
        {
            var stat = Marshal.AllocHGlobal(512);
            try
            {
                var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? FstatDarwin(file, stat) : Fstat(file, stat);
                if (result != 0) throw new IOException("Cannot inspect file permissions.");
                if (OperatingSystem.IsLinux())
                {
                    var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                    writer.Write(Marshal.ReadInt32(stat, arm ? 16 : 24));
                    writer.Write(Marshal.ReadInt32(stat, arm ? 24 : 28));
                    writer.Write(Marshal.ReadInt32(stat, arm ? 28 : 32));
                    writer.Write(LinuxFlags(file));
                }
                else
                {
                    writer.Write(Marshal.ReadInt16(stat, 4)); writer.Write(Marshal.ReadInt32(stat, 16)); writer.Write(Marshal.ReadInt32(stat, 20));
                    writer.Write(Marshal.ReadInt32(stat, 116));
                    var acl = AclGetFd(file);
                    if (acl == IntPtr.Zero) throw new IOException("Cannot inspect file ACL.");
                    try
                    {
                        var text = AclToText(acl, out var length);
                        if (text == IntPtr.Zero) throw new IOException("Cannot inspect file ACL.");
                        try { writer.Write(Marshal.PtrToStringUTF8(text, checked((int)length))!); }
                        finally { AclFree(text); }
                    }
                    finally { AclFree(acl); }
                }
                foreach (var (name, value) in ExtendedAttributes(file).OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.Write(name); writer.Write(value.Length); writer.Write(value); }
            }
            finally { Marshal.FreeHGlobal(stat); }
        }
        writer.Flush(); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }

    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr info, uint size, int kind);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, int size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(byte[] descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorGroup(IntPtr descriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr acl, IntPtr sacl);
    [DllImport("libc", SetLastError = true, EntryPoint = "acl_get_fd")] private static extern IntPtr AclGetFd(SafeFileHandle handle);
    [DllImport("libc", SetLastError = true, EntryPoint = "acl_set_fd")] private static extern int AclSetFd(SafeFileHandle handle, IntPtr acl);
    [DllImport("libc", EntryPoint = "acl_free")] private static extern int AclFree(IntPtr acl);
    [DllImport("libc", EntryPoint = "acl_to_text")] private static extern IntPtr AclToText(IntPtr acl, out nint length);
    [DllImport("libc", SetLastError = true, EntryPoint = "fchflags")] private static extern int Fchflags(SafeFileHandle handle, uint flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int Ioctl(SafeFileHandle handle, nuint request, ref ulong value);
    [DllImport("libc", SetLastError = true, EntryPoint = "renameat")] private static extern int RenameAt(SafeFileHandle oldParent, string oldName, SafeFileHandle parent, string name);
    [DllImport("libc", SetLastError = true, EntryPoint = "linkat")] private static extern int LinkAt(SafeFileHandle oldParent, string oldName, SafeFileHandle parent, string name, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "unlinkat")] private static extern int UnlinkAt(SafeFileHandle parent, string name, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fchmod")] private static extern int Fchmod(SafeFileHandle handle, int mode);
    [DllImport("libc", SetLastError = true, EntryPoint = "fchown")] private static extern int Fchown(SafeFileHandle handle, int uid, int gid);
    [DllImport("libc", SetLastError = true, EntryPoint = "flistxattr")] private static extern nint Flistxattr(SafeFileHandle handle, byte[]? value, nuint size);
    [DllImport("libc", SetLastError = true, EntryPoint = "fgetxattr")] private static extern nint Fgetxattr(SafeFileHandle handle, string name, byte[]? value, nuint size);
    [DllImport("libc", SetLastError = true, EntryPoint = "fsetxattr")] private static extern int Fsetxattr(SafeFileHandle handle, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "flistxattr")] private static extern nint DarwinFlistxattr(SafeFileHandle handle, byte[]? value, nuint size, int options);
    [DllImport("libc", SetLastError = true, EntryPoint = "fgetxattr")] private static extern nint DarwinFgetxattr(SafeFileHandle handle, string name, byte[]? value, nuint size, uint position, int options);
    [DllImport("libc", SetLastError = true, EntryPoint = "fsetxattr")] private static extern int DarwinFsetxattr(SafeFileHandle handle, string name, byte[] value, nuint size, uint position, int options);
}
