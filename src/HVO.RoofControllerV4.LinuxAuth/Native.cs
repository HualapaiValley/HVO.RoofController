using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HVO.RoofControllerV4.LinuxAuth;

internal static class Native
{
    [DllImport("libc", SetLastError = true)] internal static extern uint geteuid();
    [DllImport("libc")] internal static extern uint umask(uint mask);
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int socket, int level, int option, out PeerCredentials value, ref uint length);
    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint owner, uint group);
    [DllImport("libc", SetLastError = true)] private static extern int getpwnam_r(string name, out Passwd record, IntPtr buffer, nuint length, out IntPtr result);
    [DllImport("libc", SetLastError = true)] private static extern int getspnam_r(string name, out Shadow record, IntPtr buffer, nuint length, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] internal struct PeerCredentials { public int Pid; public uint Uid; public uint Gid; }
    [StructLayout(LayoutKind.Sequential)] private struct Passwd
    {
        public IntPtr Name, Password;
        public uint Uid, Gid;
        public IntPtr Gecos, Directory, Shell;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Shadow
    {
        public IntPtr Name, Password;
        public long LastChange, Minimum, Maximum, Warn, Inactive, Expire;
        public ulong Flag;
    }
    internal static PeerCredentials Peer(Socket socket)
    {
        uint size = 12;
        if (getsockopt(socket.Handle.ToInt32(), 1, 17, out var result, ref size) != 0 || size != 12 || result.Pid <= 0)
            throw new UnauthorizedAccessException();
        return result;
    }
    internal static void SetOwner(string path, uint group)
    {
        if (chown(path, 0, group) != 0) throw new UnauthorizedAccessException();
    }
    internal static void NotifyReady()
    {
        var destination = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(destination)) return;
        if (destination[0] == '@') destination = "\0" + destination[1..];
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        socket.SendTo(Encoding.UTF8.GetBytes("READY=1"), new UnixDomainSocketEndPoint(destination));
    }
    internal static bool MatchesAccount(string userName, uint uid) => AccountMatches(userName, uid) == true;
    internal static bool? AccountMatches(string userName, uint uid)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (getpwnam_r(userName, out var record, buffer, 65536, out var result) != 0) return null;
            return result != IntPtr.Zero && record.Uid == uid && string.Equals(Marshal.PtrToStringUTF8(record.Name), userName, StringComparison.Ordinal);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    /// <summary>
    /// The account's full name: the first comma-separated field of its GECOS entry, trimmed (#209). Null when the account
    /// is not <paramref name="userName"/> with <paramref name="uid"/>, or has no full name. Display only.
    /// </summary>
    internal static string? FullName(string userName, uint uid)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (getpwnam_r(userName, out var record, buffer, 65536, out var result) != 0 || result == IntPtr.Zero || record.Uid != uid
                || !string.Equals(Marshal.PtrToStringUTF8(record.Name), userName, StringComparison.Ordinal))
                return null;
            return GecosFullName(record.Gecos == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(record.Gecos));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The full name in a GECOS field (<c>Full Name,Room,Work phone,Home phone,Other</c>), or null.</summary>
    internal static string? GecosFullName(string? gecos)
        => gecos?.Split(',', 2)[0].Trim() is { Length: > 0 and <= 256 } name ? name : null;

    internal static bool PasswordIsUnlocked(string userName) => PasswordUnlocked(userName) == true;
    internal static bool? PasswordUnlocked(string userName)
    {
        // pam_unix account management does not reject a password locked with passwd -l.
        // Inspect only the first byte of the host-native shadow result; never marshal/log the hash.
        // Linux web/session identities deliberately require an enabled local password account.
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (getspnam_r(userName, out var record, buffer, 65536, out var result) != 0) return null;
            if (result == IntPtr.Zero || record.Password == IntPtr.Zero) return false;
            return Marshal.ReadByte(record.Password) is not (0 or (byte)'!' or (byte)'*');
        }
        finally
        {
            // This buffer can contain a password hash. Erase it before releasing native memory.
            for (var index = 0; index < 65536; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>Open without following a final link; check the opened inode, and all trusted parent directories.</summary>
internal static class TrustedFiles
{
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int create(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int flock(int descriptor, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, IntPtr result);
    private const int AtEmptyPath = 0x1000, AtNoFollow = 0x100, AtCurrentDirectory = -100;
    internal static byte[] Read(string path)
    {
        ValidateDirectory(Path.GetDirectoryName(path)!);
        var descriptor = open(path, 0x80000 | 0x20000); // O_CLOEXEC | O_NOFOLLOW, read-only.
        if (descriptor < 0) throw new UnauthorizedAccessException();
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        Validate(descriptor, "", AtEmptyPath, directory: false);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException();
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }
    internal static SafeFileHandle LockWorker(string directory)
    {
        ValidateDirectory(directory);
        var descriptor = create(Path.Combine(directory, "worker.lock"), 0x80000 | 0x20000 | 0x40 | 0x1, 0x180);
        if (descriptor < 0) throw new UnauthorizedAccessException();
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            Validate(descriptor, "", AtEmptyPath, directory: false);
            if (flock(descriptor, 2 | 4) != 0) throw new UnauthorizedAccessException(); // LOCK_EX | LOCK_NB
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void RemoveStaleSocket(string path)
    {
        ValidateDirectory(Path.GetDirectoryName(path)!);
        // Called only while holding the exclusive worker lock. No unprivileged process can
        // replace a leaf in the trusted parent. Refuse links, regular files and foreign sockets.
        var data = Marshal.AllocHGlobal(256);
        try
        {
            if (statx(AtCurrentDirectory, path, AtNoFollow, 0x7ff, data) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) return; // ENOENT
                throw new UnauthorizedAccessException();
            }
            if (Marshal.ReadInt32(data, 20) != 0 || ((ushort)Marshal.ReadInt16(data, 28) & 0xf000) != 0xc000)
                throw new UnauthorizedAccessException();
            File.Delete(path);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void ValidateDirectory(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (true)
        {
            Validate(AtCurrentDirectory, current, AtNoFollow, directory: true);
            if (current == "/") break;
            current = Path.GetDirectoryName(current) ?? throw new UnauthorizedAccessException();
        }
    }
    private static void Validate(int descriptor, string path, int flags, bool directory)
    {
        // statx ABI is fixed on supported Linux arm64 and x64 (unlike libc struct stat).
        var data = Marshal.AllocHGlobal(256);
        try
        {
            if (statx(descriptor, path, flags, 0x7ff, data) != 0) throw new UnauthorizedAccessException();
            var uid = (uint)Marshal.ReadInt32(data, 20);
            var mode = (ushort)Marshal.ReadInt16(data, 28);
            var expected = directory ? 0x4000 : 0x8000;
            if (uid != 0 || (mode & 0xf000) != expected || (mode & 0x12) != 0 // group/other write
                || !directory && (mode & 0x7) != 0) // no world access to private configuration/key
                throw new UnauthorizedAccessException();
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
