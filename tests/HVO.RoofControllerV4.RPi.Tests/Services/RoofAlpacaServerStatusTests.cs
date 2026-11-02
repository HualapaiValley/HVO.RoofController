using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
public sealed class RoofAlpacaServerStatusTests
{
    [TestMethod]
    [DataRow("true", true)]
    [DataRow("True", false)]
    [DataRow("TRUE", false)]
    [DataRow("false", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void Enabled_MatchesTheSupervisorsLiteralOptIn(string? flag, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alpaca:Enabled"] = flag,
            ["Alpaca:StatusFilePath"] = Path.Combine(Path.GetTempPath(), "t3-q229c-missing-" + Guid.NewGuid().ToString("N"))
        }).Build();

        new RoofAlpacaServerStatus(config, TimeProvider.System).Read().Enabled.Should().Be(enabled);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("oversized")]
    [DataRow("future")]
    [DataRow("invalid-id")]
    [DataRow("negative-count")]
    [DataRow("wrong-port")]
    public void InvalidHeartbeat_NeverClaimsALiveServerOrReturnsUntrustedData(string invalid)
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-read-").FullName;
        var path = Path.Combine(directory, "status.json");
        try
        {
            var heartbeat = new AlpacaServerHeartbeat(DateTimeOffset.UtcNow, 11111,
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 2, 1);
            heartbeat = invalid switch
            {
                "future" => heartbeat with { GeneratedAtUtc = DateTimeOffset.UtcNow.AddDays(1) },
                "invalid-id" => heartbeat with { DomeUniqueId = "not-an-identity" },
                "negative-count" => heartbeat with { DomeConnectedClients = -1 },
                "wrong-port" => heartbeat with { Port = 47677 },
                _ => heartbeat
            };
            File.WriteAllText(path, invalid switch
            {
                "malformed" => "{invalid",
                "oversized" => new string('x', 5000),
                _ => JsonSerializer.Serialize(heartbeat)
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:Enabled"] = "true", ["Alpaca:StatusFilePath"] = path
            }).Build();
            var summary = new RoofAlpacaServerStatus(config, TimeProvider.System).Read();
            summary.Enabled.Should().BeTrue();
            summary.Running.Should().BeFalse();
            summary.Port.Should().Be(11111);
            summary.DomeConnectedClients.Should().BeNull();
            summary.SafetyMonitorConnectedClients.Should().BeNull();
            summary.DomeUniqueId.Should().NotBe("not-an-identity");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
