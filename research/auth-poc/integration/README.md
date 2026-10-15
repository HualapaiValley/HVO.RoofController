# Disposable integrated authentication installation

This extends the same authentication POC with real Linux accounts, native sudo and PAM,
a Unix broker socket, and loopback HTTP requests. It is **not the production HVO installer,
controller API or website**. No roof endpoint exists in this fixture.

The [recorded verification](verification.json) has **31 passed, zero failed** on Linux
ARM64. Four test accounts were created only in the container's writable layer. The
container, those accounts, its PAM/sudo policy, and the temporary image were removed.
Host account files and fixture-policy absence matched before/after. Existing production
authentication tests separately passed **122/122** in Release; these are in-process/emulated
tests, not an installed kiosk or new-provider deployment.

## What ran

- Native sudo authorizes only a fixed root-owned helper. The helper rejects identity and
  saved-session fields; sudo supplies the original UID. Forged environment inputs cannot
  promote a nonadministrator or turn the sudo-authorized viewer into an administrator.
- The broker checks the root helper's kernel Unix peer UID and independently checks the
  original caller's controller role. Root is part of the trusted computing base.
- Real pam_unix authentication and account validation accept a disposable account, reject
  a wrong password, and reject an expired account. Host credentials are never used.
- Linux and virtual users with the same display name remain separate principals. HTTP
  requests check viewer/admin roles, session reuse, expiry, logout and role changes.
- Both principal types own inert scoped keys; out-of-scope and revoked keys are rejected.

Application authorization still uses the Python policy model. Credential handles are
predictable MOCK-ONLY values, with no persistence. The virtual hash uses public fixture
inputs and parameters, not production password-hardening settings. PAM conversation code
is a test bridge, not a production worker; it does not establish MFA support, memory
zeroization, rate limiting or allocation-failure hardening. No TLS or browser UI is tested.

## Reproduce on the authorized Linux development environment

The Dockerfile expects the existing local emulated controller image. Packages are added
only to the temporary image. The build needs package-repository access; the test runtime
has no network interface beyond its private loopback, host mounts, published ports or
devices. Never execute fixture.py directly as host root. Installation also checks for the
container marker, explicit fixture environment flag, and absence of its fixed prefix.

From the repository root:

~~~sh
sudo -n docker build -f research/auth-poc/integration/Dockerfile \
  -t hvo-auth-fixture:disposable research/auth-poc
sudo -n docker run --rm --name hvo-auth-fixture-disposable \
  --label hvo.auth-fixture=disposable --network none \
  --pids-limit 128 --memory 768m --cpus 2 \
  --cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE \
  --cap-add SETUID --cap-add SETGID --cap-add FOWNER --cap-add AUDIT_WRITE \
  --tmpfs /run:rw,nosuid,nodev,size=16m --tmpfs /tmp:rw,nosuid,nodev,size=32m \
  hvo-auth-fixture:disposable
sudo -n docker image rm hvo-auth-fixture:disposable
~~~

An existing container with that name makes creation fail instead of replacing it.
The image has no accounts/passwords baked in; they are created by the runtime test.
The runtime needs these limited capabilities and setuid sudo to test the actual
authorization path; it is not privileged and has no Docker socket or other host mount.
If interrupted, inspect that exact labeled fixture container before removing it.
Do not remove unrelated containers, images or caches.

## Remaining production installation blocker

The current production installer has fixed system paths, service/account changes and
shared Docker resource names. It has no isolated-prefix full-install mode, and it does
not install the proposed broker, Linux website provider or scoped-owner key schema.
The production application also lacks those adapters and schema migrations. Therefore a
full installation with the proposed identities has not been demonstrated.

Production work still needs the versioned store migration, provider/broker integration,
safe host/container identity boundary, installer integration and isolated system tests.
Polkit, challenge handling, OS-account changes, namespace mappings, passed sockets/PID
reuse defenses and the graphical kiosk require further coverage. Existing PIN/device/
session/Stop tests passed without changing their implementation. No host users, PAM,
polkit or sudo policy, network settings, Pi state or live application services changed.

See the [full-install isolation and adapter analysis](full-install-gap.md) for concrete
prefix, systemd-container and disposable-guest options and the required production wiring.
An additional **557 isolated installer tests passed**; real host installation categories
were explicitly excluded.
