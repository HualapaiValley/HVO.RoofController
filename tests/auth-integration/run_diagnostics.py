#!/usr/bin/env python3
"""Serial diagnostic comparison, guarded to a specific disposable guest."""
import argparse,datetime,json,os,re,subprocess,threading,time,uuid
from collections import Counter
from pathlib import Path
import urllib.request,urllib.error
from run_acceptance import guard,Api,PASSWORD,STATE,private_write
EVENTS=set("pam_child_enter pam_child_rejected pam_child_result main_exception request_received request_exception request_finished lock_wait_begin lock_acquired throttle_rejected password_denied password_success pam_start_result pam_authenticate_result pam_account_result pam_process_begin pam_process_start_false pam_process_started pam_input_sent pam_process_timeout pam_process_exited pam_process_exception transport_start transport_response transport_failure child_diagnostics_complete diagnostic_dropped".split())
REASONS=set("none input uid_mismatch password_locked password revalidate local failure_window not_enrolled enrollment_disabled pam_or_process authenticate account http_timeout cancellation IOException UnauthorizedAccessException JsonException ArgumentException InvalidOperationException BadHttpRequestException Win32Exception HttpRequestException TaskCanceledException InvalidDataException OperationCanceledException TimeoutException Exception other_exception".split())
PAT=re.compile(r"HVO_AUTH_DIAG\|component=(native|controller)\|event=([a-z_]+)\|trace=(none|[a-f0-9]{32})\|pid=([0-9]+)\|stamp=([0-9]+)\|elapsed_ms=([0-9.]+)\|value=(-?[0-9]+)\|reason=([A-Za-z_]+)(?:\r?$)")
def call(*args,check=True):
    p=subprocess.run(args,capture_output=True,text=True,timeout=90,env={**os.environ,"LC_ALL":"C"})
    if check and p.returncode: raise RuntimeError("command failed: "+args[0])
    return p
def records(since):
    result=[]
    for args in (["journalctl","-u","hvo-roof-linux-auth","--since",since,"-o","cat","--no-pager"],
                 ["docker","logs","--since",since,"roof-controller"]):
        p=call(*args)
        for line in (p.stdout+"\n"+p.stderr).splitlines():
            m=PAT.search(line)
            if not m: continue
            c,e,t,pid,stamp,elapsed,value,reason=m.groups()
            if e not in EVENTS or reason not in REASONS:continue
            result.append(dict(component=c,event=e,trace=t,pid=int(pid),stamp=int(stamp),elapsed_ms=float(elapsed),value=int(value),reason=reason))
    return sorted(result,key=lambda r:(r["stamp"],r["pid"],r["event"]))
def children():
    found=[]
    for p in Path("/proc").glob("[0-9]*/cmdline"):
        try:
            args=p.read_bytes().split(b"\0")
            if b"pam-check" not in args:continue
            stat=(p.parent/"stat").read_text().rsplit(")",1)[1].split()
            found.append({"pid":int(p.parent.name),"state":stat[0],"cpu_ticks":int(stat[11])+int(stat[12])})
        except (FileNotFoundError,ProcessLookupError,PermissionError):pass
    return found
def service(unit):
    raw=call("systemctl","show",unit,"--property=ActiveState,SubState,MainPID,NRestarts,ExecMainStatus,CPUUsageNSec,MemoryCurrent").stdout
    return dict(line.split("=",1) for line in raw.splitlines() if "=" in line)
def basic(groups):
    out={"at":time.time(),"cpu_pressure":Path("/proc/pressure/cpu").read_text().strip(),"load":Path("/proc/loadavg").read_text().split()[:3],"children":children(),"groups":{}}
    for name,path in groups.items():
        fields={}
        for f in ("cpu.stat","cpu.pressure","memory.current","memory.events","pids.current","pids.events"):
            p=path/f
            if p.exists():fields[f]=p.read_text().strip()
        out["groups"][name]=fields
    return out
def correlate(fresh,trace,worker,api_status=None):
    c=sorted([r for r in fresh if r["component"]=="controller"],key=lambda r:r["stamp"])
    n=sorted([r for r in fresh if r["component"]=="native"],key=lambda r:r["stamp"])
    if len(c) not in (2,4) or any(r["trace"]!=trace for r in c):return False
    if any(r["trace"] not in ("none",trace) for r in n):return False
    stages=len(c)//2
    for i in range(stages):
        begin,end=c[2*i:2*i+2];kind=("password","revalidate")[i]
        if begin["event"]!="transport_start" or begin["reason"]!=kind or end["event"] not in ("transport_response","transport_failure") or begin["stamp"]>=end["stamp"]:return False
    first_ok=c[1]["event"]=="transport_response" and c[1]["value"]==200
    if stages==2 and not first_ok:return False
    if api_status==200 and (not first_ok or stages!=2 or c[-1]["event"]!="transport_response" or c[-1]["value"]!=200):return False
    if stages==2 and c[1]["stamp"]>=c[2]["stamp"]:return False
    received=[r for r in n if r["event"]=="request_received"]
    finished=[r for r in n if r["event"]=="request_finished"]
    if len(received)!=stages or len(finished)!=stages:return False
    if any(r["pid"]!=worker for r in received+finished):return False
    starts=[r for r in n if r["event"]=="pam_process_started"]
    done=[r for r in n if r["event"]=="child_diagnostics_complete"]
    if len(starts)!=len(done) or len({r["value"] for r in starts})!=len(starts):return False
    childids={r["value"] for r in starts}
    if any(r["pid"] not in {worker}|childids for r in n):return False
    if any(r["event"]=="diagnostic_dropped" for r in fresh):return False
    for i in range(stages):
        a,b=received[i]["stamp"],finished[i]["stamp"]
        if c[2*i+1]["event"]=="transport_response" and c[2*i+1]["value"]!=finished[i]["value"]:return False
        if a>=b or i and finished[i-1]["stamp"]>=a:return False
        if c[2*i]["stamp"]>a:return False
        interval=[r for r in n if r["pid"]==worker and a<=r["stamp"]<=b]
        locks=[r for r in interval if r["event"]=="lock_wait_begin"]
        acquired=[r for r in interval if r["event"]=="lock_acquired"]
        if len(locks)!=1 or len(acquired)!=1 or locks[0]["reason"]!=("password","revalidate")[i] or not a<=locks[0]["stamp"]<=acquired[0]["stamp"]<b:return False
        ss=[r for r in interval if r["event"]=="pam_process_started"]
        if len(ss)>1:return False
        if not ss:
            if not any(r["event"] in ("throttle_rejected","password_denied","pam_process_start_false","pam_process_exception") for r in interval):return False
        elif acquired[0]["stamp"]>ss[0]["stamp"]:return False
    for i,start in enumerate(starts):
        child=[r for r in n if r["pid"]==start["value"]]
        if done[i]["pid"]!=worker or done[i]["value"]!=len(child) or done[i]["value"]<0 or done[i]["stamp"]<start["stamp"]:return False
        if i+1<len(starts) and done[i]["stamp"]>=starts[i+1]["stamp"]:return False
        interval_index=next((j for j in range(stages) if received[j]["stamp"]<=start["stamp"]<=finished[j]["stamp"]),None)
        if interval_index is None:return False
        if any(r["stamp"]<start["stamp"] or r["stamp"]>finished[interval_index]["stamp"] for r in child):return False
        if interval_index==1 and any(r["event"]=="pam_authenticate_result" for r in child):return False
    for r in n:
        if r["pid"]==worker and r["event"]!="child_diagnostics_complete" and not any(a["stamp"]<=r["stamp"]<=b["stamp"] for a,b in zip(received,finished)):return False
    return True

def main():
    ap=argparse.ArgumentParser();ap.add_argument("--run-id",required=True);ap.add_argument("--phase",required=True,choices=["off-first","on","off-last"]);ap.add_argument("--kiosk",choices=["on","off"],required=True);ap.add_argument("--count",type=int,default=3);ap.add_argument("--cooldown",type=int,default=70)
    a=ap.parse_args();mark=guard();assert mark["run_id"]==a.run_id
    assert 1<=a.count<=3 and a.cooldown>=65
    since=datetime.datetime.now(datetime.timezone.utc).isoformat()
    before=service("hvo-roof-kiosk")
    call("systemctl","start" if a.kiosk=="on" else "stop","hvo-roof-kiosk")
    end=time.monotonic()+a.cooldown
    print(json.dumps({"phase":a.phase,"state":"settling","seconds":a.cooldown}),flush=True)
    while time.monotonic()<end:time.sleep(min(10,end-time.monotonic()))
    assert not children(),"prior native child still active after settling"
    st=service("hvo-roof-kiosk");assert (st["ActiveState"]=="active")== (a.kiosk=="on")
    status=call("passwd","-S","fixture-admin").stdout.split()
    aging=call("chage","-l","fixture-admin").stdout
    assert status[1]=="P" and any(x.startswith("Account expires") and x.split(":",1)[1].strip()=="never" for x in aging.splitlines()),"fixture not eligible"
    groups={}
    units={u:service(u) for u in ("hvo-roof-kiosk","hvo-roof-linux-auth")}
    pid=int(call("docker","inspect","--format","{{.State.Pid}}","roof-controller").stdout)
    pids={**{u:int(v["MainPID"]) for u,v in units.items()},"controller":pid}
    for name,pid in pids.items():
        if pid:
            line=Path("/proc",str(pid),"cgroup").read_text().splitlines()
            path=next(l.split("::",1)[1] for l in line if l.startswith("0::"))
            groups[name]=Path("/sys/fs/cgroup"+path)
    stop=threading.Event();samples=[]
    def sample():
        while not stop.is_set():
            try:samples.append(basic(groups))
            except Exception: samples.append({"at":time.time(),"sample_error":True})
            stop.wait(2)
    thread=threading.Thread(target=sample,daemon=True);thread.start()
    trials=[];api=Api()
    try:
        for i in range(a.count):
            assert not children(), "prior child still active"
            baseline_records=Counter(tuple(sorted(r.items())) for r in records(since))
            trace=uuid.uuid4().hex;span=uuid.uuid4().hex[:16]
            req=urllib.request.Request(api.url+"/api/v4.0/Auth/ProviderSession",data=json.dumps({"provider":"linux","name":"fixture-admin","password":PASSWORD}).encode(),headers={"Accept":"application/json","Content-Type":"application/json","traceparent":"00-"+trace+"-"+span+"-01"},method="POST")
            start=time.monotonic();status=None;body=None;error=None
            try:
                with urllib.request.urlopen(req,context=api.ssl,timeout=60) as response:status=response.status;body=json.loads(response.read())
            except urllib.error.HTTPError as ex:
                status=ex.code
                try:body=json.loads(ex.read())
                except Exception:pass
            except Exception as ex:error=type(ex).__name__
            trial={"index":i+1,"trace":trace,"elapsed_ms":round((time.monotonic()-start)*1000,3),"status":status,"token_present":isinstance(body,dict) and bool(body.get("token")),"error_type":error}
            if isinstance(body,dict):
                code=body.get("code") or (body.get("extensions",{}).get("code") if isinstance(body.get("extensions"),dict) else None)
                if isinstance(code,str) and re.fullmatch("[A-Za-z]{1,64}",code):trial["problem_code"]=code
            del body
            settle=time.monotonic()+60;complete=False;quiet_snapshot=None
            while time.monotonic()<settle:
                observed=records(since)
                snapshot=Counter(tuple(sorted(r.items())) for r in observed)
                delta=snapshot-baseline_records
                fresh=[dict(row) for row,n in delta.items() for _ in range(n)]
                unique=baseline_records<=snapshot and correlate(fresh,trace,pids["hvo-roof-linux-auth"],status)
                events=sorted(fresh,key=lambda r:(r["stamp"],r["pid"]))
                if unique and not children():
                    if quiet_snapshot==snapshot:complete=True;break
                    quiet_snapshot=snapshot
                else:quiet_snapshot=None
                time.sleep(2)
            trial["settled"]=complete;trial["records"]=events
            trial["correlation"]="exclusive-serial-native-stages" if complete else "ambiguous-or-incomplete"
            trial["native_trace_present"]=any(r["component"]=="native" and r["trace"]==trace for r in events)
            trial["diagnostics_incomplete"]=any(r["event"]=="child_diagnostics_complete" and r["value"]<0 for r in events)
            trials.append(trial)
            print(json.dumps({k:v for k,v in trial.items() if k!="records"}),flush=True)
            if not complete:break
            time.sleep(2)
    finally:stop.set();thread.join(timeout=5)
    all_records=records(since)
    result={"run_id":a.run_id,"phase":a.phase,"kiosk_requested":a.kiosk,"cooldown_seconds":a.cooldown,"fixture_eligible":True,"original_kiosk":before,"services_start":units,"services_end":{u:service(u) for u in units},"trials":trials,"samples":samples,"all_records":all_records,"inconclusive":len(trials)!=a.count or any(not t["settled"] or t["diagnostics_incomplete"] for t in trials) or any(r["event"]=="diagnostic_dropped" for r in all_records)}
    private_write(STATE/("diagnostic-"+a.phase+".json"),json.dumps(result,indent=2)+"\n")
    print(json.dumps({"phase":a.phase,"trials":len(trials),"inconclusive":result["inconclusive"]}),flush=True)
if __name__=="__main__":main()
