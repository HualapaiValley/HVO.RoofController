using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Cli;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client library's side of the roof command history (#206): the request it sends, the program it names in its
/// User-Agent, local authorization, and the words every client shows an entry in.
/// </summary>
[TestClass]
public sealed class RoofHistoryClientTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 18, 5, 9, TimeSpan.Zero);

    [TestMethod]
    public void TheQuery_IsSentAsTheQueryString_TimesInUtc()
    {
        RoofApiRoutes.HistoryPage(new RoofHistoryQuery()).Should().Be("api/v4.0/RoofControl/History?take=50");
        RoofApiRoutes.HistoryPage(new RoofHistoryQuery
        {
            User = " ada@lab ",
            From = new DateTimeOffset(2026, 10, 30, 11, 0, 0, TimeSpan.FromHours(-7)),
            To = At,
            Skip = 50,
            Take = 25
        }).Should().Be(
            "api/v4.0/RoofControl/History?user=ada%40lab&from=2026-10-30T18%3A00%3A00.0000000%2B00%3A00"
            + "&to=2026-10-30T18%3A05%3A09.0000000%2B00%3A00&skip=50&take=25");
    }

    [TestMethod]
    public async Task EveryRequest_NamesTheProgramInItsUserAgent()
    {
        var agents = new List<string>();
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            Credential = new RoofApiKeyCredential("test-history-key-not-a-real-secret"),
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                agents.Add(request.Headers.UserAgent.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RoofCommandHistoryPage(), options: RoofClientJson.Options)
                });
            })
        });

        await client.Roof.GetHistoryAsync();
        await client.StopAsync();

        agents.Should().HaveCount(2).And.OnlyContain(agent => agent.Length > 0, "Stop, which is recorded too, names it as well");
        var (name, version) = RoofUserAgent.Of(Assembly.GetEntryAssembly());
        agents[0].Should().Be(version is null ? name : $"{name}/{version}");
    }

    [TestMethod]
    public void TheUserAgent_IsTheProgramsNameAndVersion_WithoutTheCommit()
    {
        var (name, version) = RoofUserAgent.Of(typeof(RoofControllerClient).Assembly);

        name.Should().Be("HVO.RoofControllerV4.Client");
        version.Should().NotBeNullOrEmpty().And.NotContain("+");
        RoofUserAgent.Of(null).Name.Should().Be("HVO.RoofControllerV4.Client", "the library names itself when the program cannot be found");
    }

    [TestMethod]
    public void LocalAuthorization_ReadsTheHistory_AndOffersNothingElseOnIt()
    {
        RoofLocalAuthorization.Operation(HttpMethod.Get, RoofApiRoutes.HistoryPage(new RoofHistoryQuery())).Should().Be("read");
        Action post = () => RoofLocalAuthorization.Operation(HttpMethod.Post, RoofApiRoutes.HistoryPage(new RoofHistoryQuery()));
        post.Should().Throw<InvalidOperationException>();
    }

    // ---- Wording --------------------------------------------------------------------------------------------------

    [TestMethod]
    public void WhoSentIt_IsSaidTheSameEverywhere()
    {
        RoofHistoryText.DescribeWho(Entry() with { CredentialKind = RoofCredentialKind.Session }).Should().Be("ada (signed in)");
        RoofHistoryText.DescribeWho(Entry() with { CredentialKind = RoofCredentialKind.Pin, Device = "kiosk-1" }).Should().Be("ada (PIN at kiosk-1)");
        RoofHistoryText.DescribeWho(Entry() with { CredentialKind = RoofCredentialKind.Pin }).Should().Be("ada (PIN)");
        RoofHistoryText.DescribeWho(Entry() with { CredentialKind = RoofCredentialKind.ApiKey }).Should().Be("ada (personal key)");
        RoofHistoryText.DescribeWho(Entry() with
        {
            User = "web-stop",
            PrincipalKind = RoofHistoryPrincipalKind.ApiKey,
            CredentialKind = RoofCredentialKind.ApiKey,
            OnBehalfOf = "ada"
        }).Should().Be("web-stop (API key) for ada");
        RoofHistoryText.DescribeWho(Entry() with { User = null, PrincipalKind = RoofHistoryPrincipalKind.Anonymous }).Should().Be("anonymous");
    }

    [TestMethod]
    public void TheResultAndTheRoofAfter_AreSaidTheSameEverywhere()
    {
        RoofHistoryText.DescribeResult(Entry()).Should().Be("Accepted");
        RoofHistoryText.DescribeResult(Entry() with { Accepted = false, Code = "FaultLatched" }).Should().Be("Refused [FaultLatched]");
        RoofHistoryText.DescribeResult(Entry() with { Accepted = false }).Should().Be("Refused");
        RoofHistoryText.DescribeOutcome(Entry()).Should().Be("Opening");
        RoofHistoryText.DescribeOutcome(Entry() with { Status = RoofControllerStatus.Closed, IsFaultLatched = true }).Should().Be("Closed, fault latched");
        RoofHistoryText.DescribeOutcome(Entry() with { Status = null }).Should().Be("unknown");
        RoofHistoryText.DescribeCommand(RoofHistoryCommand.ClearFault).Should().Be("Clear fault");
        RoofHistoryText.DescribeCommand(RoofHistoryCommand.Lease).Should().Be("Lease renewal");
    }

    [TestMethod]
    [DataRow("HVO.RoofControllerV4.Web/4.2.0", "127.0.0.1", "web UI, 127.0.0.1")]
    [DataRow("hvo-roof-kiosk/4.2.0", "192.168.1.20", "kiosk, 192.168.1.20")]
    [DataRow("hvo-roof-mac/4.2.0", null, "Mac app")]
    [DataRow("hvo-roof/4.2.0", "10.0.0.5", "hvo-roof, 10.0.0.5")]
    [DataRow("curl/8.5.0", "10.0.0.5", "curl, 10.0.0.5")]
    [DataRow(null, "10.0.0.5", "10.0.0.5")]
    [DataRow(null, null, "unknown")]
    public void TheSource_NamesTheProgramPeopleKnow_AndTheAddress(string? client, string? address, string expected)
        => RoofHistoryText.DescribeSource(Entry() with { Client = client, RemoteAddress = address }).Should().Be(expected);

    [TestMethod]
    public void APage_SaysWhoseCommandsItHolds_WhichOnes_AndHowLongTheyAreKept()
    {
        var page = new RoofCommandHistoryPage
        {
            Entries = [Entry(), Entry()],
            TotalCount = 1234,
            Skip = 50,
            Take = 2,
            AllUsers = true,
            RetentionDays = 90,
            MaximumEntries = 10_000,
            IsPersistent = true
        };

        RoofHistoryText.DescribeScope(page).Should().Be("Everyone's roof commands (admin).");
        RoofHistoryText.DescribeScope(page with { AllUsers = false }).Should().Be("Your own roof commands.");
        RoofHistoryText.DescribeRange(page).Should().Be("51–52 of 1,234 commands.");
        RoofHistoryText.DescribeRange(page with { Entries = [], TotalCount = 0, Skip = 0 }).Should().Be("No commands.");
        RoofHistoryText.DescribeRetention(page).Should().Be("The controller keeps commands for 90 days, up to 10,000.");
        RoofHistoryText.DescribeRetention(page with { IsPersistent = false, RetentionDays = 1 })
            .Should().Be("The controller keeps commands for 1 day, up to 10,000. It keeps them in memory only, so a restart loses them.");
        RoofHistoryText.DescribeRow(Entry()).Should().Equal("2026-10-30 18:05:09Z", "Open", "ada (signed in)", "Accepted", "Opening", "web UI, 127.0.0.1");
        RoofHistoryText.DescribeLine(Entry()).Should().Be("2026-10-30 18:05:09Z  Open  Accepted");
    }

    private static RoofCommandHistoryEntry Entry() => new()
    {
        Id = 7,
        TimestampUtc = At,
        Command = RoofHistoryCommand.Open,
        User = "ada",
        PrincipalKind = RoofHistoryPrincipalKind.Person,
        CredentialKind = RoofCredentialKind.Session,
        RemoteAddress = "127.0.0.1",
        Client = "HVO.RoofControllerV4.Web/4.2.0",
        Accepted = true,
        Status = RoofControllerStatus.Opening
    };
}
