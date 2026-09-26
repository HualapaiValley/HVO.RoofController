using System.Buffers.Text;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

[TestClass]
public sealed class CameraStreamTicketServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Issue_ThenValidate_IsValidForTheSameCamera()
    {
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(clock);

        var issued = service.Issue(3);

        issued.Url.Should().StartWith("/api/v1.0/Camera/3/mjpeg?ticket=");
        Assert.AreEqual(Start.AddSeconds(60), issued.ExpiresUtc);
        Assert.AreEqual(CameraStreamTicketValidation.Valid, service.Validate(TicketOf(issued.Url), 3));
    }

    [TestMethod]
    public void Validate_AfterLifetime_IsExpired()
    {
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(clock);
        var ticket = TicketOf(service.Issue(3).Url);

        clock.Now = Start.AddSeconds(60);
        Assert.AreEqual(CameraStreamTicketValidation.Valid, service.Validate(ticket, 3), "still valid at the expiry second");

        clock.Now = Start.AddSeconds(61);
        Assert.AreEqual(CameraStreamTicketValidation.Expired, service.Validate(ticket, 3));
    }

    [TestMethod]
    public void Validate_OtherCamera_IsWrongCamera()
    {
        var service = CreateService(new ManualTimeProvider(Start));
        var ticket = TicketOf(service.Issue(3).Url);

        Assert.AreEqual(CameraStreamTicketValidation.WrongCamera, service.Validate(ticket, 4));
    }

    [TestMethod]
    public void Validate_EditedPayload_IsBadSignature()
    {
        var service = CreateService(new ManualTimeProvider(Start));
        var ticket = TicketOf(service.Issue(3).Url);
        var parts = ticket.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace("|3|", "|4|", StringComparison.Ordinal);
        var forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + "." + parts[1];

        Assert.AreEqual(CameraStreamTicketValidation.BadSignature, service.Validate(forged, 4));
    }

    [TestMethod]
    public void Validate_TicketFromAnotherKey_IsBadSignature()
    {
        var issuer = CreateService(new ManualTimeProvider(Start), fill: 1);
        var verifier = CreateService(new ManualTimeProvider(Start), fill: 2);

        Assert.AreEqual(CameraStreamTicketValidation.BadSignature, verifier.Validate(TicketOf(issuer.Issue(3).Url), 3));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("no-separator")]
    [DataRow(".sig")]
    [DataRow("payload.")]
    [DataRow("a.b.c")]
    [DataRow("!!!.???")]
    public void Validate_Malformed_IsMalformed(string? ticket)
    {
        var service = CreateService(new ManualTimeProvider(Start));

        Assert.AreEqual(CameraStreamTicketValidation.Malformed, service.Validate(ticket, 3));
    }

    [TestMethod]
    public void Validate_OverlongTicket_IsMalformed()
    {
        var service = CreateService(new ManualTimeProvider(Start));

        Assert.AreEqual(CameraStreamTicketValidation.Malformed, service.Validate(new string('a', 600) + ".b", 3));
    }

    [TestMethod]
    public void Issue_TwiceForTheSameCamera_ProducesDifferentTickets()
    {
        var service = CreateService(new ManualTimeProvider(Start));

        Assert.AreNotEqual(service.Issue(3).Url, service.Issue(3).Url);
    }

    [TestMethod]
    public void Constructor_ShortKey_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CameraStreamTicketService(TimeProvider.System, new byte[16]));
    }

    private static CameraStreamTicketService CreateService(TimeProvider clock, byte fill = 7)
        => new(clock, Enumerable.Repeat(fill, 32).ToArray());

    private static string TicketOf(string url)
        => Uri.UnescapeDataString(url[(url.IndexOf("ticket=", StringComparison.Ordinal) + "ticket=".Length)..]);
}
