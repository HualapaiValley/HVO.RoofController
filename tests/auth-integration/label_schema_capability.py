#!/usr/bin/env python3
"""Label verified schema2 test images without changing executable filesystem layers."""
import json,subprocess
from run_acceptance import guard,STATE,private_write,expect
def docker(*args,input=None):
    r=subprocess.run(["docker",*args],input=input,capture_output=True,text=True,timeout=300)
    if r.returncode:raise RuntimeError("Guest capability fixture operation failed: "+args[0])
    return r.stdout
def main():
    marker=guard()
    result={"run_id":marker["run_id"],"label":"io.hvo.roof.identity-schema","supported_schema":2,"images":{}}
    rows=[("a","4.0.2-auth.1","sha256:f4d89609f0508694c9bbb2a7ddfe68afc03c25a5f316858b5b905627da98b5bd"),
          ("b","4.0.2-auth.2","sha256:32c9d7de4caa2f41e0aee6c62597f2e71a215ba29980b00a7dfa695ceb2cb0e3")]
    for label,version,expected in rows:
        source="hvo-auth-controller:"+version
        old=json.loads(docker("image","inspect",source))[0]
        expect(old["Id"],expected)
        target="127.0.0.1:5000/hvo/roof-controller:"+version
        docker("build","--pull=false","--network=none","--tag",target,"-",input="FROM "+source+"\nLABEL io.hvo.roof.identity-schema=2\n")
        new=json.loads(docker("image","inspect",target))[0]
        expect(new["RootFS"],old["RootFS"])
        def config(image):
            value=json.loads(json.dumps(image["Config"]))
            value["Labels"].pop("io.hvo.roof.identity-schema",None)
            return value
        expect(config(new),config(old))
        expect(new["Config"]["Labels"]["io.hvo.roof.identity-schema"],"2")
        docker("push",target)
        new=json.loads(docker("image","inspect",target))[0]
        repository="127.0.0.1:5000/hvo/roof-controller"
        digest=next(x.split("@",1)[1] for x in new["RepoDigests"] if x.startswith(repository+"@"))
        result["images"][label]={"version":version,"source_image":old["Id"],"image":new["Id"],"digest":digest,
            "filesystem_layers_unchanged":True,"runtime_config_except_capability_label_unchanged":True}
    private_write(STATE/"schema-capability-images.json",json.dumps(result,indent=2)+"\n")
    print(json.dumps(result),flush=True)
if __name__=="__main__":main()
