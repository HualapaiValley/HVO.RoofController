using System.Threading;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Set when <c>POST System/Restart</c> stops the controller, so that <c>Main</c> exits with
/// <see cref="Common.Models.RoofSettingsContract.RestartExitCode"/> and whatever supervises the process starts it again.
/// </summary>
public sealed class RoofRestartSignal
{
    private int _requested;

    public bool Requested => Volatile.Read(ref _requested) == 1;

    public void Request() => Volatile.Write(ref _requested, 1);
}
