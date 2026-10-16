using System.Text.Json;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>Refuses a release that cannot read the current identity store; never restores old credentials.</summary>
internal static class IdentitySchemaGuard
{
    public static string? Problem(InstallerMachine machine, string path, ReleaseManifest release)
    {
        if (machine.ReadText(path) is not { } text) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "The identity schema is invalid; refusing a controller replacement.";
            if (root.EnumerateObject().Count(property => property.NameEquals("schemaVersion")) > 1)
                return "The identity schema contains duplicate fields; refusing a controller replacement.";
            var schema = 1;
            if (root.TryGetProperty("schemaVersion", out var value))
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out schema) || schema is not (1 or 2 or 3))
                    return "The identity schema is malformed or unsupported; refusing a controller replacement.";
            }
            return schema > release.IdentityStoreSchema
                ? $"Release {release.Version} reads identity schema {release.IdentityStoreSchema}, but this store uses {schema}. Rollback is refused before replacing the controller; an old credential backup is never restored automatically."
                : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return "The identity store is invalid JSON; refusing a controller replacement.";
        }
    }
}
