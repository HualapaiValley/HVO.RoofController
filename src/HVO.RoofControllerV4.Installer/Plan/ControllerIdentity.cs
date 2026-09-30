using System.Text.Json;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// A person, or an API key added through the API, as the identity store names it: its name and role, and for a person
/// whether they have a PIN for the kiosk.
/// </summary>
public sealed record IdentityEntry(string Name, string Role, bool HasPin = false);

/// <summary>
/// What the controller's identity store (identity.json) says that is not secret: its people's names and roles, and the
/// names and roles of the API keys added through the API. Its hashes are never read: only whether a person has a PIN.
/// </summary>
public sealed record ControllerIdentity(IReadOnlyList<IdentityEntry> Users, IReadOnlyList<IdentityEntry> ApiKeys)
{
    public static ControllerIdentity Empty { get; } = new([], []);

    /// <summary>
    /// The identity store at <paramref name="path"/>; <see cref="Empty"/> when there is none yet. Throws
    /// <see cref="UnauthorizedAccessException"/> when only root may read it, and <see cref="InstallerException"/> when it
    /// is not an identity store.
    /// </summary>
    public static ControllerIdentity Read(InstallerMachine machine, string path)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (machine.ReadText(path) is not { } json)
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new ControllerIdentity(Entries(document.RootElement, "users"), Entries(document.RootElement, "apiKeys"))
                : throw new InstallerException($"{path} is not the controller's identity store.");
        }
        catch (JsonException error)
        {
            throw new InstallerException($"{path} is not the controller's identity store: {error.Message}");
        }
    }

    private static IdentityEntry[] Entries(JsonElement root, string member)
        => root.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(entry => new IdentityEntry(Text(entry, "name"), Text(entry, "role"), Text(entry, "pinHash").Length > 0))
                .Where(entry => entry.Name.Length > 0)]
            : [];

    private static string Text(JsonElement entry, string member)
        => entry.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
}
