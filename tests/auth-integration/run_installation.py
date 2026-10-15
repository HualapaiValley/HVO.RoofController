#!/usr/bin/env python3
"""Real installer lifecycle phases. Execute only inside the marked disposable guest."""
import argparse,hashlib,json,re,socket,subprocess,time
from pathlib import Path
from run_acceptance import guard,STATE,VOLATILE,PASSWORD,Api,private_write,expect
ROOT=Path("/opt/hvo-auth-test")
IDENTITY=Path("/var/lib/hvo-roof/identity/identity.json")
RECORD=Path("/etc/hvo-roof/install.json")

def command(args,phase,expected=0):
    result=subprocess.run([str(x) for x in args],capture_output=True,text=True,timeout=1200)
    private_write(STATE/(phase+".log"),result.stdout+"\n"+result.stderr)
    if expected is not None:expect(result.returncode,expected)
    return result

def fingerprint():
    data=json.loads(subprocess.check_output(["docker","inspect","roof-controller"],text=True))[0]
    native={}
    for p in Path("/etc/hvo-roof/linux-auth").rglob("*"):
        if p.is_file():native[str(p)]=hashlib.sha256(p.read_bytes()).hexdigest()
    identity=json.loads(IDENTITY.read_text())
    return {"container":data["Id"],"started":data["State"]["StartedAt"],
        "native_files":native,"principals":identity.get("linuxPrincipals",[]),
        "record_sha256":hashlib.sha256(RECORD.read_bytes()).hexdigest(),
        "users_sha256":hashlib.sha256(json.dumps(identity["users"],sort_keys=True).encode()).hexdigest()}

def answers(native=False,kiosk=False):
    controller={"httpsPort":8443,"webPort":8088,"firstAdmin":{"name":"fixture-admin","pin":True},
        "rig":{"openToLan":False}}
    if native:
        controller["linuxAuth"]={"enabled":True,"users":[
            {"name":"fixture-admin","role":"RoofAdmin","localAdmin":True},
            {"name":"fixture-limited","role":"RoofViewer","localAdmin":True},
            {"name":"fixture-user","role":"RoofViewer","localAdmin":False},
            {"name":"fixture-expired","role":"RoofViewer","localAdmin":False}]}
    value={"schema":1,"roles":["rig"]+(["kiosk"] if kiosk else []),
        "controller":controller,"rigConfirmation":socket.gethostname()}
    return value

def wait_ready():
    end=time.monotonic()+600
    while time.monotonic()<end:
        try:
            api=Api()
            status,session=api.request("POST","Auth/Session",{"name":"fixture-admin","password":PASSWORD})
            if status==200 and api.request("GET","RoofControl/Status",token=session["token"])[0]==200:return api
        except Exception:pass
        time.sleep(3)
    raise RuntimeError("Installed application did not become ready")

def install(folder,native,phase,kiosk=False):
    answer=STATE/("answers-"+phase+".json")
    private_write(answer,json.dumps(answers(native,kiosk)))
    argv=[folder/"hvo-roof-install-linux-arm64","--answers",answer,"--release",folder]
    for name,option in (("admin-password","--admin-password-file"),("admin-pin","--admin-pin-file")):
        if (VOLATILE/name).exists():argv += [option,VOLATILE/name]
    command(argv,phase)
    return wait_ready()

def verify_enabled(api,kiosk,previous=None):
    current=json.loads(IDENTITY.read_text())
    expect(current["schemaVersion"],2)
    backup=Path(str(IDENTITY)+".schema1.bak")
    expect(backup.is_file(),True)
    saved=json.loads(backup.read_text())
    expect(saved.get("schemaVersion",1),1)
    current_user=next(x for x in current["users"] if x["name"]=="fixture-admin")
    saved_user=next(x for x in saved["users"] if x["name"]=="fixture-admin")
    # These two fields are deliberately introduced by MigrateProviderIdentity.
    # A forward repair begins with schema2, so its pre-install user is not a
    # byte-for-byte schema1 backup. Every pre-existing field must stay identical.
    legacy=lambda user:{k:v for k,v in user.items() if k not in ("principalId","authenticationEpoch")}
    expect(legacy(current_user),legacy(saved_user))
    expect(bool(re.fullmatch("[0-9a-f]{32}",current_user.get("principalId",""))),True)
    expect(current_user.get("authenticationEpoch"),saved_user["stamp"])
    if previous is not None:
        prior=next(x for x in previous["users"] if x["name"]=="fixture-admin")
        expect(legacy(current_user),legacy(prior))
        if previous.get("schemaVersion",1)==2:
            expect(current_user,prior)
    expect(api.request("GET","Auth/Providers")[1]["linuxEnabled"],True)
    expect(len(current["linuxPrincipals"]),4)
    private_write(STATE/"installed-options.json",json.dumps({"kiosk":kiosk}))

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("phase",choices=["legacy","enable","verify-enabled","rerun","reject-legacy","upgrade","rollback","rollback-again"])
    parser.add_argument("--kiosk",action="store_true")
    args=parser.parse_args()
    marker=guard()
    phase=args.phase
    result={"phase":phase,"run_id":marker["run_id"],"passed":False}
    try:
        if phase=="legacy":
            api=install(ROOT/"release-legacy",False,phase)
            expect(json.loads(IDENTITY.read_text()).get("schemaVersion",1),1)
            expect(api.request("GET","Auth/Providers")[0],404)
            result["legacy_schema_1"]=True
        elif phase=="enable":
            previous=json.loads(IDENTITY.read_text())
            api=install(ROOT/"release-a",True,phase,args.kiosk)
            verify_enabled(api,args.kiosk,previous)
            result["migration_preserved_legacy_user"]=True
        elif phase=="verify-enabled":
            # Observational follow-up after a completed installer invocation.
            verify_enabled(wait_ready(),args.kiosk)
            result["verification_only"]=True
            result["migration_preserved_legacy_user"]=True
        elif phase=="rerun":
            before=fingerprint()
            kiosk=json.loads((STATE/"installed-options.json").read_text())["kiosk"]
            install(ROOT/"release-a",True,phase,kiosk)
            after=fingerprint()
            # Readiness sign-in changes sessions only; managed files/principals/container/record stay stable.
            expect(after,before)
            result["container_keys_enrollment_and_record_unchanged"]=True
        elif phase=="reject-legacy":
            before=fingerprint()
            called=command(["/usr/local/sbin/hvo-roof-install","rollback","--release",ROOT/"release-legacy"],phase,None)
            expect(called.returncode!=0,True)
            expect("identity schema" in (called.stdout+called.stderr).lower(),True)
            expect(fingerprint(),before)
            result["incompatible_rollback_refused_before_replacement"]=True
        elif phase=="upgrade":
            command(["/usr/local/sbin/hvo-roof-install","upgrade","--version","4.0.2-auth.2","--release",ROOT/"release-b"],phase)
            wait_ready()
            expect(json.loads(RECORD.read_text())["version"],"4.0.2-auth.2")
        elif phase in ("rollback","rollback-again"):
            before=fingerprint()
            command(["/usr/local/sbin/hvo-roof-install","rollback","--release",ROOT/"release-a"],phase)
            wait_ready()
            expect(json.loads(RECORD.read_text())["version"],"4.0.2-auth.1")
            if phase=="rollback-again":expect(fingerprint(),before)
        result["passed"]=True
    except Exception as error:
        result["error_type"]=type(error).__name__
        if hasattr(error,"diagnostic"):result["diagnostic"]=error.diagnostic
    private_write(STATE/(phase+"-installation.json"),json.dumps(result,indent=2)+"\n")
    print(json.dumps(result),flush=True)
    return 0 if result["passed"] else 1
if __name__=="__main__":raise SystemExit(main())
