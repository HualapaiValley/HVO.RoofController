using System.Diagnostics;
using System.Text.Json;

namespace HVO.RoofControllerV4.LinuxAuth;

/// <summary>Each PAM transaction runs in a killable child, with bounded lifetime and no credential arguments.</summary>
internal static class PamProcess
{
    internal static bool Check(string userName, string? password, bool authenticate, uint uid, bool requireUnlockedPassword)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/opt/hvo-roof-linux-auth/HVO.RoofControllerV4.LinuxAuth")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("pam-check");
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        try
        {
            if (!process.Start()) return false;
            process.StandardInput.Write(JsonSerializer.Serialize(new PamRequest(userName, password, authenticate, uid, requireUnlockedPassword), Program.Json));
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return false;
        }
    }
}
