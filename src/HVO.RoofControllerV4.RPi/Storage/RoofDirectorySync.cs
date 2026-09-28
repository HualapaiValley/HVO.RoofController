using System;
using System.Runtime.InteropServices;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>
/// Flushes a directory's entries to disk. After a file is renamed over another, the rename itself can sit in memory
/// for the file system's commit interval (about 5 seconds on ext4); a power cut in that window brings the old file
/// back. Flushing the directory makes a confirmed save survive it.
/// </summary>
internal static class RoofDirectorySync
{
    // O_RDONLY | O_CLOEXEC; both have the same value on every Linux architecture the controller runs on.
    private const int OpenReadOnlyCloseOnExec = 0x80000;

    /// <summary>
    /// Best effort: true when <paramref name="directory"/> was flushed; false on other systems or when it could not be.
    /// Never throws.
    /// </summary>
    public static bool TryFlush(string directory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            var descriptor = Open(directory, OpenReadOnlyCloseOnExec);
            if (descriptor < 0)
            {
                return false;
            }

            try
            {
                return Fsync(descriptor) == 0;
            }
            finally
            {
                _ = Close(descriptor);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // DllImport rather than LibraryImport: the generated LibraryImport stubs need unsafe code, which this project
    // otherwise has no use for. The runtime maps "libc" to the system C library on Linux.
    [DllImport("libc", EntryPoint = "open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync")]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
