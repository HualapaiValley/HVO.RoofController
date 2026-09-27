using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>The non-secret identity of a validated API key.</summary>
/// <param name="Name">Configured key name (used in logs and audit records).</param>
/// <param name="Role">Canonical role granted by the key.</param>
/// <param name="KeyId">Non-reversible identifier of the key value; changes when the key is rotated.</param>
public sealed record RoofApiKeyIdentity(string Name, string Role, string KeyId);

/// <summary>
/// Holds the configured API keys as SHA-256 hashes and validates presented keys in constant time (the presented key
/// is hashed and compared against every entry with <see cref="CryptographicOperations.FixedTimeEquals"/>). Key values
/// are never logged; configuration problems are reported by key name and index only. Reloads on configuration change.
/// </summary>
public sealed class RoofApiKeyStore : IDisposable
{
    /// <summary>Upper bound on a presented key, so an attacker cannot make us hash arbitrarily large inputs.</summary>
    internal const int MaximumPresentedKeyLength = 512;

    private readonly ILogger<RoofApiKeyStore> _logger;
    private readonly IDisposable? _changeRegistration;
    private volatile Snapshot _snapshot;

    public RoofApiKeyStore(IOptionsMonitor<RoofControllerSecurityOptions> options, ILogger<RoofApiKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshot = Build(options.CurrentValue);
        _changeRegistration = options.OnChange(updated =>
        {
            _snapshot = Build(updated);
            _logger.LogInformation("API key configuration reloaded: {KeyCount} usable key(s).", _snapshot.Entries.Length);
            foreach (var problem in _snapshot.Problems)
            {
                _logger.LogError("API key configuration problem: {Problem}", problem);
            }
        });
    }

    /// <summary>True when at least one usable key is configured.</summary>
    public bool HasKeys => _snapshot.Entries.Length > 0;

    /// <summary>Number of usable keys.</summary>
    public int KeyCount => _snapshot.Entries.Length;

    /// <summary>Problems found while loading the configuration (entries that were skipped). Never contain key values.</summary>
    public IReadOnlyList<string> ConfigurationProblems => _snapshot.Problems;

    /// <summary>Validates <paramref name="presentedKey"/> against every configured key in constant time.</summary>
    public bool TryValidate(string? presentedKey, [NotNullWhen(true)] out RoofApiKeyIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length > MaximumPresentedKeyLength)
        {
            return false;
        }

        var entries = _snapshot.Entries;
        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey), presentedHash);

        Entry? match = null;
        foreach (var entry in entries)
        {
            // No early exit: every entry is compared so timing does not depend on which entry matched.
            if (CryptographicOperations.FixedTimeEquals(presentedHash, entry.Hash) && match is null)
            {
                match = entry;
            }
        }

        if (match is null)
        {
            return false;
        }

        identity = match.Identity;
        return true;
    }

    /// <summary>Finds the key whose <see cref="RoofApiKeyIdentity.KeyId"/> is <paramref name="keyId"/>.</summary>
    public bool TryFindByKeyId(string? keyId, [NotNullWhen(true)] out RoofApiKeyIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrEmpty(keyId))
        {
            return false;
        }

        foreach (var entry in _snapshot.Entries)
        {
            if (string.Equals(entry.Identity.KeyId, keyId, StringComparison.Ordinal))
            {
                identity = entry.Identity;
                return true;
            }
        }

        return false;
    }

    public void Dispose() => _changeRegistration?.Dispose();

    /// <summary>
    /// Loads <paramref name="options"/> with the same rules as the store and returns the usable keys' identities and the
    /// problems found. Used by the deployment check; never returns key values.
    /// </summary>
    internal static (IReadOnlyList<RoofApiKeyIdentity> Keys, IReadOnlyList<string> Problems) Inspect(RoofControllerSecurityOptions? options)
    {
        var snapshot = Build(options);
        return (Array.ConvertAll(snapshot.Entries, entry => entry.Identity), snapshot.Problems);
    }

    internal static string ComputeKeyId(ReadOnlySpan<byte> keyHash)
    {
        Span<byte> buffer = stackalloc byte[SHA256.HashSizeInBytes];
        var prefix = "hvo-roof-key-id:"u8;
        var input = new byte[prefix.Length + keyHash.Length];
        prefix.CopyTo(input);
        keyHash.CopyTo(input.AsSpan(prefix.Length));
        SHA256.HashData(input, buffer);
        return Convert.ToHexStringLower(buffer[..16]);
    }

    private static Snapshot Build(RoofControllerSecurityOptions? options)
    {
        var entries = new List<Entry>();
        var problems = new List<string>();
        var configured = options?.ApiKeys ?? new List<RoofApiKeyOptions>();

        for (var index = 0; index < configured.Count; index++)
        {
            var key = configured[index];
            if (key is null)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(key.Name)
                ? string.Create(CultureInfo.InvariantCulture, $"ApiKeys[{index}]")
                : string.Create(CultureInfo.InvariantCulture, $"ApiKeys[{index}] ('{key.Name.Trim()}')");

            if (string.IsNullOrWhiteSpace(key.Name))
            {
                problems.Add($"{label}: Name is required; entry ignored.");
                continue;
            }

            var role = RoofPrincipalFactory.NormalizeRole(key.Role?.Trim());
            if (role is null)
            {
                problems.Add($"{label}: Role must be RoofViewer, RoofOperator or RoofAdmin; entry ignored.");
                continue;
            }

            var hasKey = !string.IsNullOrEmpty(key.Key);
            var hasHash = !string.IsNullOrWhiteSpace(key.KeySha256);
            if (hasKey && hasHash)
            {
                problems.Add($"{label}: set either Key or KeySha256, not both; entry ignored.");
                continue;
            }

            if (!hasKey && !hasHash)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{label}: no Key or KeySha256 was provided (set RoofControllerSecurity__ApiKeys__{index}__Key as an environment variable or Docker secret); entry ignored."));
                continue;
            }

            byte[] hash;
            if (hasKey)
            {
                if (key.Key!.Length < RoofControllerSecurityOptions.MinimumKeyLength)
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{label}: Key is shorter than {RoofControllerSecurityOptions.MinimumKeyLength} characters; entry ignored."));
                    continue;
                }

                if (key.Key.Length > MaximumPresentedKeyLength)
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{label}: Key is longer than {MaximumPresentedKeyLength} characters; entry ignored."));
                    continue;
                }

                hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Key));
            }
            else
            {
                var hex = key.KeySha256!.Trim();
                if (hex.Length != SHA256.HashSizeInBytes * 2 || !IsHex(hex))
                {
                    problems.Add($"{label}: KeySha256 must be 64 hexadecimal characters; entry ignored.");
                    continue;
                }

                hash = Convert.FromHexString(hex);
            }

            entries.Add(new Entry(new RoofApiKeyIdentity(key.Name.Trim(), role, ComputeKeyId(hash)), hash));
        }

        return new Snapshot(entries.ToArray(), problems.AsReadOnly());
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Entry(RoofApiKeyIdentity Identity, byte[] Hash);

    private sealed record Snapshot(Entry[] Entries, IReadOnlyList<string> Problems);
}
