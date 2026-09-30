using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// hvo-roof and the Mac app on a person's own machine: the release's files for them, the controller's CA as the
/// controller serves it, and a Mac's ditto, xattr, security and launchctl, answered from what the test set up.
/// </summary>
internal sealed partial class FakeMachine
{
    /// <summary>The controller's CA in these tests: a certificate alone (its key was never kept), the same each run.</summary>
    public const string ControllerCaPem = """
        -----BEGIN CERTIFICATE-----
        MIIBvTCCAWSgAwIBAgIUWP5MfB98MQkT6loH+GrDyzPbhzMwCgYIKoZIzj0EAwIw
        KzEpMCcGA1UEAwwgSFZPIFJvb2YgQ0EgKHJvb2ZwaSwgMjAyNi0xMC0wMSkwHhcN
        MjYwOTMwMjMxNTAyWhcNMzYwOTI3MjMxNTAyWjArMSkwJwYDVQQDDCBIVk8gUm9v
        ZiBDQSAocm9vZnBpLCAyMDI2LTEwLTAxKTBZMBMGByqGSM49AgEGCCqGSM49AwEH
        A0IABKafDQ9iRy/n7Paf3/8ktZo3MTf4hlNxHUWy2CsksB1Y8S8CwDoD40+8B1tV
        t73gHv0g6g/wMKsnIfgSPo5j63SjZjBkMB8GA1UdIwQYMBaAFBVTPFr3OduCvJAH
        xUvp9f3U+NkbMBIGA1UdEwEB/wQIMAYBAf8CAQAwDgYDVR0PAQH/BAQDAgEGMB0G
        A1UdDgQWBBQVUzxa9znbgryQB8VL6fX91PjZGzAKBggqhkjOPQQDAgNHADBEAiAM
        So6lp+xxMjkj+Z+O5g/UcV9vblU7V0tTjapc3oerIQIgIcCzontoTjHhYa1RLId7
        F1o6AOaaTvaIKBjRa7csHaA=
        -----END CERTIFICATE-----
        """;

    /// <summary>The CA's name, as the controller's installer names it.</summary>
    public const string ControllerCaName = "HVO Roof CA (roofpi, 2026-10-01)";

    /// <summary>The CA's SHA-256 fingerprint, as <c>hvo-roof-install cert show</c> shows it.</summary>
    public const string ControllerCaSha256 = "FB:26:8B:E2:34:20:52:AF:FB:D8:05:14:87:EB:B5:D6:4A:D8:6D:4E:65:BC:35:26:C7:D2:31:11:2A:D1:C0:76";

    /// <summary>The controller hvo-roof and the Mac app are set up against in these tests.</summary>
    public const string ControllerUrl = "https://roofpi.local:8443";

    /// <summary>The answers for a client of <see cref="ControllerUrl"/>, trusting its CA by <see cref="ControllerCaSha256"/>.</summary>
    public static ClientSettings ClientAnswers { get; } = new() { Controller = ControllerUrl, CaSha256 = ControllerCaSha256 };

    /// <summary>The platforms the release has hvo-roof for, as build/release-assets.py names them.</summary>
    public static readonly IReadOnlyList<string> CliPlatforms = ["linux-arm64", "linux-x64", "osx-arm64"];

    private const string QuarantineMark = ".fake-" + ClientSteps.QuarantineAttribute;

    /// <summary>
    /// What the controller at an address serves at <c>GET /ca.crt</c>, checked against its TLS handshake: by default
    /// <see cref="ControllerCaPem"/> from any address.
    /// </summary>
    public Func<Uri, CancellationToken, Task<X509Certificate2>> FetchCa { get; set; }
        = (_, _) => Task.FromResult(RoofCertificateAuthority.FromPem(ControllerCaPem));

    /// <summary>Each address the installer fetched a controller's CA from, in order.</summary>
    public ConcurrentQueue<Uri> CaFetches { get; } = new();

    /// <summary>The controller's API, in process; null for none (a request fails as the client's own would).</summary>
    public Func<HttpMessageHandler>? ApiHandler { get; set; }

    /// <summary>The session a Mac's installer runs in (launchctl managername): Aqua on its own screen, Background over SSH.</summary>
    public string MacSession { get; set; } = "Aqua";

    /// <summary>True when macOS's quarantine mark stays on an app whatever xattr is told (a file the person cannot change).</summary>
    public bool QuarantineSticks { get; set; }

    /// <summary>The certificates in the login keychain, trusted for websites, by their SHA-256 (hex): their names.</summary>
    public Dictionary<string, string> KeychainTrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <c>security add-trusted-cert</c> says when the person does not give their password; null when they do.</summary>
    public string? KeychainRefusal { get; set; }

    /// <summary>What the Mac app's <c>--check</c> exits with: 0 once its window is drawn.</summary>
    public int MacCheckExitCode { get; set; }

    /// <summary>The settings folder of each run of the Mac app's <c>--check</c>, in order.</summary>
    public List<string?> MacChecks { get; } = [];

    /// <summary>hvo-roof in the fake release, for <paramref name="platform"/>: a script that says which it is.</summary>
    public static byte[] CliProgram(string version, string platform) => Encoding.UTF8.GetBytes($"#!/bin/sh\necho 'hvo-roof {version} ({platform})'\n");

    /// <summary>The name of the fake release's hvo-roof for <paramref name="platform"/>, as build/release-assets.py names it.</summary>
    public static string CliAssetName(string platform) => $"hvo-roof-{platform}";

    /// <summary>The Mac app's program in the fake release.</summary>
    public static byte[] MacProgram(string version) => Encoding.UTF8.GetBytes($"#!/bin/sh\necho 'HVO Roof {version}'\n");

    /// <summary>The name of the fake release's Mac app, as build/release-assets.py names it.</summary>
    public static string MacAssetName(string version) => $"HVO-Roof-{version}.zip";

    /// <summary>
    /// The fake release's Mac app, zipped as the release workflow zips it: <c>HVO Roof.app</c> at the top, the same bytes
    /// each time. <paramref name="program"/> replaces its program (one that is not the release's).
    /// </summary>
    public static byte[] MacAppZip(string version, byte[]? program = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] content, UnixFileMode mode)
            {
                var entry = zip.CreateEntry($"{MachineSurveyor.MacAppBundle}/{name}", CompressionLevel.Optimal);
                entry.LastWriteTime = Today;

                // A regular file's Unix mode, in the high half, as zip on a Mac writes it.
                entry.ExternalAttributes = (0x8000 | (int)mode) << 16;
                using var stream = entry.Open();
                stream.Write(content);
            }

            Add("Contents/Info.plist", Encoding.UTF8.GetBytes($"<plist><dict><key>CFBundleShortVersionString</key><string>{version}</string></dict></plist>\n"), Modes.File);
            Add($"Contents/MacOS/{ClientSteps.MacProgramName}", program ?? MacProgram(version), Modes.Program);
        }

        return output.ToArray();
    }

    /// <summary>The fake release's files for hvo-roof and the Mac app, by name.</summary>
    public static IEnumerable<(string Name, byte[] Content)> ClientAssets(string version)
        => CliPlatforms.Select(platform => (CliAssetName(platform), CliProgram(version, platform))).Append((MacAssetName(version), MacAppZip(version)));

    /// <summary>
    /// Puts the release's Mac app of <paramref name="version"/> in <paramref name="folder"/>, as a person who downloaded it
    /// would have it: with macOS's quarantine mark when <paramref name="quarantined"/>.
    /// </summary>
    public FakeMachine WithMacApp(string folder, string version = "4.0.0", bool quarantined = false, byte[]? program = null)
    {
        Folder(folder);
        using (var zip = new ZipArchive(new MemoryStream(MacAppZip(version, program)), ZipArchiveMode.Read))
        {
            zip.ExtractToDirectory(OnDisk(folder));
        }

        if (quarantined)
        {
            Quarantine(Path.Join(folder, MachineSurveyor.MacAppBundle));
        }

        return this;
    }

    /// <summary>Marks <paramref name="app"/> as macOS marks a download.</summary>
    public void Quarantine(string app) => File.WriteAllText(Path.Join(OnDisk(app), QuarantineMark), string.Empty);

    /// <summary>True when <paramref name="app"/> carries macOS's quarantine mark.</summary>
    public bool IsQuarantined(string app) => File.Exists(Path.Join(OnDisk(app), QuarantineMark));

    // The release.json entries for ClientAssets, as build/release-assets.py writes them.
    private static IEnumerable<object> ClientAssetEntries(string version)
    {
        foreach (var platform in CliPlatforms)
        {
            var program = CliProgram(version, platform);
            yield return new
            {
                name = CliAssetName(platform),
                kind = ClientSteps.CliAssetKind,
                platform,
                size = program.LongLength,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(program))
            };
        }

        var zip = MacAppZip(version);
        yield return new
        {
            name = MacAssetName(version),
            kind = ClientSteps.MacAssetKind,
            platform = ClientSteps.MacAssetPlatform,
            size = zip.LongLength,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)),
            files = new Dictionary<string, string> { [ClientSteps.MacProgramName] = Convert.ToHexStringLower(SHA256.HashData(MacProgram(version))) }
        };
    }

    // A Mac's commands for hvo-roof and the Mac app; null for one this fake does not know.
    private CommandResult? ClientCommand(CommandLine command)
    {
        var arguments = command.Arguments;
        if (Os != InstallerOs.MacOS)
        {
            return null;
        }

        return command.Program switch
        {
            "launchctl" when arguments is ["managername"] => Answer(MacSession),
            "ditto" when arguments is ["-x", "-k", "--noqtn", var zip, var into] => Unzip(zip, into),
            "xattr" when arguments is ["-r", var app] => Directory.Exists(OnDisk(app))
                ? Answer(IsQuarantined(app) ? $"{app}: {ClientSteps.QuarantineAttribute}" : string.Empty)
                : new CommandResult(1, string.Empty, $"xattr: No such file: {app}\n"),
            "xattr" when arguments is ["-dr", ClientSteps.QuarantineAttribute, var app] => Answer(string.Empty, after: () =>
            {
                if (!QuarantineSticks && IsQuarantined(app))
                {
                    File.Delete(Path.Join(OnDisk(app), QuarantineMark));
                }
            }),
            "security" when arguments is ["find-certificate", "-a", "-Z", "-c", var name, _] => KeychainTrusted.Where(entry => entry.Value.Contains(name, StringComparison.Ordinal)).ToArray() is { Length: > 0 } found
                ? Answer(string.Join('\n', found.Select(entry => $"SHA-256 hash: {entry.Key.ToUpperInvariant()}\nkeychain: \"login.keychain-db\"")))
                : new CommandResult(44, string.Empty, "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.\n"),
            "security" when arguments is ["dump-trust-settings"] => KeychainTrusted.Count > 0
                ? Answer($"Number of trusted certs = {KeychainTrusted.Count}\n" + string.Join('\n', KeychainTrusted.Values.Select((name, index) => $"Cert {index}: {name}\n   Number of trust settings : 1")))
                : new CommandResult(1, string.Empty, "SecTrustSettingsCopyCertificates: No Trust Settings were found.\n"),
            "security" when arguments is ["add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", _, var file] => KeychainRefusal is { } refusal
                ? new CommandResult(1, string.Empty, refusal + "\n")
                : Answer(string.Empty, after: () =>
                {
                    using var authority = RoofCertificateAuthority.FromPem(Read(file));
                    KeychainTrusted[Convert.ToHexString(authority.GetCertHash(HashAlgorithmName.SHA256))] = authority.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                }),
            _ when command.Program.EndsWith($"/Contents/MacOS/{ClientSteps.MacProgramName}", StringComparison.Ordinal) && arguments is ["--check"] => Exists(command.Program)
                ? MacCheck(command)
                : new CommandResult(CommandResult.NotFound, string.Empty, $"{command.Program}: not found"),
            _ => null
        };
    }

    private CommandResult Unzip(string zip, string into)
    {
        if (!File.Exists(OnDisk(zip)) || !Directory.Exists(OnDisk(into)))
        {
            return new CommandResult(1, string.Empty, $"ditto: {zip}: No such file or directory\n");
        }

        ZipFile.ExtractToDirectory(OnDisk(zip), OnDisk(into));
        return Answer(string.Empty);
    }

    private CommandResult MacCheck(CommandLine command)
    {
        MacChecks.Add(command.Environment?.GetValueOrDefault(ClientSteps.MacSettingsVariable));
        return MacCheckExitCode == 0
            ? Answer(string.Empty)
            : new CommandResult(MacCheckExitCode, string.Empty, "The window was not drawn within 60 seconds.\n");
    }
}
