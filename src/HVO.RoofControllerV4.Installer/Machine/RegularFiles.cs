using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.RoofControllerV4.Installer.Machine;

/// <summary>
/// What is at a path is not a regular file (a FIFO, a device, a socket or a folder), and nothing was read from it.
/// </summary>
public sealed class NotARegularFileException(string path, string kind) : IOException($"{path} is {kind}, not a file")
{
    /// <summary>What it is: "a FIFO", "a character device", "a socket", …</summary>
    public string Kind => kind;
}

/// <summary>
/// Reading a file only when it is a regular file, without ever waiting on it: it is opened without blocking (a FIFO with
/// no writer would hold an ordinary open forever), then what was opened is checked by its descriptor (statx(2) on Linux,
/// fstat(2) on a Mac), so a path swapped for something else between a check and the open is never read either.
/// </summary>
internal static class RegularFiles
{
    // O_RDONLY (0) with O_NONBLOCK and O_CLOEXEC: the same on every Linux architecture the installer runs on, and on macOS.
    private const int LinuxFlags = 0x800 | 0x80000;
    private const int MacFlags = 0x4 | 0x1000000;

    // errno: the same on Linux and macOS, but for EOPNOTSUPP (opening a socket on a Mac).
    private const int NotPermitted = 1;
    private const int NotThere = 2;
    private const int Interrupted = 4;
    private const int NoDevice = 6;
    private const int Denied = 13;
    private const int NotAFolder = 20;
    private const int LinuxNotSupported = 95;
    private const int MacNotSupported = 45;

    // statx(2): AT_EMPTY_PATH (the descriptor itself) and STATX_TYPE; struct statx, whose stx_mask is at 0 and stx_mode at
    // 28 on every architecture. A Mac's struct stat (arm64, the only Mac the installer runs on) has st_mode at 4.
    private const int EmptyPath = 0x1000;
    private const uint TypeWanted = 0x1;
    private const int StatxModeAt = 28;
    private const int MacModeAt = 4;

    // S_IFMT and the types under it, the same on Linux and macOS.
    private const int TypeBits = 0xF000;
    private const int Fifo = 0x1000;
    private const int CharacterDevice = 0x2000;
    private const int Folder = 0x4000;
    private const int BlockDevice = 0x6000;
    private const int Regular = 0x8000;
    private const int Socket = 0xC000;

    /// <summary>
    /// The file at <paramref name="onDisk"/>, read whole; null when there is nothing there. Something there that is not a
    /// regular file throws <see cref="NotARegularFileException"/> (naming it <paramref name="path"/>) before anything is
    /// read from it; one the installer may not open throws <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static byte[]? Read(string onDisk, string path)
    {
        using var handle = Open(onDisk, path);
        if (handle is null)
        {
            return null;
        }

        using var content = new MemoryStream();
        var buffer = new byte[81920];
        long offset = 0;
        int read;
        while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
        {
            content.Write(buffer, 0, read);
            offset += read;
        }

        return content.ToArray();
    }

    private static SafeFileHandle? Open(string onDisk, string path)
    {
        var flags = OperatingSystem.IsMacOS() ? MacFlags : LinuxFlags;
        int descriptor;
        int error;
        do
        {
            descriptor = open(onDisk, flags);
            error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (descriptor < 0 && error == Interrupted);

        if (descriptor < 0)
        {
            return error switch
            {
                NotThere or NotAFolder => null,
                Denied or NotPermitted => throw new UnauthorizedAccessException($"Access to the path '{onDisk}' is denied."),
                NoDevice or LinuxNotSupported or MacNotSupported => throw new NotARegularFileException(path, "a socket or a device"),
                _ => throw new IOException($"Could not open {onDisk}: {Marshal.GetPInvokeErrorMessage(error)}.", error)
            };
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            var type = TypeOf(handle, onDisk);
            if (type != Regular)
            {
                throw new NotARegularFileException(path, type switch
                {
                    Fifo => "a FIFO",
                    CharacterDevice => "a character device",
                    BlockDevice => "a block device",
                    Socket => "a socket",
                    Folder => "a folder",
                    _ => "not a regular file"
                });
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // The type of what the descriptor is open on (its mode's S_IFMT bits).
    private static int TypeOf(SafeFileHandle handle, string onDisk)
    {
        var buffer = new byte[256];
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var descriptor = (int)handle.DangerousGetHandle();
            if (OperatingSystem.IsMacOS())
            {
                return fstat(descriptor, buffer) == 0
                    ? BitConverter.ToUInt16(buffer, MacModeAt) & TypeBits
                    : throw new IOException($"Could not tell what {onDisk} is: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}.");
            }

            if (statx(descriptor, string.Empty, EmptyPath, TypeWanted, buffer) != 0)
            {
                throw new IOException($"Could not tell what {onDisk} is: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}.");
            }

            return (BitConverter.ToUInt32(buffer, 0) & TypeWanted) != 0
                ? BitConverter.ToUInt16(buffer, StatxModeAt) & TypeBits
                : throw new IOException($"Could not tell what {onDisk} is: statx did not say.");
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int statx(int descriptor, string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int descriptor, byte[] buffer);
}
