# Authentication and interface rights

The controller API enforces permissions for every client. Linux administrator membership alone never enrolls
someone in RoofController. Linux and virtual users remain separate even when their names match.

| Interface | Sign-in | Allowed work |
|---|---|---|
| Local CLI / TUI | Explicit Linux enrollment, verified native sudo helper identity and independent controller role; short-lived operation-scoped assertion | Role-authorized roof operations and administration, including virtual users, passwords, personal PINs, managed API keys, sessions and enrolled Linux users' roles/access. Local authorization is never saved as a reusable login. |
| Remote CLI / TUI | Explicit remote mode, Linux or virtual password session, or configured API key | The credential's controller role and scope apply. Personal expiring keys require a remote password session. |
| Web | Select Linux (enrolled account, Linux password checked through PAM) or virtual (controller password) | Viewer reads; Operator operates; Admin manages virtual people/passwords/PINs, managed keys, sessions and enrolled Linux roles/access. Linux passwords are never changed by the site. |
| Kiosk | Device viewer key for status and Stop; name and personal PIN to unlock | Roof operation only: Open, Close, Stop, lease and Clear fault. Admins' PIN sessions are capped at Operator. Settings writes, identity administration and administrative system actions are refused. The kiosk has no Settings or System navigation. |
| Mac app | Password session | Role-authorized desktop administration through the same API; the kiosk PIN restriction does not reduce password-session rights. |

Stop remains available with the kiosk device key while locked. API roles do not bypass the roof's safety interlocks.
PIN tokens still need protection: possession grants their limited rights until expiry/revocation.

## First installation and sudo

Native Linux authentication remains an explicit choice. In a fresh Linux controller/rig answers file, use
`controller.linuxAuth.enabled: true` and `controller.linuxAuth.enrollSetupUser: true` to enroll only the intended
setup user. Run the installer through sudo from that account. The installer resolves the numeric `SUDO_UID`
through `getent passwd`, requires its name to equal `SUDO_USER`, and rejects root, missing/malformed identifiers,
mismatches and conflicting explicit roles. Running directly as root requires an explicitly named account in
`linuxAuth.users`. No sudo-group scan is used.

Planning resolves the opt-in to a concrete named `RoofAdmin` enrollment with `localAdmin: true`, shown in the
plan and saved in the installation record. Upgrades keep that user rather than enrolling whoever later runs sudo.
The fixed sudo helper rule grants only the named account its no-argument helper command. The installer creates
no Linux account and changes no Linux password. Native enrollment and controller authorization must both agree.

With this Linux administrator selected, omit `controller.firstAdmin` to avoid creating a virtual bootstrap
password or PIN. Verify `hvo-roof --local whoami` and `hvo-roof --local users list` from the intended account.
Linux web password sign-in is a separate PAM path: an SSH-key-only or locked-password account does not gain a
Linux password from enrollment. Create a virtual recovery administrator and personal kiosk PINs explicitly,
using private prompts, when authorized. Installation never assigns a default credential.

## Credential policy and migration

New or changed virtual passwords contain 7–256 characters, with no character-type requirements. Linux passwords
retain Linux/PAM policy. Existing virtual password hashes are not rewritten or invalidated by the new minimum.
New personal kiosk PINs are exactly four ASCII digits. Existing longer PIN hashes and sign-in remain supported
until their owner deliberately changes them; no deployed PIN is silently shortened or reset.

The reserved temporary code `0000` is not a personal PIN and is never a universal fallback. Temporary access
requires both explicit controller configuration `RoofControllerSecurity:Identity:AllowTemporaryKioskPin: true`
and an individual password-bearing Operator/Admin account created with `temporaryPin: true` through the API
(or CLI `users add NAME --role operator --temporary-pin`). It is accepted only while the controller uses the HAT
emulator and the account has not configured a personal PIN. Creation on physical hardware is refused; live
temporary sessions are refused when commissioning access is disabled. Temporary sessions are excluded from
identity-file saves, so they do not survive restart or software rollback. Setting or removing a personal PIN
permanently clears that account's temporary grant. The setting is off by default and the installer never enables it.
A personal PIN is therefore required for kiosk operation with real hardware.

Ordinary lockout/rate limits, device-key binding and idle/lifetime limits also apply to temporary PIN sign-in.
Administrators see a temporary grant in the PIN column and can revoke it with Remove PIN.
The web/TUI personal-PIN editors reject the reserved code. They do not enable temporary commissioning access.

## Administrator controls

- CLI: `users`, `pins`, `keys` and `sessions` manage virtual accounts and credentials; `principals list` and
  `principals set ID --role viewer|operator|admin --enabled true|false` manage enrolled Linux controller access.
- TUI: People provides Users, API keys, Sessions and Linux users. Linux users' Role / access dialog takes an
  explicit role and enabled state. Linux passwords remain outside the application.
- Web: People provides the same categories. Select Linux users, read the enrolled users, then change Role / access.
- Initial native enrollment is host-side installer work. UI role/access changes neither create Linux accounts
  nor silently modify native PAM/sudo enrollment. A later installer run refuses conflicting enrollment rather
  than undoing administrative revocation. Review the explicit host enrollment choices when reconciling changes.

Role/access changes revoke Linux sessions and personal keys. Virtual password/role changes end affected sessions;
PIN changes end PIN sessions. Managed device keys and personal scoped keys have different lifecycles; use
[Security](security.md) for their details.

## Implemented and validation boundaries

The above policy, CLI/TUI/web Linux role/access controls, private virtual credential management, and explicit
sudo-caller resolution are implemented. Automated tests use isolated test accounts, in-memory or temporary
identity stores, fake native assertions and emulated roof services. They do not verify a person's Linux password.
Native account creation, Linux-password reset, automatic shared credentials and a first-login password-reset
workflow are not implemented. Initial native enrollment uses installer answers; there is no Linux enrollment
wizard page. Temporary commissioning grants are explicit API/CLI creation choices, not a web/TUI toggle.

No actual passwords, PINs, Linux account policy, or deployed controller authentication policy were changed during
this implementation validation. A live PAM password sign-in or deployed personal-PIN change still needs an
authorized operator to supply that credential privately. DRM validation is independent of that boundary.
