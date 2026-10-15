# Pending authentication UX and administrator bootstrap requirements

> Implementation update: the installer-readiness branch now implements optional effective-OS
> username defaults and interactive remote sign-in with retry/cancel. Explicit names win;
> sudo defaults to its effective account. Native local authority, provider selection and
> environment credential precedence are preserved. See [current CLI behavior](../cli.md#sign-in-defaults-and-retry).
> The pending CLI sections below retain the original design record; their implementation
> status is superseded by this note. The accepted initial-admin direction is explicit enrollment
> of a selected existing Linux administrator, no shared default password, and private later
> provisioning of a virtual recovery administrator and kiosk PIN. See
> [fresh Linux administrator installation](../install-linux-auth.md#fresh-installation-using-an-explicitly-selected-linux-administrator).
> The historical fixed-bootstrap-name/password discussion below is superseded by that direction.


**Status: accepted Linux-administrator direction documented above; historical alternatives retained below.**
This is documentation only. It does not implement authentication changes, generate
credentials, or authorize resetting the current application account.

**Subsequent direction:** the [isolated Linux authentication design and POC](linux-auth-poc.md)
supersedes the local-terminal application-password flow below. Local CLI/UI access should
establish the actual Linux caller and require approved Linux admin rights plus controller
authorization; the website supports Linux-backed and virtual users, with scoped keys for
both, while the graphical kiosk keeps its PIN flow. Inline application login remains
relevant to explicit remote mode. The sections below preserve the earlier requirements;
the bootstrap password decision remains deferred. No production authentication changed.

## Initial administrator bootstrap

The operator wants a fresh installation to complete without mandatory application
credential entry and to provide a fixed, documented initial administrator username.
The exact username, initial password scheme and whether a password change is required
at first login remain undecided. The operator explicitly deferred those design choices
until implementation; there is no agreed default password or first-login policy.

The earlier recommendation was a unique temporary password per installation with a
required first-login change, rather than a universal shared password. It remains a
recommendation for that later discussion, not an accepted implementation requirement.

The requirement concerns the Roof Controller application account, independently of
OS/SSH credentials. Existing administrator accounts and credentials must remain intact
on reruns, upgrades and restores. Private credential delivery, limited initial access,
PIN enrollment, expiration/recovery and cleanup need decisions during implementation.

Current behavior remains as documented in
[Passwords and PINs](../install.md#passwords-and-pins): a person supplies the configured
first administrator's password and requested PIN before the installer applies its plan.
Omitting the first administrator is possible today, but does not provide the requested
initial login experience. Any future bootstrap exception to the current password-entry
rules must be agreed and documented before it is implemented.

## Pending CLI login username default

The operator also requested that application login default to the current Pi/OS username
when no username is supplied: the intended interface is `hvo-roof login [NAME]`.
An explicit command-line username must take precedence over the inferred OS username.
This is a separate convenience requirement and does not choose the fixed bootstrap
administrator username above.

Only the username is inferred. Application password authentication remains separate:
there is no automatic authorization, OS-password reuse or implicit account creation.
The behavior under sudo, including whether the effective user or original invoking user
is selected, remains a design and test consideration for implementation.

This requirement is recorded for later implementation only. The installed CLI still
requires `hvo-roof login NAME`; application sign-in success has not yet been confirmed.

## Pending interactive terminal sign-in

The operator requested a review of the authentication experience: the UI should prompt
for username and password itself, without requiring a separate `hvo-roof login` command.
The proposed flow for an interactive `hvo-roof ui` session is:

1. Open the UI directly when an existing controller session is valid and authorized;
   do not ask for a redundant login. When authentication is missing, or a session has
   expired or been revoked, open an in-UI login screen. Handle expiry during use as well
   as at startup, without repeatedly opening duplicate dialogs.
2. Prefill the editable username with the current Pi/OS username. An explicit username
   override takes precedence over that default; the exact UI command-line option remains
   to be designed. Keep the sudo/effective-user question above open for design and tests.
3. Accept the application password in a hidden field and authenticate through the existing
   client/API. The OS username is only a convenience, not proof of authority; do not reuse
   an OS password, create an account implicitly or bypass API authorization.
4. On success, continue into the UI and reconnect its status feed using the authenticated
   session. Do not replay a previously refused command or a pending control action.
5. On failure, show a clear message with Retry and Cancel. Preserve the editable username,
   clear the password, and respect API lockouts/rate limits. Cancel dismisses the login
   without authentication, with a clear way to leave the UI. Distinguish connection/trust
   failures from rejected credentials. If already authenticated but lacking the required
   role, show an explicit permission error for the refused operation, not another password
   prompt or a login loop.

This captures the operator's qualification that prompting is needed only when the account
does not already have access through valid controller authentication. A possible future
secure mapping from local OS identity to an application identity would require a separate
trust design and review; merely matching usernames proves nothing. No such trust mechanism
is introduced or approved for implementation by this proposal.

The automatic login screen is for an interactive UI only. Noninteractive commands,
scripts, redirected input/output and JSON output must retain deterministic error/exit
behavior without surprise prompts or attempts to consume input as a password. Preserve
the explicitly invoked login command's supported secret-input behavior. Passwords and
tokens must not appear in arguments, ordinary output, logs or screenshots; never persist
the password. Preserve existing certificate checks and API authorization on every request.

## Saved-session lifecycle review and acceptance cases

Review session persistence together with the interactive flow, rather than adding a
prompt only at startup. Existing credential precedence, private file permissions, and
API session semantics are documented in [CLI credentials](../cli.md#credentials) and
[signing in](../security.md#signing-in). The implementation review must cover:

- Reopening the UI with an accepted saved session, and expired/revoked sessions detected
  both at startup and through API or status-hub failures during use. Preserve server-side
  expiration, revocation and authorization; no silent password storage or automatic renewal
  that bypasses those policies.
- Saving only the returned session with existing private storage protections; handling
  failed writes visibly; replacing or clearing an invalid session without losing controller
  address, CA trust or unrelated credentials. Review concurrent CLI/UI file updates.
- Explicit sign-out, local token cleanup and server revocation, including an unavailable
  controller. The UI must accurately distinguish local cleanup from confirmed server logout.
- Environment credentials, explicit credential-file/controller overrides and saved API
  keys. Preserve documented precedence, explain an overriding credential, and avoid silently
  switching identity or privilege to hide a failed session. Exact fallback UX needs review.
- Retry, cancel, incorrect credentials, rate limits, network/TLS failure, insufficient
  permissions, username override, sudo behavior and noninteractive operation. Tests must
  demonstrate that reauthentication never replays a control action or weakens secret handling.

These are pending requirements and review cases, not implemented behavior or changes to
session lifetimes. The bootstrap administrator's password and first-login policy remain
deferred. Current application accounts and credentials remain intact, and successful
operator application sign-in is still unconfirmed.
