using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Http;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The web UI names each signed-in person's browser to the controller (#206): every request to the web UI sets the
/// address from the connection, never from a header the browser sent, and every request for the person carries it in
/// <c>X-Forwarded-For</c>, Stop included.
/// </summary>
[TestClass]
public sealed class WebForwardedForTests
{
    [TestMethod]
    public async Task ARequestOfThePerson_SetsTheAddressTheirCommandsName_FromTheConnectionOnly()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        var middleware = new WebForwardedFor(_ => Task.CompletedTask, harness.Store);
        var context = new DefaultHttpContext { User = WebAuthentication.CreatePrincipal(session) };
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.168.1.20");
        context.Request.Headers[RoofCommandHistoryContract.ForwardedForHeaderName] = "10.6.6.6";

        await middleware.InvokeAsync(context);

        session.Credential.ForwardedFor.Should().Be("192.168.1.20", "a header the browser sent is never passed on");
        var forwarded = new KeyValuePair<string, string>(RoofCommandHistoryContract.ForwardedForHeaderName, "192.168.1.20");
        session.Credential.GetHeaders(RoofCredentialUse.Request).Should().Contain(forwarded);
        session.Credential.GetHeaders(RoofCredentialUse.Stop).Should().Contain(forwarded);
        session.Credential.GetHeaders(RoofCredentialUse.Request).Count(header => header.Key == RoofCommandHistoryContract.ForwardedForHeaderName)
            .Should().Be(1);
    }

    [TestMethod]
    public async Task ARequestOfNobodySignedIn_ChangesNoOnesAddress()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        session.ForwardFor(IPAddress.Parse("192.168.1.20"));
        var middleware = new WebForwardedFor(_ => Task.CompletedTask, harness.Store);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.99");

        await middleware.InvokeAsync(context);

        session.Credential.ForwardedFor.Should().Be("192.168.1.20");
    }

    [TestMethod]
    public void WithoutAnAddress_NoHeaderIsSent()
    {
        var credential = new RoofWebCredential(new RoofSessionCredential("test-session-token-not-a-secret", "ada", RoofControllerApiContract.OperatorRole, "session-1"), stopKey: null);

        credential.GetHeaders(RoofCredentialUse.Request).Should().NotContain(header => header.Key == RoofCommandHistoryContract.ForwardedForHeaderName);
        credential.ForwardFor(IPAddress.Parse("192.168.1.20"));
        credential.ForwardFor(null);
        credential.GetHeaders(RoofCredentialUse.Request).Should().NotContain(header => header.Key == RoofCommandHistoryContract.ForwardedForHeaderName);
    }
}
