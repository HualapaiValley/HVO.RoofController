using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The web UI's Alpaca tests (#220) against the controller's real Alpaca command state in process: its Stop hold (set at
/// the controller's start), the operator lease the person's session holds, grants and alerts. The tests raise alerts on
/// the controller's own state, as a refused Alpaca Close would.
/// </summary>
internal static class WebAlpacaSupport
{
    /// <summary>Alpaca Close on, with no telescope set to block the roof (so the controller warns of it).</summary>
    public static Dictionary<string, string?> CloseOnly(int? idleMinutes = null)
    {
        var settings = new Dictionary<string, string?> { [$"{RoofAlpacaCommandOptions.SectionName}:AllowCommands"] = nameof(RoofAlpacaCommandMode.CloseOnly) };
        if (idleMinutes is { } minutes)
        {
            settings[$"{RoofOperatorLeaseOptions.SectionName}:OperatorLeaseIdleMinutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return settings;
    }

    /// <summary>The controller's Alpaca command state.</summary>
    public static RoofAlpacaCommands Alpaca(this WebRoofHarness harness) => harness.Host.Services.GetRequiredService<RoofAlpacaCommands>();

    /// <summary>A refused Alpaca Close's alert, as the controller raises it, from a client at a documentation address.</summary>
    public static RoofAlpacaAlert RaiseRefusedClose(this WebRoofHarness harness, params string[] reasonKeys)
    {
        harness.Alpaca().RaiseAlert(
            "Refused",
            RoofAlpacaCommandKind.Close,
            $"An Alpaca Close was refused: {RoofAlpacaText.DescribeReasons(reasonKeys)}.",
            reasonKeys,
            new RoofAlpacaClient(7, 3, "192.0.2.50", "header"));
        return harness.Alpaca().Alerts().First(alert => alert.Kind == "Refused" && alert.ReasonKeys.SequenceEqual(reasonKeys.Order(StringComparer.Ordinal)));
    }

    /// <summary>The lease heartbeats and releases the page sent, in order: <c>POST input</c>, <c>POST</c>, <c>DELETE</c>.</summary>
    public static List<string> RecordLeaseRequests(this WebRoofHarness harness, List<string>? grants = null)
    {
        var requests = new List<string>();
        harness.HoldAnswer = async request =>
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == RoofApiRoutes.AlpacaLease)
            {
                lock (requests)
                {
                    requests.Add(request.Method == HttpMethod.Post
                        ? request.RequestUri.Query.Contains("input=true", StringComparison.Ordinal) ? "POST input" : "POST"
                        : request.Method.Method);
                }
            }
            else if (grants is not null && path == RoofApiRoutes.AlpacaGrants && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync();
                lock (grants)
                {
                    grants.Add(body);
                }
            }
        };
        return requests;
    }

    /// <summary>The page's first heartbeat (the keeper's timer, due at once) takes the lease.</summary>
    public static async Task TakeTheLeaseAsync(this WebRoofHarness harness, List<string> requests)
    {
        var lease = harness.Session!.OperatorLease!;
        harness.Clock.Advance(TimeSpan.Zero);
        await ClientTestSupport.WaitUntilAsync(() => Count(requests) == 1 && lease.State is { Held: true }, "the lease to be taken");
        await ClientTestSupport.WaitUntilAsync(() => harness.Console.View.Status?.Alpaca?.OperatorLease.Held == true, "the status to show it");
    }

    /// <summary>
    /// Sends one heartbeat now and waits for its answer. A heartbeat the keeper is still finishing (its answer is in, its
    /// turn not yet over) is sent again.
    /// </summary>
    public static async Task BeatAsync(this WebOperatorLease lease, List<string> requests)
    {
        var before = Count(requests);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Count(requests) == before)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException("Timed out waiting for a lease heartbeat.");
            }

            await lease.Keeper!.BeatAsync();
            await Task.Delay(10);
        }
    }

    public static int Count(List<string> requests)
    {
        lock (requests)
        {
            return requests.Count;
        }
    }

    /// <summary>A page context with the page's console and the web UI's services the roof page uses.</summary>
    public static BunitContext Context(WebRoofHarness harness)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(harness.Console);
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        context.Services.AddSingleton(Options.Create(new RoofWebOptions()));
        WebCameraStreamTests.SetUpThePlayer(context, out _, out _);
        return context;
    }

    // The text as it reads: whitespace from the markup collapsed.
    public static string Text(IElement element) => Regex.Replace(element.TextContent, @"\s+", " ").Trim();
}
