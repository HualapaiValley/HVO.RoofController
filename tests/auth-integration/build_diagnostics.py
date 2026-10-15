#!/usr/bin/env python3
"""Build credential-free diagnostic copies; never edits production source files."""
import argparse,difflib,hashlib,json,pathlib,subprocess,tarfile

DIAGNOSTIC = r'''using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
namespace NAMESPACE;
internal static class AuthDiagnostic
{
    private sealed record Item(string Kind, string Reason, string Trace, int Pid, long Stamp, double Elapsed, int Value);
    private static readonly ConcurrentQueue<Item> Queue = new();
    private static readonly AutoResetEvent Ready = new(false);
    private static int initialized, queued, dropped;
    private static bool childMode, childCanWrite;
    private static readonly HashSet<string> Events =
    ["pam_child_enter","pam_child_rejected","pam_child_result","main_exception","request_received","request_exception",
     "request_finished","lock_wait_begin","lock_acquired","throttle_rejected","password_denied","password_success",
     "pam_start_result","pam_authenticate_result","pam_account_result","pam_process_begin","pam_process_start_false",
     "pam_process_started","pam_input_sent","pam_process_timeout","pam_process_exited","pam_process_exception",
     "transport_start","transport_response","transport_failure","child_diagnostics_complete","diagnostic_dropped"];
    private static readonly HashSet<string> Reasons =
    ["none","input","uid_mismatch","password_locked","password","revalidate","local","failure_window",
     "not_enrolled","enrollment_disabled","pam_or_process","authenticate","account","http_timeout","cancellation",
     "IOException","UnauthorizedAccessException","JsonException","ArgumentException","InvalidOperationException",
     "BadHttpRequestException","Win32Exception","HttpRequestException","TaskCanceledException","InvalidDataException",
     "OperationCanceledException","TimeoutException","Exception","other_exception"];
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int argument);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] data, nuint length);
    internal static long Now => Stopwatch.GetTimestamp();
    private static string SafeTrace(string? value) => value is { Length: 32 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0') ? value : "none";
    internal static string Trace => SafeTrace(Activity.Current?.TraceId.ToString()
        ?? Environment.GetEnvironmentVariable("HVO_AUTH_DIAG_TRACE"));
    internal static void ConfigureChild()
    {
        childMode = true;
        try
        {
            var flags = fcntl(2, 3, 0); // Linux F_GETFL.
            childCanWrite = flags >= 0 && fcntl(2, 4, flags | 0x800) == 0; // F_SETFL, O_NONBLOCK.
        }
        catch (Exception) { childCanWrite = false; }
    }
    internal static void Emit(string kind, string reason = "none", long started = 0, int value = -1)
    {
        try
        {
            if (!Events.Contains(kind)) return;
            var now = Now;
            var item = new Item(kind, Reasons.Contains(reason) ? reason : "other_exception", Trace,
                Environment.ProcessId, now, started == 0 ? 0 : Stopwatch.GetElapsedTime(started, now).TotalMilliseconds, value);
            if (childMode)
            {
                // Bounded writes to an explicitly nonblocking child pipe. Never wait on diagnostics.
                if (childCanWrite)
                {
                    var bytes = Encoding.UTF8.GetBytes(Format(item) + "\n");
                    if (bytes.Length <= 512) _ = write(2, bytes, (nuint)bytes.Length);
                }
                return;
            }
            Enqueue(item);
        }
        catch (Exception) { /* Diagnostics never change the authentication result. */ }
    }
    private static void Enqueue(Item item)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) == 0)
            new Thread(WriteLoop) { IsBackground = true, Name = "HvoAuthDiagnostic" }.Start();
        if (Interlocked.Increment(ref queued) > 4096)
        {
            Interlocked.Decrement(ref queued); Interlocked.Increment(ref dropped); return;
        }
        Queue.Enqueue(item); Ready.Set();
    }
    private static string Format(Item item) => "HVO_AUTH_DIAG|component=COMPONENT|event=" + item.Kind
        + "|trace=" + item.Trace + "|pid=" + item.Pid.ToString(CultureInfo.InvariantCulture)
        + "|stamp=" + item.Stamp.ToString(CultureInfo.InvariantCulture)
        + "|elapsed_ms=" + item.Elapsed.ToString("F3", CultureInfo.InvariantCulture)
        + "|value=" + item.Value.ToString(CultureInfo.InvariantCulture) + "|reason=" + item.Reason;
    private static void WriteLoop()
    {
        while (true)
        {
            Ready.WaitOne();
            while (Queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref queued);
                try { Console.Error.WriteLine(Format(item)); } catch (Exception) { Interlocked.Increment(ref dropped); }
            }
            var lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0)
                try { Console.Error.WriteLine(Format(new("diagnostic_dropped","none","none",Environment.ProcessId,Now,0,lost))); }
                catch (Exception) { }
        }
    }
    internal static void ForwardChild(Task<string> pending)
    {
        // Observe EOF asynchronously; do not race IsCompleted or wait in authentication.
        _ = pending.ContinueWith(task =>
        {
            try
            {
                var count = 0;
                foreach (var line in task.Result.Split('\n'))
                {
                    var pieces = line.Split('|');
                    if (pieces.Length != 9 || pieces[0] != "HVO_AUTH_DIAG" || pieces[1] != "component=native") continue;
                    var fields = pieces.Skip(2).Select(p => p.Split('=', 2)).ToArray();
                    if (fields.Any(p => p.Length != 2)) continue;
                    var expected = new[] { "event","trace","pid","stamp","elapsed_ms","value","reason" };
                    if (!fields.Select(p => p[0]).SequenceEqual(expected)) continue;
                    var kind=fields[0][1]; var trace=fields[1][1]; var reason=fields[6][1];
                    if (!Events.Contains(kind) || !Reasons.Contains(reason) || SafeTrace(trace) != trace && trace != "none"
                        || !int.TryParse(fields[2][1], out var pid) || pid <= 0
                        || !long.TryParse(fields[3][1], out var stamp) || stamp < 0
                        || !double.TryParse(fields[4][1], CultureInfo.InvariantCulture, out var elapsed) || !double.IsFinite(elapsed) || elapsed < 0
                        || !int.TryParse(fields[5][1], out var value)) continue;
                    Enqueue(new(kind,reason,trace,pid,stamp,elapsed,value)); count++;
                }
                Emit("child_diagnostics_complete", value: count);
            }
            catch (Exception) { Emit("child_diagnostics_complete", value: -1); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
'''

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--base",required=True)
    ap.add_argument("--output",required=True,type=pathlib.Path)
    args=ap.parse_args()
    repo=pathlib.Path(__file__).resolve().parents[2]
    assert not args.output.exists(), "Output must be new"
    args.output.mkdir(mode=0o700)
    src=args.output/"source";src.mkdir()
    archive=args.output/"source.tar"
    with archive.open("wb") as stream:
        subprocess.run(["git","archive",args.base],cwd=repo,stdout=stream,check=True)
    with tarfile.open(archive) as tf:tf.extractall(src,filter="data")
    archive.unlink()
    changed={}
    def change(name,fn):
        p=src/name;old=p.read_text();new=fn(old)
        assert old!=new,name
        p.write_text(new)
        changed[name]={"before":hashlib.sha256(old.encode()).hexdigest(),"after":hashlib.sha256(new.encode()).hexdigest()}
        return "".join(difflib.unified_diff(old.splitlines(True),new.splitlines(True),fromfile="a/"+name,tofile="b/"+name))
    def once(s,old,new,count=1):
        assert s.count(old)==count,(old[:80],s.count(old))
        return s.replace(old,new)
    patch=[]
    def program(s):
        s=once(s,'if (args[0] == "pam-check")\n            {','if (args[0] == "pam-check")\n            {\n                AuthDiagnostic.ConfigureChild(); AuthDiagnostic.Emit("pam_child_enter");')
        s=once(s,'''return request is not null && Native.MatchesAccount(request.UserName, request.Uid)
                    && (!request.RequireUnlockedPassword || Native.PasswordIsUnlocked(request.UserName))
                    && Pam.Check(configuration.PamService, request.UserName, request.Password, request.Authenticate) ? 0 : 1;''','''if (request is null) { AuthDiagnostic.Emit("pam_child_rejected", "input"); return 1; }
                if (!Native.MatchesAccount(request.UserName, request.Uid)) { AuthDiagnostic.Emit("pam_child_rejected", "uid_mismatch"); return 1; }
                if (request.RequireUnlockedPassword && !Native.PasswordIsUnlocked(request.UserName)) { AuthDiagnostic.Emit("pam_child_rejected", "password_locked"); return 1; }
                var passed = Pam.Check(configuration.PamService, request.UserName, request.Password, request.Authenticate);
                AuthDiagnostic.Emit("pam_child_result", value: passed ? 0 : 1);
                return passed ? 0 : 1;''')
        s=once(s,'catch (Exception)\n        {','catch (Exception exception)\n        {\n            AuthDiagnostic.Emit("main_exception", exception.GetType().Name);')
        s=once(s,'''app.Use(async (context, next) =>
        {
            try''','''app.Use(async (context, next) =>
        {
            var diagnosticStarted = AuthDiagnostic.Now;
            AuthDiagnostic.Emit("request_received");
            try''')
        s=once(s,'''                if (!context.Response.HasStarted)
                {''','''                AuthDiagnostic.Emit("request_exception", exception.GetType().Name, diagnosticStarted);
                if (!context.Response.HasStarted)
                {''')
        s=once(s,'''            }
        });
        app.MapPost("/v1/password"''','''            }
            finally { AuthDiagnostic.Emit("request_finished", started: diagnosticStarted, value: context.Response.StatusCode); }
        });
        app.MapPost("/v1/password"''')
        for method,kind in (("Password(PasswordRequest request)","password"),("Revalidate(RevalidateRequest request)","revalidate"),("Local(LocalRequest request)","local")):
            old="public string? "+method+"\n    {\n        lock (gate)\n        {"
            new="public string? "+method+'\n    {\n        AuthDiagnostic.Emit("lock_wait_begin", "'+kind+'");\n        var diagnosticWait = AuthDiagnostic.Now;\n        lock (gate)\n        {\n            AuthDiagnostic.Emit("lock_acquired", "'+kind+'", diagnosticWait);'
            s=once(s,old,new)
        s=once(s,'''if (failures.Count >= 1000 || failures.TryGetValue(request.UserName, out var previous) && previous.Attempts >= 5)
                return null;''','''if (failures.Count >= 1000 || failures.TryGetValue(request.UserName, out var previous) && previous.Attempts >= 5)
            { AuthDiagnostic.Emit("throttle_rejected", "failure_window"); return null; }''')
        s=once(s,'''                failures[request.UserName] = failures.TryGetValue(request.UserName, out previous)''','''                AuthDiagnostic.Emit("password_denied", entry is null ? "not_enrolled" : !entry.Enabled ? "enrollment_disabled" : "pam_or_process");
                failures[request.UserName] = failures.TryGetValue(request.UserName, out previous)''')
        s=once(s,'''            failures.Remove(request.UserName);
            return Sign(entry, "linux-password", "login");''','''            failures.Remove(request.UserName);
            AuthDiagnostic.Emit("password_success");
            return Sign(entry, "linux-password", "login");''')
        return s
    patch.append(change("src/HVO.RoofControllerV4.LinuxAuth/Program.cs",program))
    def pam(s):
        s=once(s,'var status = pam_start(service, user, ref conversation, out var handle);','var diagnosticStarted = AuthDiagnostic.Now;\n        var status = pam_start(service, user, ref conversation, out var handle);\n        AuthDiagnostic.Emit("pam_start_result", started: diagnosticStarted, value: status);')
        s=once(s,'if (authenticate) status = pam_authenticate(handle, 0x8000); // PAM_DISALLOW_NULL_AUTHTOK','if (authenticate)\n            {\n                diagnosticStarted = AuthDiagnostic.Now;\n                status = pam_authenticate(handle, 0x8000); // PAM_DISALLOW_NULL_AUTHTOK\n                AuthDiagnostic.Emit("pam_authenticate_result", started: diagnosticStarted, value: status);\n            }')
        s=once(s,'if (status == 0) status = pam_acct_mgmt(handle, 0);','if (status == 0)\n            {\n                diagnosticStarted = AuthDiagnostic.Now;\n                status = pam_acct_mgmt(handle, 0);\n                AuthDiagnostic.Emit("pam_account_result", started: diagnosticStarted, value: status);\n            }')
        return s
    patch.append(change("src/HVO.RoofControllerV4.LinuxAuth/Pam.cs",pam))
    def process(s):
        s=once(s,'        using var process = new Process','        var diagnosticStarted = AuthDiagnostic.Now;\n        AuthDiagnostic.Emit("pam_process_begin", authenticate ? "authenticate" : "account");\n        using var process = new Process')
        s=once(s,'process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";','process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";\n        process.StartInfo.Environment["HVO_AUTH_DIAG_TRACE"] = AuthDiagnostic.Trace;')
        s=once(s,'if (!process.Start()) return false;','if (!process.Start()) { AuthDiagnostic.Emit("pam_process_start_false"); return false; }\n            AuthDiagnostic.Emit("pam_process_started", started: diagnosticStarted, value: process.Id);\n            var diagnosticErrors = process.StandardError.ReadToEndAsync();\n            AuthDiagnostic.ForwardChild(diagnosticErrors);')
        s=once(s,'process.StandardInput.Close();','process.StandardInput.Close();\n            AuthDiagnostic.Emit("pam_input_sent", started: diagnosticStarted);\n            var diagnosticWait = AuthDiagnostic.Now;')
        s=once(s,'''                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                return false;''','''                AuthDiagnostic.Emit("pam_process_timeout", started: diagnosticWait, value: process.Id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                return false;''')
        s=once(s,'return process.ExitCode == 0;','AuthDiagnostic.Emit("pam_process_exited", started: diagnosticWait, value: process.ExitCode);\n            return process.ExitCode == 0;')
        s=once(s,'''            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }''','''            AuthDiagnostic.Emit("pam_process_exception", exception.GetType().Name, diagnosticStarted);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }''')
        assert 'WaitForExit(5000)' in s and 'WaitForExit(2000)' in s
        return s
    patch.append(change("src/HVO.RoofControllerV4.LinuxAuth/PamProcess.cs",process))
    def client(s):
        start=s.index("    public async Task<string?> PasswordAsync")
        end=s.index("    public bool IsCurrent",start)
        block=s[start:end]
        block=once(block,'        try\n        {','        var diagnosticStarted = AuthDiagnostic.Now;\n        AuthDiagnostic.Emit("transport_start", "password");\n        try\n        {')
        block=once(block,'            if (!result.IsSuccessStatusCode) return null;','            AuthDiagnostic.Emit("transport_response", "password", diagnosticStarted, (int)result.StatusCode);\n            if (!result.IsSuccessStatusCode) return null;')
        block=once(block,'{ return null; }','{ AuthDiagnostic.Emit("transport_failure", ex is TaskCanceledException ? (ex.InnerException is TimeoutException ? "http_timeout" : "cancellation") : ex.GetType().Name, diagnosticStarted, cancellationToken.IsCancellationRequested ? 1 : 0); return null; }')
        s=s[:start]+block+s[end:]
        start=s.index("    public bool IsCurrent")
        end=s.index("    public RoofNativeAssertion? Verify",start)
        block=s[start:end]
        block=once(block,'        try\n        {','        var diagnosticStarted = AuthDiagnostic.Now;\n        AuthDiagnostic.Emit("transport_start", "revalidate");\n        try\n        {')
        block=once(block,'            if (!result.IsSuccessStatusCode) return false;','            AuthDiagnostic.Emit("transport_response", "revalidate", diagnosticStarted, (int)result.StatusCode);\n            if (!result.IsSuccessStatusCode) return false;')
        block=once(block,'{ return false; }','{ AuthDiagnostic.Emit("transport_failure", ex is TaskCanceledException ? (ex.InnerException is TimeoutException ? "http_timeout" : "cancellation") : ex.GetType().Name, diagnosticStarted); return false; }')
        s=s[:start]+block+s[end:]
        assert 'Timeout = TimeSpan.FromSeconds(8)' in s
        return s
    patch.append(change("src/HVO.RoofControllerV4.RPi/Security/Identity/RoofNativeAuthClient.cs",client))
    for folder,namespace,component in (
        ("src/HVO.RoofControllerV4.LinuxAuth","HVO.RoofControllerV4.LinuxAuth","native"),
        ("src/HVO.RoofControllerV4.RPi/Security/Identity","HVO.RoofControllerV4.RPi.Security.Identity","controller")):
        p=src/folder/"AuthDiagnostic.cs"
        content=DIAGNOSTIC.replace("NAMESPACE",namespace).replace("COMPONENT",component)
        p.write_text(content)
        changed[str(p.relative_to(src))]={"added":hashlib.sha256(content.encode()).hexdigest()}
        patch.append("".join(difflib.unified_diff([],content.splitlines(True),fromfile="/dev/null",tofile="b/"+str(p.relative_to(src)))))
    (args.output/"instrumentation.patch").write_text("".join(patch))
    receipt={"base":args.base,"scope":"temporary diagnostic source copy only; production worktree not modified","changes":changed,"deadlines":{"pam_wait_ms":5000,"pam_reap_ms":2000,"controller_http_seconds":8,"worker_service_start_seconds":15},"credentials_logged":False}
    for project,label in (("HVO.RoofControllerV4.LinuxAuth","native"),("HVO.RoofControllerV4.RPi","controller")):
        target=args.output/label
        with (args.output/(label+"-publish.log")).open("w") as log:
            r=subprocess.run(["dotnet","publish",project+"/"+project+".csproj","-c","Release","-r","linux-arm64","--self-contained","true","-p:PublishSingleFile=true","-p:IncludeNativeLibrariesForSelfExtract=true","-p:Version=4.0.2-auth.1","-p:SourceRevisionId="+args.base,"-o",str(target)],cwd=src/"src",stdout=log,stderr=subprocess.STDOUT)
        receipt[label+"_build_exit"]=r.returncode
        if r.returncode:
            (args.output/"build-receipt.json").write_text(json.dumps(receipt,indent=2)+"\n")
            raise SystemExit(label+" diagnostic build failed; inspect private publish log")
        binary=target/project
        receipt[label+"_sha256"]=hashlib.sha256(binary.read_bytes()).hexdigest()
    (args.output/"build-receipt.json").write_text(json.dumps(receipt,indent=2)+"\n")
    print(json.dumps(receipt,indent=2))
if __name__=="__main__":main()
