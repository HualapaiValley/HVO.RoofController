using System.Security.Cryptography;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>A release's files other than its images: the kiosk's tarball, hvo-roof, the Mac app's zip, the installer.</summary>
public static class ReleaseFiles
{
    /// <summary>The release's asset of <paramref name="kind"/> for <paramref name="platform"/>, or null when it has none.</summary>
    public static ReleaseAsset? Find(ReleaseManifest release, string kind, string platform)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Assets.FirstOrDefault(asset => asset.Kind == kind && asset.Platform == platform);
    }

    /// <summary>
    /// The release's <paramref name="asset"/> on this machine: from the release's folder (<c>--release DIR</c>), or
    /// downloaded into <paramref name="work"/>; checked either way against release.json's size and SHA-256, so a file
    /// that differs is never used.
    /// </summary>
    public static async Task<string> GetAsync(InstallContext context, ReleaseManifest release, ReleaseAsset asset, DeployScript.WorkFolder work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(work);
        string path;
        if (context.Release.FolderPath is { } folder)
        {
            path = Path.Join(folder, asset.Name);
            if (!context.Machine.FileExists(path))
            {
                throw new InstallerException($"There is no {asset.Name} in {folder}: download it with the release's other files from {ReleaseManifest.ReleasesUrl}.");
            }
        }
        else
        {
            path = Path.Join(work.Path, asset.Name);
            var uri = ReleaseManifest.DownloadUri(release.Version, asset.Name);
            context.Progress?.Invoke($"Downloading {asset.Name} ({asset.Size / (1024 * 1024)} MB)…");
            try
            {
                await context.Machine.DownloadFileAsync(uri, path, asset.Size, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
                || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new InstallerException(
                    $"The installer could not download {asset.Name} from {uri}: {error.Message} "
                    + $"Download the release's files from {ReleaseManifest.ReleasesUrl} on a machine that can, and give their folder: --release DIR.");
            }
        }

        var stream = context.Machine.OpenRead(path) ?? throw new InstallerException($"{path} is not there.");
        await using (stream.ConfigureAwait(false))
        {
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (stream.Length != asset.Size || sha256 != asset.Sha256)
            {
                throw new InstallerException($"{path} is not the release's {asset.Name}: its size or SHA-256 differs from {ReleaseManifest.FileName}'s.");
            }
        }

        return path;
    }

    /// <summary>A release's single-file program (<paramref name="asset"/>), fetched and checked, then copied to <paramref name="path"/> as a program.</summary>
    public static async Task CopyProgramAsync(InstallContext context, ReleaseManifest release, ReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var work = new DeployScript.WorkFolder(context.Machine);
        var file = await GetAsync(context, release, asset, work, cancellationToken).ConfigureAwait(false);

        // A copy, not the download itself: it carries no extended attributes, so macOS has no quarantine mark to act on.
        await context.Machine.CopyFileAsync(file, path, Modes.Program, cancellationToken).ConfigureAwait(false);
    }
}
