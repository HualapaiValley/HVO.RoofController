using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

[TestClass]
public class RedirectToLoginTests
{
    [TestMethod]
    public async Task RedirectsToLogin_WithFullPageLoad_AndEncodedReturnUrl()
    {
        await using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("roof-control?tab=camera");

        context.Render<RedirectToLogin>();

        var bunitNavigation = (Bunit.TestDoubles.BunitNavigationManager)navigation;
        bunitNavigation.History.Should().Contain(entry =>
            entry.Uri == "login?returnUrl=%2Froof-control%3Ftab%3Dcamera" && entry.Options.ForceLoad);
    }
}
