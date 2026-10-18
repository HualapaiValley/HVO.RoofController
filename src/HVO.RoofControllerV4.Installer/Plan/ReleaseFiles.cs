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
    /// The release's <paramref name="asset"/> in <paramref name="work"/>, a folder only the installer's user can open:
    /// copied from the release's folder (<c>--release DIR</c>) and checked as it is written, or downloaded there and
    /// checked against release.json's size and SHA-256. Only this private file is used afterwards, never an offline
    /// source someone could replace or write over after verification. The work folder owns its lifetime.
    /// </summary>
    public static async Task<string> GetAsync(InstallContext context, ReleaseManifest release, ReleaseAsset asset, DeployScript.WorkFolder work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(work);
        var path = Path.Join(work.Path, asset.Name);
        if (context.Release.FolderPath is { } folder)
        {
            var source = Path.Join(folder, asset.Name);
            if (!context.Machine.FileExists(source))
            {
                throw new InstallerException($"There is no {asset.Name} in {folder}: download it with the release's other files from {ReleaseManifest.ReleasesUrl}.");
            }

            await CopyCheckedAsync(context, asset, source, path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
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

            await CheckAsync(context, asset, path, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    // A private download, or the controller archive that the deploy script will copy and check again as it writes:
    // checking a source here alone never makes it safe to reopen from a folder others can change.
    internal static async Task CheckAsync(InstallContext context, ReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        var stream = context.Machine.OpenRead(path) ?? throw new InstallerException($"{path} is not there.");
        await using (stream.ConfigureAwait(false))
        {
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (stream.Length != asset.Size || sha256 != asset.Sha256)
            {
                throw new InstallerException($"{path} is not the release's {asset.Name}: its size or SHA-256 differs from {ReleaseManifest.FileName}'s.");
            }
        }
    }

    /// <summary>
    /// Copies the release's <paramref name="asset"/> from <paramref name="source"/> to <paramref name="copy"/>, a new file
    /// that only the installer's user can read, in a folder only it can open (<see cref="DeployScript.WorkFolder"/>): the
    /// source is read once, and what is written to the copy is checked against release.json's size and SHA-256 as it is
    /// written. The copy is then the release's file whatever later becomes of <paramref name="source"/> (replaced, or
    /// written over in place), so it is what is used, never the source. Throws, and leaves no copy, when it is not the
    /// release's file or cannot be written.
    /// </summary>
    public static async Task CopyCheckedAsync(InstallContext context, ReleaseAsset asset, string source, string copy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(asset);
        var from = context.Machine.OpenRead(source) ?? throw new InstallerException($"{source} is not there.");
        bool released;
        try
        {
            await using (from.ConfigureAwait(false))
            {
                var to = context.Machine.CreateNew(copy, Modes.PrivateFile);
                await using (to.ConfigureAwait(false))
                {
                    using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long length = 0;
                    int read;

                    // At most one byte more than release.json's size: a larger file is not the release's, and is never copied whole.
                    while (length <= asset.Size
                        && (read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, asset.Size + 1 - length)), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        sha256.AppendData(buffer, 0, read);
                        await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        length += read;
                    }

                    await to.FlushAsync(cancellationToken).ConfigureAwait(false);
                    released = length == asset.Size && Convert.ToHexStringLower(sha256.GetHashAndReset()) == asset.Sha256;
                }
            }
        }
        catch (IOException error)
        {
            context.Machine.DeleteFile(copy);
            throw new InstallerException($"Could not copy {source} into {Path.GetDirectoryName(copy)}: {error.Message}");
        }

        if (!released)
        {
            context.Machine.DeleteFile(copy);
            throw new InstallerException($"{source} is not the release's {asset.Name}: its size or SHA-256 differs from {ReleaseManifest.FileName}'s.");
        }
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
