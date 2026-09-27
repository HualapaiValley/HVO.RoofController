using System.Globalization;
using System.Text;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// File access for the local settings file. Writes go to a temporary file in the same directory, are flushed to
/// disk and then renamed over the target, so a crash or power loss leaves either the old or the new file.
/// </summary>
public sealed class SettingsFileStore
{
    private readonly TimeProvider _timeProvider;

    public SettingsFileStore(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    public string ReadAllText() => File.ReadAllText(FilePath, Encoding.UTF8);

    /// <summary>
    /// Moves the current file aside as <c>{name}.corrupt-{UTC timestamp}</c> and returns the backup path.
    /// </summary>
    public string Quarantine()
    {
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var backupPath = $"{FilePath}.corrupt-{stamp}";
        for (var suffix = 1; File.Exists(backupPath); suffix++)
        {
            backupPath = $"{FilePath}.corrupt-{stamp}-{suffix}";
        }

        File.Move(FilePath, backupPath);
        return backupPath;
    }

    public void WriteAtomically(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(FilePath) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, FilePath, overwrite: true);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
