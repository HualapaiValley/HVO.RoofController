#!/usr/bin/env python3
"""Actual application acceptance inside the disposable guest only. Never logs credentials."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import pwd
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

# Public inert fixture inputs, provisioned only after the disposable-guest guard.
PASSWORD = "Public-disposable-HVO-fixture-2026!"
PIN = "629417"
USERS = {"fixture-admin":26001,"fixture-limited":26002,"fixture-user":26003,"fixture-expired":26004}
STATE = Path("/var/lib/hvo-auth-acceptance")
VOLATILE = Path("/run/hvo-auth-acceptance")
REPORT = STATE/"report.json"

def guard():
    marker = json.loads(Path("/etc/hvo-disposable-guest.json").read_text())
    if os.geteuid() != 0 or marker.get("purpose") != "hvo-auth-install-test":
        raise RuntimeError("Disposable guest required")
    kind = subprocess.run(["systemd-detect-virt"],capture_output=True,text=True).stdout.strip()
    if kind not in ("qemu","kvm") or Path("/dev/gpiomem").exists():
        raise RuntimeError("Expected disposable emulated guest")
    return marker

def run(argv, **kwargs):
    return subprocess.run(argv,capture_output=True,text=True,timeout=90,**kwargs)

def private_write(path, value):
    path.parent.mkdir(mode=0o700,parents=True,exist_ok=True)
    path.parent.chmod(0o700)
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
    with os.fdopen(fd,"w") as f: f.write(value)
    path.chmod(0o600)

def prepare():
    marker=guard()
    STATE.mkdir(mode=0o700,exist_ok=True)
    VOLATILE.mkdir(mode=0o700,exist_ok=True)
    for name,uid in USERS.items():
        try:
            existing=pwd.getpwnam(name)
            assert existing.pw_uid==uid
        except KeyError:
            result=run(["useradd","--create-home","--user-group","--uid",str(uid),"--shell","/bin/bash",name])
            if result.returncode: raise RuntimeError("Fixture account creation failed")
    result=run(["chpasswd"],input="".join(name+":"+PASSWORD+"\n" for name in USERS))
    if result.returncode: raise RuntimeError("Fixture credential provisioning failed")
    result=run(["chage","--expiredate","0","fixture-expired"])
    if result.returncode: raise RuntimeError("Fixture account expiration failed")
    private_write(VOLATILE/"admin-password",PASSWORD+"\n")
    private_write(VOLATILE/"admin-pin",PIN+"\n")
    # These fixture-only overrides are deliberately created after the QEMU/root guard.
    secrets=Path("/etc/hvo-roof/secrets")
    private_write(secrets/"RoofControllerSecurity__Identity__PinSessionIdleTimeout","00:00:05\n")
    private_write(secrets/"RoofControllerSecurity__Identity__SignInAttemptsPerMinute","0\n")
    print(json.dumps({"prepared":True,"run_id":marker["run_id"],"fixture_users":list(USERS)}))

class Api:
    def __init__(self,url="https://localhost:8443",ca="/etc/hvo-roof/ca.crt"):
        self.url=url
        self.ssl=ssl.create_default_context(cafile=ca)
    def request(self,method,path,body=None,token=None,key=None):
        headers={"Accept":"application/json"}
        if token: headers["Authorization"]="Bearer "+token
        if key: headers["X-Api-Key"]=key
        data=None if body is None else json.dumps(body).encode()
        if data is not None: headers["Content-Type"]="application/json"
        request=urllib.request.Request(self.url+"/api/v4.0/"+path,data=data,headers=headers,method=method)
        try:
            with urllib.request.urlopen(request,context=self.ssl,timeout=60) as response:
                raw=response.read()
                return response.status,json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            raw=error.read()
            try: body=json.loads(raw) if raw else None
            except ValueError: body=None
            return error.code,body
    def login(self,provider="virtual",name="fixture-admin",password=PASSWORD):
        status,body=self.request("POST","Auth/ProviderSession",{"provider":provider,"name":name,"password":password})
        if status!=200 or not isinstance(body,dict) or not body.get("token"):
            raise RuntimeError("Fixture sign-in failed: HTTP "+str(status))
        return body["token"]

class AcceptanceMismatch(AssertionError):
    def __init__(self,actual,expected):
        super().__init__("Unexpected acceptance result")
        # Only numeric/bool results are safe to include. Never serialize response bodies/tokens.
        self.diagnostic = ({"actual":actual,"expected":expected}
                           if type(actual) in (int,bool) and type(expected) in (int,bool)
                           else {"detail":"response shape or value mismatch"})

def expect(actual,expected):
    if actual!=expected:
        raise AcceptanceMismatch(actual,expected)

def local_assertion(user,operation="read",extras=None,environment=None,direct=False):
    command=["runuser","-u",user,"--"]
    if not direct: command += ["sudo","-n","--"]
    command += ["/usr/local/libexec/hvo-roof-local-auth"]
    request={"operation":operation}
    request.update(extras or {})
    env=os.environ.copy()
    env.update(environment or {})
    result=run(command,input=json.dumps(request),env=env)
    if result.returncode:
        return result.returncode,None
    try: return result.returncode,json.loads(result.stdout).get("assertion")
    except ValueError: return result.returncode,None

def acceptance():
    marker=guard()
    api=Api()
    cases=[]
    state={"run_id":marker["run_id"],"boot_id":Path("/proc/sys/kernel/random/boot_id").read_text().strip()}
    def record(name,fn,coverage="real-production-api"):
        try:
            fn()
            cases.append({"name":name,"passed":True,"coverage":coverage})
        except Exception as error:
            cases.append({"name":name,"passed":False,"coverage":coverage,"error_type":type(error).__name__})
            if isinstance(error,AcceptanceMismatch): cases[-1]["diagnostic"]=error.diagnostic
        private_write(REPORT,json.dumps({"phase":"acceptance","cases":cases},indent=2)+"\n")
        print(json.dumps(cases[-1]),flush=True)
    try:
        record("provider_capability_enabled",lambda:expect(api.request("GET","Auth/Providers"),(200,{"linuxEnabled":True,"providers":["virtual","linux"]})))
        virtual=api.login()
        linux=api.login("linux")
        viewer=api.login("linux","fixture-user")
        record("real_linux_admin_signin",lambda:expect(api.request("GET","Auth/Me",token=linux)[1]["role"],"RoofAdmin"),"real-PAM-and-production-api")
        record("real_linux_nonadmin_viewer_signin",lambda:expect(api.request("GET","Auth/Me",token=viewer)[1]["role"],"RoofViewer"),"real-PAM-and-production-api")
        record("wrong_linux_password_denied",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-admin","password":"wrong-public-fixture"})[0],401),"real-PAM-and-production-api")
        record("expired_linux_account_denied",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-expired","password":PASSWORD})[0],401),"real-PAM-and-production-api")
        record("wrong_virtual_password_denied",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"virtual","name":"fixture-admin","password":"wrong-public-fixture"})[0],401))
        record("linux_viewer_admin_api_denied",lambda:expect(api.request("GET","Identity/Users",token=viewer)[0],403))
        record("linux_viewer_configuration_write_denied",lambda:expect(api.request("POST","RoofControl/Configuration",{},token=viewer)[0],403))
        record("linux_admin_identity_api_allowed",lambda:expect(api.request("GET","Identity/Users",token=linux)[0],200))
        record("web_session_reused",lambda:(expect(api.request("GET","Auth/Me",token=linux)[0],200),expect(api.request("GET","RoofControl/Status",token=linux)[0],200)))
        record("unauthenticated_key_mint_denied",lambda:expect(api.request("POST","Auth/Keys",{"name":"fixture-noauth","scopes":["read"],"expiresUtc":(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=1)).isoformat()})[0],401))
        owners={}
        for provider,session in [("linux",linux),("virtual",virtual)]:
            status,created=api.request("POST","Auth/Keys",{"name":"fixture-"+provider,"scopes":["read"],"expiresUtc":(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=1)).isoformat()},token=session)
            expect(status,201)
            secret=created["secret"]
            meta=created["key"]
            owners[provider]=meta["ownerId"]
            record(provider+"_owned_key_reads",lambda k=secret:expect(api.request("GET","RoofControl/Status",key=k)[0],200))
            record(provider+"_scoped_key_config_denied",lambda k=secret:expect(api.request("POST","RoofControl/Configuration",{},key=k)[0],403))
            record(provider+"_scoped_key_identity_denied",lambda k=secret:expect(api.request("GET","Identity/Users",key=k)[0],403))
            record(provider+"_valid_scoped_key_stop_allowed",lambda k=secret:expect(api.request("POST","RoofControl/Stop",{},key=k)[0],200))
            record(provider+"_api_key_cannot_mint_key",lambda k=secret:expect(api.request("POST","Auth/Keys",{"name":"fixture-escalation","scopes":["read"],"expiresUtc":(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=1)).isoformat()},key=k)[0],401))
            record(provider+"_personal_identity_scope_rejected",lambda s=session:expect(api.request("POST","Auth/Keys",{"name":"fixture-identity-scope","scopes":["identity"],"expiresUtc":(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=1)).isoformat()},token=s)[0],400))
            other=virtual if provider=="linux" else linux
            record(provider+"_other_owner_cannot_revoke_key_nondisclosing_404",lambda s=other,i=meta["id"]:expect(api.request("DELETE","Auth/Keys/"+i,token=s)[0],404))
            record(provider+"_cross_owner_attempt_preserves_key",lambda k=secret:expect(api.request("GET","RoofControl/Status",key=k)[0],200))
            record(provider+"_owner_revokes_key",lambda s=session,i=meta["id"]:expect(api.request("DELETE","Auth/Keys/"+i,token=s)[0],204))
            record(provider+"_revoked_key_denied",lambda k=secret:expect(api.request("GET","RoofControl/Status",key=k)[0],401))
            state[provider+"_revoked_key"]=secret
        record("same_name_provider_owners_distinct",lambda:expect(owners["linux"]!=owners["virtual"],True))

        def mint(session,name,lifetime=3600):
            expires=dt.datetime.now(dt.timezone.utc)+dt.timedelta(seconds=lifetime)
            status,body=api.request("POST","Auth/Keys",{"name":name,"scopes":["read"],"expiresUtc":expires.isoformat()},token=session)
            expect(status,201)
            return body["secret"],body["key"]["id"],expires.timestamp()

        transient_session=api.login("linux")
        transient_key,transient_id,_=mint(transient_session,"fixture-native-expiration")
        try:
            expect(run(["chage","--expiredate","0","fixture-admin"]).returncode,0)
            record("native_account_expiry_denies_existing_linux_session",lambda:expect(api.request("GET","RoofControl/Status",token=transient_session)[0],401),"real-account-eligibility-and-production-api")
            record("native_account_expiry_denies_existing_linux_key",lambda:expect(api.request("GET","RoofControl/Status",key=transient_key)[0],401),"real-account-eligibility-and-production-api")
            record("native_account_expiry_denies_new_linux_login",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-admin","password":PASSWORD})[0],401),"real-account-eligibility-and-production-api")
        finally:
            expect(run(["chage","--expiredate","-1","fixture-admin"]).returncode,0)
        linux=api.login("linux")
        record("restored_account_allows_fresh_login_nonpermanent_eligibility_denial",lambda:expect(api.request("GET","Auth/Me",token=linux)[0],200),"real-account-eligibility-and-production-api")
        record("eligible_owner_can_revoke_native_expiry_fixture_key",lambda:expect(api.request("DELETE","Auth/Keys/"+transient_id,token=linux)[0],204))

        status,principals=api.request("GET","Identity/Principals",token=virtual)
        expect(status,200)
        principal=next(p for p in principals if p["provider"]=="linux" and p["name"]=="fixture-admin")
        for action,role,enabled in [("role_change","RoofViewer",True),("disable","RoofAdmin",False)]:
            old_session=api.login("linux")
            old_key,_,_=mint(old_session,"fixture-principal-"+action)
            try:
                expect(api.request("PUT","Identity/Principals/"+principal["id"],{"role":role,"enabled":enabled},token=virtual)[0],200)
                record(action+"_invalidates_existing_linux_session",lambda t=old_session:expect(api.request("GET","RoofControl/Status",token=t)[0],401))
                record(action+"_invalidates_existing_owned_key",lambda k=old_key:expect(api.request("GET","RoofControl/Status",key=k)[0],401))
                if enabled:
                    changed=api.login("linux")
                    record("changed_role_fresh_login_has_only_viewer_access",lambda t=changed:(expect(api.request("GET","RoofControl/Status",token=t)[0],200),expect(api.request("GET","Identity/Users",token=t)[0],403)))
                else:
                    record("disabled_principal_cannot_sign_in",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-admin","password":PASSWORD})[0],401))
            finally:
                expect(api.request("PUT","Identity/Principals/"+principal["id"],{"role":"RoofAdmin","enabled":True},token=virtual)[0],200)
            record(action+"_restore_does_not_revive_old_credentials",lambda t=old_session,k=old_key:(expect(api.request("GET","RoofControl/Status",token=t)[0],401),expect(api.request("GET","RoofControl/Status",key=k)[0],401)))
        linux=api.login("linux")

        expiring_key,_,key_deadline=mint(virtual,"fixture-wall-clock-expiry",lifetime=8)
        record("personal_key_valid_before_real_expiry",lambda:expect(api.request("GET","RoofControl/Status",key=expiring_key)[0],200))
        time.sleep(max(0,min(10,key_deadline-time.time()+1)))
        record("personal_key_real_wall_clock_expiry",lambda:expect(api.request("GET","RoofControl/Status",key=expiring_key)[0],401),"real-time-and-production-api")

        # PIN/device credentials remain independent of Linux identity and worker availability.
        expect(api.request("PUT","Identity/Users/fixture-admin",{"role":"RoofAdmin","pin":PIN},token=virtual)[0],200)
        devices={}
        for suffix in ("a","b"):
            name="fixture-acceptance-kiosk-"+suffix
            cleanup_status=api.request("DELETE","Identity/ApiKeys/"+name,token=virtual)[0]
            expect(cleanup_status in (204,404),True)
            status,created=api.request("POST","Identity/ApiKeys",{"name":name,"role":"RoofViewer","kiosk":True},token=virtual)
            expect(status,201)
            devices[suffix]=created["secret"]
        record("wrong_pin_denied",lambda:expect(api.request("POST","Auth/Pin",{"name":"fixture-admin","pin":"000000"},key=devices["a"])[0],401))
        status,pin_session=api.request("POST","Auth/Pin",{"name":"fixture-admin","pin":PIN},key=devices["a"])
        expect(status,200)
        pin_token=pin_session["token"]
        record("correct_pin_creates_session",lambda:expect(api.request("GET","Auth/Me",token=pin_token)[0],200))
        record("pin_session_uses_fixture_five_second_idle_timeout",lambda:expect(pin_session.get("idleTimeoutSeconds"),5))
        time.sleep(6)
        record("pin_session_real_idle_expiry",lambda:expect(api.request("GET","Auth/Me",token=pin_token)[0],401),"real-time-and-production-api")
        record("stop_with_idle_expired_pin_and_valid_kiosk_key",lambda:expect(api.request("POST","RoofControl/Stop",{},token=pin_token,key=devices["b"])[0],200))
        status,pin_session=api.request("POST","Auth/Pin",{"name":"fixture-admin","pin":PIN},key=devices["a"])
        expect(status,200)
        revoked_pin=pin_session["token"]
        expect(api.request("DELETE","Identity/ApiKeys/fixture-acceptance-kiosk-a",token=virtual)[0],204)
        record("kiosk_key_revoke_invalidates_its_pin_session",lambda:expect(api.request("GET","Auth/Me",token=revoked_pin)[0],401))
        record("stop_with_revoked_pin_and_other_valid_kiosk_key",lambda:expect(api.request("POST","RoofControl/Stop",{},token=revoked_pin,key=devices["b"])[0],200))

        def controller_identity():
            result=run(["docker","inspect","--format","{{.Id}} {{.State.StartedAt}}","roof-controller"])
            expect(result.returncode,0)
            return result.stdout.strip()
        before_container=controller_identity()
        before_directory=Path("/run/hvo-roof-linux-auth/controller").stat().st_ino
        expect(run(["systemctl","restart","hvo-roof-linux-auth.service"]).returncode,0)
        linux=api.login("linux")
        record("worker_restart_recovers_linux_auth_without_controller_replacement",lambda:(expect(api.request("GET","Auth/Me",token=linux)[0],200),expect(controller_identity()==before_container,True),expect(Path("/run/hvo-roof-linux-auth/controller").stat().st_ino,before_directory)),"real-systemd-and-production-api")
        worker_key,worker_key_id,_=mint(linux,"fixture-worker-outage")
        try:
            expect(run(["systemctl","stop","hvo-roof-linux-auth.service"]).returncode,0)
            record("stopped_worker_denies_linux_login",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-admin","password":PASSWORD})[0],401),"real-systemd-and-production-api")
            record("stopped_worker_denies_existing_linux_session",lambda:expect(api.request("GET","RoofControl/Status",token=linux)[0],401),"real-systemd-and-production-api")
            record("stopped_worker_denies_existing_linux_key",lambda:expect(api.request("GET","RoofControl/Status",key=worker_key)[0],401),"real-systemd-and-production-api")
            record("stopped_worker_preserves_kiosk_stop_with_invalid_pin",lambda:expect(api.request("POST","RoofControl/Stop",{},token=revoked_pin,key=devices["b"])[0],200),"real-systemd-and-production-api")
            record("stopped_worker_preserves_virtual_login",lambda:expect(api.request("GET","Auth/Me",token=api.login())[0],200),"real-systemd-and-production-api")
        finally:
            expect(run(["systemctl","start","hvo-roof-linux-auth.service"]).returncode,0)
        linux=api.login("linux")
        record("worker_restart_after_outage_recovers_existing_container",lambda:(expect(api.request("GET","Auth/Me",token=linux)[0],200),expect(controller_identity()==before_container,True)),"real-systemd-and-production-api")

        # Pause only this disposable guest's native worker. The independently valid kiosk key must
        # authorize Stop without waiting for bearer validation to time out against the hung worker.
        unit="hvo-roof-linux-auth.service"
        unit_type=run(["systemctl","show","--property=Type","--value",unit])
        expect(unit_type.returncode,0)
        expect(unit_type.stdout.strip(),"notify")
        main_pid_result=run(["systemctl","show","--property=MainPID","--value",unit])
        expect(main_pid_result.returncode,0)
        main_pid=int(main_pid_result.stdout.strip())
        expect(main_pid>1,True)
        stop_timing={}
        try:
            expect(run(["systemctl","kill","--kill-whom=main","--signal=STOP",unit]).returncode,0)
            stopped=False
            for _ in range(20):
                status_text=Path("/proc")/str(main_pid)/"status"
                process_state=next(line.split()[1] for line in status_text.read_text().splitlines() if line.startswith("State:"))
                if process_state in ("T","t"):
                    stopped=True
                    break
                time.sleep(.05)
            expect(stopped,True)
            def stop_while_worker_hung():
                started=time.monotonic()
                status,_=api.request("POST","RoofControl/Stop",{},token=linux,key=devices["b"])
                elapsed=time.monotonic()-started
                stop_timing["elapsed_seconds"]=round(elapsed,3)
                expect(status,200)
                expect(elapsed<4,True)
            record("hung_native_worker_does_not_delay_stop_with_independent_kiosk_key",stop_while_worker_hung,"real-paused-worker-and-production-api")
            cases[-1].update(stop_timing)
            cases[-1]["required_elapsed_seconds_less_than"]=4
            cases[-1]["worker_state_verified_stopped"]=True
        finally:
            expect(run(["systemctl","kill","--kill-whom=main","--signal=CONT",unit]).returncode,0)
        linux=api.login("linux")
        record("continued_worker_recovers_after_hung_stop_check",lambda:(expect(api.request("GET","Auth/Me",token=linux)[0],200),expect(controller_identity()==before_container,True)),"real-systemd-and-production-api")
        expect(api.request("DELETE","Auth/Keys/"+worker_key_id,token=linux)[0],204)
        state["kiosk_key"]=devices["b"]
        state["invalid_pin_token"]=revoked_pin

        try:
            expect(run(["passwd","--lock","fixture-admin"]).returncode,0)
            record("ssh_key_only_admin_web_password_denied",lambda:expect(api.request("POST","Auth/ProviderSession",{"provider":"linux","name":"fixture-admin","password":PASSWORD})[0],401),"real-PAM-and-production-api")
            locked_rc,locked_assertion=local_assertion("fixture-admin")
            expect(locked_rc,0)
            status,locked_session=api.request("POST","Auth/LocalSession",{"assertion":locked_assertion})
            expect(status,200)
            record("ssh_key_only_admin_native_sudo_session_allowed",lambda:expect(api.request("GET","RoofControl/Status",token=locked_session["token"])[0],200),"native-sudo-and-production-api")
        finally:
            expect(run(["passwd","--unlock","fixture-admin"]).returncode,0)

        record("nonadmin_native_helper_denied",lambda:expect(local_assertion("fixture-user")[0]!=0,True),"native-sudo")
        record("direct_unprivileged_helper_denied",lambda:expect(local_assertion("fixture-user",direct=True)[0]!=0,True),"native-sudo")
        record("forged_environment_cannot_promote_caller",lambda:expect(local_assertion("fixture-user",environment={"SUDO_UID":str(USERS["fixture-admin"]),"SUDO_USER":"fixture-admin"})[0]!=0,True),"native-sudo")
        record("caller_identity_claim_rejected",lambda:expect(local_assertion("fixture-admin",extras={"uid":0})[0]!=0,True),"native-sudo")
        record("saved_web_session_field_rejected",lambda:expect(local_assertion("fixture-admin",extras={"token":virtual})[0]!=0,True),"native-sudo")
        rc,assertion=local_assertion("fixture-admin")
        expect(rc,0)
        status,session=api.request("POST","Auth/LocalSession",{"assertion":assertion})
        expect(status,200)
        local=session["token"]
        state["local_expires"]=session["expiresUtc"]
        record("native_assertion_exchanges_for_local_session",lambda:expect(api.request("GET","RoofControl/Status",token=local)[0],200),"native-sudo-and-production-api")
        record("native_assertion_replay_denied",lambda:expect(api.request("POST","Auth/LocalSession",{"assertion":assertion})[0],401),"native-sudo-and-production-api")
        record("local_read_session_cannot_write",lambda:expect(api.request("POST","RoofControl/Configuration",{},token=local)[0],403))
        record("local_session_cannot_mint_owned_key",lambda:expect(api.request("POST","Auth/Keys",{"name":"fixture-local-mint","scopes":["read"],"expiresUtc":(dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=1)).isoformat()},token=local)[0],403))
        rc,limited=local_assertion("fixture-limited","configure")
        expect(rc,0)
        record("sudo_authorized_viewer_controller_role_denied",lambda:expect(api.request("POST","Auth/LocalSession",{"assertion":limited})[0],403),"native-sudo-and-production-api")
        state["expired_local_token"]=local
        state["owners"]=owners
        record("legacy_virtual_session_endpoint_preserved",lambda:expect(api.request("POST","Auth/Session",{"name":"fixture-admin","password":PASSWORD})[0],200))
        record("protected_status_requires_authentication",lambda:expect(api.request("GET","RoofControl/Status")[0],401))
        state["legacy_session"]=virtual
        expiry=dt.datetime.fromisoformat(state["local_expires"].replace("Z","+00:00")).timestamp()
        remaining=expiry-time.time()+1
        if remaining>0: time.sleep(min(remaining,35))
        record("local_session_real_wall_clock_expiry",lambda:expect(api.request("GET","RoofControl/Status",token=local)[0],401),"real-time-and-production-api")
        private_write(STATE/"state.json",json.dumps(state))
    except Exception as error:
        cases.append({"name":"acceptance_prerequisite","passed":False,"error_type":type(error).__name__})
        if isinstance(error,AcceptanceMismatch): cases[-1]["diagnostic"]=error.diagnostic
    result={"phase":"acceptance","run_id":marker["run_id"],"passed":sum(c["passed"] for c in cases),"failed":sum(not c["passed"] for c in cases),"cases":cases,"scope":"installed production binaries with actual PAM/sudo and HTTP; no mock model"}
    private_write(REPORT,json.dumps(result,indent=2)+"\n")
    print(json.dumps({"passed":result["passed"],"failed":result["failed"],"report":str(REPORT)}))
    return 0 if result["failed"]==0 else 1

def persistence(phase):
    marker=guard()
    api=Api()
    state=json.loads((STATE/"state.json").read_text())
    results=[]
    for provider in ("linux","virtual"):
        status,_=api.request("GET","RoofControl/Status",key=state[provider+"_revoked_key"])
        results.append({"name":provider+"_revocation_persisted","passed":status==401})
    results.append({"name":"expired_local_session_stays_expired","passed":api.request("GET","RoofControl/Status",token=state["expired_local_token"])[0]==401})
    results.append({"name":"kiosk_stop_with_invalid_pin_after_"+phase,"passed":api.request("POST","RoofControl/Stop",{},token=state["invalid_pin_token"],key=state["kiosk_key"])[0]==200})
    for provider in ("linux","virtual"):
        token=api.login(provider)
        results.append({"name":provider+"_login_after_"+phase,"passed":api.request("GET","Auth/Me",token=token)[0]==200})
    boot_id=Path("/proc/sys/kernel/random/boot_id").read_text().strip()
    if phase=="reboot": results.append({"name":"actual_guest_reboot","passed":boot_id!=state["boot_id"]})
    result={"phase":phase,"run_id":marker["run_id"],"cases":results,"passed":sum(c["passed"] for c in results),"failed":sum(not c["passed"] for c in results)}
    private_write(STATE/(phase+"-report.json"),json.dumps(result,indent=2)+"\n")
    print(json.dumps(result))
    return 0 if result["failed"]==0 else 1

if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("phase",choices=["prepare","acceptance","reboot","rollback"])
    args=parser.parse_args()
    if args.phase=="prepare": prepare()
    elif args.phase=="acceptance": sys.exit(acceptance())
    else: sys.exit(persistence(args.phase))
