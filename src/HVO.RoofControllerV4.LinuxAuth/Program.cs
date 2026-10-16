using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Connections.Features;

namespace HVO.RoofControllerV4.LinuxAuth;

/// <summary>Opt-in host worker. It is never started by the controller process.</summary>
public static class Program
{
    public const string ConfigPath = "/etc/hvo-roof/linux-auth/worker.json";
    public const string HelperPath = "/usr/local/libexec/hvo-roof-local-auth";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static readonly HashSet<string> Operations = ["read", "operate", "configure", "identity"];

    public static async Task<int> Main(string[] args)
    {
        // The helper accepts neither configuration overrides nor identity arguments.
        if (!OperatingSystem.IsLinux() || Native.geteuid() != 0 || args.Length != 1)
            return Fail();
        try
        {
            var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(TrustedFiles.Read(ConfigPath), Json)
                ?? throw new InvalidDataException();
            configuration.Validate();
            if (args[0] == "helper") return await RunHelper(configuration);
            if (args[0] == "pam-check")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var request = JsonSerializer.Deserialize<PamRequest>(await ReadBounded(Console.OpenStandardInput(), 16 * 1024, timeout.Token), Json);
                if (request is null) return 1;
                var account = Native.AccountMatches(request.UserName, request.Uid);
                var unlocked = request.RequireUnlockedPassword ? Native.PasswordUnlocked(request.UserName) : true;
                if (account == false || unlocked == false) return 2;
                if (account is null || unlocked is null) return 1;
                return Pam.CheckResult(configuration.PamService, request.UserName, request.Password, request.Authenticate)
                    switch { true => 0, false => 2, null => 1 };
            }
            if (args[0] != "serve") return Fail();
            await RunWorker(configuration);
            return 0;
        }
        catch (Exception)
        {
            // No credential, assertion, key, request body or exception text enters a log.
            return Fail();
        }
    }

    private static int Fail()
    {
        Console.Error.WriteLine("Linux authentication unavailable or denied.");
        return 1;
    }

    private static async Task<int> RunHelper(WorkerConfiguration configuration)
    {
        if (Environment.GetEnvironmentVariable("SUDO_COMMAND") != HelperPath
            || !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var uid) || uid == 0)
            return Fail();
        using var inputTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = await ReadBounded(Console.OpenStandardInput(), 1024, inputTimeout.Token);
        var request = JsonSerializer.Deserialize<HelperRequest>(input, Json);
        if (request is null || !Operations.Contains(request.Operation)) return Fail();
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(configuration.LocalSocketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.PostAsJsonAsync("http://localhost/v1/local", new LocalRequest(uid, request.Operation), Json);
        if (response.StatusCode != HttpStatusCode.OK) return Fail();
        var result = await response.Content.ReadFromJsonAsync<AssertionResponse>(Json);
        if (result is null || string.IsNullOrEmpty(result.Assertion)) return Fail();
        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    [SupportedOSPlatform("linux")]
    private static async Task RunWorker(WorkerConfiguration configuration)
    {
        Native.umask(0x3f); // 0077, before either socket is created.
        var key = TrustedFiles.Read(configuration.AssertionKeyPath);
        if (key.Length != 32) throw new InvalidDataException();
        TrustedFiles.ValidateDirectory(Path.GetDirectoryName(configuration.ControllerSocketPath)!);
        TrustedFiles.ValidateDirectory(Path.GetDirectoryName(configuration.LocalSocketPath)!);
        using var workerLock = TrustedFiles.LockWorker(Path.GetDirectoryName(configuration.LocalSocketPath)!);
        foreach (var path in new[] { configuration.ControllerSocketPath, configuration.LocalSocketPath })
        {
            TrustedFiles.RemoveStaleSocket(path);
        }
        // systemd defaults CWD to '/'. Default appsettings reload watchers otherwise recursively
        // enumerate the entire host filesystem before Kestrel can start. This worker has its own
        // trusted configuration and must not load or watch arbitrary working-directory files.
        TrustedFiles.ValidateDirectory(AppContext.BaseDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.ListenUnixSocket(configuration.ControllerSocketPath);
            options.ListenUnixSocket(configuration.LocalSocketPath);
        });
        var app = builder.Build();
        var worker = new Worker(configuration, key);
        app.Use(async (context, next) =>
        {
            try
            {
                var socket = context.Features.Get<IConnectionSocketFeature>()?.Socket
                    ?? throw new UnauthorizedAccessException();
                var peer = Native.Peer(socket);
                if (peer.Uid != 0 && peer.Uid != configuration.ControllerUid)
                    throw new UnauthorizedAccessException();
                context.Items["peerUid"] = peer.Uid;
                context.Items["localSocket"] = socket.LocalEndPoint?.ToString() == configuration.LocalSocketPath;
                await next(context);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or InvalidOperationException or BadHttpRequestException)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { code = "authentication_failed" });
                }
            }
        });
        app.MapPost("/v1/password", async (HttpContext context) =>
        {
            var request = await ReadRequest<PasswordRequest>(context);
            if (request is null || string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password)
                || request.UserName.Length > 256 || request.Password.Length > 4096)
                return Denied();
            return worker.Password(request) is { } assertion ? Results.Json(new AssertionResponse(assertion)) : Denied();
        });
        app.MapPost("/v1/revalidate", async (HttpContext context) =>
        {
            var request = await ReadRequest<RevalidateRequest>(context);
            return request is null ? Denied() : RevalidationResult(worker.Revalidate(request));
        });
        app.MapPost("/v1/local", async (HttpContext context) =>
        {
            if ((uint)context.Items["peerUid"]! != 0 || context.Items["localSocket"] is not true) return Denied();
            var request = await ReadRequest<LocalRequest>(context);
            return request is not null && Operations.Contains(request.Operation) && worker.Local(request) is { } assertion
                ? Results.Json(new AssertionResponse(assertion)) : Denied();
        });
        await app.StartAsync();
        Native.SetOwner(configuration.ControllerSocketPath, configuration.ControllerGroupId);
        File.SetUnixFileMode(configuration.ControllerSocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        File.SetUnixFileMode(configuration.LocalSocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Native.NotifyReady(); // Type=notify becomes ready only after both private sockets have final permissions.
        try { await app.WaitForShutdownAsync(); }
        finally
        {
            await app.DisposeAsync();
            TrustedFiles.RemoveStaleSocket(configuration.ControllerSocketPath);
            TrustedFiles.RemoveStaleSocket(configuration.LocalSocketPath);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static IResult RevalidationResult(Revalidation response)
        => response.Valid is null ? Results.StatusCode(503)
            : Results.Json(new { valid = response.Valid.Value, userName = response.UserName });

    private static IResult Denied() => Results.Json(new { code = "authentication_failed" }, statusCode: 401);
    private static async Task<T?> ReadRequest<T>(HttpContext context) =>
        JsonSerializer.Deserialize<T>(await ReadBounded(context.Request.Body, 16 * 1024, context.RequestAborted), Json);
    private static async Task<byte[]> ReadBounded(Stream input, int limit, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[1024];
        int length;
        while ((length = await input.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + length > limit) throw new InvalidDataException();
            result.Write(buffer, 0, length);
        }
        return result.ToArray();
    }
}

internal sealed record WorkerConfiguration
{
    public uint ControllerUid { get; init; }
    public uint ControllerGroupId { get; init; }
    public string Audience { get; init; } = "hvo-roof-controller";
    public string AssertionKeyPath { get; init; } = "/etc/hvo-roof/linux-auth/assertion.key";
    public string EnrollmentPath { get; init; } = "/etc/hvo-roof/linux-auth/enrollment.json";
    public string ControllerSocketPath { get; init; } = "/run/hvo-roof-linux-auth/controller/worker.sock";
    public string LocalSocketPath { get; init; } = "/run/hvo-roof-linux-auth/local.sock";
    public string PamService { get; init; } = "hvo-roof-linux-auth";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Audience) || Audience.Length > 256 || PamService != "hvo-roof-linux-auth"
            || new[] { AssertionKeyPath, EnrollmentPath, ControllerSocketPath, LocalSocketPath }.Any(p => !Path.IsPathFullyQualified(p))
            || ControllerSocketPath == LocalSocketPath)
            throw new InvalidDataException();
    }
}
internal sealed record HelperRequest(string Operation);
internal sealed record LocalRequest(uint Uid, string Operation);
internal sealed record PasswordRequest(string UserName, string Password);
internal sealed record PamRequest(string UserName, string? Password, bool Authenticate, uint Uid, bool RequireUnlockedPassword);
internal sealed record RevalidateRequest(string Subject, string EnrollmentStamp, string Purpose = "linux-password");
internal sealed record AssertionResponse(string Assertion);
internal sealed record Enrollment(uint Uid, string UserName, string Subject, string EnrollmentStamp, bool Enabled);

internal sealed class Worker(WorkerConfiguration configuration, byte[] key)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTimeOffset Since, int Attempts)> failures = new(StringComparer.Ordinal);
    private Enrollment[] Enrollments()
    {
        var entries = JsonSerializer.Deserialize<Enrollment[]>(TrustedFiles.Read(configuration.EnrollmentPath), Program.Json)
            ?? throw new InvalidDataException();
        if (entries.Length > 1000 || entries.Any(e => e.Uid == 0 || string.IsNullOrWhiteSpace(e.UserName)
                || string.IsNullOrWhiteSpace(e.Subject) || string.IsNullOrWhiteSpace(e.EnrollmentStamp))
            || entries.Select(e => e.Uid).Distinct().Count() != entries.Length
            || entries.Select(e => e.UserName).Distinct(StringComparer.Ordinal).Count() != entries.Length
            || entries.Select(e => e.Subject).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException();
        return entries;
    }
    private static bool Current(Enrollment entry, bool requireUnlockedPassword) => entry.Enabled
        && PamProcess.Check(entry.UserName, null, authenticate: false, entry.Uid, requireUnlockedPassword);
    public string? Password(PasswordRequest request)
    {
        lock (gate)
        {
            if (string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password)
                || request.UserName.Contains('\0') || request.Password.Contains('\0')) return null;
            var now = DateTimeOffset.UtcNow;
            foreach (var name in failures.Where(p => now - p.Value.Since > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
                failures.Remove(name);
            if (failures.Count >= 1000 || failures.TryGetValue(request.UserName, out var previous) && previous.Attempts >= 5)
                return null;
            var entry = Enrollments().SingleOrDefault(e => e.UserName == request.UserName);
            if (entry is null || !entry.Enabled
                || !PamProcess.Check(request.UserName, request.Password, authenticate: true, entry.Uid, requireUnlockedPassword: true))
            {
                failures[request.UserName] = failures.TryGetValue(request.UserName, out previous)
                    ? (previous.Since, previous.Attempts + 1) : (now, 1);
                return null;
            }
            failures.Remove(request.UserName);
            return Sign(entry, "linux-password", "login");
        }
    }
    public Revalidation Revalidate(RevalidateRequest request)
    {
        lock (gate)
        {
            if (request.Purpose is not ("linux-password" or "local-admin")) return new(null, null);
            var entry = Enrollments().SingleOrDefault(e => e.Subject == request.Subject && e.EnrollmentStamp == request.EnrollmentStamp);
            if (entry is null || !entry.Enabled) return new(false, null);
            var valid = PamProcess.CheckResult(entry.UserName, null, false, entry.Uid, request.Purpose == "linux-password");
            return new(valid, valid == true ? entry.UserName : null);
        }
    }
    public string? Local(LocalRequest request)
    {
        lock (gate)
        {
            var entry = Enrollments().SingleOrDefault(e => e.Uid == request.Uid);
            return entry is not null && Current(entry, requireUnlockedPassword: false) ? Sign(entry, "local-admin", request.Operation) : null;
        }
    }
    private string Sign(Enrollment entry, string purpose, string operation)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, purpose, provider = "linux", subject = entry.Subject, userName = entry.UserName,
            enrollmentStamp = entry.EnrollmentStamp, audience = configuration.Audience, operation,
            issuedAt = now, expiresAt = now + 30, nonce = Encode(RandomNumberGenerator.GetBytes(32))
        }, Program.Json);
        return Encode(payload) + "." + Encode(HMACSHA256.HashData(key, payload));
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record Revalidation(bool? Valid, string? UserName);
