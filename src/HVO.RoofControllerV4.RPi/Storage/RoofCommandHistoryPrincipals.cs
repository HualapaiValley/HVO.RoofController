using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Storage;

/// <summary>
/// The identifiers the command history records and matches who sent a command by (#206). Never a name alone, so a
/// person removed and added again, or an API key named like a person, never reads another's commands:
/// <list type="bullet">
/// <item>A person with a principal ID (identity schema 2 and later, and every Linux principal): <c>principal:&lt;id&gt;</c>.</item>
/// <item>A person without one (identity schema 1): <c>person:&lt;name&gt;@&lt;created ticks&gt;</c>. A person keeps this
/// one as well once the store gives them a principal ID, so their commands from before stay theirs.</item>
/// <item>An API key: <c>key:&lt;key id&gt;</c>, the non-reversible identifier of its value. A rotated key is a new one.</item>
/// <item>A person's own (personal) key: the person.</item>
/// </list>
/// </summary>
public sealed class RoofCommandHistoryPrincipals
{
    private const string LinuxProvider = "linux";

    private readonly RoofIdentityStore? _identity;
    private readonly RoofApiKeyStore? _keys;

    /// <param name="identity">The people; without it (tests), a session's person is known only by the session.</param>
    /// <param name="keys">The configured and managed API keys, for an admin's search by name.</param>
    public RoofCommandHistoryPrincipals(RoofIdentityStore? identity = null, RoofApiKeyStore? keys = null)
    {
        _identity = identity;
        _keys = keys;
    }

    /// <summary>The identifier a command <paramref name="user"/> sends is recorded under; null when not signed in.</summary>
    public string? IdOf(ClaimsPrincipal user) => IdsOf(user).FirstOrDefault();

    /// <summary>
    /// Every identifier commands of <paramref name="user"/> may be recorded under, the one recorded now first: what they
    /// read as their own. Empty when not signed in.
    /// </summary>
    public IReadOnlyList<string> IdsOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } name })
        {
            return [];
        }

        var kind = RoofPrincipalFactory.GetCredentialKind(user);
        var principalId = user.FindFirst(RoofPrincipalFactory.PrincipalIdClaimType)?.Value;
        var person = kind is RoofCredentialKind.Session or RoofCredentialKind.Pin || user.HasClaim(RoofPrincipalFactory.OwnedKeyClaimType, "true");
        if (!person)
        {
            return user.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value is { Length: > 0 } keyId ? ["key:" + keyId] : [];
        }

        StoredUser? stored = null;
        if (_identity is not null)
        {
            if (principalId is not null)
            {
                if (user.FindFirst(RoofPrincipalFactory.ProviderClaimType)?.Value != LinuxProvider)
                {
                    stored = _identity.Users.FirstOrDefault(candidate => candidate.PrincipalId == principalId);
                }
            }
            else
            {
                _identity.TryGetUser(name, out stored);
            }
        }

        if (stored is not null)
        {
            return IdsOf(stored);
        }

        if (principalId is not null)
        {
            return ["principal:" + principalId];
        }

        // A session whose person is gone: only that session is theirs.
        return user.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value is { Length: > 0 } sessionId ? ["session:" + sessionId] : [];
    }

    /// <summary>
    /// The identifiers of everyone and every API key now named <paramref name="name"/> (case-insensitive), for an admin's
    /// search: someone removed, or a key since rotated, is found by the identifier the history shows, not by name.
    /// </summary>
    public IReadOnlyList<string> IdsNamed(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var ids = new List<string>();
        if (_identity is not null)
        {
            if (_identity.TryGetUser(name, out var stored))
            {
                ids.AddRange(IdsOf(stored));
            }

            ids.AddRange(_identity.ProviderPrincipals
                .Where(principal => principal.Provider == LinuxProvider && string.Equals(principal.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(principal => "principal:" + principal.Id));
        }

        if (_keys is not null)
        {
            ids.AddRange(_keys.Keys
                .Where(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(key => "key:" + key.KeyId));
        }

        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A person's identifiers: their principal ID when the store gives one, then the one from their name and creation.</summary>
    internal static IReadOnlyList<string> IdsOf(StoredUser user)
    {
        var created = "person:" + user.Name.ToLowerInvariant() + "@" + user.CreatedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return user.PrincipalId is { Length: > 0 } principalId ? ["principal:" + principalId, created] : [created];
    }
}
