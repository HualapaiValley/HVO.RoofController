using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Alpaca;
using HVO.RoofControllerV4.Common.Safety;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>
/// One ASCOM Alpaca device, read with the Alpaca Device API (<c>GET {server}/api/v1/{type}/{number}/{member}</c>,
/// https://ascom-standards.org/api/). Only reads: the controller never connects, disconnects, parks or moves a device,
/// so it never takes a mount away from the program that drives it. A read that is not a clean answer throws
/// <see cref="RoofSafetySourceException"/>, so it is a failed read, never a value: the server unreachable, an HTTP status
/// other than success, an answer that is not the Alpaca JSON, a non-zero <c>ErrorNumber</c>, or anything short of an
/// unambiguous success (<see cref="ReadValue"/>).
/// </summary>
/// <remarks>
/// The failure's words name no address and quote nothing the device sent (its <c>ErrorMessage</c> is not used), since
/// they are logged and shown on every client; and a failure carries no inner exception, whose message (an
/// <see cref="HttpRequestException"/>'s) names the host and port. The HTTP client is <see cref="HttpClientName"/>, which
/// writes no log of its own: its request lines would carry the device's address.
/// </remarks>
public sealed class AlpacaDevice
{
    /// <summary>The <see cref="IHttpClientFactory"/> client every Alpaca read uses.</summary>
    public const string HttpClientName = "RoofSafety.Alpaca";

    /// <summary>The largest answer read; an Alpaca boolean answer is a few hundred bytes.</summary>
    public const int MaxAnswerBytes = 64 * 1024;

    /// <summary>The ASCOM error a member that is not implemented answers (NotImplementedException).</summary>
    public const int NotImplementedError = 0x400;

    /// <summary>The ASCOM error a member read while the device is not connected answers (NotConnectedException).</summary>
    public const int NotConnectedError = 0x407;

    /// <summary>Marks the requests made here, so the HTTP client trace leaves out a poll every few seconds.</summary>
    public static readonly HttpRequestOptionsKey<bool> RequestMarker = new("HVO.RoofSafety.Alpaca");

    /// <summary>
    /// The Alpaca ClientID this controller sends on every request: chosen at start between 1 and 65535, as the Alpaca API
    /// suggests, so a device's log can tell this client's requests apart.
    /// </summary>
    internal static readonly uint ClientId = (uint)Random.Shared.Next(1, 65536);

    private static long s_transactions;

    private readonly IHttpClientFactory _clients;
    private readonly Uri _server;

    /// <param name="server">The Alpaca device server's address (http or https, host and port, and a path only behind a proxy).</param>
    /// <param name="deviceType">The Alpaca device type: <c>telescope</c>, <c>safetymonitor</c> (sent in lower case).</param>
    /// <param name="deviceNumber">The device's number on that server.</param>
    /// <param name="clients">Makes the <see cref="HttpClientName"/> client.</param>
    public AlpacaDevice(string server, string deviceType, int deviceNumber, IHttpClientFactory clients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceType);
        ArgumentOutOfRangeException.ThrowIfNegative(deviceNumber);
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        if (!RoofSafetyOptionsValidator.IsUsableUrl(server))
        {
            // The address is not repeated: it is a private one.
            throw new ArgumentException("The Alpaca server must be an http or https address with no credentials, query or fragment.", nameof(server));
        }

        var trimmed = server.Trim();
        _server = new Uri(trimmed.EndsWith('/') ? trimmed : trimmed + "/", UriKind.Absolute);
        DeviceType = deviceType.ToLowerInvariant();
        DeviceNumber = deviceNumber;
    }

    /// <summary>The Alpaca device type, in lower case.</summary>
    public string DeviceType { get; }

    /// <summary>The device's number on its server.</summary>
    public int DeviceNumber { get; }

    /// <summary>
    /// Reads a boolean member (<c>connected</c>, <c>canpark</c>, <c>atpark</c>, <c>issafe</c>). Throws
    /// <see cref="RoofSafetySourceException"/> unless the device answered it cleanly; ends with
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<bool> ReadBooleanAsync(string member, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        var transaction = NextTransaction();
        var answer = await ReadAsync(DevicePath(member), transaction, cancellationToken).ConfigureAwait(false);
        using (answer)
        {
            return ReadValue(answer.RootElement, transaction);
        }
    }

    /// <summary>
    /// Reads a text member (<c>driverinfo</c>). Throws <see cref="RoofSafetySourceException"/> unless the device answered
    /// it cleanly with text; ends with <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<string> ReadTextAsync(string member, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        var transaction = NextTransaction();
        var answer = await ReadAsync(DevicePath(member), transaction, cancellationToken).ConfigureAwait(false);
        using (answer)
        {
            return ReadEnvelope(answer.RootElement, transaction) is { ValueKind: JsonValueKind.String } text
                ? text.GetString()!
                : throw new RoofSafetySourceException("the Alpaca server's answer has no text Value");
        }
    }

    /// <summary>
    /// The <c>Value</c> of a Management API answer from the device's server (<c>v1/configureddevices</c>), or null when the
    /// server does not serve it (an HTTP error, or not JSON). An answer it does give must be this request's own clean
    /// answer: every field once, the request's <c>ClientTransactionID</c> echoed and a <c>ServerTransactionID</c>, and,
    /// when it carries the device API's <c>ErrorNumber</c> and <c>ErrorMessage</c> (the Management API does not ask for
    /// them), 0 and empty; otherwise it throws <see cref="RoofSafetySourceException"/>, as a device answer would (a cached or
    /// replayed answer is not proof of anything). Ends with <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<JsonElement?> TryReadManagementAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var transaction = NextTransaction();
        JsonDocument answer;
        try
        {
            answer = await ReadAsync($"management/{path}", transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (RoofSafetySourceException)
        {
            return null;
        }

        using (answer)
        {
            var fields = Fields(answer.RootElement);
            RequireTransactions(fields, transaction);
            if (fields.TryGetValue("ErrorNumber", out var errorNumber))
            {
                if (errorNumber.ValueKind != JsonValueKind.Number || !errorNumber.TryGetInt32(out var error))
                {
                    throw new RoofSafetySourceException("the Alpaca server's answer has no ErrorNumber");
                }

                if (error != 0)
                {
                    throw new AlpacaErrorException(error);
                }
            }

            if (fields.TryGetValue("ErrorMessage", out var errorMessage)
                && (errorMessage.ValueKind != JsonValueKind.String || errorMessage.GetString()!.Length != 0))
            {
                throw new RoofSafetySourceException("the Alpaca server's answer reports success with an error message");
            }

            return fields.TryGetValue("Value", out var value) ? value.Clone() : null;
        }
    }

    /// <summary>
    /// The <c>Value</c> of an Alpaca answer, only when the answer is an unambiguous success to the request that carried
    /// <paramref name="clientTransaction"/>: every field once (names matched whatever their case, as the Alpaca API
    /// allows), a numeric <c>ErrorNumber</c> and a string <c>ErrorMessage</c>, the request's <c>ClientTransactionID</c>
    /// echoed and a <c>ServerTransactionID</c>, then <c>ErrorNumber</c> 0 with an empty <c>ErrorMessage</c> (the Alpaca
    /// API's success, which a client checks both halves of) and a true or false <c>Value</c>. Anything else throws, so it
    /// is a failed read that neither makes nor refreshes a reading. The device's own <c>ErrorMessage</c> is never quoted.
    /// </summary>
    internal static bool ReadValue(JsonElement answer, uint clientTransaction)
        => ReadEnvelope(answer, clientTransaction) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => throw new RoofSafetySourceException("the Alpaca server's answer has no true or false Value")
        };

    /// <summary>The <c>Value</c> of an unambiguous success (<see cref="ReadValue"/>), or null when it has none.</summary>
    private static JsonElement? ReadEnvelope(JsonElement answer, uint clientTransaction)
    {
        var fields = Fields(answer);
        if (!fields.TryGetValue("ErrorNumber", out var errorNumber) || errorNumber.ValueKind != JsonValueKind.Number || !errorNumber.TryGetInt32(out var error))
        {
            throw new RoofSafetySourceException("the Alpaca server's answer has no ErrorNumber");
        }

        if (!fields.TryGetValue("ErrorMessage", out var errorMessage) || errorMessage.ValueKind != JsonValueKind.String)
        {
            throw new RoofSafetySourceException("the Alpaca server's answer has no ErrorMessage");
        }

        RequireTransactions(fields, clientTransaction);
        if (error != 0)
        {
            throw new AlpacaErrorException(error);
        }

        if (errorMessage.GetString()!.Length != 0)
        {
            throw new RoofSafetySourceException("the Alpaca server's answer reports success with an error message");
        }

        return fields.TryGetValue("Value", out var value) ? value : null;
    }

    /// <summary>An answer's fields by name, whatever their case; one given twice makes the answer ambiguous.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object)
        {
            throw new RoofSafetySourceException("the Alpaca server's answer is not an Alpaca answer");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in answer.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                // Two values for one field: which the device meant cannot be known.
                throw new RoofSafetySourceException("the Alpaca server's answer repeats a field");
            }
        }

        return fields;
    }

    /// <summary>The request's own ClientTransactionID echoed, and a ServerTransactionID: the answer is this request's.</summary>
    private static void RequireTransactions(Dictionary<string, JsonElement> fields, uint clientTransaction)
    {
        if (!fields.TryGetValue("ClientTransactionID", out var echoed) || echoed.ValueKind != JsonValueKind.Number
            || !echoed.TryGetUInt32(out var echoedTransaction) || echoedTransaction != clientTransaction)
        {
            // A cached or replayed answer, or one meant for another request.
            throw new RoofSafetySourceException("the Alpaca server's answer is not the answer to this request");
        }

        if (!fields.TryGetValue("ServerTransactionID", out var server) || server.ValueKind != JsonValueKind.Number || !server.TryGetUInt32(out _))
        {
            throw new RoofSafetySourceException("the Alpaca server's answer has no ServerTransactionID");
        }
    }

    /// <summary>An Alpaca error number in words: its number and the ASCOM exception it stands for.</summary>
    internal static string DescribeError(int error)
    {
        var meaning = error switch
        {
            NotImplementedError => "not implemented",
            0x401 => "invalid value",
            0x402 => "value not set",
            NotConnectedError => "not connected",
            0x408 => "invalid while parked",
            0x409 => "invalid while slaved",
            0x40B => "invalid operation",
            0x40C => "action not implemented",
            0x40E => "operation cancelled",
            >= 0x500 and <= 0xFFF => "driver error",
            _ => "error"
        };
        return error == NotConnectedError
            ? "the device is not connected (Alpaca error 0x407)"
            : string.Create(CultureInfo.InvariantCulture, $"the device reports Alpaca error 0x{error:X3} ({meaning})");
    }

    private string DevicePath(string member) => string.Create(CultureInfo.InvariantCulture, $"api/v1/{DeviceType}/{DeviceNumber}/{member.ToLowerInvariant()}");

    private async Task<JsonDocument> ReadAsync(string path, uint transaction, CancellationToken cancellationToken)
    {
        var uri = new Uri(_server, string.Create(CultureInfo.InvariantCulture, $"{path}?ClientID={ClientId}&ClientTransactionID={transaction}"));

        var client = _clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Options.Set(RequestMarker, true);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RoofSafetySourceException($"the Alpaca server could not be reached ({DescribeUnreachable(ex)})");
        }

        using (response)
        {
            // Nothing that arrives once the read was given up on counts, whatever it says.
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                // A 400 or 500 carries the server's own text, which is not shown.
                throw new RoofSafetySourceException(string.Create(CultureInfo.InvariantCulture, $"the Alpaca server answered HTTP {(int)response.StatusCode}"));
            }

            if (response.Content.Headers.ContentLength is > MaxAnswerBytes)
            {
                throw new RoofSafetySourceException("the Alpaca server's answer is too long to be an Alpaca answer");
            }

            try
            {
                var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new RoofSafetySourceException("the Alpaca server's answer is not JSON");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                throw new RoofSafetySourceException("the Alpaca server's answer could not be read");
            }
        }
    }

    /// <summary>Why a request could not be sent, in words that name no address: "connection refused", "host unreachable".</summary>
    private static string DescribeUnreachable(HttpRequestException failure)
    {
        if (failure.InnerException is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "connection refused",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "host unreachable",
                SocketError.TimedOut => "connection timed out",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "host name not found",
                _ => "network error"
            };
        }

        return failure.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "host name not found",
            HttpRequestError.SecureConnectionError => "TLS failed",
            HttpRequestError.ConnectionError => "connection failed",
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => "not an HTTP answer",
            _ => "network error"
        };
    }

    // 1 to 4294967295, then 1 again: the Alpaca API reserves 0.
    private static uint NextTransaction() => (uint)(((Interlocked.Increment(ref s_transactions) - 1) % uint.MaxValue) + 1);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxAnswerBytes)
                {
                    throw new RoofSafetySourceException("the Alpaca server's answer is too long to be an Alpaca answer");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}

/// <summary>An Alpaca answer with a non-zero <c>ErrorNumber</c>: a failed read, in <see cref="AlpacaDevice.DescribeError"/>'s words.</summary>
public sealed class AlpacaErrorException : RoofSafetySourceException
{
    public AlpacaErrorException(int errorNumber)
        : base(AlpacaDevice.DescribeError(errorNumber))
    {
        ErrorNumber = errorNumber;
    }

    /// <summary>The Alpaca error number the device answered.</summary>
    public int ErrorNumber { get; }
}

/// <summary>
/// A telescope mount's park state from its ASCOM Alpaca <c>Telescope</c>: <c>Connected</c>, <c>CanPark</c>, then
/// <c>AtPark</c>. A mount that is not connected is a failed read ("the device is not connected"), never "not parked" or
/// "parked". A mount that cannot park can never show it is clear of the roof, so it is a configuration error, a failed
/// read that says so, not "not parked".
/// </summary>
public sealed class AlpacaTelescopeParkSource : ITelescopeParkSource
{
    /// <summary>The failure's words when the mount says it cannot park.</summary>
    public const string CannotParkFailure = "configuration error: the mount reports it cannot park (CanPark is false)";

    /// <summary>The failure's words when the mount does not implement AtPark.</summary>
    public const string NoAtParkFailure = "configuration error: the mount does not implement AtPark";

    private readonly AlpacaDevice _device;

    public AlpacaTelescopeParkSource(RoofSafetyTelescopeOptions options, IHttpClientFactory clients)
    {
        ArgumentNullException.ThrowIfNull(options);
        Name = options.Name.Trim();
        MaxReadingAge = options.MaxReadingAge;
        BlocksRoof = options.BlocksRoof;
        _device = new AlpacaDevice(options.Url, "telescope", options.DeviceNumber, clients);
    }

    public string Name { get; }

    public TimeSpan? MaxReadingAge { get; }

    public bool BlocksRoof { get; }

    public async Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken)
    {
        if (!await _device.ReadBooleanAsync("connected", cancellationToken).ConfigureAwait(false))
        {
            throw new RoofSafetySourceException("the device is not connected");
        }

        try
        {
            if (!await _device.ReadBooleanAsync("canpark", cancellationToken).ConfigureAwait(false))
            {
                throw new RoofSafetySourceException(CannotParkFailure);
            }

            return new TelescopeParkReading(await _device.ReadBooleanAsync("atpark", cancellationToken).ConfigureAwait(false));
        }
        catch (AlpacaErrorException ex) when (ex.ErrorNumber == AlpacaDevice.NotImplementedError)
        {
            throw new RoofSafetySourceException(NoAtParkFailure);
        }
    }
}

/// <summary>
/// Whether the weather is safe, from an ASCOM Alpaca <c>SafetyMonitor</c>: its identity (below), <c>Connected</c>, then
/// <c>IsSafe</c>. A monitor that is not connected is a failed read ("the device is not connected"), never safe or unsafe
/// weather.
/// </summary>
/// <remarks>
/// The monitor is never this roof controller's own Alpaca SafetyMonitor, which publishes the weather the controller
/// reads: reading it would loop (#220). Every read checks the monitor's identity first, before <c>Connected</c> and
/// <c>IsSafe</c>, so a device that changes behind the address between two good reads is caught at the next: its own
/// DriverInfo (which a hub relays) must not be this controller's Alpaca server's (<see cref="AlpacaServerIdentity"/>),
/// and, when <see cref="RoofSafetyOptions.OwnAlpacaUniqueIds"/> lists this installation's own Alpaca devices, the
/// UniqueID its server's Management API lists for it must not be one of them, whatever that server calls itself. With
/// own UniqueIDs listed, a monitor whose UniqueID cannot be read is refused too (<see cref="UnverifiedFailure"/>): it
/// cannot be told from ours. Each refusal is a failed read, so it neither makes nor refreshes a reading.
/// </remarks>
public sealed class AlpacaSafetyMonitorSource : IWeatherSafetySource
{
    /// <summary>The failure's words for this roof controller's own SafetyMonitor.</summary>
    public const string OwnDeviceFailure =
        "configuration error: the device is this roof controller's own Alpaca SafetyMonitor, which publishes the weather the controller reads (a loop)";

    /// <summary>The failure's words for a monitor whose UniqueID its server does not give.</summary>
    public const string UnverifiedFailure =
        "the Alpaca server does not list this device's UniqueID, so it cannot be told from this roof controller's own SafetyMonitor";

    private readonly AlpacaDevice _device;
    private readonly HashSet<string> _ownUniqueIds;

    /// <param name="options">The source's settings.</param>
    /// <param name="clients">Makes the HTTP client.</param>
    /// <param name="ownUniqueIds">This installation's own Alpaca devices' UniqueIDs (<see cref="RoofSafetyOptions.OwnAlpacaUniqueIds"/>).</param>
    public AlpacaSafetyMonitorSource(RoofSafetyWeatherSourceOptions options, IHttpClientFactory clients, IEnumerable<string>? ownUniqueIds = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Name = options.Name.Trim();
        MaxReadingAge = options.MaxReadingAge;
        Requirement = options.Requirement;
        _device = new AlpacaDevice(options.Url, "safetymonitor", options.DeviceNumber, clients);
        _ownUniqueIds = new HashSet<string>((ownUniqueIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public TimeSpan? MaxReadingAge { get; }

    public RoofWeatherSourceRequirement Requirement { get; }

    public async Task<WeatherSafetyReading> ReadWeatherSafetyAsync(CancellationToken cancellationToken)
    {
        // First, before Connected: the identity is readable unconnected, and a hub relaying our own monitor connects it.
        await RefuseOwnMonitorAsync(cancellationToken).ConfigureAwait(false);
        if (!await _device.ReadBooleanAsync("connected", cancellationToken).ConfigureAwait(false))
        {
            throw new RoofSafetySourceException("the device is not connected");
        }

        return new WeatherSafetyReading(await _device.ReadBooleanAsync("issafe", cancellationToken).ConfigureAwait(false));
    }

    private async Task RefuseOwnMonitorAsync(CancellationToken cancellationToken)
    {
        // The device's own words, which a hub relays as they are.
        if (AlpacaServerIdentity.IsOwnDriverInfo(await _device.ReadTextAsync("driverinfo", cancellationToken).ConfigureAwait(false)))
        {
            throw new RoofSafetySourceException(OwnDeviceFailure);
        }

        if (_ownUniqueIds.Count == 0)
        {
            return;
        }

        // The UniqueID its server lists for it, whatever that server calls itself (a hub may list ours for a device it relays).
        var devices = await _device.TryReadManagementAsync("v1/configureddevices", cancellationToken).ConfigureAwait(false);
        var uniqueId = devices is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Where(device => device.ValueKind == JsonValueKind.Object
                    && string.Equals(Text(device, "DeviceType"), "SafetyMonitor", StringComparison.OrdinalIgnoreCase)
                    && Number(device, "DeviceNumber") == _device.DeviceNumber)
                .Select(device => Text(device, "UniqueID"))
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            : null;
        if (uniqueId is null)
        {
            throw new RoofSafetySourceException(UnverifiedFailure);
        }

        if (_ownUniqueIds.Contains(uniqueId.Trim()))
        {
            throw new RoofSafetySourceException(OwnDeviceFailure);
        }
    }

    private static string? Text(JsonElement value, string name)
        => value.EnumerateObject().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } text
            ? text.GetString()
            : null;

    private static int? Number(JsonElement value, string name)
        => value.EnumerateObject().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.Number } number
            && number.TryGetInt32(out var result)
            ? result
            : null;
}
