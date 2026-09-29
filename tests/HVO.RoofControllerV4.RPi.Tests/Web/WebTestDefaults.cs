using Bunit;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// bUnit's waits for the whole assembly. The page tests run in parallel with the rest of the suite, where a page that
/// reads the controller over HTTP can take longer than bUnit's default second to render. A wait returns as soon as it
/// passes, so the longer limit only slows a test that fails.
/// </summary>
[TestClass]
public sealed class WebTestDefaults
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [AssemblyInitialize]
    public static void SetWaitTimeout(Microsoft.VisualStudio.TestTools.UnitTesting.TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BunitContext.DefaultWaitTimeout = WaitTimeout;
    }
}
