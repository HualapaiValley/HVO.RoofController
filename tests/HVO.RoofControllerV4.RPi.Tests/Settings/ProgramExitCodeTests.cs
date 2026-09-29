using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.RoofControllerV4.RPi.Tests.Settings;

/// <summary>
/// The exit code <c>Program.Main</c> returns when the host stops: 0 after a normal stop (<c>docker stop</c>), and 75
/// after POST System/Restart. The host disposes its services when it stops, so the answer must not need them then.
/// </summary>
[TestClass]
public sealed class ProgramExitCodeTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, RoofSettingsContract.RestartExitCode)]
    public async Task TheController_ExitsWith0AfterAStop_AndWith75AfterARestartRequest(bool restart, int expected)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<RoofRestartSignal>();
        var app = builder.Build();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var signal = app.Services.GetRequiredService<RoofRestartSignal>();
        lifetime.ApplicationStarted.Register(() =>
        {
            if (restart)
            {
                signal.Request();
            }

            lifetime.StopApplication();
        });

        var exitCode = await Task.Run(() => Program.Run(app)).WaitAsync(TimeSpan.FromSeconds(30));

        exitCode.Should().Be(expected);
    }
}
