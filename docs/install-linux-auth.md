# Optional Linux authentication installation

Host authentication is disabled by default. Existing answers retain legacy password, PIN and device-key
behavior. The opt-in requires Linux, systemd, PAM and sudo, plus a release containing the native worker
for the machine's processor. The installer never creates Linux accounts or changes their passwords.

Add this non-secret section inside the existing controller answers:

    "linuxAuth": {
      "enabled": true,
      "users": [
        { "name": "fixture-admin", "role": "RoofAdmin", "localAdmin": true },
        { "name": "fixture-viewer", "role": "RoofViewer", "localAdmin": false }
      ]
    }

These are example disposable names. Each account must already exist with a nonzero UID. The role is
the independent controller role. localAdmin grants only the fixed, no-argument sudo helper command
to that named account; it does not add an administrator-group membership. By default there is no automatic
enrollment of the installing user, all Linux administrators, or matching virtual-user display names.
Explicit `enrollSetupUser: true` instead resolves only the verified sudo caller to a named admin enrollment;
see [first installation and sudo](authentication-interfaces.md#first-installation-and-sudo).
Missing and reassigned accounts are refused.

Review the ordinary installer with --plan --answers FILE --release DIR, then apply that same answers
file. The existing wizard preserves this advanced setting; no separate wizard page or password field
is added.

## Fresh installation using an explicitly selected Linux administrator

A fresh installation can omit `controller.firstAdmin` and explicitly enroll an existing
non-root Linux account with `role: RoofAdmin` and `localAdmin: true`. This creates no virtual
bootstrap user, password or PIN. The installer does not infer enrollment from the installing
username or sudo-group membership and does not change the Linux account's password.

After installing the per-user CLI with the local controller and verified CA, the enrolled
operator can use local authorization to manage identities. The helper verifies the actual
caller and the controller checks its independently assigned role for each operation.
Linux web sign-in remains a separate password/PAM flow; an SSH-key-only or locked-password
account can retain authorized local access without gaining web password access.

Create a virtual recovery administrator privately afterward with
`hvo-roof users add RECOVERY_NAME --role admin --password`. Create or assign kiosk PINs
through the private CLI prompts, for example
`hvo-roof users add KIOSK_NAME --role operator --pin`. These are explicit operator actions:
no password or PIN is generated, displayed in ordinary output, or supplied in an argument.
Virtual and Linux accounts remain distinct even when their names match. The kiosk stays
locked until a valid kiosk PIN is enrolled; local administrator access never unlocks it.

Keep existing accounts and credentials on reruns, upgrades and restores. Establish and test
the recovery account before relying on the new installation operationally. This flow adds no
shared default password, temporary-password delivery mechanism, or first-login password reset.

## Files and boundaries

The SHA-256 checked release asset hvo-roof-linux-auth-linux-arm64 or hvo-roof-linux-auth-linux-x64
installs as /opt/hvo-roof-linux-auth/HVO.RoofControllerV4.LinuxAuth. The fixed wrapper is
/usr/local/libexec/hvo-roof-local-auth. Dedicated systemd, PAM and sudoers files belong to this feature.
The sudoers candidate passes visudo before any installed native file is changed.

Private files under /etc/hvo-roof/linux-auth contain the random 32-byte assertion key, explicit
UID/name/subject enrollment, worker configuration and ownership manifest. Every path and parent must
be root-owned and not writable by others. The existing controller runs as root inside its container;
its configured Unix peer UID is zero. That does not authorize its connection to act as the sudo helper.

Only /run/hvo-roof-linux-auth/controller and the single read-only assertion key are mounted into the
controller container. The local helper socket, enrollment file, PAM configuration and host password
database are not mounted. The worker preserves its runtime directory across restarts so the directory
bind mount remains attached to the same inode. A feature-owned Docker unit drop-in orders the worker
before Docker at boot; the installer does not restart Docker.

After deployment, the installer enrolls each native subject through the controller's actual
Identity/Principals API. An existing different role, disabled principal, changed name/UID or changed
enrollment stamp is refused. Installation never restores revoked access. A loopback CLI connection
on an opted-in host receives local authentication mode; remote profiles retain their configured mode.

## Rerun, removal and recovery

Reruns preserve assertion keys, subject IDs and enrollment stamps, checking owned content, release
hashes, service state and exact application enrollment. Omitting linuxAuth preserves the recorded
choice. Explicit enabled:false redeploys the controller without native authentication and removes the
owned worker, helper, PAM service, sudo rule and Docker ordering drop-in. Ordinary uninstall removes
the same runtime files. Private data remains for reinstall; uninstall --purge removes it with the
controller configuration.

Normal private installer backups already include /etc/hvo-roof, hence the native configuration and
credentials. Handle them as credential-bearing backups. Runtime code comes from the release again.
Modified or unowned files are refused. If the native installation step fails, its original files and
original service active/enabled states are restored.

An enabled identity store uses schema 2, or schema 3 when first-install kiosk commissioning was provisioned. Release manifests declare identityStoreSchema
and the identity store's state contract ([State contracts](install.md#state-contracts)); older
manifests default to schema 1. A rollback to incompatible software is refused before replacing the
controller. The schema-1 migration backup is never replayed because it could revive
revoked credentials. Compatible release rollback keeps current identity data and revocations; only
`rollback --restore-backup`, asked for and confirmed, puts back the identity store the upgrade backed up, losing
the changes made since ([Rolling back](install.md#rolling-back)).

## Release and test scope

CI publishes a self-contained native worker for Linux arm64 and x64. The release builder accepts
--linux-auth DIR, containing one runtime subdirectory per processor and each
HVO.RoofControllerV4.LinuxAuth binary. It checks processor, size and SHA-256. A bundle without these
optional assets supports legacy installs but refuses an explicit native install.

Installer unit tests redirect files and fake system commands. They verify planning, stable identities,
ownership refusal, service restoration and schema compatibility, not real PAM/sudo/systemd behavior.
Disposable guest verification exercises the real installer and services with an emulated HAT without
creating accounts or applying policy on the development VM host.

For interface rights, credential policy and implemented-versus-planned boundaries, see [Authentication and interface rights](authentication-interfaces.md).

## First-install owner PIN

See [First-install kiosk PIN commissioning](kiosk-commissioning.md) for eligibility, mandatory replacement,
retirement, persistence and rollback. Existing installations receive no temporary access automatically.
