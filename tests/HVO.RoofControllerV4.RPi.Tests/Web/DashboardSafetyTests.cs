using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The roof page's safety badge and Safety row, from a live page's roof console.</summary>
[TestClass]
public sealed class DashboardSafetyTests
{
    [TestMethod]
    public async Task Live_ShowsTheSafetyBadge_InItsColour_WithTheReasonsInItsTitle_AndTheSafetyRow()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = new BunitContext();
        context.Services.AddSingleton(harness.Console);
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        context.Services.AddSingleton(Options.Create(new RoofWebOptions()));
        WebCameraStreamTests.SetUpThePlayer(context, out _, out _);
        await harness.StartLiveAsync();

        var cut = context.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() =>
        {
            var badge = cut.Find("[data-testid=safety]");
            Text(badge).Should().Be("Safety: Unknown");
            badge.GetAttribute("data-state").Should().Be("Unknown");
            badge.ClassList.Should().Contain("bg-warning");
            badge.GetAttribute("title").Should().Be("UNKNOWN: No safety sources configured (advisory: the safety interlock is off)");
            cut.FindAll("[data-testid=status-rows] .rc2-row")
                .Select(row => $"{Text(row.QuerySelector("dt")!)}: {Text(row.QuerySelector("dd")!)}")
                .Should().Contain("Safety: UNKNOWN: No safety sources configured (advisory: the safety interlock is off)");
            cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeFalse("the interlock is off, so Open is offered as before");
        });
    }

    private static string Text(IElement element) => Regex.Replace(element.TextContent, @"\s+", " ").Trim();
}
