FROM hvo/roof-controller:emulated-webfix1
USER root
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends python3 sudo && rm -rf /var/lib/apt/lists/*
COPY auth_model.py /opt/hvo-poc-package/auth_model.py
COPY integration/fixture.py /opt/hvo-poc-package/fixture.py
ENV HVO_DISPOSABLE_AUTH_FIXTURE=1
ENTRYPOINT ["/usr/bin/python3", "-I", "/opt/hvo-poc-package/fixture.py"]
