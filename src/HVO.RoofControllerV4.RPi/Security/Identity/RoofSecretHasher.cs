using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>The outcome of checking a password or PIN.</summary>
internal enum RoofSecretCheck
{
    /// <summary>Wrong.</summary>
    Failed = 0,

    /// <summary>Right.</summary>
    Succeeded = 1,

    /// <summary>Right, and the hash should be replaced with one made with the current settings.</summary>
    SucceededRehashNeeded = 2,

    /// <summary>Not checked: too many checks were already running (the caller answers 429 and the client retries).</summary>
    Busy = 3
}

/// <summary>
/// Hashes and checks passwords and PINs with ASP.NET Core's <see cref="PasswordHasher{TUser}"/> (PBKDF2 with a random
/// salt; the cost comes from <see cref="PasswordHasherOptions"/>). Each hash costs tens of milliseconds of CPU on a
/// Raspberry Pi, so at most one or two run at once and a caller that cannot get a turn within
/// <see cref="SlotWait"/> is told to retry; sign-in can then never starve the roof's own work.
/// </summary>
internal sealed class RoofSecretHasher : IDisposable
{
    /// <summary>How long a check waits for a turn before it is refused as busy.</summary>
    internal static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(5);

    private static readonly object HashOwner = new();

    private readonly PasswordHasher<object> _hasher;
    private readonly SemaphoreSlim _slots;
    private readonly Lazy<string> _unknownUserHash;

    public RoofSecretHasher(IOptions<PasswordHasherOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hasher = new PasswordHasher<object>(options);
        var slots = Math.Clamp(Environment.ProcessorCount / 2, 1, 2);
        _slots = new SemaphoreSlim(slots, slots);

        // Checked when the name is unknown, so an unknown name takes as long as a wrong password.
        _unknownUserHash = new Lazy<string>(() => _hasher.HashPassword(HashOwner, Convert.ToHexString(Guid.NewGuid().ToByteArray())));
    }

    /// <summary>
    /// Hashes <paramref name="secret"/>, waiting for a turn. Returns null when no turn came within <see cref="SlotWait"/>.
    /// </summary>
    public async Task<string?> HashAsync(string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!await _slots.WaitAsync(SlotWait, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return _hasher.HashPassword(HashOwner, secret);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Checks <paramref name="secret"/> against <paramref name="hash"/>. A null hash (unknown name, or no password or PIN
    /// set) is checked against a dummy hash and always fails, taking the same time as a real check.
    /// </summary>
    public async Task<RoofSecretCheck> VerifyAsync(string? hash, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!await _slots.WaitAsync(SlotWait, cancellationToken).ConfigureAwait(false))
        {
            return RoofSecretCheck.Busy;
        }

        try
        {
            var result = _hasher.VerifyHashedPassword(HashOwner, hash ?? _unknownUserHash.Value, secret);
            if (hash is null)
            {
                return RoofSecretCheck.Failed;
            }

            return result switch
            {
                PasswordVerificationResult.Success => RoofSecretCheck.Succeeded,
                PasswordVerificationResult.SuccessRehashNeeded => RoofSecretCheck.SucceededRehashNeeded,
                _ => RoofSecretCheck.Failed
            };
        }
        catch (FormatException)
        {
            // A hash edited by hand into something unreadable: a wrong secret, not a server error.
            return RoofSecretCheck.Failed;
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
