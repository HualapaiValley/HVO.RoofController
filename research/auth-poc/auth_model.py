"""In-memory authentication policy experiment. NOT a credential service.

All provider/admin results are explicit test fixtures. No PAM, sudo, polkit, real
passwords, persistent tokens, controller connections or hardware operations exist.
"""
from dataclasses import dataclass
from enum import Enum
import json


class Denied(Exception):
    def __init__(self, code, status=403):
        super().__init__(code)
        self.code, self.status = code, status


class Role(Enum):
    VIEWER = "viewer"
    OPERATOR = "operator"
    ADMIN = "admin"


# Inert operation names: this model never performs these operations.
PERMISSIONS = {
    Role.VIEWER: frozenset({"status:read"}),
    Role.OPERATOR: frozenset({"status:read", "settings:read"}),
    Role.ADMIN: frozenset({"status:read", "settings:read", "settings:write"}),
}
KNOWN_OPERATIONS = PERMISSIONS[Role.ADMIN]


@dataclass(frozen=True)
class Peer:
    """Trusted transport input in the model, never taken from request JSON.

    binding models a server-owned connection/process handle. The test supplies it;
    production needs a verified race-resistant kernel/bus binding, not this string.
    """
    uid: int
    pid: int
    binding: str


@dataclass
class Principal:
    id: str
    provider: str
    subject: str
    role: Role
    epoch: int = 0
    enabled: bool = True


@dataclass(frozen=True)
class Credential:
    """A public MOCK fixture handle; meaningless outside this process."""
    owner: str
    epoch: int
    audience: str
    scopes: frozenset
    expires: int
    peer: Peer | None = None


class MockAdminAuthority:
    """Emulates a trusted admin-policy reply. Does not inspect sudo or polkit."""
    def __init__(self):
        self.grants = {}  # exact (peer, operation) -> expiry
        self.challenges = set()
        self.available = True

    def check(self, peer, operation, now):
        if not self.available:
            raise Denied("AdminAuthorityUnavailable", 503)
        if self.grants.get((peer, operation), -1) > now:
            return
        if (peer, operation) in self.challenges:
            raise Denied("LinuxAdminAuthenticationRequired")
        raise Denied("LinuxAdminPermissionDenied")


class MockPasswordProvider:
    """Fixture models authentication AND account checks; no actual password input.

    Map login -> (canonical subject, authentication succeeds, account permitted).
    The caller may select a provider, but cannot supply these authentication results.
    """
    def __init__(self, entries):
        self.entries = entries
        self.available = True

    def authenticate_and_check_account(self, login, fixture_input):
        if not self.available:
            raise Denied("IdentityProviderUnavailable", 503)
        entry = self.entries.get(login)
        if (entry is None or not entry[1] or not entry[2]
                or fixture_input != "public-mock-input"):
            raise Denied("SignInFailed", 401)
        return entry[0]


class Model:
    def __init__(self, principals, linux_subjects, admin, providers):
        self.principals = {p.id: p for p in principals}
        # A trusted mock enrollment snapshot: UID -> account incarnation.
        # Real NSS/UID reuse detection and enrollment lifecycle are not implemented.
        self.linux_subjects = linux_subjects
        self.admin = admin
        self.providers = providers
        self.credentials = {}
        self.now = 100
        self._sequence = 0

    def _principal(self, provider, subject):
        matches = [p for p in self.principals.values()
                   if p.provider == provider and p.subject == subject and p.enabled]
        if len(matches) != 1:
            raise Denied("PrincipalNotEnrolled")
        return matches[0]

    @staticmethod
    def _permit(principal, operation):
        if operation not in KNOWN_OPERATIONS:
            raise Denied("UnknownOperation", 400)
        if operation not in PERMISSIONS[principal.role]:
            raise Denied("ControllerRoleDenied")

    def _issue(self, principal, audience, scopes, ttl, peer=None):
        if type(ttl) is not int or not 0 < ttl <= 300:
            raise Denied("InvalidLifetime", 400)
        self._sequence += 1
        handle = f"MOCK-ONLY-{audience}-{self._sequence}"
        self.credentials[handle] = Credential(
            principal.id, principal.epoch, audience, frozenset(scopes),
            self.now + ttl, peer)
        return handle

    def local_open(self, peer, operation="status:read"):
        # Both independent gates must pass. Even UID 0 gets no implicit bypass.
        self.admin.check(peer, operation, self.now)
        subject = self.linux_subjects.get(peer.uid)
        principal = self._principal("linux", subject)
        self._permit(principal, operation)
        return self._issue(principal, "local", PERMISSIONS[principal.role], 60, peer)

    def local_request(self, peer, raw):
        if not isinstance(raw, bytes) or len(raw) > 1024:
            raise Denied("InvalidRequest", 400)
        def no_duplicates(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise Denied("InvalidRequest", 400)
                result[key] = value
            return result
        try:
            request = json.loads(raw, object_pairs_hook=no_duplicates)
        except (ValueError, UnicodeDecodeError, RecursionError):
            raise Denied("InvalidRequest", 400) from None
        if (not isinstance(request, dict) or set(request) != {"operation"}
                or not isinstance(request["operation"], str)):
            raise Denied("InvalidRequest", 400)
        operation = request["operation"]
        if operation not in KNOWN_OPERATIONS:
            raise Denied("UnknownOperation", 400)
        return self.local_open(peer, operation)

    def web_login(self, provider, login, fixture_input):
        if provider not in {"linux", "virtual"} or provider not in self.providers:
            raise Denied("SignInFailed", 401)
        subject = self.providers[provider].authenticate_and_check_account(login, fixture_input)
        principal = self._principal(provider, subject)
        return self._issue(principal, "web", PERMISSIONS[principal.role], 120)

    def check(self, handle, audience, operation, peer=None):
        credential = self.credentials.get(handle)
        if credential is None or credential.expires <= self.now:
            raise Denied("AuthenticationRequired", 401)
        principal = self.principals.get(credential.owner)
        if principal is None or not principal.enabled or principal.epoch != credential.epoch:
            raise Denied("AuthenticationRequired", 401)
        if credential.audience != audience:
            raise Denied("WrongAudience", 401)
        if audience == "local":
            if peer is None or peer != credential.peer:
                raise Denied("PeerBindingMismatch", 401)
            if self.linux_subjects.get(peer.uid) != principal.subject:
                raise Denied("AccountBindingChanged", 401)
            self.admin.check(peer, operation, self.now)
        self._permit(principal, operation)
        if operation not in credential.scopes:
            raise Denied("ScopeDenied")
        return principal.id

    def issue_mock_key(self, owner_session, owner_audience, scopes, ttl=60, peer=None):
        # Self-service issuance model only; no unauthenticated owner-id parameter.
        owner = self.check(owner_session, owner_audience, "status:read", peer)
        principal = self.principals[owner]
        scopes = frozenset(scopes)
        if not scopes or not scopes <= PERMISSIONS[principal.role]:
            raise Denied("ScopeEscalationDenied")
        if owner_audience == "api":
            raise Denied("KeyCannotMintKey")
        return self._issue(principal, "api", scopes, ttl)

    def revoke(self, handle):
        # Trusted management operation, not an exposed unauthenticated endpoint.
        self.credentials.pop(handle, None)

    def change_role(self, principal_id, role):
        principal = self.principals[principal_id]
        principal.role = role
        principal.epoch += 1


def interaction(error, mode, interactive):
    """UI policy only. No prompts are displayed and no commands are replayed."""
    if error is None:
        return "continue"
    if not interactive:
        return "error-no-prompt"
    if mode == "local" and error.code == "LinuxAdminAuthenticationRequired":
        return "os-admin-agent-retry-or-cancel"
    if mode == "remote" and error.status == 401:
        return "inline-application-login-retry-or-cancel"
    return "show-error-no-login-loop"


def fixtures():
    """No real account names/credentials: the same label exists in both realms."""
    admin = MockAdminAuthority()
    peer = Peer(1001, 101, "mock-connection-A")
    principals = [Principal("linux-A", "linux", "uid1001/enrollment-A", Role.ADMIN),
                  Principal("virtual-A", "virtual", "virtual-enrollment-A", Role.VIEWER)]
    providers = {
        "linux": MockPasswordProvider({"demo": (principals[0].subject, True, True)}),
        "virtual": MockPasswordProvider({"demo": (principals[1].subject, True, True)}),
    }
    model = Model(principals, {1001: principals[0].subject}, admin, providers)
    for operation in KNOWN_OPERATIONS:
        admin.grants[(peer, operation)] = 200
    return model, peer
