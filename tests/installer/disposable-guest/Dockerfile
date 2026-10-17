FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    qemu-system-arm qemu-efi-aarch64 qemu-utils genisoimage openssh-client \
    curl ca-certificates python3 jq && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/guest
RUN curl --fail --location --retry 4 --output SHA256SUMS https://cloud-images.ubuntu.com/noble/20260926/SHA256SUMS && \
    curl --fail --location --retry 4 --output base.qcow2 https://cloud-images.ubuntu.com/noble/20260926/noble-server-cloudimg-arm64.img && \
    python3 -c 'import hashlib,pathlib; wanted=[x.split()[0] for x in pathlib.Path("SHA256SUMS").read_text().splitlines() if x.split()[-1].lstrip("*")=="noble-server-cloudimg-arm64.img"][0]; actual=hashlib.sha256(pathlib.Path("base.qcow2").read_bytes()).hexdigest(); assert wanted==actual; pathlib.Path("verified-image-sha256.txt").write_text(actual+"\n")'
COPY prepare_guest.py /opt/guest/
RUN python3 /opt/guest/prepare_guest.py
RUN apt-get update && apt-get install -y --no-install-recommends python3-pip && \
    python3 -m pip install --break-system-packages playwright==1.55.0 && \
    python3 -m playwright install --with-deps chromium && \
    rm -rf /var/lib/apt/lists/* /root/.cache/pip
COPY guestctl.py resume_state.py /opt/guest/
ENTRYPOINT ["python3", "/opt/guest/guestctl.py", "serve"]
