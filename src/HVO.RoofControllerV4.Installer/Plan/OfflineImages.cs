using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// A release's offline image archives (docs/install.md#the-release-it-installs). With the release read from a folder
/// (<c>--release DIR</c>) that lists an archive of an image for this machine's platform, the image is loaded from it:
/// the archive is copied into a folder only root can open and checked there against release.json, Docker loads that
/// copy (never the file in the release's folder, which someone else might change once it is checked), the image it
/// then holds must have one of the IDs the release gives it, and it runs by that ID, never pulled, with the
/// release-image label naming its reference. The deploy script makes its copy of the controller's archive the same way,
/// and checks it against the SHA-256 the installer gives it. A release from GitHub, or one from before offline
/// archives, is pulled by its digest as before.
/// </summary>
public static class OfflineImages
{
    /// <summary>
    /// The room a private copy of an archive must leave on its file system, beside the archive's own size, as the deploy
    /// script's copy leaves: 16 MiB.
    /// </summary>
    internal const long CopyHeadroom = 16L * Mebibyte;

    private const long Mebibyte = 1024 * 1024;

    /// <summary>
    /// The archive <paramref name="image"/> is loaded from on this machine: the release is read from a folder, and lists
    /// one for this machine's platform. Null when the image is pulled.
    /// </summary>
    public static OfflineArchive? For(InstallContext context, ReleaseImage image)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(image);
        return context.Release.FolderPath is null ? null : image.ArchiveFor(ControllerStep.Platform(context.Machine));
    }

    /// <summary>How the image comes to this machine, for a plan: "by digest", or "from &lt;archive&gt;".</summary>
    public static string How(InstallContext context, ReleaseImage image)
        => For(context, image) is { } archive ? $"from {archive.Name}" : "by digest";

    /// <summary>
    /// Why <paramref name="image"/> (<paramref name="what"/>) cannot be loaded from its archive: the release's folder does
    /// not hold the file, or holds one of another size, or the installer's temporary folder has no room for the copy that
    /// Docker loads. Null when it can, or when the image is pulled. Its SHA-256 is checked when it is loaded.
    /// </summary>
    public static async Task<string?> ProblemAsync(InstallContext context, ReleaseManifest release, ReleaseImage image, string what)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (For(context, image) is not { } archive)
        {
            return null;
        }

        var folder = context.Release.FolderPath!;
        var stream = context.Machine.OpenRead(Path.Join(folder, archive.Name));
        if (stream is null)
        {
            return $"{folder} has no {archive.Name}, {what} for {archive.Platform}, which release {release.Version} lists: download it with the release's other files from {ReleaseManifest.PageUri(release.Version)} into {folder}, then run the installer again";
        }

        await using (stream.ConfigureAwait(false))
        {
            return stream.Length == archive.Size
                ? NoRoom(context, archive)
                : $"{folder}'s {archive.Name} is not release {release.Version}'s: it holds {stream.Length} bytes, and release.json says {archive.Size}. Download it again from {ReleaseManifest.PageUri(release.Version)}, then run the installer again";
        }
    }

    // Why the installer's temporary folder, where the installer and the deploy script copy an archive before Docker loads
    // it, has no room for that copy; null when it has, or when the system does not say (the copy then fails if it must).
    private static string? NoRoom(InstallContext context, OfflineArchive archive)
    {
        var folder = context.Machine.TemporaryDirectory;
        return context.Machine.FreeSpace(folder) is { } free && free < archive.Size + CopyHeadroom
            ? $"there is not room in {folder} for a private copy of {archive.Name}, which is what Docker loads, so that it reads only what was checked: "
                + $"it needs {(archive.Size + CopyHeadroom + Mebibyte - 1) / Mebibyte} MiB (the archive and 16 MiB more), and {free / Mebibyte} MiB is free. Free some room there, then run the installer again"
            : null;
    }

    /// <summary>
    /// The archive in the release's folder, once it is checked against release.json's size and SHA-256: what the deploy
    /// script is given (IMAGE_ARCHIVE). The script copies it into a folder of its own, checks that the copy has
    /// release.json's SHA-256 (IMAGE_ARCHIVE_SHA256) and loads the copy, so a change to the file after this check is never
    /// what Docker reads.
    /// </summary>
    public static Task<string> PathAsync(InstallContext context, ReleaseManifest release, OfflineArchive archive, DeployScript.WorkFolder work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReleaseFiles.GetAsync(context, release, archive.Asset, work, cancellationToken);
    }

    /// <summary>
    /// <paramref name="image"/> (<paramref name="what"/>) loaded from <paramref name="archive"/>: copied from the release's
    /// folder into one only the installer's user (root) can open, the copy checked against release.json as it is written
    /// (<see cref="ReleaseFiles.CopyCheckedAsync"/>), that copy loaded by Docker, and the image found by one of the IDs the
    /// release gives it (<see cref="ReleaseImage.LoadedIds"/>), for the archive's platform. The copy is deleted once the
    /// load is done, whether it worked or not. Returns that ID, which <c>docker run</c> runs with <see cref="RunOptions"/>.
    /// </summary>
    public static async Task<string> LoadAsync(InstallContext context, ReleaseManifest release, ReleaseImage image, OfflineArchive archive, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(archive);
        using var work = new DeployScript.WorkFolder(context.Machine);
        var path = Path.Join(context.Release.FolderPath!, archive.Name);
        if (NoRoom(context, archive) is { } noRoom)
        {
            throw new InstallerException($"{char.ToUpperInvariant(noRoom[0])}{noRoom[1..]}.");
        }

        var copy = Path.Join(work.Path, archive.Name);
        await ReleaseFiles.CopyCheckedAsync(context, archive.Asset, path, copy, cancellationToken).ConfigureAwait(false);
        context.Progress?.Invoke($"Loading {archive.Name} ({archive.Size / Mebibyte} MB)…");
        var load = await context.Machine.Commands.RunAsync(new CommandLine("docker", "load", "-i", copy) { Timeout = TimeSpan.FromMinutes(20) }, cancellationToken).ConfigureAwait(false);
        if (!load.Succeeded)
        {
            throw new InstallerException($"Could not load {what} from {path}: {load.Reason}");
        }

        foreach (var id in image.LoadedIds(archive))
        {
            var inspect = await context.Machine.Commands.RunAsync(new CommandLine("docker", "image", "inspect", "--format", "{{.Id}} {{.Os}}/{{.Architecture}}", id), cancellationToken).ConfigureAwait(false);
            if (!inspect.Succeeded || inspect.Output.Trim().Split(' ') is not [var found, var platform] || found != id)
            {
                continue;
            }

            if (platform != archive.Platform)
            {
                throw new InstallerException($"Docker loaded {path} as the image {id}, which is for {platform}, not {archive.Platform}: it is not release {release.Version}'s {what} for this machine.");
            }

            context.Log.Write($"Loaded {what} from {path}, through a copy checked against release.json: the image {id}, release {release.Version}'s {image.Reference}. Nothing was pulled.");
            return id;
        }

        throw new InstallerException(
            $"Docker loaded {path}, and holds no image with an ID release.json gives {what}: {string.Join(", ", image.LoadedIds(archive))}. "
            + $"Download {archive.Name} again from {ReleaseManifest.PageUri(release.Version)}, then run the installer again.");
    }

    /// <summary>
    /// What <c>docker run</c> takes, before the image's ID, for an image loaded from an archive: never pull, and the
    /// release-image label naming the image's reference, which the survey reads (<see cref="ContainerSurvey.ReleaseImage"/>).
    /// </summary>
    public static IReadOnlyList<string> RunOptions(ReleaseImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ["--pull", "never", "--label", $"{MachineSurveyor.ReleaseImageLabel}={image.Reference}"];
    }
}
