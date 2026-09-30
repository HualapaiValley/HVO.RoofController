using Bunit;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>What the whole test process starts with, before its first test.</summary>
[TestClass]
public sealed class TestProcessDefaults
{
    /// <summary>
    /// bUnit's waits. The page tests run in parallel with the rest of the suite, where a page that reads the controller
    /// over HTTP can take longer than bUnit's default second to render. A wait returns as soon as it passes, so the longer
    /// limit only slows a test that fails.
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The fewest pool worker threads the test process has.</summary>
    public const int MinWorkerThreads = 32;

    [AssemblyInitialize]
    public static void Initialize(Microsoft.VisualStudio.TestTools.UnitTesting.TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BunitContext.DefaultWaitTimeout = WaitTimeout;

        // An in-process emulator (the HAT emulator, a scripted one, the camera) answers on pool threads, while the code
        // under test often blocks a pool thread until it answers: the controller's HAT client, and the socket tests'
        // reads with a 1 s request timeout. With the tests running in parallel, a pool that starts with one worker per
        // core and adds about 2 a second can hold an answer past that timeout. Raised here, before the first test, so a
        // test that runs early has it too. A deployed emulator is a separate process and never shares the pool.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, MinWorkerThreads), completionPorts);
    }
}
