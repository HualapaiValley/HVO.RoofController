"""Deterministic, inert scenarios; no terminal input or network connection."""
import json
from auth_model import Denied, Peer, fixtures, interaction


def run():
    model, peer = fixtures()
    cases = []
    def attempt(name, action, mode="local", interactive=False):
        try:
            action()
            cases.append({"scenario": name, "result": "allowed-mock-only"})
        except Denied as error:
            cases.append({"scenario": name, "result": error.code,
                          "ux": interaction(error, mode, interactive)})
    attempt("verified-admin-and-controller-role", lambda: model.local_open(peer))
    attempt("spoofed-username-and-sudo-fields", lambda: model.local_request(
        Peer(1002, 202, "mock-B"), b'{"operation":"status:read","SUDO_UID":1001}'))
    attempt("unapproved-linux-caller", lambda: model.local_open(Peer(1002, 202, "mock-B")))
    web = model.web_login("virtual", "demo", "public-mock-input")
    attempt("web-session-cannot-bypass-local-admin", lambda: model.check(web, "local", "status:read", peer))
    attempt("viewer-cannot-write-settings", lambda: model.check(web, "web", "settings:write"), "remote", True)
    key = model.issue_mock_key(web, "web", {"status:read"})
    attempt("scoped-virtual-user-key", lambda: model.check(key, "api", "status:read"))
    model.revoke(key)
    attempt("revoked-key", lambda: model.check(key, "api", "status:read"))
    model.now = 250
    attempt("expired-web-session", lambda: model.check(web, "web", "status:read"), "remote", True)
    return {"warning": "Policy simulation only; credentials are inert public fixtures", "scenarios": cases}


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))
