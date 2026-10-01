using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>An image a release names: where it is, and the digest it is pulled by.</summary>
/// <param name="Repository">The image's repository: ghcr.io/hualapaivalley/roof-controller.</param>
/// <param name="Digest">sha256:&lt;64 hex digits&gt;: the image index both platforms share.</param>
/// <param name="Reference">The repository, the release's tag and the digest: what Docker pulls.</param>
public sealed record ReleaseImage(string Repository, string Digest, string Reference);

/// <summary>A file a release has besides its images, such as the kiosk's tarball.</summary>
/// <param name="Name">Its name among the release's files: letters, digits, dots, dashes and underscores.</param>
/// <param name="Kind">What it is: cli, kiosk, mac-app, compose or deploy-script.</param>
/// <param name="Platform">The runtime it is built for (linux-arm64), or null when it runs anywhere.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Sha256">Its SHA-256, 64 lower-case hex digits.</param>
/// <param name="Files">For an archive, the SHA-256 of each file in it, by name; empty otherwise.</param>
public sealed record ReleaseAsset(string Name, string Kind, string? Platform, long Size, string Sha256, IReadOnlyDictionary<string, string> Files);

/// <summary>What to know before upgrading to a release, from its docs/upgrade-notes/X.Y.Z.md.</summary>
/// <param name="Version">The release the notes are for: X.Y.Z.</param>
/// <param name="Text">The notes, a few lines.</param>
public sealed record UpgradeNote(string Version, string Text);

/// <summary>
/// A release's <c>release.json</c> (build/release-assets.py writes it): its version, the images it is made of, each
/// pinned to its digest, and its other files with their SHA-256. The installer deploys those images and files and no
/// others.
/// </summary>
public sealed partial record ReleaseManifest(string Version, string? Commit, ReleaseImage Controller, ReleaseImage HatEmulator)
{
    public const string FileName = "release.json";

    /// <summary>The schema this installer reads.</summary>
    public const int Schema = 1;

    /// <summary>Where a release's files are on GitHub.</summary>
    public const string ReleasesUrl = "https://github.com/HualapaiValley/HVO.RoofController/releases";

    /// <summary>Where the latest release has its release.json: GitHub's latest release, drafts and pre-releases aside.</summary>
    public static readonly Uri LatestUri = new($"{ReleasesUrl}/latest/download/{FileName}");

    /// <summary>The release's page on GitHub, with its notes.</summary>
    public static Uri PageUri(string version) => new($"{ReleasesUrl}/tag/v{version}");

    /// <summary>Where the release of <paramref name="version"/> has its release.json.</summary>
    public static Uri DownloadUri(string version) => DownloadUri(version, FileName);

    /// <summary>Where the release of <paramref name="version"/> has the file <paramref name="name"/>.</summary>
    public static Uri DownloadUri(string version, string name) => new($"{ReleasesUrl}/download/v{version}/{Uri.EscapeDataString(name)}");

    /// <summary>The release's files besides its images; empty for a release.json that lists none.</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>
    /// Every release's upgrade notes up to this one's, oldest first: an upgrade shows each one newer than the release it
    /// upgrades from. Empty when there are none.
    /// </summary>
    public IReadOnlyList<UpgradeNote> UpgradeNotes { get; init; } = [];

    /// <summary>
    /// The version a release.json is for, without checking the rest: what <c>upgrade</c> reads first to know which
    /// release it goes to. Throws <see cref="InstallerException"/> when it is not a release.json of a version.
    /// </summary>
    public static string PeekVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var version = document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, "version") : null;
            return version is not null && RoofSemVer.IsValid(version) ? version : throw Invalid("it names no version");
        }
        catch (JsonException error)
        {
            throw new InstallerException($"The installer cannot read {FileName}: it is not valid JSON ({error.Message}).");
        }
    }

    /// <summary>
    /// The release's <paramref name="kind"/> for <paramref name="platform"/>. Throws <see cref="InstallerException"/>
    /// when it has none.
    /// </summary>
    public ReleaseAsset Asset(string kind, string platform)
        => Assets.FirstOrDefault(asset => asset.Kind == kind && asset.Platform == platform)
            ?? throw Invalid($"it has no {kind} for {platform}");

    /// <summary>
    /// Reads release.json, and checks it is the release of <paramref name="version"/> with both images pinned to a
    /// digest. Throws <see cref="InstallerException"/> saying what is wrong.
    /// </summary>
    public static ReleaseManifest Parse(string json, string version)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("it is not a JSON object");
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number)
            {
                throw Invalid("it has no schemaVersion");
            }

            if (!schema.TryGetInt32(out var number) || number != Schema)
            {
                throw Invalid($"it is schema {schema.GetRawText()}, and this installer reads schema {Schema}: use that release's installer");
            }

            var released = Text(root, "version") ?? throw Invalid("it has no version");
            if (released != version)
            {
                throw Invalid($"it is the release of {released}, and this installer installs {version}");
            }

            if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("it lists no images");
            }

            return new ReleaseManifest(released, Text(root, "commit"), Image(images, "controller"), Image(images, "hatEmulator"))
            {
                Assets = ReadAssets(root),
                UpgradeNotes = ReadUpgradeNotes(root)
            };
        }
        catch (JsonException error)
        {
            throw new InstallerException($"The installer cannot read {FileName}: it is not valid JSON ({error.Message}).");
        }
    }

    private static ReleaseImage Image(JsonElement images, string key)
    {
        if (!images.TryGetProperty(key, out var image) || image.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"it has no {key} image");
        }

        var repository = Text(image, "repository");
        var digest = Text(image, "digest");
        var reference = Text(image, "reference");
        if (repository is null || !RepositoryPattern().IsMatch(repository))
        {
            throw Invalid($"the {key} image's repository is not an image repository");
        }

        if (digest is null || !DigestPattern().IsMatch(digest))
        {
            throw Invalid($"the {key} image's digest is not sha256:<64 hex digits>");
        }

        if (reference is null || !reference.StartsWith(repository + ":", StringComparison.Ordinal) || !reference.EndsWith("@" + digest, StringComparison.Ordinal)
            || !ReferencePattern().IsMatch(reference))
        {
            throw Invalid($"the {key} image's reference is not its repository, a tag and its digest");
        }

        return new ReleaseImage(repository, digest, reference);
    }

    private static UpgradeNote[] ReadUpgradeNotes(JsonElement root)
    {
        if (!root.TryGetProperty("upgradeNotes", out var notes) || notes.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (notes.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("its upgrade notes are not a list");
        }

        return [.. notes.EnumerateArray().Select((note, index) =>
            note.ValueKind == JsonValueKind.Object && Text(note, "version") is { } version && RoofSemVer.IsValid(version) && Text(note, "text") is { } text
                ? new UpgradeNote(version, text)
                : throw Invalid($"upgrade note {index + 1} is not a release's version and its text"))];
    }

    private static ReleaseAsset[] ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets))
        {
            return [];
        }

        if (assets.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("its assets are not a list");
        }

        return [.. assets.EnumerateArray().Select(ReadAsset)];
    }

    private static ReleaseAsset ReadAsset(JsonElement asset, int index)
    {
        if (asset.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"asset {index + 1} is not a JSON object");
        }

        var name = Text(asset, "name");
        if (name is null || !FileNamePattern().IsMatch(name))
        {
            throw Invalid($"asset {index + 1}'s name is not a file name");
        }

        var kind = Text(asset, "kind") ?? throw Invalid($"{name} has no kind");
        string? platform = null;
        if (asset.TryGetProperty("platform", out var platformValue) && platformValue.ValueKind != JsonValueKind.Null)
        {
            platform = Text(asset, "platform") ?? throw Invalid($"{name}'s platform is not a runtime");
        }

        if (!asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size < 0)
        {
            throw Invalid($"{name}'s size is not a number of bytes");
        }

        var sha256 = Text(asset, "sha256");
        if (sha256 is null || !Sha256Pattern().IsMatch(sha256))
        {
            throw Invalid($"{name}'s sha256 is not 64 lower-case hex digits");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (asset.TryGetProperty("files", out var filesValue))
        {
            if (filesValue.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"{name}'s files are not a JSON object");
            }

            foreach (var file in filesValue.EnumerateObject())
            {
                if (!FileNamePattern().IsMatch(file.Name) || file.Value.ValueKind != JsonValueKind.String
                    || file.Value.GetString() is not { } hash || !Sha256Pattern().IsMatch(hash))
                {
                    throw Invalid($"{name}'s file {file.Name} has no SHA-256 of 64 lower-case hex digits");
                }

                files[file.Name] = hash;
            }
        }

        return new ReleaseAsset(name, kind, platform, size, sha256, files);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static InstallerException Invalid(string reason) => new($"The installer will not use {FileName}: {reason}.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._/:-]*$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    // A name alone, never a path: the installer saves an asset by its name.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex FileNamePattern();

    // As the deploy script checks IMAGE_REF.
    [GeneratedRegex("^[a-z0-9][A-Za-z0-9._/:-]*@sha256:[0-9a-f]{64}$")]
    private static partial Regex ReferencePattern();
}

/// <summary>
/// Where the installer gets the release it installs: release.json from a folder (<c>--release DIR</c>, for a machine
/// that cannot reach GitHub, or a release not published yet), or from the release on GitHub. Read once a run.
/// </summary>
public sealed class ReleaseSource
{
    private readonly string? _folder;
    private Task<ReleaseManifest>? _manifest;

    private ReleaseSource(string? folder) => _folder = folder;

    /// <summary>The release on GitHub.</summary>
    public static ReleaseSource GitHub() => new(null);

    /// <summary>The release.json in <paramref name="folder"/>, a folder on the machine.</summary>
    public static ReleaseSource Folder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new ReleaseSource(folder);
    }

    /// <summary>The folder it reads, or null for GitHub.</summary>
    public string? FolderPath => _folder;

    /// <summary>
    /// The release of <paramref name="version"/>, read the first time it is asked for; a failure says how to go on, and
    /// the next call reads it again (a download that timed out may work the second time).
    /// </summary>
    public Task<ReleaseManifest> GetAsync(InstallerMachine machine, InstallLog log, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(log);
        if (_manifest is { IsFaulted: true } or { IsCanceled: true })
        {
            _manifest = null;
        }

        return _manifest ??= LoadAsync(machine, log, version, cancellationToken);
    }

    /// <summary>
    /// The version this source's release is for: the folder's release.json, or GitHub's latest release. What
    /// <c>upgrade</c> goes to when it is not given a version.
    /// </summary>
    public async Task<string> LatestVersionAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var (json, _) = await ReadAsync(machine, ReleaseManifest.LatestUri, "the latest release", cancellationToken).ConfigureAwait(false);
        return ReleaseManifest.PeekVersion(json);
    }

    private async Task<ReleaseManifest> LoadAsync(InstallerMachine machine, InstallLog log, string version, CancellationToken cancellationToken)
    {
        var (json, from) = await ReadAsync(machine, ReleaseManifest.DownloadUri(version), version, cancellationToken).ConfigureAwait(false);
        var manifest = ReleaseManifest.Parse(json, version);
        log.Write($"The release: {manifest.Version} from {from}; the controller {manifest.Controller.Reference}, the HAT emulator {manifest.HatEmulator.Reference}.");
        return manifest;
    }

    private async Task<(string Json, string From)> ReadAsync(InstallerMachine machine, Uri uri, string what, CancellationToken cancellationToken)
    {
        if (_folder is not null)
        {
            var file = Path.Join(_folder, ReleaseManifest.FileName);
            return (machine.ReadText(file) ?? throw new InstallerUsageException($"There is no {ReleaseManifest.FileName} in {_folder}."), file);
        }

        try
        {
            return (await machine.DownloadTextAsync(uri, cancellationToken).ConfigureAwait(false), uri.ToString());
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new InstallerException(
                $"The installer could not get {ReleaseManifest.FileName} for {what} from {uri}: {error.Message} "
                + $"Download the release's files from {ReleaseManifest.ReleasesUrl} on a machine that can, and give their folder: --release DIR.");
        }
    }
}
