using System.Reflection;
using Bunit;
using Bunit.Rendering;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// <see cref="BunitEvents"/>: an event on an element bUnit found in markup the page has since drawn again is fired once
/// more, on the page as it is. The race that leaves bUnit with that markup cannot be timed from a test, so its result is
/// set directly: the markup bUnit keeps (a private field of bUnit 2.11.3's <c>RenderedComponent</c>) is put back to the
/// markup of an earlier render.
/// </summary>
[TestClass]
public sealed class BunitEventsTests
{
    [TestMethod]
    public async Task AClick_OnMarkupThePageHasDrawnAgain_IsSentOnceMore_ToThePageAsItIs()
    {
        await using var context = new BunitContext();
        var cut = context.Render<Counter>();
        var old = cut.Nodes;
        cut.Click("button");
        cut.Instance.Count.Should().Be(1);

        KeepOldMarkup(cut, old);
        FluentActions.Invoking(() => cut.InvokeAsync(() => cut.Find("button").Click()).GetAwaiter().GetResult())
            .Should().Throw<UnknownEventHandlerIdException>("bUnit fires the handler of the old markup, which the page dropped");
        cut.Instance.Count.Should().Be(1, "no handler ran");

        KeepOldMarkup(cut, old);
        cut.Click("button");

        cut.Instance.Count.Should().Be(2, "the click is sent once, to the page as it is");
        cut.Find("button").TextContent.Should().Be("2");
    }

    private static void KeepOldMarkup(IRenderedComponent<Counter> cut, AngleSharp.Dom.INodeList nodes)
    {
        var field = cut.GetType().GetField("latestRenderNodes", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("bUnit keeps the markup it finds elements in there (if it has moved, see BunitEvents)");
        field!.SetValue(cut, nodes);
    }

    /// <summary>
    /// A button whose click handler is new at every render, as a page's are when they hold what they were drawn with (a
    /// setting's key, a person's name): Blazor keeps a handler's id only while it is the same delegate.
    /// </summary>
    private sealed class Counter : ComponentBase
    {
        public int Count { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var drawn = Count;
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => Count = drawn + 1));
            builder.AddContent(2, Count);
            builder.CloseElement();
        }
    }
}
