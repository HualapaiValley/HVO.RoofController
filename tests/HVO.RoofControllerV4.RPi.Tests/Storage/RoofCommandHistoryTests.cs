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
    public void AnOwner_ReadsOnlyTheirOwn_IncludingWhatAKeySentForThem()
    {
        using var history = Create(new ManualTimeProvider(Start));
        history.Record(Entry(RoofHistoryCommand.Open, "ada"));
        history.Record(Entry(RoofHistoryCommand.Close, "bob"));
        history.Record(Entry(RoofHistoryCommand.Stop, "web-stop", RoofHistoryPrincipalKind.ApiKey, onBehalfOf: "ada"));
        history.Record(Entry(RoofHistoryCommand.Stop, "ada", RoofHistoryPrincipalKind.ApiKey));

        var ada = history.Query(new RoofCommandHistoryFilter { Owner = new(RoofHistoryPrincipalKind.Person, "ADA") });
        var adaKey = history.Query(new RoofCommandHistoryFilter { Owner = new(RoofHistoryPrincipalKind.ApiKey, "ada") });

        ada.AllUsers.Should().BeFalse();
        ada.Entries.Select(entry => (entry.User, entry.Command)).Should().Equal(("web-stop", RoofHistoryCommand.Stop), ("ada", RoofHistoryCommand.Open));
        adaKey.Entries.Should().ContainSingle("a key named like a person is not that person")
            .Which.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.ApiKey);
    }

    [TestMethod]
    public void TheUserAndTimeFilters_AndPaging_Apply()
    {
        var clock = new ManualTimeProvider(Start);
        using var history = Create(clock);
        for (var i = 0; i < 6; i++)
        {
            history.Record(Entry(RoofHistoryCommand.Open, i % 2 == 0 ? "ada" : "bob"));
            clock.AdvanceWithoutTimers(TimeSpan.FromHours(1));
        }

        history.Query(new RoofCommandHistoryFilter { User = "Bob" }).Entries.Select(entry => entry.Id).Should().Equal(6, 4, 2);
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
    [DataRow(25_001, 10_000)]
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
        var context = Context(
            Session("ada", RoofCredentialKind.Pin, RoofControllerApiContract.OperatorRole, device: "kiosk-1"),
            IPAddress.Parse("::ffff:192.168.1.20"),
            "hvo-roof-kiosk/4.2.0\u001b[31m");
        var refusal = new ProblemDetails { Detail = "A fault is latched." };
        refusal.Extensions[RoofControllerApiContract.ProblemCodeExtension] = nameof(RoofControllerErrorCode.FaultLatched);
        refusal.Extensions[RoofControllerApiContract.ProblemStatusExtension] = Snapshot(RoofControllerStatus.Closed, faultLatched: true);

        var entry = RoofCommandHistory.Describe(context, RoofHistoryCommand.Open, status: null, refusal);

        entry.Should().BeEquivalentTo(new RoofCommandHistoryEntry
        {
            Command = RoofHistoryCommand.Open,
            User = "ada",
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
        }, "the escape character a terminal would act on is removed");
    }

    [TestMethod]
    public void Describe_KeysAndAnonymousStops()
    {
        var key = RoofPrincipalFactory.Create(
            new RoofApiKeyIdentity("web-stop", RoofControllerApiContract.ViewerRole, "key-1"), RoofControllerSecurityDefaults.ApiKeyScheme, onBehalfOf: "ada");
        var keyEntry = RoofCommandHistory.Describe(Context(key, IPAddress.Loopback, null), RoofHistoryCommand.Stop, Snapshot(RoofControllerStatus.Stopped), null);
        keyEntry.User.Should().Be("web-stop");
        keyEntry.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.ApiKey);
        keyEntry.CredentialKind.Should().Be(RoofCredentialKind.ApiKey);
        keyEntry.OnBehalfOf.Should().Be("ada");
        keyEntry.Accepted.Should().BeTrue();
        keyEntry.Client.Should().BeNull();
        keyEntry.Status.Should().Be(RoofControllerStatus.Stopped);

        var anonymous = RoofCommandHistory.Describe(Context(new ClaimsPrincipal(new ClaimsIdentity()), null, "curl/8"), RoofHistoryCommand.Stop, null, null);
        anonymous.User.Should().BeNull();
        anonymous.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.Anonymous);
        anonymous.CredentialKind.Should().BeNull();
        anonymous.RemoteAddress.Should().BeNull();
        RoofCommandHistory.OwnerOf(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
        RoofCommandHistory.OwnerOf(key).Should().Be(new RoofCommandHistoryOwner(RoofHistoryPrincipalKind.ApiKey, "web-stop"));
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
        RoofCredentialKind? credential = null)
        => new()
        {
            Command = command,
            User = user,
            PrincipalKind = kind,
            CredentialKind = credential ?? (kind == RoofHistoryPrincipalKind.ApiKey ? RoofCredentialKind.ApiKey : RoofCredentialKind.Session),
            OnBehalfOf = onBehalfOf,
            Device = device,
            Accepted = accepted,
            Code = code,
            Status = RoofControllerStatus.Closed
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
