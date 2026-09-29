using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// Events for the pages' bUnit tests: the element is found and the event fired on the renderer's thread, and both are
/// done once more if bUnit found the element in markup the page has since drawn again.
/// </summary>
/// <remarks>
/// bUnit (2.11.3) keeps the markup it finds elements in (<c>RenderedComponent.Nodes</c>) until the page's next render,
/// and a render forgets it before it writes the new markup. A read from the test's thread in between (a <c>Find</c>, a
/// wait) parses the old markup, and bUnit keeps that until the page renders again: an event fired on an element found
/// there names a handler the renderer has dropped, and fails with <see cref="UnknownEventHandlerIdException"/> before
/// any handler runs. The page is then rendered again, which makes bUnit read its markup afresh, and the event fired
/// again. The pages have no <c>OnParametersSet</c>, so that render changes nothing else.
/// </remarks>
internal static class BunitEvents
{
    /// <summary>Finds an element and fires an event on it (<paramref name="fire"/> does both), on the renderer's thread.</summary>
    public static void Fire<TComponent>(this IRenderedComponent<TComponent> cut, Action fire)
        where TComponent : IComponent
    {
        try
        {
            cut.InvokeAsync(fire).GetAwaiter().GetResult();
        }
        catch (UnknownEventHandlerIdException)
        {
            cut.Render(_ => { });
            cut.InvokeAsync(fire).GetAwaiter().GetResult();
        }
    }

    public static void Click<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
        => cut.Fire(() => cut.Find(selector).Click());

    public static void Input<TComponent, TValue>(this IRenderedComponent<TComponent> cut, string selector, TValue value)
        where TComponent : IComponent
        => cut.Fire(() => cut.Find(selector).Input(value));

    public static void Change<TComponent, TValue>(this IRenderedComponent<TComponent> cut, string selector, TValue value)
        where TComponent : IComponent
        => cut.Fire(() => cut.Find(selector).Change(value));

    public static void Submit<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
        => cut.Fire(() => cut.Find(selector).Submit());
}
