using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Storage;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Storage;

/// <summary>
/// The roof command history store (#206): what it keeps and returns, its retention by count and age, and its file:
/// written in the background, read back after a restart, tolerant of a torn line, and bounded by compaction.
/// </summary>
[TestClass]
public sealed class RoofCommandHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    // ---- What is kept and returned --------------------------------------------------------------------------------

    [TestMethod]
    public void Record_GivesIncreasingIdentifiersAndTheTimeNow_AndQueryIsNewestFirst()
    {
        var clock = new ManualTimeProvider(Start);
        using var history = Create(clock);

        var first = history.Record(Entry(RoofHistoryCommand.Open, "ada"))!;
        clock.AdvanceWithoutTimers(TimeSpan.FromSeconds(5));
        var second = history.Record(Entry(RoofHistoryCommand.Stop, "ada"))!;

        first.Id.Should().Be(1);
        first.TimestampUtc.Should().Be(Start);
        second.Id.Should().Be(2);
        second.TimestampUtc.Should().Be(Start.AddSeconds(5));
        var page = history.Query(new RoofCommandHistoryFilter());
        page.Entries.Select(entry => entry.Command).Should().Equal(RoofHistoryCommand.Stop, RoofHistoryCommand.Open);
        page.TotalCount.Should().Be(2);
        page.AllUsers.Should().BeTrue();
        page.IsPersistent.Should().BeFalse("no file is configured");
        page.RetentionDays.Should().Be(90);
        page.MaximumEntries.Should().Be(10_000);
    }

    [TestMethod]
    public void AnAcceptedLeaseRenewal_IsNotKept_ButARefusedOneIs()
    {
        using var history = Create(new ManualTimeProvider(Start));

        history.Record(Entry(RoofHistoryCommand.Lease, "ada")).Should().BeNull("it only keeps a recorded motion going");
        history.Record(Entry(RoofHistoryCommand.Lease, "ada", accepted: false, code: "LeaseNotActive")).Should().NotBeNull();

        history.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle()
            .Which.Code.Should().Be(nameof(RoofControllerErrorCode.LeaseNotActive));
    }

    [TestMethod]
    public void Entries_AreMatchedByIdentifier_NeverByName()
    {
        using var history = Create(new ManualTimeProvider(Start));
        history.Record(Entry(RoofHistoryCommand.Open, "ada", principalId: "person:ada@1"));
        history.Record(Entry(RoofHistoryCommand.Close, "ada", principalId: "person:ada@2"));
        history.Record(Entry(RoofHistoryCommand.Stop, "web-stop", RoofHistoryPrincipalKind.ApiKey, onBehalfOf: "ada", principalId: "key:web"));
        history.Record(Entry(RoofHistoryCommand.Stop, "ada", RoofHistoryPrincipalKind.ApiKey, principalId: "key:ada"));

        var ada = history.Query(new RoofCommandHistoryFilter { PrincipalIds = ["person:ada@1"] });

        ada.AllUsers.Should().BeFalse("a request for some identifiers is someone's own unless it says otherwise");
        history.Query(new RoofCommandHistoryFilter { PrincipalIds = ["person:ada@1"], AllUsers = true }).AllUsers.Should().BeTrue();
        ada.Entries.Should().ContainSingle(
                "a person added again under the same name, a key named like them, and a key that only said it acted for them are others")
            .Which.Command.Should().Be(RoofHistoryCommand.Open);
        history.Query(new RoofCommandHistoryFilter { PrincipalIds = ["person:ada@1", "person:ada@2"] }).TotalCount
            .Should().Be(2, "an admin's search may name several identifiers");
        history.Query(new RoofCommandHistoryFilter { PrincipalIds = [] }).TotalCount.Should().Be(0, "no identifier matches nothing");
    }

    [TestMethod]
    public void TheIdentifierAndTimeFilters_AndPaging_Apply()
    {
        var clock = new ManualTimeProvider(Start);
        using var history = Create(clock);
        for (var i = 0; i < 6; i++)
        {
            history.Record(Entry(RoofHistoryCommand.Open, i % 2 == 0 ? "ada" : "bob"));
            clock.AdvanceWithoutTimers(TimeSpan.FromHours(1));
        }

        history.Query(new RoofCommandHistoryFilter { PrincipalIds = ["person:bob@1"] }).Entries.Select(entry => entry.Id).Should().Equal(6, 4, 2);
        history.Query(new RoofCommandHistoryFilter { From = Start.AddHours(2), To = Start.AddHours(4) }).Entries
            .Select(entry => entry.Id).Should().Equal([4, 3], "from is included and to is not");

        var page = history.Query(new RoofCommandHistoryFilter { Skip = 2, Take = 3 });
        page.Entries.Select(entry => entry.Id).Should().Equal(4, 3, 2);
        page.TotalCount.Should().Be(6);
        page.Skip.Should().Be(2);
        page.Take.Should().Be(3);
    }

    // ---- Retention ------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheOldestEntries_GoBeyondTheMaximum()
    {
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { MaximumEntries = 100 });

        for (var i = 0; i < 130; i++)
        {
            history.Record(Entry(RoofHistoryCommand.Stop, "ada"));
        }

        history.Count.Should().Be(100);
        var page = history.Query(new RoofCommandHistoryFilter { Take = 500 });
        page.Entries[0].Id.Should().Be(130);
        page.Entries[^1].Id.Should().Be(31);
    }

    [TestMethod]
    public void AnEntry_GoesOnceItIsOlderThanTheRetention()
    {
        var clock = new ManualTimeProvider(Start);
        using var history = Create(clock, new RoofCommandHistoryOptions { Retention = TimeSpan.FromDays(2) });
        history.Record(Entry(RoofHistoryCommand.Open, "ada"));
        clock.AdvanceWithoutTimers(TimeSpan.FromDays(1));
        history.Record(Entry(RoofHistoryCommand.Close, "ada"));

        clock.AdvanceWithoutTimers(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        history.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Close);
        history.Count.Should().Be(1);
    }

    [TestMethod]
    [DataRow(0, 10_000)]
    [DataRow(99, 10_000)]
    [DataRow(20_001, 10_000)]
    [DataRow(250, 250)]
    public void AMaximumOutOfRange_IsLogged_AndTheDefaultUsed(int configured, int expected)
    {
        var logger = new CapturingLogger<RoofCommandHistory>();
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { MaximumEntries = configured }, logger: logger);

        history.MaximumEntries.Should().Be(expected);
        logger.Contains(LogLevel.Error, nameof(RoofCommandHistoryOptions.MaximumEntries)).Should().Be(configured != expected);
    }

    [TestMethod]
    public void ARetentionOutOfRange_IsLogged_AndTheDefaultUsed()
    {
        var logger = new CapturingLogger<RoofCommandHistory>();
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { Retention = TimeSpan.FromHours(1) }, logger: logger);

        history.Retention.Should().Be(TimeSpan.FromDays(90));
        logger.Contains(LogLevel.Error, nameof(RoofCommandHistoryOptions.Retention)).Should().BeTrue();
    }

    // ---- The file -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheFile_IsNextToTheIdentityStore_UnlessConfigured()
    {
        RoofCommandHistory.ResolvePath(null, "/var/lib/hvo-roof/identity/identity.json")
            .Should().Be("/var/lib/hvo-roof/identity/command-history.jsonl");
        RoofCommandHistory.ResolvePath("/data/history.jsonl", "/var/lib/hvo-roof/identity/identity.json").Should().Be("/data/history.jsonl");
        RoofCommandHistory.ResolvePath(" ", null).Should().BeNull("with neither, the history is kept in memory");
    }

    [TestMethod]
    public void EntriesWritten_AreReadBackAfterARestart_AndIdentifiersCarryOn()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(Start);
        using (var history = Create(clock, identityStorePath: directory.File("identity.json")))
        {
            history.IsPersistent.Should().BeTrue();
            history.FilePath.Should().Be(directory.File(RoofCommandHistoryOptions.DefaultFileName));
            history.Record(Entry(RoofHistoryCommand.Open, "ada", device: "kiosk-1", credential: RoofCredentialKind.Pin));
            history.Record(Entry(RoofHistoryCommand.Open, "bob", accepted: false, code: "FaultLatched"));
            history.Flush().Should().BeTrue();
        }

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(directory.File(RoofCommandHistoryOptions.DefaultFileName))
                .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "only the controller's user may read who did what");
        }

        using var restarted = Create(clock, identityStorePath: directory.File("identity.json"));
        var page = restarted.Query(new RoofCommandHistoryFilter());
        page.IsPersistent.Should().BeTrue();
        page.Entries.Select(entry => (entry.Id, entry.User, entry.Accepted, entry.Code, entry.Device)).Should().Equal(
            (2, "bob", false, "FaultLatched", null),
            (1, "ada", true, null, "kiosk-1"));
        restarted.Record(Entry(RoofHistoryCommand.Stop, "ada"))!.Id.Should().Be(3);
    }

    [TestMethod]
    public void ALineCutShortByAPowerCut_IsSkipped_AndTheNextEntryStartsAFreshLine()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        var clock = new ManualTimeProvider(Start);
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }))
        {
            history.Record(Entry(RoofHistoryCommand.Open, "ada"));
            history.Flush().Should().BeTrue();
        }

        File.AppendAllText(path, "{\"id\":2,\"timestampUtc\":\"2026-10-30T12:00:01+00:00\",\"comm");
        var logger = new CapturingLogger<RoofCommandHistory>();
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }, logger: logger))
        {
            logger.Contains(LogLevel.Warning, "could not be read").Should().BeTrue();
            history.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle().Which.Id.Should().Be(1);
            history.Record(Entry(RoofHistoryCommand.Close, "ada"))!.Id.Should().Be(2);
            history.Flush().Should().BeTrue();
        }

        File.ReadAllLines(path).Should().HaveCount(2, "the torn line is left out when the file is compacted");
        using var again = Create(clock, new RoofCommandHistoryOptions { StorePath = path });
        again.Query(new RoofCommandHistoryFilter()).Entries.Select(entry => entry.Command).Should().Equal(RoofHistoryCommand.Close, RoofHistoryCommand.Open);
    }

    [TestMethod]
    public void TheFile_IsCompacted_SoItHoldsLittleMoreThanTheMaximum()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { StorePath = path, MaximumEntries = 100 });

        for (var i = 0; i < 400; i++)
        {
            history.Record(Entry(RoofHistoryCommand.Stop, "ada"));
            history.Flush().Should().BeTrue();
        }

        var lines = File.ReadAllLines(path);
        lines.Length.Should().BeInRange(100, 150);
        JsonDocument.Parse(lines[^1]).RootElement.GetProperty("id").GetInt64().Should().Be(400);
    }

    [TestMethod]
    public void ExpiredEntries_AreDroppedAtLoad_AndLeftOutOfTheFileAtTheNextWrite()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        var clock = new ManualTimeProvider(Start);
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }))
        {
            history.Record(Entry(RoofHistoryCommand.Open, "ada"));
            clock.AdvanceWithoutTimers(TimeSpan.FromDays(80));
            history.Record(Entry(RoofHistoryCommand.Close, "ada"));
            history.Flush().Should().BeTrue();
        }

        clock.AdvanceWithoutTimers(TimeSpan.FromDays(20));
        using var restarted = Create(clock, new RoofCommandHistoryOptions { StorePath = path });

        restarted.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Close);
        restarted.Flush().Should().BeTrue();
        File.ReadAllLines(path).Should().ContainSingle();
    }

    [TestMethod]
    public async Task TheBackgroundWriter_SavesARecordedEntry_AndWhatIsLeftWhenTheHostStops()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        using var history = Create(TimeProvider.System, new RoofCommandHistoryOptions { StorePath = path });

        await history.StartAsync(CancellationToken.None);
        history.Record(Entry(RoofHistoryCommand.Open, "ada"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!File.Exists(path) || File.ReadAllLines(path).Length == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        File.ReadAllLines(path).Should().ContainSingle("the writer saves soon after a command is recorded");
        history.Record(Entry(RoofHistoryCommand.Stop, "ada"));
        await history.StopAsync(CancellationToken.None);

        File.ReadAllLines(path).Should().HaveCount(2, "stopping writes what was recorded until then");
    }

    [TestMethod]
    public void AMissingDirectory_KeepsTheHistoryInMemory_AndWritesNothing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "missing", "history.jsonl");
        var logger = new CapturingLogger<RoofCommandHistory>();
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { StorePath = path }, logger: logger);

        history.IsPersistent.Should().BeFalse();
        logger.Contains(LogLevel.Error, "Command history unavailable").Should().BeTrue();
        history.Record(Entry(RoofHistoryCommand.Stop, "ada")).Should().NotBeNull("a command is still recorded");
        history.Flush().Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    [TestMethod]
    public void AFileThatCannotBeWritten_KeepsEntriesInMemory_AndTheyAreSavedOnceItCanBe()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs Unix permissions that apply to the test's user.");
            return;
        }

        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        var logger = new CapturingLogger<RoofCommandHistory>();
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { StorePath = path }, logger: logger);
        history.Record(Entry(RoofHistoryCommand.Open, "ada"));
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            history.Flush().Should().BeFalse();
            logger.Contains(LogLevel.Error, "could not be saved").Should().BeTrue();
            history.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle();
        }
        finally
        {
            File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        history.Flush().Should().BeTrue();
        File.ReadAllLines(path).Should().ContainSingle();
        logger.Contains(LogLevel.Information, "saved again").Should().BeTrue();
    }

    // ---- Who sent it ----------------------------------------------------------------------------------------------

    [TestMethod]
    public void Describe_ARefusedPinCommand_NamesThePersonTheKioskTheCodeAndTheStatus()
    {
        using var history = Create(new ManualTimeProvider(Start));
        var context = Context(
            Session("ada", RoofCredentialKind.Pin, RoofControllerApiContract.OperatorRole, device: "kiosk-1"),
            IPAddress.Parse("::ffff:192.168.1.20"),
            "hvo-roof-kiosk/4.2.0\u001b[31m");
        var refusal = new ProblemDetails { Detail = "A fault is latched." };
        refusal.Extensions[RoofControllerApiContract.ProblemCodeExtension] = nameof(RoofControllerErrorCode.FaultLatched);
        refusal.Extensions[RoofControllerApiContract.ProblemStatusExtension] = Snapshot(RoofControllerStatus.Closed, faultLatched: true);

        var entry = history.Describe(context, RoofHistoryCommand.Open, status: null, refusal);

        entry.Should().BeEquivalentTo(new RoofCommandHistoryEntry
        {
            Command = RoofHistoryCommand.Open,
            User = "ada",
            PrincipalId = "session:session-1",
            PrincipalKind = RoofHistoryPrincipalKind.Person,
            CredentialKind = RoofCredentialKind.Pin,
            Device = "kiosk-1",
            RemoteAddress = "192.168.1.20",
            Client = "hvo-roof-kiosk/4.2.0[31m",
            Accepted = false,
            Code = nameof(RoofControllerErrorCode.FaultLatched),
            Detail = "A fault is latched.",
            Status = RoofControllerStatus.Closed,
            StopReason = RoofControllerStopReason.NormalStop,
            IsFaultLatched = true
        }, "the escape character a terminal would act on is removed; without the people, only the session is known");
    }

    [TestMethod]
    public void Describe_KeysAndAnonymousStops()
    {
        using var history = Create(new ManualTimeProvider(Start));
        var key = RoofPrincipalFactory.Create(
            new RoofApiKeyIdentity("web-stop", RoofControllerApiContract.ViewerRole, "key-1"), RoofControllerSecurityDefaults.ApiKeyScheme, onBehalfOf: "ada");
        var keyEntry = history.Describe(Context(key, IPAddress.Parse("192.168.1.9"), null), RoofHistoryCommand.Stop, Snapshot(RoofControllerStatus.Stopped), null);
        keyEntry.User.Should().Be("web-stop");
        keyEntry.PrincipalId.Should().Be("key:key-1", "a key is matched by its identifier");
        keyEntry.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.ApiKey);
        keyEntry.CredentialKind.Should().Be(RoofCredentialKind.ApiKey);
        keyEntry.OnBehalfOf.Should().Be("ada");
        keyEntry.Accepted.Should().BeTrue();
        keyEntry.Client.Should().BeNull();
        keyEntry.Status.Should().Be(RoofControllerStatus.Stopped);

        var anonymous = history.Describe(Context(new ClaimsPrincipal(new ClaimsIdentity()), null, "curl/8"), RoofHistoryCommand.Stop, null, null);
        anonymous.User.Should().BeNull();
        anonymous.PrincipalId.Should().BeNull();
        anonymous.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.Anonymous);
        anonymous.CredentialKind.Should().BeNull();
        anonymous.RemoteAddress.Should().BeNull();
        history.Principals.IdsOf(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeEmpty();
    }

    [TestMethod]
    public void APerson_IsKnownByTheirPrincipalId_AndByTheirNameAndCreation_NeverByNameAlone()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var legacy = new StoredUser("Ada", RoofControllerApiContract.OperatorRole, null, null, "stamp", created, created);

        RoofCommandHistoryPrincipals.IdsOf(legacy).Should().Equal($"person:ada@{created.UtcTicks}");
        RoofCommandHistoryPrincipals.IdsOf(legacy with { PrincipalId = "0123abcd" })
            .Should().Equal("principal:0123abcd", $"person:ada@{created.UtcTicks}");
        RoofCommandHistoryPrincipals.IdsOf(legacy with { CreatedUtc = created.AddDays(1) })
            .Should().NotIntersectWith(RoofCommandHistoryPrincipals.IdsOf(legacy), "a person added again under the name is someone else");
    }

    [TestMethod]
    [DataRow("open", RoofHistoryCommand.Open)]
    [DataRow("close", RoofHistoryCommand.Close)]
    [DataRow("stop", RoofHistoryCommand.Stop)]
    [DataRow("clear_fault", RoofHistoryCommand.ClearFault)]
    [DataRow("lease", RoofHistoryCommand.Lease)]
    public void EveryRoofCommand_HasAHistoryCommand(string operation, RoofHistoryCommand command)
        => RoofCommandHistory.CommandFor(operation).Should().Be(command);

    [TestMethod]
    public void AnOperationThatIsNotARoofCommand_IsNotRecorded()
        => RoofCommandHistory.CommandFor("update_configuration").Should().BeNull();

    // ---- From where -----------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("127.0.0.1", "192.168.1.20", "192.168.1.20", "127.0.0.1")]
    [DataRow("::1", "192.168.1.20", "192.168.1.20", "::1")]
    [DataRow("::ffff:127.0.0.1", "::ffff:192.168.1.20", "192.168.1.20", "127.0.0.1")]
    [DataRow("192.168.1.5", "10.9.9.9", "192.168.1.5", null)]
    [DataRow("172.17.0.1", "10.9.9.9", "172.17.0.1", null)]
    [DataRow("127.0.0.1", "10.9.9.9, 192.168.1.20", "127.0.0.1", null)]
    [DataRow("127.0.0.1", "not-an-address", "127.0.0.1", null)]
    [DataRow("127.0.0.1", null, "127.0.0.1", null)]
    public void TheForwardedAddress_IsBelievedOnlyFromTheLoopback_AndOnlyWhenItIsOne(
        string connection, string? forwarded, string expectedAddress, string? expectedForwardedBy)
    {
        using var history = Create(new ManualTimeProvider(Start));
        var context = Context(Session("ada", RoofCredentialKind.Session, RoofControllerApiContract.OperatorRole), IPAddress.Parse(connection), null);
        if (forwarded is not null)
        {
            context.Request.Headers[RoofCommandHistoryContract.ForwardedForHeaderName] = forwarded;
        }

        var entry = history.Describe(context, RoofHistoryCommand.Open, null, null);

        entry.RemoteAddress.Should().Be(expectedAddress);
        entry.ForwardedBy.Should().Be(expectedForwardedBy);
    }

    [TestMethod]
    public void TrustedProxies_ReplaceTheLoopback()
    {
        var logger = new CapturingLogger<RoofCommandHistory>();
        using var history = Create(
            new ManualTimeProvider(Start),
            new RoofCommandHistoryOptions { TrustedProxies = ["10.0.0.2", "nonsense"] },
            logger: logger);
        logger.Contains(LogLevel.Error, "'nonsense', which is not an IP address").Should().BeTrue();

        var throughProxy = Context(Session("ada", RoofCredentialKind.Session, RoofControllerApiContract.OperatorRole), IPAddress.Parse("10.0.0.2"), null);
        throughProxy.Request.Headers[RoofCommandHistoryContract.ForwardedForHeaderName] = "192.168.1.20";
        var fromLoopback = Context(Session("ada", RoofCredentialKind.Session, RoofControllerApiContract.OperatorRole), IPAddress.Loopback, null);
        fromLoopback.Request.Headers[RoofCommandHistoryContract.ForwardedForHeaderName] = "192.168.1.20";

        history.Describe(throughProxy, RoofHistoryCommand.Open, null, null).RemoteAddress.Should().Be("192.168.1.20");
        history.Describe(fromLoopback, RoofHistoryCommand.Open, null, null).RemoteAddress.Should().Be("127.0.0.1", "only the configured proxies are believed");
    }

    // ---- Anonymous Stops ------------------------------------------------------------------------------------------

    [TestMethod]
    public void AnonymousStopsFromOneSource_AreOneEntry_WithACount_WhileEachComesWithinTenMinutes()
    {
        var clock = new ManualTimeProvider(Start);
        using var history = Create(clock);

        for (var i = 0; i < 3; i++)
        {
            history.Record(AnonymousStop("192.168.1.66"));
            clock.AdvanceWithoutTimers(TimeSpan.FromMinutes(9));
        }

        history.Record(AnonymousStop("192.168.1.67"));
        history.Record(AnonymousStop("192.168.1.66", accepted: false, code: "RelayStateUnverified"));
        clock.AdvanceWithoutTimers(TimeSpan.FromMinutes(11));
        history.Record(AnonymousStop("192.168.1.66"));

        var entries = history.Query(new RoofCommandHistoryFilter()).Entries;
        entries.Select(entry => (entry.RemoteAddress, entry.Accepted, entry.Count)).Should().Equal(
            ("192.168.1.66", true, 1),
            ("192.168.1.66", false, 1),
            ("192.168.1.67", true, 1),
            ("192.168.1.66", true, 3));
        entries[3].TimestampUtc.Should().Be(Start);
        entries[3].LastTimestampUtc.Should().Be(Start.AddMinutes(18));
    }

    [TestMethod]
    public void AnonymousStopsFromOneIpv6Network_AreOneSource()
    {
        using var history = Create(new ManualTimeProvider(Start));

        history.Record(AnonymousStop("2001:db8:1:2::10"));
        history.Record(AnonymousStop("2001:db8:1:2::99"));

        history.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle("a new address from the same /64 is the same source")
            .Which.Count.Should().Be(2);
    }

    [TestMethod]
    public void SignedInStops_AreNeverCounted_EachIsAnEntry()
    {
        using var history = Create(new ManualTimeProvider(Start));

        history.Record(Entry(RoofHistoryCommand.Stop, "ada") with { RemoteAddress = "192.168.1.66" });
        history.Record(Entry(RoofHistoryCommand.Stop, "ada") with { RemoteAddress = "192.168.1.66" });

        history.Query(new RoofCommandHistoryFilter()).TotalCount.Should().Be(2);
    }

    [TestMethod]
    public void AnonymousStops_FromEverywhere_NeverPushAnyonesCommandsOut()
    {
        using var history = Create(new ManualTimeProvider(Start), new RoofCommandHistoryOptions { MaximumEntries = 100 });
        for (var i = 0; i < 100; i++)
        {
            history.Record(Entry(RoofHistoryCommand.Open, "ada"));
        }

        for (var i = 0; i < 2_000; i++)
        {
            // A new source for each: no two are counted as one.
            history.Record(AnonymousStop($"10.{i / 250}.{i % 250}.1"));
        }

        var all = history.Query(new RoofCommandHistoryFilter { Take = 500 });
        history.Query(new RoofCommandHistoryFilter { PrincipalIds = ["person:ada@1"] }).TotalCount.Should().Be(100, "every Open is still there");
        all.TotalCount.Should().Be(100 + RoofCommandHistoryContract.MaximumAnonymousEntries, "anonymous entries have a cap of their own");
        all.Entries[0].RemoteAddress.Should().Be("10.7.249.1", "the newest anonymous Stops are the ones kept");
    }

    [TestMethod]
    public void ACountedStop_IsSaved_AndReadBackWithItsCount()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        var clock = new ManualTimeProvider(Start);
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }))
        {
            history.Record(AnonymousStop("192.168.1.66"));
            history.Flush().Should().BeTrue();
            clock.AdvanceWithoutTimers(TimeSpan.FromMinutes(1));
            history.Record(AnonymousStop("192.168.1.66"));
            history.Record(AnonymousStop("192.168.1.66"));
            history.Flush().Should().BeTrue();
        }

        File.ReadAllLines(path).Should().HaveCount(2, "a change is one more line, not one per Stop");
        using var restarted = Create(clock, new RoofCommandHistoryOptions { StorePath = path });
        var entry = restarted.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle().Which;
        entry.Count.Should().Be(3);
        entry.LastTimestampUtc.Should().Be(Start.AddMinutes(1));
        restarted.Record(AnonymousStop("192.168.1.66"))!.Count.Should().Be(1, "after a restart the next Stop starts a new entry");
    }

    // ---- A torn file, and its size ---------------------------------------------------------------------------------

    [TestMethod]
    public void ALastLineCutShortAnywhere_NeverLosesTheEntriesBeforeIt()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(Start);
        var whole = directory.File("whole.jsonl");
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = whole }))
        {
            for (var i = 0; i < 3; i++)
            {
                history.Record(Entry(RoofHistoryCommand.Open, "ada") with { Detail = "näive détail" });
            }

            history.Flush().Should().BeTrue();
        }

        var bytes = File.ReadAllBytes(whole);
        var lastLineStart = Array.LastIndexOf(bytes, (byte)'\n', bytes.Length - 2) + 1;

        // Only the newline lost: the line is whole, and kept; the next entry starts a line of its own.
        var unfinished = directory.File("unfinished.jsonl");
        File.WriteAllBytes(unfinished, bytes[..^1]);
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = unfinished }))
        {
            history.Query(new RoofCommandHistoryFilter()).TotalCount.Should().Be(3);
            history.Record(Entry(RoofHistoryCommand.Close, "ada"));
            history.Flush().Should().BeTrue();
        }

        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = unfinished }))
        {
            history.Query(new RoofCommandHistoryFilter()).Entries.Select(entry => entry.Id).Should().Equal(4, 3, 2, 1);
        }

        for (var cut = lastLineStart; cut < bytes.Length - 1; cut++)
        {
            var path = directory.File($"cut-{cut}.jsonl");
            File.WriteAllBytes(path, bytes[..cut]);
            using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }))
            {
                history.Query(new RoofCommandHistoryFilter()).Entries.Select(entry => entry.Id)
                    .Should().Equal([2, 1], "cut at byte {0}, only the last line is lost", cut);
                history.Flush().Should().BeTrue();
            }

            using var again = Create(clock, new RoofCommandHistoryOptions { StorePath = path });
            again.Query(new RoofCommandHistoryFilter()).TotalCount.Should().Be(2, "the file is kept, without the torn line");
        }
    }

    [TestMethod]
    public void ATailOfZeroes_AfterAPowerCut_IsSkipped_AndTheFileKept()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("history.jsonl");
        var clock = new ManualTimeProvider(Start);
        using (var history = Create(clock, new RoofCommandHistoryOptions { StorePath = path }))
        {
            history.Record(Entry(RoofHistoryCommand.Open, "ada"));
            history.Flush().Should().BeTrue();
        }

        File.AppendAllText(path, new string('\0', 4096));

        using var restarted = Create(clock, new RoofCommandHistoryOptions { StorePath = path });
        restarted.Query(new RoofCommandHistoryFilter()).Entries.Should().ContainSingle().Which.Id.Should().Be(1);
        restarted.Record(Entry(RoofHistoryCommand.Close, "ada"))!.Id.Should().Be(2);
        restarted.Flush().Should().BeTrue();
        File.ReadAllLines(path).Should().HaveCount(2);
    }

    [TestMethod]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheLargestFile_FitsWellInsideABackup()
    {
        // The longest entry the controller can record: every text at its limit, a refusal with an explanation.
        var longest = new RoofCommandHistoryEntry
        {
            Id = long.MaxValue,
            TimestampUtc = DateTimeOffset.MaxValue,
            Command = RoofHistoryCommand.ClearFault,
            User = new string('u', RoofIdentityContract.MaximumNameLength),
            PrincipalId = "person:" + new string('u', RoofIdentityContract.MaximumNameLength) + "@" + long.MaxValue,
            PrincipalKind = RoofHistoryPrincipalKind.ApiKey,
            CredentialKind = RoofCredentialKind.Session,
            OnBehalfOf = new string('o', RoofIdentityContract.MaximumNameLength),
            Device = new string('d', RoofIdentityContract.MaximumNameLength),
            RemoteAddress = "ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255",
            ForwardedBy = "ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255",
            Client = new string('c', 100),
            Accepted = false,
            Code = nameof(RoofControllerErrorCode.ConfigurationVersionConflict),
            Detail = new string('x', 200),
            Status = RoofControllerStatus.PartiallyClose,
            StopReason = RoofControllerStopReason.DepartureLimitNotReleased,
            IsFaultLatched = false,
            Count = int.MaxValue,
            LastTimestampUtc = DateTimeOffset.MaxValue
        };
        var line = JsonSerializer.SerializeToUtf8Bytes(longest, RoofCommandHistoryJsonContext.Default.RoofCommandHistoryEntry).Length + 1;
        var kept = RoofCommandHistoryOptions.MostEntries + RoofCommandHistoryContract.MaximumAnonymousEntries;
        var largestFile = (long)line * (kept + Math.Max(kept / 4, 50));

        largestFile.Should().BeLessThan(HVO.RoofControllerV4.Installer.BackupCommands.MaxBytes / 2,
            "a backup holds the identity store's directory whole, and takes {0} bytes in all", HVO.RoofControllerV4.Installer.BackupCommands.MaxBytes);
    }

    // ---- Support --------------------------------------------------------------------------------------------------

    internal static RoofCommandHistory Create(
        TimeProvider clock,
        RoofCommandHistoryOptions? options = null,
        string? identityStorePath = null,
        ILogger<RoofCommandHistory>? logger = null)
        => new(
            Options.Create(options ?? new RoofCommandHistoryOptions()),
            Options.Create(new RoofIdentityOptions { StorePath = identityStorePath }),
            clock,
            logger ?? new CapturingLogger<RoofCommandHistory>());

    internal static RoofCommandHistoryEntry Entry(
        RoofHistoryCommand command,
        string? user,
        RoofHistoryPrincipalKind kind = RoofHistoryPrincipalKind.Person,
        bool accepted = true,
        string? code = null,
        string? onBehalfOf = null,
        string? device = null,
        RoofCredentialKind? credential = null,
        string? principalId = null)
        => new()
        {
            Command = command,
            User = user,
            PrincipalId = principalId ?? (kind == RoofHistoryPrincipalKind.ApiKey ? $"key:{user}" : $"person:{user}@1"),
            PrincipalKind = kind,
            CredentialKind = credential ?? (kind == RoofHistoryPrincipalKind.ApiKey ? RoofCredentialKind.ApiKey : RoofCredentialKind.Session),
            OnBehalfOf = onBehalfOf,
            Device = device,
            Accepted = accepted,
            Code = code,
            Status = RoofControllerStatus.Closed
        };

    private static RoofCommandHistoryEntry AnonymousStop(string address, bool accepted = true, string? code = null)
        => new()
        {
            Command = RoofHistoryCommand.Stop,
            PrincipalKind = RoofHistoryPrincipalKind.Anonymous,
            RemoteAddress = address,
            Accepted = accepted,
            Code = code,
            Status = RoofControllerStatus.Stopped
        };

    private static ClaimsPrincipal Session(string name, RoofCredentialKind kind, string role, string? device = null)
        => RoofPrincipalFactory.CreateForSession(
            new StoredSession("session-1", new string('a', 64), name, role, kind, device, device is null ? null : "kiosk-key-1", Start, Start.AddHours(1)),
            RoofControllerSecurityDefaults.SessionScheme);

    private static DefaultHttpContext Context(ClaimsPrincipal user, IPAddress? remote, string? userAgent)
    {
        var context = new DefaultHttpContext { User = user };
        context.Connection.RemoteIpAddress = remote;
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return context;
    }

    private static RoofStatusResponse Snapshot(RoofControllerStatus status, bool faultLatched = false)
        => new(status, false, RoofControllerStopReason.NormalStop, Start, false, null, false, true, false) { IsFaultLatched = faultLatched };
}
