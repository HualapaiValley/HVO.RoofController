using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Installer.Machine;

/// <summary>The operating systems the installer supports.</summary>
public enum InstallerOs
{
    Linux,
    MacOS
}

/// <summary>
/// The machine the installer runs on. Every path the installer names is a path on this machine (<c>/etc/hvo-roof</c>);
/// <see cref="Root"/> is where that path is on disk: <c>/</c> on the real machine, a folder of its own in a test, so a
/// test can give the installer a whole fake Pi. Programs run through <see cref="Commands"/>, which a test fakes too.
/// </summary>
public sealed class InstallerMachine
{
    public required string Root { get; init; }

    public required ICommandRunner Commands { get; init; }

    public required InstallerOs Os { get; init; }

    public required Architecture Architecture { get; init; }

    /// <summary>True when the installer runs as root (with sudo).</summary>
    public required bool IsRoot { get; init; }

    /// <summary>The user the installer runs as.</summary>
    public required string UserName { get; init; }

    /// <summary>That user's home folder, as a path on this machine.</summary>
    public required string Home { get; init; }

    public required string HostName { get; init; }

    /// <summary>The installer's environment.</summary>
    public Func<string, string?> Environment { get; init; } = global::System.Environment.GetEnvironmentVariable;

    /// <summary>The folder the installer was started in: where the wizard offers to save answers.</summary>
    public string CurrentDirectory { get; init; } = "/";

    /// <summary>This machine, running its commands through <paramref name="commands"/> (one that logs them, for one).</summary>
    public InstallerMachine WithCommands(ICommandRunner commands) => new()
    {
        Root = Root,
        Commands = commands,
        Os = Os,
        Architecture = Architecture,
        IsRoot = IsRoot,
        UserName = UserName,
        Home = Home,
        HostName = HostName,
        Environment = Environment,
        CurrentDirectory = CurrentDirectory,
        IsPortInUse = IsPortInUse,
        NetworkAddresses = NetworkAddresses,
        ServedCertificateAsync = ServedCertificateAsync,
        DownloadTextAsync = DownloadTextAsync,
        DownloadToAsync = DownloadToAsync,
        FetchCaAsync = FetchCaAsync,
        ApiHandler = ApiHandler,
        TemporaryDirectory = TemporaryDirectory
    };

    /// <summary>
    /// True when something on this machine accepts connections on the TCP port: it answers on the loopback address
    /// within a second.
    /// </summary>
    public Func<int, bool> IsPortInUse { get; init; } = AnswersOnLoopback;

    /// <summary>The machine's addresses on the interfaces that are up, with each interface's name (the loopback's left out).</summary>
    public Func<IReadOnlyList<NetworkAddress>> NetworkAddresses { get; init; } = CurrentAddresses;

    /// <summary>
    /// The certificate presented over TLS on the TCP port, on the loopback address, or null when nothing there answers
    /// with TLS within a few seconds. Nothing is sent but the handshake.
    /// </summary>
    public Func<int, CancellationToken, Task<X509Certificate2?>> ServedCertificateAsync { get; init; } = PresentedOnLoopbackAsync;

    /// <summary>
    /// Downloads a small text file over HTTPS (release.json): its text, or an <see cref="HttpRequestException"/> saying
    /// why not. Redirects are followed; anything over a megabyte is refused.
    /// </summary>
    public Func<Uri, CancellationToken, Task<string>> DownloadTextAsync { get; init; } = DownloadAsync;

    /// <summary>
    /// Downloads a file over HTTPS (a release's kiosk) into the stream, or throws an <see cref="HttpRequestException"/>
    /// saying why not, or an <see cref="InvalidDataException"/> when it is larger than the most given. Redirects are
    /// followed.
    /// </summary>
    public Func<Uri, Stream, long, CancellationToken, Task> DownloadToAsync { get; init; } = DownloadStreamAsync;

    /// <summary>
    /// The CA a controller serves at <c>GET /ca.crt</c>, checked against the certificate it presents (see
    /// <see cref="RoofCertificateAuthority.FetchAsync"/>): what hvo-roof and the Mac app trust once its fingerprint is the
    /// one the person gave.
    /// </summary>
    public Func<Uri, CancellationToken, Task<X509Certificate2>> FetchCaAsync { get; init; }
        = (controller, cancellationToken) => RoofCertificateAuthority.FetchAsync(controller, TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>
    /// The innermost handler for the controller's API, or null for the client's own. A test puts a fake controller
    /// here; the installer never changes how the certificate is checked.
    /// </summary>
    public Func<HttpMessageHandler>? ApiHandler { get; init; }

    /// <summary>The folder for the installer's temporary files, as a path on this machine.</summary>
    public string TemporaryDirectory { get; init; } = Path.GetTempPath();

    /// <summary>The platform as release assets name it: linux-arm64, linux-x64 or osx-arm64 (or another the installer refuses).</summary>
    public string RuntimeIdentifier => $"{(Os == InstallerOs.MacOS ? "osx" : "linux")}-{Architecture.ToString().ToLowerInvariant()}";

    /// <summary>Where <paramref name="path"/>, a path on this machine, is on disk.</summary>
    public string OnDisk(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));
        }

        return Root == "/" ? path : Path.Join(Root, path);
    }

    public bool FileExists(string path) => File.Exists(OnDisk(path));

    public bool DirectoryExists(string path) => Directory.Exists(OnDisk(path));

    /// <summary>The file's text, or null when there is no such file.</summary>
    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(OnDisk(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file's bytes, or null when there is no such file.</summary>
    public byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(OnDisk(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file's or folder's permission bits, or null when it does not exist.</summary>
    public UnixFileMode? GetMode(string path)
    {
        var onDisk = OnDisk(path);
        return File.Exists(onDisk) || Directory.Exists(onDisk) ? File.GetUnixFileMode(onDisk) : null;
    }

    /// <summary>When the file was last written, or null when there is no such file (or it cannot be seen).</summary>
    public DateTimeOffset? LastWritten(string path)
    {
        var onDisk = OnDisk(path);
        return File.Exists(onDisk) ? new DateTimeOffset(File.GetLastWriteTimeUtc(onDisk), TimeSpan.Zero) : null;
    }

    /// <summary>The files in the folder (not in the folders within it), as paths on this machine; none when it is not there.</summary>
    public IReadOnlyList<string> ListFiles(string path)
    {
        var onDisk = OnDisk(path);
        return Directory.Exists(onDisk)
            ? [.. Directory.EnumerateFiles(onDisk).Select(file => Path.Join(path, Path.GetFileName(file))).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The names of the entries in the folder (files, folders and links), sorted; none when it is not there.</summary>
    public IReadOnlyList<string> ListNames(string path)
    {
        var onDisk = OnDisk(path);
        return Directory.Exists(onDisk)
            ? [.. Directory.EnumerateFileSystemEntries(onDisk).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="path"/>, a new file only its owner may read, of no more than
    /// <paramref name="maxBytes"/>: a download that fails leaves no file.
    /// </summary>
    public async Task DownloadFileAsync(Uri uri, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var onDisk = OnDisk(path);
        var stream = new FileStream(onDisk, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = Modes.PrivateFile
        });
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await DownloadToAsync(uri, stream, maxBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(onDisk);
            throw;
        }
    }

    /// <summary>Deletes the folder and everything in it; nothing when it is not there.</summary>
    public void DeleteDirectory(string path)
    {
        var onDisk = OnDisk(path);
        if (Directory.Exists(onDisk))
        {
            Directory.Delete(onDisk, recursive: true);
        }
    }

    public void SetMode(string path, UnixFileMode mode) => File.SetUnixFileMode(OnDisk(path), mode);

    /// <summary>Makes the folder, and any parent missing, with <paramref name="mode"/> (the parents get 0755).</summary>
    public void CreateDirectory(string path, UnixFileMode mode)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !DirectoryExists(parent))
        {
            CreateDirectory(parent, Modes.Folder);
        }

        Directory.CreateDirectory(OnDisk(path), mode);

        // The umask may have taken bits off.
        SetMode(path, mode);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> in one step: to a new file beside it, made with
    /// <paramref name="mode"/> from the start (so a secret is never readable by others, even briefly), then renamed over
    /// the old one. Someone reading the file sees the old content or the new, never part of it.
    /// </summary>
    public void WriteAtomically(string path, byte[] content, UnixFileMode mode)
    {
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode
            }))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void WriteAtomically(string path, string content, UnixFileMode mode)
        => WriteAtomically(path, Encoding.UTF8.GetBytes(content), mode);

    /// <summary>
    /// Replaces a file's text in one step, through a new file renamed over it, and sets no mode: for a file system that
    /// keeps none (the Pi's FAT boot partition, where setting one fails).
    /// </summary>
    public void ReplaceText(string path, string content)
    {
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> in one step, as <see cref="WriteAtomically(string, byte[], UnixFileMode)"/> does,
    /// with what <paramref name="write"/> puts in the stream it is given: when it throws, the old file stays as it was.
    /// </summary>
    public async Task WriteAtomicallyAsync(string path, Func<Stream, CancellationToken, Task> write, UnixFileMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var onDisk = OnDisk(path);
        var temporary = $"{onDisk}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode
            });
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, onDisk, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> in one step, with <paramref name="mode"/>.</summary>
    public Task CopyFileAsync(string source, string destination, UnixFileMode mode, CancellationToken cancellationToken)
        => WriteAtomicallyAsync(
            destination,
            async (stream, token) =>
            {
                var from = OpenRead(source) ?? throw new FileNotFoundException($"{source} is not there.", source);
                await using (from.ConfigureAwait(false))
                {
                    await from.CopyToAsync(stream, token).ConfigureAwait(false);
                }
            },
            mode,
            cancellationToken);

    /// <summary>The file, open to read, or null when there is no such file.</summary>
    public Stream? OpenRead(string path)
    {
        try
        {
            return new FileStream(OnDisk(path), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file's SHA-256, in lower-case hex, or null when there is no such file.</summary>
    public async Task<string?> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = OpenRead(path);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Renames <paramref name="source"/> over <paramref name="destination"/>, in one step.</summary>
    public void MoveFile(string source, string destination) => File.Move(OnDisk(source), OnDisk(destination), overwrite: true);

    /// <summary>Renames the folder <paramref name="source"/> to <paramref name="destination"/>, which must not be there.</summary>
    public void MoveDirectory(string source, string destination) => Directory.Move(OnDisk(source), OnDisk(destination));

    /// <summary>True when the installer may make files in the folder (or change the file): the system says it may write there.</summary>
    public bool IsWritable(string path) => access(OnDisk(path), WriteAccess) == 0;

    /// <summary>Deletes the file; nothing when it is not there.</summary>
    public void DeleteFile(string path) => File.Delete(OnDisk(path));

    /// <summary>Adds a line to the end of the file, making the file with <paramref name="mode"/> when it is not there.</summary>
    public void AppendLine(string path, string line, UnixFileMode mode)
    {
        using var stream = new FileStream(OnDisk(path), new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            UnixCreateMode = mode
        });
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
    }

    /// <summary>The machine the installer is running on.</summary>
    public static InstallerMachine Current()
    {
        var root = geteuid() == 0;
        var user = global::System.Environment.UserName;
        var home = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);
        return new InstallerMachine
        {
            Root = "/",
            Commands = new ProcessCommandRunner(),
            Os = OperatingSystem.IsMacOS() ? InstallerOs.MacOS : InstallerOs.Linux,
            Architecture = RuntimeInformation.OSArchitecture,
            IsRoot = root,
            UserName = string.IsNullOrEmpty(user) ? (root ? "root" : "unknown") : user,
            Home = string.IsNullOrEmpty(home) ? "/" : home,
            HostName = System.Net.Dns.GetHostName(),
            CurrentDirectory = global::System.Environment.CurrentDirectory
        };
    }

    private static async Task<string> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = limit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hvo-roof-install");
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadStreamAsync(Uri uri, Stream destination, long limit, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hvo-roof-install");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is > 0 and var length && length > limit)
        {
            throw new InvalidDataException($"It is {length} bytes, more than the {limit} expected.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException($"It is more than the {limit} bytes expected.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool AnswersOnLoopback(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static IReadOnlyList<NetworkAddress> CurrentAddresses()
    {
        var passing = PassingAddresses();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown
                && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(unicast => new NetworkAddress(network.Name, unicast.Address)
            {
                Temporary = passing.Contains((network.Name, unicast.Address))
            }))
            .ToArray();
    }

    // Linux says which IPv6 addresses are temporary or deprecated in /proc/net/if_inet6; .NET does not. macOS has no such
    // file, and its addresses count as lasting.
    private static IReadOnlySet<(string Interface, IPAddress Address)> PassingAddresses()
    {
        try
        {
            return File.Exists("/proc/net/if_inet6") ? NetworkAddress.PassingIn(File.ReadAllText("/proc/net/if_inet6")) : new HashSet<(string, IPAddress)>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HashSet<(string, IPAddress)>();
        }
    }

    private static async Task<X509Certificate2?> PresentedOnLoopbackAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        X509Certificate2? presented = null;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            // Only read: whatever it presents is accepted, and nothing is sent over the connection.
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        presented ??= certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                        return true;
                    }
                },
                timeout.Token).ConfigureAwait(false);
            return presented;
        }
        catch (Exception error) when (error is SocketException or IOException or System.Security.Authentication.AuthenticationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            presented?.Dispose();
            return null;
        }
    }

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    // W_OK, the same on Linux and macOS.
    private const int WriteAccess = 2;

    [DllImport("libc", SetLastError = false, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int access(string path, int mode);
}

/// <summary>An address of the machine, and the network interface it is on (eth0, wlan0, docker0).</summary>
public sealed record NetworkAddress(string Interface, IPAddress Address)
{
    // The kernel's address flags (linux/if_addr.h): a temporary (privacy) address, and one that is on its way out.
    private const int TemporaryFlag = 0x01;
    private const int DeprecatedFlag = 0x20;

    /// <summary>
    /// A temporary (privacy) or deprecated IPv6 address: the machine drops it within a day or so, so a certificate that
    /// named it would name an address the controller no longer has.
    /// </summary>
    public bool Temporary { get; init; }

    /// <summary>The temporary and deprecated addresses in <c>/proc/net/if_inet6</c>'s text, with their interfaces.</summary>
    public static IReadOnlySet<(string Interface, IPAddress Address)> PassingIn(string ifInet6)
    {
        ArgumentNullException.ThrowIfNull(ifInet6);
        var passing = new HashSet<(string, IPAddress)>();

        // Each line: the address in 32 hex digits, the interface's index, the prefix length, the scope, the flags, the name.
        foreach (var line in ifInet6.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields is [var hex, _, _, _, var flags, var name]
                && hex.Length == 32
                && int.TryParse(flags, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && (value & (TemporaryFlag | DeprecatedFlag)) != 0)
            {
                try
                {
                    passing.Add((name, new IPAddress(Convert.FromHexString(hex))));
                }
                catch (FormatException)
                {
                }
            }
        }

        return passing;
    }
}

/// <summary>The permissions the installer gives what it makes.</summary>
public static class Modes
{
    /// <summary>0755: a folder anyone may read.</summary>
    public const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>0750: a folder its owner and group may read.</summary>
    public const UnixFileMode GroupFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    /// <summary>0700: a folder only its owner may open.</summary>
    public const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>0644: a file anyone may read.</summary>
    public const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>0640: a file its owner and group may read.</summary>
    public const UnixFileMode GroupFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>0600: a file only its owner may read.</summary>
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>0400: a file only its owner may read, and nobody writes.</summary>
    public const UnixFileMode OwnerReadOnly = UnixFileMode.UserRead;

    /// <summary>0755: a program.</summary>
    public const UnixFileMode Program = Folder;

    /// <summary>The mode as <c>ls</c> and <c>chmod</c> write it: 0755.</summary>
    public static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');
}
