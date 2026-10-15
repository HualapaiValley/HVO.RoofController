FROM hvo/roof-controller:emulated-webfix1
USER root
LABEL hvo.native-auth-fixture="true"
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends python3 sudo && rm -rf /var/lib/apt/lists/*
COPY worker/ /opt/hvo-roof-linux-auth/
COPY hvo-roof-local-auth /usr/local/libexec/hvo-roof-local-auth
COPY hvo-roof-linux-auth.pam /etc/pam.d/hvo-roof-linux-auth
COPY native_worker_fixture.py /opt/native_worker_fixture.py
RUN chmod 0755 /usr/local/libexec/hvo-roof-local-auth /opt/hvo-roof-linux-auth/HVO.RoofControllerV4.LinuxAuth
ENV HVO_DISPOSABLE_FIXTURE=1
ENTRYPOINT ["python3", "-I", "/opt/native_worker_fixture.py"]
