#!/usr/bin/env python3
"""Actual installed CLI acceptance, run as root only inside the disposable QEMU guest."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import pwd
import ssl
import subprocess
import sys
import time
from run_acceptance import Api, PASSWORD, USERS, STATE, guard, private_write

def check(value):
    if not value:
        raise AssertionError("Acceptance condition failed")

def identity_file(user):
    return Path(pwd.getpwnam(user).pw_dir)/".config/hvo-roof/credentials.json"

def as_user(user, arguments, input_text="", environment=None, timeout=120):
    home=pwd.getpwnam(user).pw_dir
    env={"HOME":home,"USER":user,"LOGNAME":user,"PATH":home+"/.local/bin:/usr/local/bin:/usr/bin:/bin","LANG":"C.UTF-8"}
    env.update(environment or {})
    started=time.monotonic()
    result=subprocess.run(["/usr/sbin/runuser","-u",user,"--",*map(str,arguments)],env=env,
        input=input_text,capture_output=True,text=True,timeout=timeout)
    result.elapsed=time.monotonic()-started
    return result

def write_for_user(path, value, user):
    owner=pwd.getpwnam(user)
    path.parent.mkdir(parents=True,mode=0o700,exist_ok=True)
    path.parent.chmod(0o700)
    os.chown(path.parent,owner.pw_uid,owner.pw_gid)
    private_write(path,value)
    os.chown(path,owner.pw_uid,owner.pw_gid)

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--release",required=True)
    parser.add_argument("--report",default=str(STATE/"clients-report.json"))
    args=parser.parse_args()
    marker=guard()
    release=Path(args.release).resolve()
    installer=release/"hvo-roof-install-linux-arm64"
    check(installer.is_file())
    api=Api()
    ca=Path("/etc/hvo-roof/ca.crt").read_text()
    fingerprint=hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca)).hexdigest().upper()
    cases=[]
    snapshots={}
    def record(name,fn,coverage="installed-cli-production-api"):
        try:
            fn()
            item={"name":name,"passed":True,"coverage":coverage}
        except Exception as error:
            item={"name":name,"passed":False,"coverage":coverage,"error_type":type(error).__name__}
        cases.append(item)
        print(json.dumps(item),flush=True)
        private_write(Path(args.report),json.dumps({"run_id":marker["run_id"],"cases":cases},indent=2)+"\n")
    def cli(user,*arguments,**kwargs):
        return as_user(user,[Path(pwd.getpwnam(user).pw_dir)/".local/bin/hvo-roof",*arguments,"--json"],**kwargs)
    try:
        for user in ("fixture-admin","fixture-limited","fixture-user"):
            check(pwd.getpwnam(user).pw_uid==USERS[user])
            answers=Path(pwd.getpwnam(user).pw_dir)/".hvo-client-acceptance/answers.json"
            write_for_user(answers,json.dumps({"roles":["cli"],"cli":{"folder":"~/.local/bin"},
                "client":{"controller":"https://localhost:8443","caSha256":fingerprint}}),user)
            def install(user=user,answers=answers):
                result=as_user(user,[installer,"--answers",answers,"--release",release],timeout=240)
                check(result.returncode==0)
                path=identity_file(user)
                data=json.loads(path.read_text())
                check(data.get("authMode")=="local")
                check(not data.get("session") and not data.get("apiKey"))
            record(user+"_real_cli_install",install,"real-per-user-installer")
            record(user+"_real_cli_install_rerun",install,"real-per-user-installer-idempotence")
            path=identity_file(user)
            if path.exists():
                snapshots[user]=path.read_bytes()
        record("admin_installed_default_local_status",lambda:check(cli("fixture-admin","status").returncode==0),"native-sudo-installed-cli-api")
        def caller():
            result=cli("fixture-admin","whoami")
            check(result.returncode==0 and json.loads(result.stdout)["caller"]["role"]=="RoofAdmin")
        record("admin_installed_default_local_identity",caller,"native-sudo-installed-cli-api")
        record("limited_installed_default_local_status",lambda:check(cli("fixture-limited","status").returncode==0),"native-sudo-installed-cli-api")
        before=api.request("GET","Settings",token=api.login())[1]
        result=cli("fixture-limited","config","set","RoofControllerUi:DefaultCamera=fixture-denied")
        record("limited_local_configuration_denied",lambda:check(result.returncode==6),"native-sudo-installed-cli-api")
        after=api.request("GET","Settings",token=api.login())[1]
        record("denied_configuration_keeps_version",lambda:check(before["version"]==after["version"]))

        admin_token=api.login()
        user="fixture-user"
        file=identity_file(user)
        saved=json.loads(file.read_text())
        saved["session"]={"token":admin_token,"name":"fixture-admin","role":"RoofAdmin","sessionId":"fixture-web-admin"}
        saved["apiKey"]="public-inert-invalid-key"
        write_for_user(file,json.dumps(saved),user)
        environment={"HVO_ROOF_SESSION":admin_token,"HVO_ROOF_API_KEY":"public-inert-invalid-key"}
        def denied():
            result=cli(user,"status",environment=environment,timeout=25)
            check(result.returncode==6 and result.elapsed<20)
        record("nonadmin_saved_and_environment_admin_session_cannot_bypass_local",denied,"native-sudo-installed-cli-api")
        record("nonadmin_explicit_local_cannot_bypass",lambda:check(cli(user,"--local","whoami",environment=environment).returncode==6),"native-sudo-installed-cli-api")
        record("local_password_login_cannot_switch_provider",lambda:check(cli(user,"login","fixture-admin","--provider","virtual",environment=environment).returncode==2))
        record("explicit_remote_saved_admin_session_works",lambda:check(cli(user,"--remote","status",environment=environment).returncode==0))
        for provider in ("virtual","linux"):
            record(provider+"_installed_remote_cli_login",
                lambda provider=provider:check(cli(user,"--remote","login","fixture-admin","--provider",provider,input_text=PASSWORD+"\n").returncode==0),
                "installed-cli-real-password-provider")
            record(provider+"_remote_session_works",lambda:check(cli(user,"--remote","whoami").returncode==0))
            record(provider+"_saved_session_still_cannot_bypass_local",lambda:check(cli(user,"status").returncode==6),"native-sudo-installed-cli-api")
        record("admin_local_stop_verified",lambda:check(cli("fixture-admin","stop").returncode==0),"native-sudo-installed-cli-stop-only")
        record("local_admin_profile_contains_no_saved_token",lambda:check(identity_file("fixture-admin").read_bytes()==snapshots["fixture-admin"]))
    except Exception as error:
        cases.append({"name":"client_acceptance_prerequisite","passed":False,"error_type":type(error).__name__})
    finally:
        for user,content in snapshots.items():
            write_for_user(identity_file(user),content.decode(),user)
        cleanup={"fixture_profiles_restored":all(identity_file(u).read_bytes()==c for u,c in snapshots.items())}
    result={"run_id":marker["run_id"],"phase":"clients","scope":"real per-user installer, installed CLI, sudo helper, production API",
        "passed":sum(c["passed"] for c in cases),"failed":sum(not c["passed"] for c in cases),"cases":cases,"cleanup":cleanup}
    private_write(Path(args.report),json.dumps(result,indent=2)+"\n")
    print(json.dumps({k:result[k] for k in ("phase","passed","failed","cleanup")}))
    return 0 if result["failed"]==0 and cleanup["fixture_profiles_restored"] else 1

if __name__=="__main__":
    sys.exit(main())
