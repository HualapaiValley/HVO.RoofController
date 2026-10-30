using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>The command history file could not be read or written. The message never contains the file's contents.</summary>
public sealed class RoofCommandHistoryException : Exception
{
    public RoofCommandHistoryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>What <see cref="RoofCommandHistoryFile.Load"/> read.</summary>
/// <param name="Entries">The entries that could be read, in the file's order.</param>
/// <param name="Lines">Every line in the file, including those that could not be read.</param>
/// <param name="Unreadable">Lines that could not be read: torn by a power cut, or written by a newer version.</param>
/// <param name="EndsWithNewline">False when the last line was cut short, so the next append starts a new line first.</param>
internal sealed record RoofCommandHistoryFileContents(List<RoofCommandHistoryEntry> Entries, int Lines, int Unreadable, bool EndsWithNewline);

/// <summary>
/// The command history file: JSON Lines, one entry per line, readable and writable by the controller's user only. New
/// entries are appended and flushed to disk. Compaction writes the kept entries to a new file next to the old one,
/// flushes it, renames it over the old one and flushes the directory, so a crash or power cut leaves one or the other.
/// A line torn by a power cut is skipped when the file is read.
/// </summary>
internal sealed class RoofCommandHistoryFile
{
    private const string TemporarySuffix = ".tmp";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const byte Newline = (byte)'\n';

    /// <summary>Throws <see cref="RoofCommandHistoryException"/> when <paramref name="path"/> cannot name a file in a directory.</summary>
    public RoofCommandHistoryFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            Path = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new RoofCommandHistoryException($"The command history path '{path}' is not a valid file path.", ex);
        }

        Directory = System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory && System.IO.Path.GetFileName(Path).Length > 0
            ? directory
            : throw new RoofCommandHistoryException($"The command history path '{path}' must name a file in a directory.");
    }

    public string Path { get; }

    public string Directory { get; }

    /// <summary>
    /// Reads the file, or nothing when it does not exist yet. Throws <see cref="RoofCommandHistoryException"/> when the
    /// directory is missing or the file cannot be read; a line that cannot be parsed is counted and skipped.
    /// </summary>
    public RoofCommandHistoryFileContents Load()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            throw new RoofCommandHistoryException($"The command history directory '{Directory}' does not exist.");
        }

        if (System.IO.Directory.Exists(Path))
        {
            throw new RoofCommandHistoryException($"The command history path '{Path}' is a directory. It must name a file.");
        }

        RemoveLeftoverTemporaryFiles();
        var entries = new List<RoofCommandHistoryEntry>();
        if (!File.Exists(Path))
        {
            return new RoofCommandHistoryFileContents(entries, 0, 0, EndsWithNewline: true);
        }

        var lines = 0;
        var unreadable = 0;
        bool endsWithNewline;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            endsWithNewline = stream.Length == 0 || EndsWithNewline(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                lines++;
                try
                {
                    if (JsonSerializer.Deserialize(line, RoofCommandHistoryJsonContext.Default.RoofCommandHistoryEntry) is { Id: > 0 } entry)
                    {
                        entries.Add(entry);
                        continue;
                    }
                }
                catch (JsonException)
                {
                }

                unreadable++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoofCommandHistoryException($"The command history '{Path}' could not be read: {ex.GetType().Name}.", ex);
        }

        return new RoofCommandHistoryFileContents(entries, lines, unreadable, endsWithNewline);
    }

    /// <summary>
    /// Appends <paramref name="entries"/> and flushes them to disk, after a newline first when
    /// <paramref name="startNewLine"/> (the last line was cut short). Throws <see cref="RoofCommandHistoryException"/>.
    /// </summary>
    public void Append(IReadOnlyList<RoofCommandHistoryEntry> entries, bool startNewLine)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var created = !File.Exists(Path);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(Path, options))
            {
                if (startNewLine)
                {
                    stream.WriteByte(Newline);
                }

                Write(stream, entries);
                stream.Flush(flushToDisk: true);
            }

            if (created)
            {
                RoofDirectorySync.TryFlush(Directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoofCommandHistoryException($"The command history '{Path}' could not be written: {ex.GetType().Name}.", ex);
        }
    }

    /// <summary>Replaces the file with <paramref name="entries"/> atomically. Throws <see cref="RoofCommandHistoryException"/>.</summary>
    public void Replace(IReadOnlyList<RoofCommandHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var temporary = $"{Path}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(temporary, options))
            {
                Write(stream, entries);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Path, overwrite: true);
            RoofDirectorySync.TryFlush(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new RoofCommandHistoryException($"The command history '{Path}' could not be written: {ex.GetType().Name}.", ex);
        }
    }

    private static void Write(Stream stream, IReadOnlyList<RoofCommandHistoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            JsonSerializer.Serialize(stream, entry, RoofCommandHistoryJsonContext.Default.RoofCommandHistoryEntry);
            stream.WriteByte(Newline);
        }
    }

    private static bool EndsWithNewline(FileStream stream)
    {
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == Newline;
    }

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
            // Only housekeeping: a leftover from an interrupted compaction is never read.
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

/// <summary>One entry per line: compact, camelCase, enums by name, nulls left out.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RoofCommandHistoryEntry))]
internal sealed partial class RoofCommandHistoryJsonContext : JsonSerializerContext
{
}
