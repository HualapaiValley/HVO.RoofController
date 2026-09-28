using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Storage;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>The identity store file could not be read or written. The message never contains the file's contents.</summary>
public sealed class RoofIdentityStoreException : Exception
{
    public RoofIdentityStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and writes the identity store file. A save writes a new file next to the old one (readable and writable by
/// the controller's user only), flushes it to disk, renames it over the old one and flushes the directory, so a crash or
/// power cut leaves either the old file or the new one, never a torn one, and a confirmed save stays saved.
/// </summary>
internal sealed class RoofIdentityFile
{
    private const string TemporarySuffix = ".tmp";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Throws <see cref="RoofIdentityStoreException"/> when <paramref name="path"/> cannot name a file in a directory.</summary>
    public RoofIdentityFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            Path = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new RoofIdentityStoreException($"The identity store path '{path}' is not a valid file path.", ex);
        }

        Directory = System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory && System.IO.Path.GetFileName(Path).Length > 0
            ? directory
            : throw new RoofIdentityStoreException(
                $"The identity store path '{path}' must name a file in a directory, for example /var/lib/hvo-roof/identity/identity.json.");
    }

    public string Path { get; }

    public string Directory { get; }

    /// <summary>
    /// Loads the file, or returns an empty document when it does not exist yet. Throws
    /// <see cref="RoofIdentityStoreException"/> when the directory is missing or the file is unreadable, torn or invalid.
    /// </summary>
    public RoofIdentityDocument Load() => Load(removeLeftovers: true);

    private RoofIdentityDocument Load(bool removeLeftovers)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            throw new RoofIdentityStoreException(
                $"The identity store directory '{Directory}' does not exist. Create it, owned by the controller's user and " +
                "readable by nobody else (docs/security.md).");
        }

        if (removeLeftovers)
        {
            RemoveLeftoverTemporaryFiles();
        }

        if (!File.Exists(Path))
        {
            return new RoofIdentityDocument();
        }

        RoofIdentityDocument? document;
        try
        {
            using var stream = File.OpenRead(Path);
            document = JsonSerializer.Deserialize(stream, RoofIdentityJsonContext.Default.RoofIdentityDocument);
        }
        catch (JsonException ex)
        {
            // The exception names the line and position, never the value.
            throw new RoofIdentityStoreException(
                $"The identity store '{Path}' is not valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine}). " +
                "Restore it from a backup, or move it aside to start with no people or sessions.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoofIdentityStoreException($"The identity store '{Path}' could not be read: {ex.GetType().Name}.", ex);
        }

        if (document is null)
        {
            throw new RoofIdentityStoreException($"The identity store '{Path}' is empty. Restore it from a backup, or delete it to start again.");
        }

        var problems = Check(document);
        if (problems.Count > 0)
        {
            throw new RoofIdentityStoreException($"The identity store '{Path}' is invalid: {string.Join(" ", problems)}");
        }

        return document;
    }

    /// <summary>Writes <paramref name="document"/> atomically. Throws <see cref="RoofIdentityStoreException"/> on failure.</summary>
    public void Save(RoofIdentityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var temporary = $"{Path}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, document, RoofIdentityJsonContext.Default.RoofIdentityDocument);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Path, overwrite: true);
            RoofDirectorySync.TryFlush(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new RoofIdentityStoreException($"The identity store '{Path}' could not be written: {ex.GetType().Name}.", ex);
        }
    }

    /// <summary>True when the file exists and other users can read or write it.</summary>
    public bool IsReadableByOthers()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(Path))
        {
            return false;
        }

        var mode = File.GetUnixFileMode(Path);
        return (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0;
    }

    /// <summary>
    /// For the deployment check: loads the file, and writes and removes a probe file next to it. Returns the problems
    /// found (empty when the store would work). Leftover temporary files are kept: the check runs while the old
    /// controller is still running, and one of them may be that controller's save in progress.
    /// </summary>
    public IReadOnlyList<string> CheckUsable()
    {
        var problems = new List<string>();
        try
        {
            Load(removeLeftovers: false);
        }
        catch (RoofIdentityStoreException ex)
        {
            problems.Add(ex.Message);
            return problems;
        }

        var probe = $"{Path}.probe-{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(
                $"The identity store directory '{Directory}' is not writable by the controller ({ex.GetType().Name}), so " +
                "people, sessions and managed keys could not be saved. Give the user the controller runs as (root in the " +
                "controller's image) write access to it.");
        }
        finally
        {
            TryDelete(probe);
        }

        return problems;
    }

    private static List<string> Check(RoofIdentityDocument document)
    {
        var problems = new List<string>();
        if (document.SchemaVersion != RoofIdentityDocument.CurrentSchemaVersion)
        {
            problems.Add($"schemaVersion {document.SchemaVersion} is not supported (expected {RoofIdentityDocument.CurrentSchemaVersion}).");
            return problems;
        }

        var users = document.Users ?? new List<StoredUser>();
        var keys = document.ApiKeys ?? new List<StoredApiKey>();
        var sessions = document.Sessions ?? new List<StoredSession>();
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            if (user is null || !RoofIdentityContract.IsValidName(user.Name) || RoofPrincipalFactory.NormalizeRole(user.Role) != user.Role
                || string.IsNullOrEmpty(user.Stamp))
            {
                problems.Add($"users[{i}] is incomplete or has an invalid name or role.");
            }
        }

        if (users.Where(user => user?.Name is not null).GroupBy(user => user.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            problems.Add("two users have the same name.");
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (key is null || !RoofIdentityContract.IsValidName(key.Name) || RoofPrincipalFactory.NormalizeRole(key.Role) != key.Role
                || !IsSha256Hex(key.KeySha256) || (key.Kiosk && key.Role != RoofControllerApiContract.ViewerRole))
            {
                problems.Add($"apiKeys[{i}] is incomplete or has an invalid name, role or hash.");
            }
        }

        if (keys.Where(key => key?.Name is not null).GroupBy(key => key.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            problems.Add("two API keys have the same name.");
        }

        if (HasDuplicates(keys.Select(key => key?.KeySha256)))
        {
            problems.Add("two API keys have the same key.");
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session is null || string.IsNullOrEmpty(session.Id) || !IsSha256Hex(session.TokenSha256)
                || string.IsNullOrEmpty(session.UserName) || RoofPrincipalFactory.NormalizeRole(session.Role) != session.Role
                || session.Kind is not (RoofCredentialKind.Session or RoofCredentialKind.Pin))
            {
                problems.Add($"sessions[{i}] is incomplete or invalid.");
            }
        }

        if (HasDuplicates(sessions.Select(session => session?.Id)))
        {
            problems.Add("two sessions have the same id.");
        }

        if (HasDuplicates(sessions.Select(session => session?.TokenSha256)))
        {
            problems.Add("two sessions have the same token.");
        }

        return problems;
    }

    private static bool HasDuplicates(IEnumerable<string?> values)
        => values.Where(value => value is not null).GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1);

    internal static bool IsSha256Hex(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private void RemoveLeftoverTemporaryFiles()
    {
        try
        {
            var name = System.IO.Path.GetFileName(Path);
            foreach (var leftover in System.IO.Directory.EnumerateFiles(Directory, name + ".*" + TemporarySuffix))
            {
                TryDelete(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only housekeeping: a leftover from an interrupted save is never read.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; it is never read.
        }
    }
}
