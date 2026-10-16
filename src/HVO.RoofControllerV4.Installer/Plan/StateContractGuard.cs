using System.Text.Json;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// One store's schema as its files here have it: <see cref="Schema"/> when they could be read, <see cref="Problem"/> when
/// they are not a store the controller reads, neither when there is no file (a store never saved, which every release
/// reads), and <see cref="Unreadable"/> when only root can read them and this is a plan without sudo.
/// </summary>
public sealed record StoreState(string Store, int? Schema, string? Problem = null, bool Unreadable = false);

/// <summary>
/// The schemas of the stores the controller keeps here, read from their files (nothing but the schema is read), and
/// whether a release reads them: what <c>upgrade</c> and <c>rollback</c> check before they change anything, and what the
/// controller's steps check again before the deploy script replaces it. Never restores old credentials.
/// </summary>
internal static class StateContractGuard
{
    private static readonly JsonDocumentOptions SettingsOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The managed secrets file the deploy script gives the controller: in the settings contract with the settings file.</summary>
    public static string SecretsFile(ControllerLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Join(layout.SettingsSecrets, "secrets.json");
    }

    /// <summary>The files each store is kept in, in the order <see cref="StateContract.Known"/> names the stores.</summary>
    public static IReadOnlyList<(string Store, IReadOnlyList<string> Files)> Files(ControllerLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return [(StateContract.Identity, [layout.IdentityFile]), (StateContract.Settings, [layout.SettingsFile, SecretsFile(layout)])];
    }

    /// <summary>
    /// Each store's schema as the files here have it. One that is not a file (a folder), or that cannot be read as root, is
    /// a problem: only a plan without sudo leaves a file it may not read to the run as root.
    /// </summary>
    public static IReadOnlyList<StoreState> Read(InstallerMachine machine, ControllerLayout layout)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return [.. Files(layout).Select(store =>
        {
            var files = store.Files.Select(file => ReadText(machine, file)).ToArray();
            return files.Select(file => file.Problem).FirstOrDefault(problem => problem is not null) is { } problem
                ? new StoreState(store.Store, null, problem)
                : Read(store.Store, files.Select(file => (file.File, file.Text, file.Unreadable)));
        })];
    }

    /// <summary>
    /// A store's schema from its files' text (null for a file that is not there), from the files here or from a backup.
    /// A file only root can read (<c>Unreadable</c>) leaves the schema unknown.
    /// </summary>
    public static StoreState Read(string store, IEnumerable<(string File, string? Text, bool Unreadable)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        StoreState? found = null;
        foreach (var (file, text, unreadable) in files)
        {
            if (unreadable)
            {
                return new StoreState(store, null, Unreadable: true);
            }

            if (text is null)
            {
                continue;
            }

            var state = store == StateContract.Identity ? Identity(text) : Settings(file, text);
            if (state.Problem is not null)
            {
                return state;
            }

            found = found is null || state.Schema > found.Schema ? state : found;
        }

        return found ?? new StoreState(store, null);
    }

    /// <summary>
    /// Why <paramref name="release"/> cannot read the stores in <paramref name="states"/>: one line for each it does not
    /// read, or whose files are not a store at all. Empty when it reads them all. A store only root can read is left to
    /// the run as root.
    /// </summary>
    public static IReadOnlyList<string> Incompatible(IReadOnlyList<StoreState> states, ReleaseManifest release)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(release);
        var problems = new List<string>();
        foreach (var state in states)
        {
            if (state.Problem is not null)
            {
                problems.Add(state.Problem);
            }
            else if (state.Schema is { } schema && release.StateContract.For(state.Store) is { } contract && !contract.Reads(schema))
            {
                problems.Add($"{StateContract.Describe(state.Store)} here is schema {schema}, and release {release.Version} reads {Noun(state.Store)} {contract.DescribeReads()}");
            }
        }

        return problems;
    }

    /// <summary>
    /// The first reason the controller of <paramref name="release"/> cannot replace the one here and keep its data, or
    /// null: the check the controller's install and rollback steps make before the deploy script runs.
    /// </summary>
    public static string? Problem(InstallerMachine machine, ControllerLayout layout, ReleaseManifest release)
        => Problem(Read(machine, layout), release);

    /// <summary>The first reason <paramref name="release"/> cannot read <paramref name="states"/>, as a step's check says it; or null.</summary>
    public static string? Problem(IReadOnlyList<StoreState> states, ReleaseManifest release)
        => Incompatible(states, release) is [var first, ..]
            ? $"{Sentence(first)}. Replacing the controller is refused before anything changes; an old credential backup is never restored automatically."
            : null;

    /// <summary>"identity" or "settings": the word for a store's schemas in a message.</summary>
    private static string Noun(string store) => store == StateContract.Identity ? "identity" : store;

    /// <summary>The line with its first letter a capital.</summary>
    internal static string Sentence(string line) => line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];

    // As the controller reads identity.json: a JSON object, one schemaVersion at most (1 when there is none), a whole
    // number from 1. Which schemas a release reads is its contract's to say.
    private static StoreState Identity(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new StoreState(StateContract.Identity, null, "the identity store's schema is invalid: it is not a JSON object");
            }

            if (root.EnumerateObject().Count(property => property.NameEquals("schemaVersion")) > 1)
            {
                return new StoreState(StateContract.Identity, null, "the identity store's schema is invalid: it has more than one schemaVersion");
            }

            var schema = 1;
            if (root.TryGetProperty("schemaVersion", out var value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out schema) || schema < 1))
            {
                return new StoreState(StateContract.Identity, null, "the identity store's schemaVersion is malformed: it is not a whole number from 1");
            }

            return new StoreState(StateContract.Identity, schema);
        }
        catch (JsonException)
        {
            return new StoreState(StateContract.Identity, null, "the identity store is not valid JSON");
        }
    }

    // As the controller reads its settings files: a JSON object, comments and trailing commas allowed. Every settings file
    // so far is schema 1; a release that changes their format teaches its installer to tell the new one.
    private static StoreState Settings(string file, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, SettingsOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new StoreState(StateContract.Settings, 1)
                : new StoreState(StateContract.Settings, null, $"{file} is not a settings file: it is not a JSON object");
        }
        catch (JsonException)
        {
            return new StoreState(StateContract.Settings, null, $"{file} is not a settings file: it is not valid JSON");
        }
    }

    // A file's text, null when it is not there; unreadable when only root can read it and this is a plan without sudo. A
    // folder where the file should be, anything else that is not a file (a FIFO, a device, a socket: refused before it is
    // read, and never waited on), or a file root cannot read, is a problem: the run as root must not take it for none.
    private static (string File, string? Text, bool Unreadable, string? Problem) ReadText(InstallerMachine machine, string file)
    {
        if (machine.DirectoryExists(file))
        {
            return (file, null, false, $"{file} is a folder, not a file: it is not a store the controller reads");
        }

        try
        {
            return (file, machine.ReadRegularText(file), false, null);
        }
        catch (NotARegularFileException error)
        {
            return (file, null, false, $"{file} is {error.Kind}, not a file: it is not a store the controller reads");
        }
        catch (UnauthorizedAccessException) when (!machine.IsRoot)
        {
            return (file, null, true, null);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return (file, null, false, $"{file} cannot be read, even as root ({(error is UnauthorizedAccessException ? "permission denied" : error.Message)})");
        }
    }
}
