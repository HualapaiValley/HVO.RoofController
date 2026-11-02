using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HVO.RoofControllerV4.Common.IO;

/// <summary>
/// Reads at most 4096 bytes of public metadata from one Unix regular-file descriptor. The final path component is never
/// followed if it is a symbolic link, and a FIFO is opened nonblocking and refused before reading. Descriptor checks
/// cannot be invalidated by replacing the pathname between a check and a read.
/// </summary>
public static class UnixRegularFileReader
{
    /// <summary>The largest public metadata file accepted, in bytes.</summary>
    public const int MaximumBytes = 4096;

    private const int Interrupted = 4;
    private const int EmptyPath = 0x1000;
    private const uint TypeAndSize = 0x1 | 0x200;
    private const int TypeBits = 0xF000;
    private const int Regular = 0x8000;

    /// <summary>
    /// Reads a regular file within <see cref="MaximumBytes"/>, detecting the same Unicode byte order marks as
    /// <see cref="File.ReadAllText(string)"/>. Missing paths throw <see cref="FileNotFoundException"/> or
    /// <see cref="DirectoryNotFoundException"/>; links, special files and oversized files throw <see cref="IOException"/>.
    /// No exception contains the path or its contents.
    /// </summary>
    public static string ReadAllText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Public metadata file reads require a supported Unix platform.");

        using var handle = OpenRegular(path);
        var buffer = new byte[MaximumBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(length), length);
            if (read == 0) break;
            length += read;
        }
        // Bound the read itself even if a writer grows a file after the first descriptor check. No sentinel byte beyond
        // the 4096-byte limit is read; the same descriptor's size is checked again before any payload is returned.
        if (Metadata(handle).Size > MaximumBytes)
            throw new IOException("Public metadata exceeds the 4096-byte limit.");

        using var text = new StreamReader(new MemoryStream(buffer, 0, length, writable: false), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return text.ReadToEnd();
    }

    private static SafeFileHandle OpenRegular(string path)
    {
        var flags = FlagsForOpen(OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        int descriptor;
        int error;
        do
        {
            descriptor = open(path, flags);
            error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (descriptor < 0 && error == Interrupted);
        if (descriptor < 0)
        {
            throw error switch
            {
                2 => new FileNotFoundException("The public metadata file does not exist."),
                20 => new DirectoryNotFoundException("The public metadata directory does not exist."),
                1 or 13 => new UnauthorizedAccessException("The public metadata file cannot be read."),
                _ => new IOException("The public metadata file could not be opened without following links.", error)
            };
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            var metadata = Metadata(handle);
            if ((metadata.Mode & TypeBits) != Regular)
                throw new IOException("Public metadata must be a regular file.");
            if (metadata.Size > MaximumBytes)
                throw new IOException("Public metadata exceeds the 4096-byte limit.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // O_RDONLY is zero. Linux O_NONBLOCK/O_CLOEXEC are shared by x64 and arm64, but O_NOFOLLOW is not:
    // arch/arm64/include/uapi/asm/fcntl.h uses 0x8000 (0100000), while asm-generic/fcntl.h uses 0x20000 (00400000).
    // Darwin bsd/sys/fcntl.h uses O_NONBLOCK=0x4, O_NOFOLLOW=0x100, O_CLOEXEC=0x1000000.
    internal static int FlagsForOpen(bool isMacOS, Architecture architecture) => (isMacOS, architecture) switch
    {
        (false, Architecture.X64) => 0x800 | 0x20000 | 0x80000,
        (false, Architecture.Arm64) => 0x800 | 0x8000 | 0x80000,
        (true, Architecture.Arm64) => 0x4 | 0x100 | 0x1000000,
        _ => throw new PlatformNotSupportedException("The metadata reader supports Linux x64/arm64 and macOS arm64.")
    };

    private static (int Mode, ulong Size) Metadata(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var descriptor = (int)handle.DangerousGetHandle();
            if (OperatingSystem.IsMacOS())
            {
                // Darwin arm64 struct stat: st_mode at 4 (uint16), st_size at 96 (int64).
                if (fstat(descriptor, buffer) != 0 || BitConverter.ToInt64(buffer, 96) < 0)
                    throw new IOException("The public metadata descriptor could not be checked.");
                return (BitConverter.ToUInt16(buffer, 4), (ulong)BitConverter.ToInt64(buffer, 96));
            }

            // Linux struct statx has the same layout on x64 and arm64: stx_mask at 0, stx_mode at 28, stx_size at 40.
            if (statx(descriptor, string.Empty, EmptyPath, TypeAndSize, buffer) != 0
                || (BitConverter.ToUInt32(buffer, 0) & TypeAndSize) != TypeAndSize)
                throw new IOException("The public metadata descriptor could not be checked.");
            return (BitConverter.ToUInt16(buffer, 28), BitConverter.ToUInt64(buffer, 40));
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int statx(int descriptor, string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int descriptor, byte[] buffer);
}
