using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Versioning;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// The controller reads exactly the identity schemas its image declares (#130). The Dockerfile's
/// <c>io.hvo.roof.state-contracts</c> label, which release.json's <c>stateContracts</c> repeats, is what the installer
/// compares the identity store with before an upgrade or a rollback: a controller that read another range would be let
/// onto a store it cannot read, or kept off one it can.
/// </summary>
[TestClass]
public sealed partial class IdentityStateContractTests
{
    [TestMethod]
    public void TheController_ReadsTheIdentitySchemasItsImageDeclares_AndNoOthers()
    {
        var identity = DeclaredContract()["identity"]!;
        var min = identity["reads"]!["min"]!.GetValue<int>();
        var max = identity["reads"]!["max"]!.GetValue<int>();
        identity["writes"]!.GetValue<int>().Should().BeInRange(min, max, "a release reads the schema it writes");
        using var directory = new TemporaryDirectory();

        for (var schema = 0; schema <= max + 1; schema++)
        {
            var path = directory.File($"identity-{schema}.json");
            File.WriteAllText(path, $"{{\"schemaVersion\":{schema}}}");
            var load = () => new RoofIdentityFile(path).Load();

            if (schema >= min && schema <= max)
            {
                load.Should().NotThrow($"the image declares it reads identity schema {schema}");
            }
            else
            {
                load.Should().Throw<RoofIdentityStoreException>($"the image declares it reads identity schemas {min} to {max}")
                    .WithMessage($"*schemaVersion {schema} is not supported*");
            }
        }
    }

    // The controller image's io.hvo.roof.state-contracts label, its quotes unescaped.
    private static JsonNode DeclaredContract()
    {
        var dockerfile = File.ReadAllText(Path.Combine(ProductVersionTests.RepositoryRoot(), "src", "HVO.RoofControllerV4.RPi", "Dockerfile"));
        var label = StateContractsLabel().Match(dockerfile);
        label.Success.Should().BeTrue("the controller's Dockerfile declares its state contract");
        return JsonNode.Parse(Regex.Replace(label.Groups[1].Value, @"\\(.)", "$1"))!;
    }

    [GeneratedRegex(@"io\.hvo\.roof\.state-contracts=""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StateContractsLabel();
}
