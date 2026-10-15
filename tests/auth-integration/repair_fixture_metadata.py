#!/usr/bin/env python3
"""Correct inherited Compose metadata in the private guest fixture; never change executable layers."""
import hashlib,json,subprocess
from pathlib import Path
from run_acceptance import guard,STATE,Api,expect,private_write
def docker(*args,input=None):
    result=subprocess.run(["docker",*args],input=input,capture_output=True,text=True,timeout=300)
    if result.returncode:raise RuntimeError("Guest fixture Docker operation failed: "+args[0])
    return result.stdout
def main():
    marker=guard()
    receipt=STATE/"fixture-metadata-repair.json"
    if receipt.exists():raise RuntimeError("Fixture repair already recorded")
    source="hvo/roof-hat-emulator:dev"
    old=json.loads(docker("image","inspect",source))[0]
    container=json.loads(docker("container","inspect","hat-emulator"))[0]
    expect(container["Image"],old["Id"])
    keys=["com.docker.compose.project","com.docker.compose.service","com.docker.compose.version"]
    expect(old["Config"]["Labels"][keys[0]],"hvo-roof-build")
    clean="127.0.0.1:5000/hvo/roof-hat-emulator:fixture-clean"
    dockerfile="FROM "+source+"\nLABEL "+" ".join(k+'=""' for k in keys)+"\n"
    docker("build","--pull=false","--network=none","--tag",clean,"-",input=dockerfile)
    new=json.loads(docker("image","inspect",clean))[0]
    expect(new["RootFS"],old["RootFS"])
    def config(value):
        copied=json.loads(json.dumps(value["Config"]))
        for key in keys:copied["Labels"].pop(key,None)
        return copied
    expect(config(new),config(old))
    expect(all(new["Config"]["Labels"].get(key,"")=="" for key in keys),True)
    docker("push",clean)
    new=json.loads(docker("image","inspect",clean))[0]
    repository="127.0.0.1:5000/hvo/roof-hat-emulator"
    digest=next(x.split("@",1)[1] for x in new["RepoDigests"] if x.startswith(repository+"@"))
    for folder in Path("/opt/hvo-auth-test").glob("release-*"):
        manifest=folder/"release.json"
        data=json.loads(manifest.read_text())
        image=data["images"]["hatEmulator"]
        image["digest"]=digest
        image["reference"]=image["repository"]+":"+image["tag"]+"@"+digest
        manifest.write_text(json.dumps(data,indent=2)+"\n")
        sums=folder/"SHA256SUMS"
        names=[line.split("  ",1)[1] for line in sums.read_text().splitlines()]
        sums.write_text("".join(hashlib.sha256((folder/name).read_bytes()).hexdigest()+"  "+name+"\n" for name in names))
    api=Api()
    status,session=api.request("POST","Auth/Session",{"name":"fixture-admin","password":__import__("run_acceptance").PASSWORD})
    expect(status,200)
    expect(api.request("POST","RoofControl/Stop",{},token=session["token"])[0],200)
    # The marker, source image and exact name establish ownership of this disposable test fixture.
    docker("rm","--force",container["Id"])
    for phase in ("legacy","enable"):
        for suffix in (".log","-installation.json"):
            path=STATE/(phase+suffix)
            if path.exists():path.rename(STATE/("before-fixture-repair-"+phase+suffix))
    result={"run_id":marker["run_id"],"source_image":old["Id"],"clean_image":new["Id"],
            "clean_digest":digest,"filesystem_layers_unchanged":True,"runtime_config_unchanged":True,
            "cleared_labels":keys,"old_fixture_container_removed":True,
            "next_required_step":"Run genuine legacy installer to create corrected emulator fixture"}
    private_write(receipt,json.dumps(result,indent=2)+"\n")
    print(json.dumps(result),flush=True)
if __name__=="__main__":main()
