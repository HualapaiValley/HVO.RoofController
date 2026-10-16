using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// A release's offline image archives (docs/install.md#the-release-it-installs). With the release read from a folder
/// (<c>--release DIR</c>) that lists an archive of an image for this machine's platform, the image is loaded from it:
/// the archive is checked against release.json, Docker loads it, the image it then holds must have one of the IDs the
/// release gives it, and it runs by that ID, never pulled, with the release-image label naming its reference. A release
/// from GitHub, or one from before offline archives, is pulled by its digest as before.
/// </summary>
public static class OfflineImages
{
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
    /// not hold the file, or holds one of another size. Null when it can, or when the image is pulled. Its SHA-256 is
    /// checked when it is loaded.
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
                ? null
                : $"{folder}'s {archive.Name} is not release {release.Version}'s: it holds {stream.Length} bytes, and release.json says {archive.Size}. Download it again from {ReleaseManifest.PageUri(release.Version)}, then run the installer again";
        }
    }

    /// <summary>
    /// The archive in the release's folder, once it is checked against release.json's size and SHA-256: what the deploy
    /// script loads (IMAGE_ARCHIVE).
    /// </summary>
    public static Task<string> PathAsync(InstallContext context, ReleaseManifest release, OfflineArchive archive, DeployScript.WorkFolder work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReleaseFiles.GetAsync(context, release, archive.Asset, work, cancellationToken);
    }

    /// <summary>
    /// <paramref name="image"/> (<paramref name="what"/>) loaded from <paramref name="archive"/>: checked against
    /// release.json, loaded by Docker, and found by one of the IDs the release gives it (<see cref="ReleaseImage.LoadedIds"/>),
    /// for the archive's platform. Returns that ID, which <c>docker run</c> runs with <see cref="RunOptions"/>.
    /// </summary>
    public static async Task<string> LoadAsync(InstallContext context, ReleaseManifest release, ReleaseImage image, OfflineArchive archive, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(image);
        using var work = new DeployScript.WorkFolder(context.Machine);
        var path = await PathAsync(context, release, archive, work, cancellationToken).ConfigureAwait(false);
        context.Progress?.Invoke($"Loading {archive.Name} ({archive.Size / (1024 * 1024)} MB)…");
        var load = await context.Machine.Commands.RunAsync(new CommandLine("docker", "load", "-i", path) { Timeout = TimeSpan.FromMinutes(20) }, cancellationToken).ConfigureAwait(false);
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

            context.Log.Write($"Loaded {what} from {path}: the image {id}, release {release.Version}'s {image.Reference}. Nothing was pulled.");
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
