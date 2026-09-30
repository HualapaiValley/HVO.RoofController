using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>An image a release names: where it is, and the digest it is pulled by.</summary>
/// <param name="Repository">The image's repository: ghcr.io/hualapaivalley/roof-controller.</param>
/// <param name="Digest">sha256:&lt;64 hex digits&gt;: the image index both platforms share.</param>
/// <param name="Reference">The repository, the release's tag and the digest: what Docker pulls.</param>
public sealed record ReleaseImage(string Repository, string Digest, string Reference);

/// <summary>
/// A release's <c>release.json</c> (build/release-assets.py writes it): its version and the images it is made of, each
/// pinned to its digest. The installer deploys those images and no others.
/// </summary>
public sealed partial record ReleaseManifest(string Version, string? Commit, ReleaseImage Controller, ReleaseImage HatEmulator)
{
    public const string FileName = "release.json";

    /// <summary>The schema this installer reads.</summary>
    public const int Schema = 1;

    /// <summary>Where a release's files are on GitHub.</summary>
    public const string ReleasesUrl = "https://github.com/HualapaiValley/HVO.RoofController/releases";

    /// <summary>Where the release of <paramref name="version"/> has its release.json.</summary>
    public static Uri DownloadUri(string version) => new($"{ReleasesUrl}/download/v{version}/{FileName}");

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

            return new ReleaseManifest(released, Text(root, "commit"), Image(images, "controller"), Image(images, "hatEmulator"));
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

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static InstallerException Invalid(string reason) => new($"The installer will not use {FileName}: {reason}.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._/:-]*$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();

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

    private async Task<ReleaseManifest> LoadAsync(InstallerMachine machine, InstallLog log, string version, CancellationToken cancellationToken)
    {
        string json;
        string from;
        if (_folder is not null)
        {
            from = Path.Join(_folder, ReleaseManifest.FileName);
            json = machine.ReadText(from) ?? throw new InstallerUsageException($"There is no {ReleaseManifest.FileName} in {_folder}.");
        }
        else
        {
            var uri = ReleaseManifest.DownloadUri(version);
            from = uri.ToString();
            try
            {
                json = await machine.DownloadTextAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
                || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new InstallerException(
                    $"The installer could not get {ReleaseManifest.FileName} for {version} from {uri}: {error.Message} "
                    + $"Download the release's files from {ReleaseManifest.ReleasesUrl} on a machine that can, and give their folder: --release DIR.");
            }
        }

        var manifest = ReleaseManifest.Parse(json, version);
        log.Write($"The release: {manifest.Version} from {from}; the controller {manifest.Controller.Reference}, the HAT emulator {manifest.HatEmulator.Reference}.");
        return manifest;
    }
}
