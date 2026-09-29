using System.Net;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>A person changes their own password; the controller checks the current one and ends their other sessions.</summary>
[TestClass]
public sealed class ChangePasswordPageTests
{
    private const string NewPassword = "a much longer new password";

    [TestMethod]
    public async Task TheRightCurrentPassword_ChangesIt_WithThePersonsSession()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();

        Submit(cut, FakeController.AdaPassword, NewPassword, NewPassword);

        cut.WaitForAssertion(() => Message(cut).Should().Be("Your password was changed. Your other sessions were ended."));
        cut.Find("[data-testid=password-message]").ClassList.Should().Contain("alert-success");
        var change = fixture.Controller.Logged(HttpMethod.Post, RoofApiRoutesTest.Password).Should().ContainSingle().Subject;
        change.Authorization.Should().Be("Bearer token-ada");
        change.Body.Should().Contain(NewPassword);
        FieldsAreEmpty(cut);
        session.IsOpen.Should().BeTrue("this session stays signed in");
    }

    [TestMethod]
    public async Task AWrongCurrentPassword_SaysSo_AndKeepsTheSession()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();

        Submit(cut, "not the password", NewPassword, NewPassword);

        cut.WaitForAssertion(() => Message(cut).Should().Be("The current password is not correct. The password was not changed."));
        cut.Find("[data-testid=password-message]").ClassList.Should().Contain("alert-danger");
        session.IsOpen.Should().BeTrue("a wrong current password does not end the session");
        FieldsAreEmpty(cut);
    }

    [TestMethod]
    [DataRow("", NewPassword, NewPassword, "Enter your current password.")]
    [DataRow(FakeController.AdaPassword, "too short", "too short", "A password has 12 to 256 characters.")]
    [DataRow(FakeController.AdaPassword, NewPassword, NewPassword + "!", "The two new passwords are not the same.")]
    public async Task AFormThatCannotBeRight_IsNotSent(string current, string newPassword, string again, string message)
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();

        Submit(cut, current, newPassword, again);

        cut.WaitForAssertion(() => Message(cut).Should().Be(message));
        fixture.Controller.Logged(HttpMethod.Post, RoofApiRoutesTest.Password).Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheControllerRefusing_SaysWhy()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        fixture.Controller.PasswordAnswer = () => FakeController.Problem(HttpStatusCode.TooManyRequests, RoofControllerErrorCode.SignInBusy, TimeSpan.FromSeconds(30));
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();

        Submit(cut, FakeController.AdaPassword, NewPassword, NewPassword);

        cut.WaitForAssertion(() => Message(cut).Should().MatchRegex(@"^The password was not changed\. \S"));
        cut.Find("[data-testid=password-message]").ClassList.Should().Contain("alert-danger");
    }

    [TestMethod]
    [DataRow(false, "The controller could not be reached.")]
    [DataRow(true, "The controller did not answer in time.")]
    public async Task AnAnswerThatNeverCame_SaysThePasswordMayHaveBeenChanged(bool timedOut, string reason)
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        fixture.Controller.PasswordAnswer = () => timedOut
            ? throw new TimeoutException("The answer never came (test).")
            : throw new HttpRequestException("The connection was reset (test).");
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();

        Submit(cut, FakeController.AdaPassword, NewPassword, NewPassword);

        cut.WaitForAssertion(() => Message(cut).Should().Be($"{reason} {ChangePassword.MayHaveChanged}"));
        Message(cut).Should().NotContain("was not changed", "the controller may have changed it before the answer was lost");
        cut.Find("[data-testid=password-message]").ClassList.Should().Contain("alert-warning");
        FieldsAreEmpty(cut);
    }

    [TestMethod]
    public async Task AnEndedSession_AsksToSignInAgain()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);
        var cut = context.Render<ChangePassword>();
        session.End();

        Submit(cut, FakeController.AdaPassword, NewPassword, NewPassword);

        cut.WaitForAssertion(() => Message(cut).Should().Be("Your session ended. Sign in again, then change your password."));
        fixture.Controller.Logged(HttpMethod.Post, RoofApiRoutesTest.Password).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ThePage_StatesTheRule_AndUsesPasswordFields()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        await using var context = Context(fixture, session);

        var cut = context.Render<ChangePassword>();

        cut.Find("p.web-muted").TextContent.Should().Contain("A password has 12 to 256 characters.");
        cut.FindAll("input").Select(input => (input.GetAttribute("type"), input.GetAttribute("autocomplete"))).Should().Equal(
            ("password", "current-password"), ("password", "new-password"), ("password", "new-password"));
        cut.FindAll("[data-testid=password-message]").Should().BeEmpty();
    }

    private static BunitContext Context(WebSessionStoreTests.Fixture fixture, WebSession session)
    {
        var context = new BunitContext();
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized(session.Name);
        authorization.SetClaims(new Claim(WebAuthentication.SessionIdClaimType, session.Id));
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddScoped<WebSessionAccessor>();
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return context;
    }

    private static void Submit(IRenderedComponent<ChangePassword> cut, string current, string newPassword, string again)
    {
        cut.Find("#password-current").Change(current);
        cut.Find("#password-new").Change(newPassword);
        cut.Find("#password-again").Change(again);
        cut.Find("form").Submit();
    }

    private static string Message(IRenderedComponent<ChangePassword> cut) => cut.Find("[data-testid=password-message]").TextContent.Trim();

    private static void FieldsAreEmpty(IRenderedComponent<ChangePassword> cut)
        => cut.FindAll("input").Select(input => input.GetAttribute("value") ?? string.Empty).Should().AllBe(string.Empty);
}
