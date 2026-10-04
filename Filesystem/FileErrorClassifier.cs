using System.ComponentModel;

namespace FilesystemMcp;

/// <summary>
/// Single classification point for filesystem I/O failures shared by every entry
/// point (read/search/list/atomic write). Classifies by HResult and by the
/// underlying Win32 error instead of matching message text, so a sharing
/// violation is never confused with an access-denied or missing-file failure.
/// FS-04 introduced the classifier; FS-05 completes the code table
/// (<c>directory_not_found</c> for a missing path component) and renders every
/// code through the single <see cref="ToolErrorMapper"/> mapping.
/// </summary>
internal static class FileErrorClassifier
{
    internal const string FileLocked = ToolErrorCodes.FileLocked;
    internal const string AccessDenied = ToolErrorCodes.AccessDenied;
    internal const string FileNotFound = ToolErrorCodes.FileNotFound;
    internal const string DirectoryNotFound = ToolErrorCodes.DirectoryNotFound;

    private const uint HResultFromWin32 = 0x80070000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int UnixAccessDeniedErrno = 13;
    private const int UnixOperationNotPermittedErrno = 1;

    internal static bool TryGetCode(Exception? exception, out string code)
    {
        code = string.Empty;
        var isWindows = OperatingSystem.IsWindows();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var mapped = ClassifyForPlatform(current, isWindows);
            if (mapped is not null)
            {
                code = mapped;
                return true;
            }
        }

        return false;
    }

    internal static string? TryGetCode(Exception? exception) =>
        TryGetCode(exception, out var code) ? code : null;

    /// <summary>True only for transient sharing/lock violations that may be retried.</summary>
    internal static bool IsSharingViolation(Exception exception) =>
        TryGetCode(exception, out var code) && code == FileLocked;

    /// <summary>
    /// Wraps a recognized filesystem failure into a typed operational error so
    /// <see cref="Program"/> maps it to <c>result.isError</c> with the same code
    /// everywhere. Cancellation, path-policy and mutation exceptions (including
    /// <c>hash_conflict</c>, <c>unsupported_encoding</c>, <c>target_not_found</c>)
    /// are returned unchanged, so no mutation is retried or misclassified.
    /// </summary>
    internal static Exception Translate(Exception exception)
    {
        if (exception is PathPolicyException or MutationException or OperationalException or OperationCanceledException)
        {
            return exception;
        }

        if (!TryGetCode(exception, out var code))
        {
            return exception;
        }

        // The canonical code message is reused here, so one code never renders two
        // different client-facing texts and no platform message (which may embed an
        // absolute path) can reach a client. The original failure stays as InnerException.
        return new MutationException(code, ToolErrorMessages.ForCode(code), exception);
    }

    /// <summary>
    /// Classifies a single exception for an explicit platform. The platform is a
    /// parameter (not a mutable global) so the Unix/Win32 split is unit-testable on
    /// any host without affecting concurrent callers.
    /// </summary>
    internal static string? ClassifyForPlatform(Exception exception, bool isWindows)
    {
        if (exception is OperationCanceledException or PathPolicyException or MutationException or OperationalException)
        {
            return null;
        }

        // .NET's not-found types are the authoritative signal for *which* component is
        // missing: an errno of ENOENT (or ERROR_PATH_NOT_FOUND in the low 16 bits) cannot
        // separate a missing file from a missing containing directory, so the type decides
        // first. The platform tables below stay for native error codes without a .NET type.
        if (exception is FileNotFoundException)
        {
            return FileNotFound;
        }

        if (exception is DirectoryNotFoundException)
        {
            return DirectoryNotFound;
        }

        // Manually constructed Win32Exception keeps the native code but not the
        // HRESULT, so inspect NativeErrorCode before falling back to HResult. On
        // Unix the same field carries errno, so the Win32 table must never apply
        // there (errno 32 is EPIPE, not a sharing violation).
        if (exception is Win32Exception win32)
        {
            var fromNative = isWindows ? FromWin32Error(win32.NativeErrorCode) : ClassifyUnixErrno(win32.NativeErrorCode);
            if (fromNative is not null)
            {
                return fromNative;
            }
        }

        var hresult = unchecked((uint)exception.HResult);
        if (isWindows)
        {
            switch (hresult)
            {
                case HResultFromWin32 | (uint)ErrorSharingViolation:
                case HResultFromWin32 | (uint)ErrorLockViolation:
                    return FileLocked;
                case HResultFromWin32 | (uint)ErrorAccessDenied:
                    return AccessDenied;
                case HResultFromWin32 | (uint)ErrorFileNotFound:
                    return FileNotFound;
                // ERROR_PATH_NOT_FOUND is the missing *containing* directory, which is a
                // different client action from a missing file, so it is never folded in.
                case HResultFromWin32 | (uint)ErrorPathNotFound:
                    return DirectoryNotFound;
            }
        }
        else if ((hresult & 0xFFFF0000) == HResultFromWin32)
        {
            // Unix encodes errno in the low 16 bits; sharing is advisory on POSIX,
            // so no lock code is claimed there.
            var mapped = ClassifyUnixErrno((int)(hresult & 0xFFFF));
            if (mapped is not null)
            {
                return mapped;
            }
        }

        if (exception is UnauthorizedAccessException)
        {
            return AccessDenied;
        }

        return null;
    }

    /// <summary>
    /// Maps a POSIX errno to an operational code. Sharing is advisory on POSIX, so
    /// this deliberately never returns <see cref="FileLocked"/>. Separately callable
    /// so the mapping is testable on any host.
    /// </summary>
    internal static string? ClassifyUnixErrno(int errno) => errno switch
    {
        UnixAccessDeniedErrno or UnixOperationNotPermittedErrno => AccessDenied,
        ErrorFileNotFound => FileNotFound,
        _ => null
    };

    private static string? FromWin32Error(int error) => error switch
    {
        ErrorSharingViolation or ErrorLockViolation => FileLocked,
        ErrorAccessDenied => AccessDenied,
        ErrorFileNotFound => FileNotFound,
        ErrorPathNotFound => DirectoryNotFound,
        _ => null
    };
}
