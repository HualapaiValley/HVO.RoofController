using System.Diagnostics;
using System.Text.Json;

namespace HVO.RoofControllerV4.Cli;

/// <summary>Calls the packaged helper through native sudo. No shell, user-selected executable, credential fallback or prompt.</summary>
internal static class RoofCliLocalAuthority
{
    internal const string HelperPath = "/usr/local/libexec/hvo-roof-local-auth";
    internal const string Denied = "Local Linux authorization failed. The installed helper and current sudo authorization are required; saved web credentials are not used.";

    internal static async Task<string> ObtainAsync(string operation, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new RoofCliRefusedException("Local authorization is available only on Linux.", RoofExitCode.Forbidden);
        }

        if (operation is not ("read" or "operate" or "configure" or "identity"))
        {
            throw new RoofCliUsageException("Unknown local authorization operation.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/sudo")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }};
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(HelperPath);
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        process.StartInfo.Environment["LANG"] = "C";
        try
        {
            process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation }).AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var response = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false); // Never echo a privileged helper's output, even on refusal.
            if (process.ExitCode != 0)
            {
                throw new RoofCliRefusedException(Denied, RoofExitCode.Forbidden);
            }

            using var json = JsonDocument.Parse(response);
            var assertion = json.RootElement.GetProperty("assertion").GetString();
            if (string.IsNullOrWhiteSpace(assertion))
            {
                throw new RoofCliRefusedException(Denied, RoofExitCode.Forbidden);
            }

            return assertion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not RoofCliRefusedException)
        {
            throw new RoofCliRefusedException(Denied, RoofExitCode.Forbidden);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8193];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new string(buffer, 0, used);
            }

            used += read;
        }

        throw new InvalidDataException("Local helper response exceeded its limit.");
    }
}
