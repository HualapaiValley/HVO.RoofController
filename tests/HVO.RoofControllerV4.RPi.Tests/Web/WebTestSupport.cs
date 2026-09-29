using System.Net;
using System.Text;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>Helpers for the web UI's tests: a stand-in controller, supervisor state files and options.</summary>
internal static class WebTestSupport
{
    /// <summary>A state file exactly as container/roof-supervisor.sh writes it (captured from a run).</summary>
    public const string SupervisorStateSample =
        "{\"supervisor\":\"running\",\"updatedAt\":\"2026-09-29T05:45:56Z\",\"crashLimit\":5,\"crashWindowSeconds\":120," +
        "\"controller\":{\"state\":\"running\",\"pid\":974360,\"starts\":2,\"recentCrashes\":1,\"lastExitCode\":1," +
        "\"lastExitReason\":\"crashed (exit code 1)\",\"lastExitAt\":\"2026-09-29T05:45:54Z\"}," +
        "\"ui\":{\"state\":\"running\",\"pid\":974380,\"starts\":1,\"recentCrashes\":0,\"lastExitCode\":null," +
        "\"lastExitReason\":null,\"lastExitAt\":null}}\n";

    /// <summary>The sample with the controller in <paramref name="state"/>.</summary>
    public static string SupervisorState(string state, int recentCrashes = 1, string? lastExitReason = "crashed (exit code 1)")
        => SupervisorStateSample
            .Replace("\"controller\":{\"state\":\"running\"", $"\"controller\":{{\"state\":\"{state}\"", StringComparison.Ordinal)
            .Replace("\"recentCrashes\":1", $"\"recentCrashes\":{recentCrashes}", StringComparison.Ordinal)
            .Replace("\"lastExitReason\":\"crashed (exit code 1)\"", lastExitReason is null ? "\"lastExitReason\":null" : $"\"lastExitReason\":\"{lastExitReason}\"", StringComparison.Ordinal);

    public static IOptionsMonitor<RoofWebOptions> Monitor(RoofWebOptions options) => new FixedOptionsMonitor(options);

    /// <summary>A controller client whose every request is answered by <paramref name="answer"/>.</summary>
    public static RoofControllerClient ControllerAnswering(Func<HttpRequestMessage, HttpResponseMessage> answer)
        => new(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = () => new StubHandler(answer),
            RequestTimeout = TimeSpan.FromSeconds(5),
        });

    public static RoofControllerClient ControllerReady() => ControllerAnswering(_ => Text(HttpStatusCode.OK, "Healthy"));

    public static HttpResponseMessage Text(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    /// <summary>A temporary directory, removed on dispose.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("hvo-web-tests-").FullName;
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return Task.FromResult(answer(request));
        }
    }

    private sealed class FixedOptionsMonitor(RoofWebOptions options) : IOptionsMonitor<RoofWebOptions>
    {
        public RoofWebOptions CurrentValue => options;

        public RoofWebOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RoofWebOptions, string?> listener) => null;
    }
}

/// <summary>A controller probe and supervisor reading, built directly, for the wording tests.</summary>
internal static class WebStatuses
{
    public static readonly DateTimeOffset Now = new(2026, 9, 29, 6, 0, 0, TimeSpan.Zero);

    public static RoofWebStatus With(ControllerReadiness readiness, SupervisorSnapshot? snapshot = null, string detail = "Healthy")
        => new(
            new ControllerProbe(readiness, detail),
            snapshot is null ? SupervisorReading.NotSupervised : new SupervisorReading(SupervisorAvailability.Available, snapshot, null),
            Now);

    public static SupervisorSnapshot Controller(string state, int recentCrashes = 0, string? lastExitReason = null, DateTimeOffset? lastExitAt = null)
        => new()
        {
            Supervisor = "running",
            UpdatedAt = Now,
            CrashLimit = 5,
            CrashWindowSeconds = 120,
            Controller = new SupervisedProcess
            {
                State = state,
                Starts = 3,
                RecentCrashes = recentCrashes,
                LastExitReason = lastExitReason,
                LastExitAt = lastExitAt,
            },
            Ui = new SupervisedProcess { State = SupervisedProcess.States.Running, Starts = 1 },
        };
}
