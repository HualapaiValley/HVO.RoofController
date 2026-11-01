using System.Net;
using System.Net.Sockets;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Alpaca;

/// <summary>
/// The controller's Alpaca command settings (#220): <c>RoofController:Alpaca</c>. All local-only. The Alpaca server
/// keeps its own <c>Alpaca</c> section; each process ignores the other's.
/// </summary>
public sealed class RoofAlpacaCommandOptions
{
    public const string SectionName = "RoofController:Alpaca";

    public const int MinOverrideWindowMinutes = 1;
    public const int MaxOverrideWindowMinutes = 15;
    public const int MinScopeRereadBudgetMs = 300;
    public const int MaxScopeRereadBudgetMs = 2000;

    /// <summary>
    /// Which Alpaca commands the controller accepts: Off (the default), CloseOnly, or OpenAndClose (#220, phase 4).
    /// Safety-critical; any change drops every override grant and never stops a motion in progress.
    /// </summary>
    public RoofAlpacaCommandMode AllowCommands { get; set; } = RoofAlpacaCommandMode.Off;

    /// <summary>
    /// With no telescope set to block the roof, whether an Alpaca Open may go on the weather alone (#220, Roy's decision):
    /// false (the default) refuses such an Open unless a person grants it; true lets the weather alone decide, and every
    /// UI warns while Alpaca Open is on. A configured blocking telescope is always checked, whatever this says.
    /// Safety-critical; any change drops every override grant.
    /// </summary>
    public bool AllowOpenWithoutBlockingScopes { get; set; }

    /// <summary>
    /// The Alpaca servers' addresses, besides loopback (always trusted): single host addresses (IPv4 or IPv6), no
    /// networks, never the Docker bridge gateway. The External Alpaca account's key, and the Alpaca server's own account,
    /// are accepted only from these, judged on the TCP peer address before anything a proxy says. A deployment setting:
    /// not in the settings catalogue.
    /// </summary>
    public string[] ServerAddresses { get; set; } = [];

    /// <summary>How long an override grant lasts, in minutes: 1 to 15, default 5. Any change drops every grant.</summary>
    public int OverrideWindowMinutes { get; set; } = 5;

    /// <summary>
    /// How long a command's last-moment re-read of the blocking telescopes may take, in milliseconds: 300 to 2000, default
    /// 600. Set it from the measured latency (the status shows each telescope's p95 and p99): at least 250 ms plus the p99
    /// of every GET the re-read sends plus 100 ms. A re-read that times out counts the telescope as unknown.
    /// </summary>
    public int ScopeRereadBudgetMs { get; set; } = 600;

    /// <summary>The trusted addresses as parsed (loopback is always trusted besides these).</summary>
    public IReadOnlyList<IPAddress> ParsedServerAddresses
        => (ServerAddresses ?? []).Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => IPAddress.TryParse(text.Trim(), out var address) ? Normalize(address) : null)
            .OfType<IPAddress>()
            .ToArray();

    /// <summary>An IPv4-mapped IPv6 address as IPv4; anything else as it is.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>True when <paramref name="peer"/> (the TCP peer, before any proxy header) is loopback or a configured server address.</summary>
    public bool IsTrustedPeer(IPAddress? peer)
    {
        if (peer is null)
        {
            return false;
        }

        var address = Normalize(peer);
        return IPAddress.IsLoopback(address) || ParsedServerAddresses.Any(trusted => trusted.Equals(address));
    }
}

/// <summary>The person-held operator lease's settings: <c>RoofController:Lease</c>. Local-only.</summary>
public sealed class RoofOperatorLeaseOptions
{
    public const string SectionName = "RoofController:Lease";

    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 120;

    /// <summary>
    /// A person's operator lease lapses after this many minutes with no input from them (1 to 120, default 10), only
    /// while Alpaca commands are on; the idle clock starts when they are turned on. A lapse never stops a motion: it
    /// waits until the person's motion ends.
    /// </summary>
    public int OperatorLeaseIdleMinutes { get; set; } = 10;
}

/// <summary>Checks <see cref="RoofAlpacaCommandOptions"/> at start and on every reload.</summary>
public sealed class RoofAlpacaCommandOptionsValidator : IValidateOptions<RoofAlpacaCommandOptions>
{
    /// <summary>Docker's default bridge gateway: the address every host process appears from in a bridged container.</summary>
    internal static readonly IPAddress DockerBridgeGateway = IPAddress.Parse("172.17.0.1");

    private readonly Func<IReadOnlyList<IPAddress>> _gateways;

    public RoofAlpacaCommandOptionsValidator()
        : this(ContainerGateways)
    {
    }

    /// <param name="gateways">This host's default gateways seen from inside a container (tests give their own).</param>
    internal RoofAlpacaCommandOptionsValidator(Func<IReadOnlyList<IPAddress>> gateways)
    {
        _gateways = gateways;
    }

    public ValidateOptionsResult Validate(string? name, RoofAlpacaCommandOptions options)
    {
        var problems = Problems(options, _gateways());
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    internal static List<string> Problems(RoofAlpacaCommandOptions options, IReadOnlyList<IPAddress> gateways)
    {
        var problems = new List<string>();
        const string section = RoofAlpacaCommandOptions.SectionName;
        if (!Enum.IsDefined(options.AllowCommands))
        {
            problems.Add($"{section}:AllowCommands must be Off, CloseOnly or OpenAndClose.");
        }

        if (options.OverrideWindowMinutes is < RoofAlpacaCommandOptions.MinOverrideWindowMinutes or > RoofAlpacaCommandOptions.MaxOverrideWindowMinutes)
        {
            problems.Add($"{section}:OverrideWindowMinutes must be between {RoofAlpacaCommandOptions.MinOverrideWindowMinutes} and {RoofAlpacaCommandOptions.MaxOverrideWindowMinutes}, got {options.OverrideWindowMinutes}.");
        }

        if (options.ScopeRereadBudgetMs is < RoofAlpacaCommandOptions.MinScopeRereadBudgetMs or > RoofAlpacaCommandOptions.MaxScopeRereadBudgetMs)
        {
            problems.Add($"{section}:ScopeRereadBudgetMs must be between {RoofAlpacaCommandOptions.MinScopeRereadBudgetMs} and {RoofAlpacaCommandOptions.MaxScopeRereadBudgetMs}, got {options.ScopeRereadBudgetMs}.");
        }

        for (var index = 0; index < (options.ServerAddresses ?? []).Length; index++)
        {
            var text = options.ServerAddresses![index]?.Trim();
            var label = $"{section}:ServerAddresses:{index}";
            if (string.IsNullOrEmpty(text))
            {
                problems.Add($"{label} is empty.");
            }
            else if (text.Contains('/', StringComparison.Ordinal))
            {
                problems.Add($"{label} is a network; give single host addresses only.");
            }
            else if (!IPAddress.TryParse(text, out var address) || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                problems.Add($"{label} is not an IP address.");
            }
            else
            {
                var normalized = RoofAlpacaCommandOptions.Normalize(address);
                if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any) || normalized.Equals(IPAddress.Broadcast)
                    || normalized.IsIPv6Multicast || (normalized.AddressFamily == AddressFamily.InterNetwork && normalized.GetAddressBytes()[0] is >= 224 and <= 239))
                {
                    problems.Add($"{label} is not a single host's address.");
                }
                else if (normalized.Equals(DockerBridgeGateway) || gateways.Any(gateway => gateway.Equals(normalized)))
                {
                    problems.Add($"{label} is a Docker bridge gateway, through which every process on the host would reach the controller. Give the Alpaca server's own address.");
                }
            }
        }

        return problems;
    }

    /// <summary>Inside a container (/.dockerenv), the default gateways from /proc/net/route; empty elsewhere.</summary>
    internal static IReadOnlyList<IPAddress> ContainerGateways()
    {
        try
        {
            if (!File.Exists("/.dockerenv") || !File.Exists("/proc/net/route"))
            {
                return [];
            }

            var gateways = new List<IPAddress>();
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 2 && fields[1] == "00000000" && uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber, null, out var gateway) && gateway != 0)
                {
                    gateways.Add(new IPAddress(gateway));
                }
            }

            return gateways;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Checks <see cref="RoofOperatorLeaseOptions"/>.</summary>
public sealed class RoofOperatorLeaseOptionsValidator : IValidateOptions<RoofOperatorLeaseOptions>
{
    public ValidateOptionsResult Validate(string? name, RoofOperatorLeaseOptions options)
        => options.OperatorLeaseIdleMinutes is < RoofOperatorLeaseOptions.MinIdleMinutes or > RoofOperatorLeaseOptions.MaxIdleMinutes
            ? ValidateOptionsResult.Fail($"{RoofOperatorLeaseOptions.SectionName}:OperatorLeaseIdleMinutes must be between {RoofOperatorLeaseOptions.MinIdleMinutes} and {RoofOperatorLeaseOptions.MaxIdleMinutes}, got {options.OperatorLeaseIdleMinutes}.")
            : ValidateOptionsResult.Success;
}
