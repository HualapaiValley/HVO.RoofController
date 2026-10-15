"""Adversarial policy tests using explicitly mocked identity mechanisms."""
import os
import unittest
from unittest.mock import patch
from auth_model import (Denied, KNOWN_OPERATIONS, Peer, Principal, Role,
                        fixtures, interaction)


class PolicyTests(unittest.TestCase):
    def setUp(self):
        self.model, self.peer = fixtures()

    def denied(self, code, action, status=None):
        with self.assertRaises(Denied) as result:
            action()
        self.assertEqual(code, result.exception.code)
        if status is not None:
            self.assertEqual(status, result.exception.status)

    def web(self, provider="linux"):
        return self.model.web_login(provider, "demo", "public-mock-input")

    def test_local_admin_and_controller_role_both_pass(self):
        handle = self.model.local_open(self.peer)
        self.assertEqual("linux-A", self.model.check(handle, "local", "status:read", self.peer))

    def test_username_claim_is_not_identity(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","username":"demo"}'))

    def test_uid_claim_is_not_identity(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","uid":0}'))

    def test_role_claim_is_not_authority(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","role":"admin"}'))

    def test_sudo_claim_is_not_authority(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","SUDO_UID":1001}'))

    def test_forged_environment_does_not_authorize(self):
        with patch.dict(os.environ, {"USER": "root", "SUDO_UID": "1001", "SUDO_USER": "demo"}):
            self.denied("LinuxAdminPermissionDenied", lambda: self.model.local_open(
                        Peer(1002, 202, "other-connection")))

    def test_uid_zero_is_not_an_implicit_application_bypass(self):
        self.denied("LinuxAdminPermissionDenied", lambda: self.model.local_open(Peer(0, 202, "root")))

    def test_unapproved_os_user_is_denied(self):
        self.model.admin.grants.clear()
        self.denied("LinuxAdminPermissionDenied", lambda: self.model.local_open(self.peer))

    def test_approved_but_unenrolled_user_is_denied(self):
        self.model.linux_subjects.clear()
        self.denied("PrincipalNotEnrolled", lambda: self.model.local_open(self.peer))

    def test_ambiguous_enrollment_is_denied(self):
        p = self.model.principals["linux-A"]
        self.model.principals["duplicate"] = Principal("duplicate", p.provider, p.subject, p.role)
        self.denied("PrincipalNotEnrolled", lambda: self.model.local_open(self.peer))

    def test_admin_rights_do_not_override_controller_role(self):
        self.model.principals["linux-A"].role = Role.VIEWER
        self.denied("ControllerRoleDenied", lambda: self.model.local_open(self.peer, "settings:write"))

    def test_admin_authority_unavailable_fails_closed(self):
        self.model.admin.available = False
        self.denied("AdminAuthorityUnavailable", lambda: self.model.local_open(self.peer), 503)

    def test_challenge_does_not_grant_access(self):
        self.model.admin.grants.clear()
        self.model.admin.challenges.add((self.peer, "status:read"))
        self.denied("LinuxAdminAuthenticationRequired", lambda: self.model.local_open(self.peer))
        self.assertEqual({}, self.model.credentials)

    def test_local_session_bound_to_exact_connection(self):
        handle = self.model.local_open(self.peer)
        reused_pid = Peer(self.peer.uid, self.peer.pid, "different-connection")
        self.denied("PeerBindingMismatch", lambda: self.model.check(handle, "local", "status:read", reused_pid))

    def test_local_session_requires_peer(self):
        handle = self.model.local_open(self.peer)
        self.denied("PeerBindingMismatch", lambda: self.model.check(handle, "local", "status:read"))

    def test_local_session_denied_after_admin_permission_removed(self):
        handle = self.model.local_open(self.peer)
        self.model.admin.grants.clear()
        self.denied("LinuxAdminPermissionDenied", lambda: self.model.check(handle, "local", "status:read", self.peer))

    def test_admin_grant_is_operation_specific(self):
        handle = self.model.local_open(self.peer)
        del self.model.admin.grants[(self.peer, "settings:write")]
        self.denied("LinuxAdminPermissionDenied", lambda: self.model.check(handle, "local", "settings:write", self.peer))

    def test_admin_expiry_is_checked_during_local_session(self):
        handle = self.model.local_open(self.peer)
        self.model.admin.grants[(self.peer, "status:read")] = self.model.now
        self.denied("LinuxAdminPermissionDenied", lambda: self.model.check(handle, "local", "status:read", self.peer))

    def test_uid_reuse_does_not_reuse_old_enrollment(self):
        handle = self.model.local_open(self.peer)
        self.model.linux_subjects[self.peer.uid] = "uid1001/recreated-account"
        self.denied("AccountBindingChanged", lambda: self.model.check(handle, "local", "status:read", self.peer))
        self.denied("PrincipalNotEnrolled", lambda: self.model.local_open(self.peer))

    def test_valid_local_request(self):
        handle = self.model.local_request(self.peer, b'{"operation":"status:read"}')
        self.assertEqual("linux-A", self.model.check(handle, "local", "status:read", self.peer))

    def test_local_wire_rejects_saved_web_session(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","session":"MOCK-ONLY-web-1"}'))

    def test_duplicate_json_field_rejected(self):
        self.denied("InvalidRequest", lambda: self.model.local_request(self.peer,
                    b'{"operation":"status:read","operation":"settings:write"}'))

    def test_malformed_or_oversized_requests_rejected(self):
        for raw in (b"[1]", b"null", b"{", b"\xff", b"x" * 1025, b'{"operation":[]}', "not bytes"):
            with self.subTest(raw_type=type(raw).__name__, size=len(raw)):
                self.denied("InvalidRequest", lambda: self.model.local_request(self.peer, raw))

    def test_unlisted_operation_has_no_execution_path(self):
        self.denied("UnknownOperation", lambda: self.model.local_request(self.peer, b'{"operation":"run-shell"}'))

    def test_same_name_in_two_providers_is_not_same_identity(self):
        first, second = self.web("linux"), self.web("virtual")
        self.assertEqual("linux-A", self.model.check(first, "web", "status:read"))
        self.assertEqual("virtual-A", self.model.check(second, "web", "status:read"))

    def test_explicit_provider_required(self):
        for provider in ("auto", "pam-fallback-virtual", "", "root"):
            with self.subTest(provider=provider):
                self.denied("SignInFailed", lambda: self.model.web_login(provider, "demo", "public-mock-input"))

    def test_wrong_password_fixture_rejected(self):
        self.denied("SignInFailed", lambda: self.model.web_login("linux", "demo", "wrong-public-fixture"), 401)
        self.assertEqual({}, self.model.credentials)

    def test_unknown_user_has_same_public_failure(self):
        self.denied("SignInFailed", lambda: self.model.web_login("linux", "missing", "public-mock-input"), 401)

    def test_pam_authentication_failure_does_not_fallback_to_virtual(self):
        provider = self.model.providers["linux"]
        subject, _, account = provider.entries["demo"]
        provider.entries["demo"] = (subject, False, account)
        self.denied("SignInFailed", lambda: self.web("linux"))
        self.assertEqual({}, self.model.credentials)

    def test_pam_account_failure_after_authentication_denies_session(self):
        provider = self.model.providers["linux"]
        subject, _, _ = provider.entries["demo"]
        provider.entries["demo"] = (subject, True, False)
        self.denied("SignInFailed", lambda: self.web("linux"))
        self.assertEqual({}, self.model.credentials)

    def test_provider_unavailable_fails_closed(self):
        self.model.providers["linux"].available = False
        self.denied("IdentityProviderUnavailable", lambda: self.web(), 503)

    def test_provider_canonical_identity_controls_mapping(self):
        self.model.providers["linux"].entries["demo"] = ("unknown-canonical-subject", True, True)
        self.denied("PrincipalNotEnrolled", lambda: self.web())

    def test_web_identity_does_not_need_local_admin_grant(self):
        self.model.admin.grants.clear()
        self.assertEqual("linux-A", self.model.check(self.web(), "web", "status:read"))

    def test_web_identity_still_obeys_controller_role(self):
        self.denied("ControllerRoleDenied", lambda: self.model.check(self.web("virtual"), "web", "settings:write"), 403)

    def test_web_session_cannot_satisfy_local_admin_requirement(self):
        self.denied("WrongAudience", lambda: self.model.check(self.web(), "local", "status:read", self.peer), 401)

    def test_local_session_cannot_be_replayed_over_web(self):
        handle = self.model.local_open(self.peer)
        self.denied("WrongAudience", lambda: self.model.check(handle, "web", "status:read"), 401)

    def test_session_expiry(self):
        handle = self.web()
        self.model.now += 120
        self.denied("AuthenticationRequired", lambda: self.model.check(handle, "web", "status:read"), 401)

    def test_logout_revokes_session(self):
        handle = self.web()
        self.model.revoke(handle)
        self.denied("AuthenticationRequired", lambda: self.model.check(handle, "web", "status:read"))

    def test_disabled_account_revokes_access(self):
        handle = self.web()
        self.model.principals["linux-A"].enabled = False
        self.denied("AuthenticationRequired", lambda: self.model.check(handle, "web", "status:read"))

    def test_role_change_invalidates_session(self):
        handle = self.web()
        self.model.change_role("linux-A", Role.VIEWER)
        self.denied("AuthenticationRequired", lambda: self.model.check(handle, "web", "settings:write"))

    def test_both_provider_types_can_own_scoped_mock_keys(self):
        for provider, owner in (("linux", "linux-A"), ("virtual", "virtual-A")):
            with self.subTest(provider=provider):
                key = self.model.issue_mock_key(self.web(provider), "web", {"status:read"})
                self.assertEqual(owner, self.model.check(key, "api", "status:read"))

    def test_key_creation_requires_valid_owner_authentication(self):
        self.denied("AuthenticationRequired", lambda: self.model.issue_mock_key("unknown", "web", {"status:read"}))

    def test_scoped_key_does_not_inherit_all_owner_permissions(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"})
        self.denied("ScopeDenied", lambda: self.model.check(key, "api", "settings:write"))

    def test_key_scopes_cannot_exceed_owner_role(self):
        self.denied("ScopeEscalationDenied", lambda: self.model.issue_mock_key(self.web("virtual"), "web", {"settings:write"}))

    def test_unknown_and_empty_scopes_denied(self):
        for scopes in (set(), {"wildcard:*"}):
            self.denied("ScopeEscalationDenied", lambda: self.model.issue_mock_key(self.web(), "web", scopes))

    def test_keys_cannot_mint_other_keys(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"})
        self.denied("KeyCannotMintKey", lambda: self.model.issue_mock_key(key, "api", {"status:read"}))

    def test_key_revocation(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"})
        self.model.revoke(key)
        self.denied("AuthenticationRequired", lambda: self.model.check(key, "api", "status:read"))

    def test_key_expiration(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"}, ttl=1)
        self.model.now += 1
        self.denied("AuthenticationRequired", lambda: self.model.check(key, "api", "status:read"))

    def test_key_lifetime_is_bounded(self):
        for ttl in (0, -1, 301, True, "60"):
            self.denied("InvalidLifetime", lambda: self.model.issue_mock_key(self.web(), "web", {"status:read"}, ttl=ttl))

    def test_owner_role_change_invalidates_key(self):
        key = self.model.issue_mock_key(self.web(), "web", {"settings:write"})
        self.model.change_role("linux-A", Role.VIEWER)
        self.denied("AuthenticationRequired", lambda: self.model.check(key, "api", "settings:write"))

    def test_owner_disable_invalidates_key(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"})
        self.model.principals["linux-A"].enabled = False
        self.denied("AuthenticationRequired", lambda: self.model.check(key, "api", "status:read"))

    def test_api_key_cannot_replace_local_linux_rights(self):
        key = self.model.issue_mock_key(self.web(), "web", {"status:read"})
        self.denied("WrongAudience", lambda: self.model.check(key, "local", "status:read", self.peer))


class InteractionTests(unittest.TestCase):
    def test_valid_session_opens_directly(self):
        self.assertEqual("continue", interaction(None, "local", True))

    def test_local_challenge_uses_os_agent_not_app_password(self):
        self.assertEqual("os-admin-agent-retry-or-cancel", interaction(Denied("LinuxAdminAuthenticationRequired"), "local", True))

    def test_remote_expiry_allows_inline_login(self):
        self.assertEqual("inline-application-login-retry-or-cancel", interaction(Denied("AuthenticationRequired", 401), "remote", True))

    def test_role_failure_is_not_a_login_loop(self):
        self.assertEqual("show-error-no-login-loop", interaction(Denied("ControllerRoleDenied"), "remote", True))

    def test_no_local_application_password_fallback(self):
        self.assertEqual("show-error-no-login-loop", interaction(Denied("AuthenticationRequired", 401), "local", True))

    def test_noninteractive_never_requests_input(self):
        for code, status in (("AuthenticationRequired", 401), ("LinuxAdminAuthenticationRequired", 403), ("ControllerRoleDenied", 403)):
            for mode in ("local", "remote"):
                self.assertEqual("error-no-prompt", interaction(Denied(code, status), mode, False))

    def test_service_failure_is_not_a_password_prompt(self):
        self.assertEqual("show-error-no-login-loop", interaction(Denied("IdentityProviderUnavailable", 503), "remote", True))


if __name__ == "__main__":
    unittest.main()
