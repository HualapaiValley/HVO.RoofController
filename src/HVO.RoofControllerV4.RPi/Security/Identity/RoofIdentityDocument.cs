using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>
/// The identity store's file: people, managed API keys and open sessions. It holds hashes only: password and PIN hashes
/// (PBKDF2), and the SHA-256 of each API key and session token. It is still sensitive (a PIN hash can be attacked
/// offline), so it lives in its own directory, readable only by the controller, never next to the shareable settings.
/// </summary>
internal sealed class RoofIdentityDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Goes up by one with every saved change.</summary>
    public long Revision { get; set; }

    public List<StoredUser> Users { get; set; } = new();

    public List<StoredApiKey> ApiKeys { get; set; } = new();

    public List<StoredSession> Sessions { get; set; } = new();

    public RoofIdentityDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Revision = Revision,
        Users = Users.ConvertAll(user => user with { }),
        ApiKeys = ApiKeys.ConvertAll(key => key with { }),
        Sessions = Sessions.ConvertAll(session => session with { })
    };
}

/// <summary>A person.</summary>
/// <param name="Stamp">Changes whenever the password or role changes, so a sign-in checked against an older password cannot open a session.</param>
public sealed record StoredUser(
    string Name,
    string Role,
    string? PasswordHash,
    string? PinHash,
    string Stamp,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>An API key added through the API.</summary>
/// <param name="KeySha256">Lower-case hex SHA-256 of the key's UTF-8 bytes.</param>
public sealed record StoredApiKey(
    string Name,
    string Role,
    bool Kiosk,
    string KeySha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>An open session.</summary>
/// <param name="Id">Non-secret identifier (32 hex characters).</param>
/// <param name="TokenSha256">Lower-case hex SHA-256 of the bearer token.</param>
/// <param name="Device">For a PIN session, the kiosk key's name.</param>
/// <param name="DeviceKeyId">For a PIN session, the kiosk key's identifier; the session ends when that key is removed or rotated.</param>
public sealed record StoredSession(
    string Id,
    string TokenSha256,
    string UserName,
    string Role,
    RoofCredentialKind Kind,
    string? Device,
    string? DeviceKeyId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(RoofIdentityDocument))]
internal sealed partial class RoofIdentityJsonContext : JsonSerializerContext
{
}
