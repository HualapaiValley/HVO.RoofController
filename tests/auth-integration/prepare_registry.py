#!/usr/bin/env python3
"""Import real offline images into a registry inside the marked disposable guest only."""
import argparse,json,subprocess,tarfile,time,urllib.request
from pathlib import Path
from run_acceptance import guard,STATE,private_write

def docker(*args):
    result=subprocess.run(["docker",*args],capture_output=True,text=True,timeout=300)
    if result.returncode:
        raise RuntimeError("Guest Docker operation failed: "+args[0]+", exit "+str(result.returncode))
    return result.stdout

def main():
    guard()
    parser=argparse.ArgumentParser()
    parser.add_argument("--existing",required=True,type=Path)
    parser.add_argument("--new",required=True,type=Path)
    args=parser.parse_args()
    with tarfile.open(args.existing) as archive:
        manifests=json.load(archive.extractfile("index.json"))["manifests"]
        registries=[m for m in manifests
                    if m.get("annotations",{}).get("containerd.io/distribution.source.docker.io")=="library/registry"]
        assert len(registries)==1
        registry=registries[0]["digest"]
    for path in (args.existing,args.new):
        docker("load","--input",str(path))
    inspected=json.loads(docker("image","inspect",registry))[0]
    assert "registry" in json.dumps(inspected["Config"]).lower()
    docker("run","--detach","--name","hvo-test-registry","--restart","unless-stopped",
           "--publish","127.0.0.1:5000:5000",registry)
    for attempt in range(60):
        try:
            with urllib.request.urlopen("http://127.0.0.1:5000/v2/",timeout=2) as response:
                assert response.status==200
            break
        except Exception:
            time.sleep(1)
    else:raise RuntimeError("Guest registry readiness failed")
    images=[
        ("legacy","hvo/roof-controller:emulated-webfix1","roof-controller","4.0.1-dev.webfix1"),
        ("a","hvo-auth-controller:4.0.2-auth.1","roof-controller","4.0.2-auth.1"),
        ("b","hvo-auth-controller:4.0.2-auth.2","roof-controller","4.0.2-auth.2"),
        ("emulator","hvo/roof-hat-emulator:dev","roof-hat-emulator","fixture"),
    ]
    result={"registry":"127.0.0.1:5000/hvo","images":{}}
    for label,source,name,version in images:
        repository=result["registry"]+"/"+name
        tag=repository+":"+version
        docker("tag",source,tag)
        docker("push",tag)
        digests=json.loads(docker("image","inspect",tag))[0]["RepoDigests"]
        digest=next(d.split("@",1)[1] for d in digests if d.startswith(repository+"@"))
        result["images"][label]={"repository":repository,"digest":digest,"tag":tag}
    private_write(STATE/"registry.json",json.dumps(result,indent=2)+"\n")
    print(json.dumps(result,indent=2))
if __name__=="__main__":main()
