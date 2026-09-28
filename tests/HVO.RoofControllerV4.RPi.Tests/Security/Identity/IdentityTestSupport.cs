using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Obviously fake secrets for the identity tests.</summary>
internal static class TestSecrets
{
    public const string Password = "test-password-not-real-01";
    public const string OtherPassword = "test-password-not-real-02";
    public const string Pin = "246810";
    public const string OtherPin = "135790";

    /// <summary>A configured admin key (at least 24 characters, as the key store requires).</summary>
    public const string AdminKey = "test-identity-admin-key-not-a-real-secret";

    /// <summary>A configured kiosk key (viewer).</summary>
    public const string KioskKey = "test-identity-kiosk-key-not-a-real-secret";
}

/// <summary>
/// An identity store, key store, hasher, lockout and sign-in service wired together as the controller wires them, with
/// a clock that moves only when told to and a cheap hash (so tests run fast).
/// </summary>
internal sealed class IdentityRig : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public IdentityRig(
        string? storePath = null,
        RoofIdentityOptions? options = null,
        TestOptionsMonitor<RoofControllerSecurityOptions>? security = null,
        int hashIterations = 1_000,
        ManualTimeProvider? time = null)
    {
        Time = time ?? new ManualTimeProvider(Start);
        Options = options ?? new RoofIdentityOptions();
        Options.StorePath = storePath;
        OptionsMonitor = new TestOptionsMonitor<RoofIdentityOptions>(Options);
        Security = security ?? KeyStoreFactory.Monitor(
            KeyStoreFactory.Key("cfg-admin", RoofControllerApiContract.AdminRole, TestSecrets.AdminKey),
            new RoofApiKeyOptions { Name = "cfg-kiosk", Role = RoofControllerApiContract.ViewerRole, Key = TestSecrets.KioskKey, Kiosk = true });
        StoreLogger = new CapturingLogger<RoofIdentityStore>();
        SignInLogger = new CapturingLogger<RoofSignInService>();
        Store = new RoofIdentityStore(OptionsMonitor, Security, Time, StoreLogger);
        Keys = new RoofApiKeyStore(Security, NullLogger<RoofApiKeyStore>.Instance, Store);
        Hasher = CreateHasher(hashIterations);
        Lockout = new RoofSignInLockout(OptionsMonitor, Time);
        SignIn = new RoofSignInService(Store, Hasher, Lockout, SignInLogger);
        Validator = new RoofCredentialValidator(Keys, Store);
    }

    public ManualTimeProvider Time { get; }

    public RoofIdentityOptions Options { get; }

    public TestOptionsMonitor<RoofIdentityOptions> OptionsMonitor { get; }

    public TestOptionsMonitor<RoofControllerSecurityOptions> Security { get; }

    public CapturingLogger<RoofIdentityStore> StoreLogger { get; }

    public CapturingLogger<RoofSignInService> SignInLogger { get; }

    public RoofIdentityStore Store { get; }

    public RoofApiKeyStore Keys { get; }

    public RoofSecretHasher Hasher { get; }

    public RoofSignInLockout Lockout { get; }

    public RoofSignInService SignIn { get; }

    public RoofCredentialValidator Validator { get; }

    /// <summary>The configured kiosk key's identity.</summary>
    public RoofApiKeyIdentity Kiosk
        => Keys.TryValidate(TestSecrets.KioskKey, out var kiosk) ? kiosk : throw new AssertFailedException("No kiosk key.");

    public static RoofSecretHasher CreateHasher(int iterations, int slots = 2, TimeSpan? slotWait = null)
        => new(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = iterations }), slots, slotWait ?? TimeSpan.FromSeconds(5));

    /// <summary>Adds a person, hashing the secrets given.</summary>
    public async Task<StoredUser> AddUserAsync(string name, string role, string? password = TestSecrets.Password, string? pin = null)
    {
        var passwordHash = password is null ? null : await Hasher.HashAsync(password, CancellationToken.None);
        var pinHash = pin is null ? null : await Hasher.HashAsync(pin, CancellationToken.None);
        var added = Store.AddUser(name, role, passwordHash, pinHash);
        Assert.IsTrue(added.Succeeded, added.Detail);
        return added.Value!;
    }

    /// <summary>Opens a session directly in the store (no secret check).</summary>
    public RoofIssuedSession OpenSession(string name, RoofCredentialKind kind = RoofCredentialKind.Session, RoofApiKeyIdentity? kiosk = null)
    {
        Assert.IsTrue(Store.TryGetUser(name, out var user));
        var created = Store.CreateSession(user.Name, user.Stamp, kind, kiosk?.Name, kiosk?.KeyId);
        Assert.IsTrue(created.Succeeded, created.Detail);
        return created.Value!;
    }

    public bool IsLive(RoofIssuedSession issued) => Store.TryValidateToken(issued.Token, touch: false, out _, out _);

    public void Dispose()
    {
        Keys.Dispose();
        Hasher.Dispose();
    }
}

/// <summary>A directory under the system temporary directory, removed when disposed.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("hvo-identity-test-").FullName;
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // A test may have made it read-only.
                System.IO.File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class LogAssertions
{
    /// <summary>Fails when any logged message contains one of <paramref name="secrets"/>.</summary>
    public static void ContainsNone(IEnumerable<string> messages, params string[] secrets)
    {
        foreach (var message in messages)
        {
            foreach (var secret in secrets)
            {
                Assert.IsFalse(message.Contains(secret, StringComparison.Ordinal), $"A log message contains a secret: {message.Replace(secret, "<secret>")}");
            }
        }
    }

    public static IEnumerable<string> Messages<T>(CapturingLogger<T> logger) => logger.Entries.Select(entry => entry.Message);

    public static int Count<T>(CapturingLogger<T> logger, LogLevel level, string fragment)
        => logger.Entries.Count(entry => entry.Level == level && entry.Message.Contains(fragment, StringComparison.Ordinal));
}
