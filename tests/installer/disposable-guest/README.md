# Disposable full-system installer guest

This harness runs a real Ubuntu ARM64 kernel, systemd and Docker under QEMU software
emulation, inside an ordinary constrained Docker container. It needs no nested KVM,
host cgroup access, privileged container, host bind mounts or host account/policy changes.
All Linux users and security policies created by installation tests belong to the guest.
It is slower than native virtualization and does not establish physical Raspberry Pi behavior.

The build downloads the dated official Ubuntu Noble cloud image and verifies the matching
entry in Ubuntu's SHA256SUMS over HTTPS. Outbound package access is used during image
preparation only. Test runtime uses Docker network `none`, drops every capability, enables
no-new-privileges, and limits the outer container to 4 GiB RAM, two CPUs and 128 processes.
The guest is assigned 3 GiB RAM and two emulated CPUs. Its system disk is a disposable
copy-on-write overlay on the prepared OS baseline.

## Build and start

Run on the designated Linux development host, from the repository root:

```sh
sudo -n docker build -t hvo-disposable-guest:auth-integration tests/installer/disposable-guest
python3 tests/installer/disposable-guest/run_guest.py start
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py ready
python3 tests/installer/disposable-guest/verify_guest.py /tmp/hvo-disposable-guest-proof.json
```

The proof script verifies native guest Docker container execution, a changed boot ID,
snapshot recovery that removes a post-snapshot file, virtual graphics discovery, and an
actual headless Chromium launch. These establish harness feasibility, not HVO application
installation or authentication correctness. Run the application acceptance suite afterward.

`run_guest.py` refuses to replace an existing container. It verifies the ownership label
and isolation settings and records account/PAM/sudo-policy hashes before creation. The inner guest
alone has a fixture Ubuntu account with sudo. Its private SSH key is generated at runtime
inside outer-container tmpfs and is destroyed when that container is removed. Password SSH
and root SSH remain disabled. Guest SSH host-key discovery is restricted to the dedicated
loopback endpoint inside this network-isolated container; no host SSH trust is altered.

## Execute, copy and inspect

```sh
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py exec uname -a
sudo -n docker cp ./payload.tar hvo-disposable-guest-auth-integration:/opt/guest/payload.tar
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py put /opt/guest/payload.tar /home/ubuntu/payload.tar
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py exec tar -xf /home/ubuntu/payload.tar -C /opt
```

`exec` runs the exact argument list as guest root through sudo. `put` and `get` transfer
files as the disposable guest account. Source artifacts and Docker image archives enter
through `docker cp`, never a host bind or Docker-socket mount. The host's pre-existing
images and containers remain outside the guest daemon.

Guest ports 22, 8080, 8081, 8088, 8443, 5200 and 5290 are forwarded to outer-container
**loopback only** at 2222, 18080, 18081, 18088, 18443, 15200 and 15290.
Nothing is published on the Linux host or LAN. A virtual virtio GPU is provided without
passing any host GPU or device through; QMP remains a container-local Unix socket.
An HTTP/browser driver can execute in the outer container. Console evidence is at
`/run/guest/console.log`; the original image hash is `/opt/guest/verified-image-sha256.txt`.

## Reboot and rollback

```sh
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py snapshot os-baseline
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py reboot
sudo -n docker exec hvo-disposable-guest-auth-integration python3 /opt/guest/guestctl.py restore os-baseline
```

Reboot checks that the guest boot ID changed and Docker returned. QEMU snapshots save
the synchronized guest disk and machine state. Tests must separately verify that their
expected application state was restored. Installer rollback remains a separate test.

## Cleanup

First copy the required sanitized reports out. Then:

```sh
python3 tests/installer/disposable-guest/run_guest.py cleanup
sudo -n docker image rm hvo-disposable-guest:auth-integration
```

Cleanup checks the exact ownership label before deleting the container, verifies its
absence, and compares host account/PAM/sudo-policy hashes and ownership/modes. No broad Docker prune or deletion of
pre-existing images is performed. Keep the evidence JSON outside source control if it
contains environment-specific inventory. Published reports must distinguish native guest
checks from mocks, and must not contain credentials, PINs, tokens or private addresses.

## Read-only installed kiosk evidence

After the installer has completed and no deployment is in progress, collect evidence
from the existing guest (substitute its expected marker run ID):

```sh
python3 tests/installer/disposable-guest/capture_kiosk_evidence.py \
  --expected-run-id <guest-run-uuid> \
  --output-dir /tmp/hvo-kiosk-evidence
```

The output directory must be new. The script checks outer-container ownership and
isolation, the root-owned guest marker and QEMU virtualization before inspecting the
installed kiosk unit and process. It compares the unit hash with the current source,
checks the executable and service account, and records connected virtual DRM displays.
It then uses QMP screendump to capture the actual virtual framebuffer, converting its
pixels losslessly to PNG. It does not restart services, modify guest files or settings,
read credentials/configuration/environment, send display input, or call roof APIs.
The temporary outer-container screenshot is removed after reading.

Evidence files are private (directory mode 0700, files 0600). The raw screenshot must
be visually reviewed for secrets and actual kiosk content before publication: metadata
cannot prove that the kiosk UI is visible. A black image can reflect the documented idle
timeout; this check deliberately does not wake the display. A splash, terminal or blank
image does not establish kiosk rendering. Exit 0 means the unit/process/display metadata
checks passed and capture succeeded; exit 2 means capture succeeded but a metadata check
failed. The JSON distinguishes this from the still-required visual review. This is a
virtual-display observation, not a physical touchscreen or roof actuation test.

## Guarded cold resume with additional CPU capacity

The current ARM virt machine has no CPU hotplug support. An ordinary container restart
of the original launcher would regenerate access material and overwrite its writable
disk. Never use that path for an installed guest. The updated launcher refuses an existing
disk unless a verified resume checkpoint exists.

`reconfigure_guest.py` has two explicit phases. Use the exact full container ID and guest
marker run UUID already verified for the current run. Keep the receipt separate from the
original creation/cleanup receipt. Run from the Linux repository root only, after stopping
other test activity:

```sh
python3 tests/installer/disposable-guest/reconfigure_guest.py prepare \
  --expected-container-id <full-existing-container-id> \
  --expected-run-id <existing-guest-run-uuid> \
  --evidence /tmp/hvo-disposable-guest-cold-resume.json
```

Review the prepared receipt. This phase verifies ownership, exact identity, isolation and
root filesystem UUID. It copies the current SSH private/public keys, strict known-host file
and seed ISO into private storage inside the same container, then atomically stages the
safe launcher for its next start. It does not reboot, change resource limits, or write the
live guest disk. The previous launcher and pre-resume console log stay container-private.

When ready for the controlled cold boot, invoke:

```sh
python3 tests/installer/disposable-guest/reconfigure_guest.py relaunch \
  --expected-container-id <same-full-container-id> \
  --expected-run-id <same-guest-run-uuid> \
  --evidence /tmp/hvo-disposable-guest-cold-resume.json
```

This verifies the prepared identity/configuration again, requests guest systemd poweroff,
and waits for a clean QEMU/container exit. It never force-kills the guest. It changes only
outer NanoCpus from 2 to 4 and Memory from 4 to 6 GiB; guest memory remains 3072 MiB.
It compares every other HostConfig value, plus Config/Mounts hashes and image identity.
The network-none policy, dropped capabilities, 128-process limit, no-new-privileges and
existing tmpfs settings remain mandatory; no host mount, device or port is added.

On the next start the resume branch validates the original disk inode, qcow2 structure,
16-GiB virtual size and backing image. Before launching QEMU it takes and hashes an offline
checkpoint of that disk inside the same container. It then restores the exact access files
to runtime tmpfs and boots the original work.qcow2 with four CPUs. Neither a disk snapshot
nor an identity backup is restored. Existing memory-state snapshots from a two-CPU machine
must not be loaded into the new four-CPU machine.

The receipt requires the same guest run UUID/root filesystem UUID, a changed boot ID, four
guest CPUs, Docker readiness and the approved isolation/resource profile. Application and
kiosk readiness still require their separate checks. All writable disks/checkpoints and
private access copies remain inside the owned container and are removed by normal guarded
cleanup; do not copy them into a report, source tree, image, or host artifact directory.

If either phase fails, inspect the receipt and exact owned container before proceeding.
Do not run ordinary serve, remove/recreate the container, restore an identity backup, or
retry a partial checkpoint blindly. A failed offline check leaves the disk unlaunched.
The original creation receipt remains the authority for final ownership/host-preservation
cleanup; its exact container ID does not change.

Offline tests (synthetic files and mocked process calls, not runtime acceptance):

```sh
cd tests/installer/disposable-guest
python3 -m unittest -v test_resume
```

## Guest-loopback web and API tunnel

The installed controller API (8443) and web service (8088) bind guest loopback.
QEMU hostfwd reaches the guest NIC address, so its outer 18443/18088 forwards cannot
reach these loopback-only services. Preserve the production binding. Instead use the
existing guest SSH connection to forward outer **127.0.0.1:28088** to guest
**127.0.0.1:8088**, and outer **127.0.0.1:28443** to guest **127.0.0.1:8443**.
`tests/auth-integration/run_browser.py` defaults to those SSH-forwarded endpoints.

When an equivalent tunnel is already running, reuse it. Otherwise, run the following
from the Linux repository root in a dedicated interactive terminal. Substitute the
already-verified full container ID and marker run UUID. The identity guard must succeed
before the SSH command starts. No private key is copied out of the container:

```sh
HVO_GUEST_CONTAINER='<full-existing-container-id>'
HVO_GUEST_RUN_ID='<existing-guest-run-uuid>'
if python3 - "$HVO_GUEST_CONTAINER" "$HVO_GUEST_RUN_ID" <<'CHECK_GUEST'
import sys
sys.path.insert(0, 'tests/installer/disposable-guest')
from reconfigure_guest import identity
if sys.flags.optimize:
    raise SystemExit('Run without Python optimization')
identity(sys.argv[1], sys.argv[2])
CHECK_GUEST
then
  sudo -n docker exec -it "$HVO_GUEST_CONTAINER" \
    timeout --signal=TERM --kill-after=5s 30m \
    ssh -F /dev/null -N -T -i /run/guest/id_ed25519 -p 2222 \
      -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none \
      -o StrictHostKeyChecking=yes \
      -o UserKnownHostsFile=/run/guest/known_hosts \
      -o ForwardAgent=no -o ExitOnForwardFailure=yes -o ConnectTimeout=5 \
      -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
      -L 127.0.0.1:28088:127.0.0.1:8088 \
      -L 127.0.0.1:28443:127.0.0.1:8443 \
      ubuntu@127.0.0.1
fi
```

The tunnel stays in the foreground of that terminal. Stop it with Ctrl-C in that same
terminal when browser acceptance finishes; the timeout also terminates it after 30 minutes
and force-kills it only if it fails to exit within five seconds. Container shutdown/removal
ends all its tunnel processes. Do not use broad process-name kills, a background `ssh -f`,
or disable host-key checking. If a forwarding port is occupied, ExitOnForwardFailure stops
the duplicate attempt; inspect/reuse the existing tunnel instead of replacing it blindly.
All listeners and destinations are explicit loopback endpoints inside the already-isolated
outer container. This adds no published host port, LAN listener, host bind, authentication
change, guest service setting, or production TLS change. Reopen the bounded tunnel only
when a later test phase needs it.
