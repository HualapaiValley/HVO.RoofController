#!/usr/bin/env python3
"""Actual installed web acceptance in the constrained outer container. No browser traces or secrets are saved."""
import argparse
import datetime as dt
from enum import Enum
import json
import os
from pathlib import Path
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from playwright.sync_api import sync_playwright, expect
from run_acceptance import PASSWORD

class BrowserPhase(str, Enum):
    PREPARE = "prepare"
    PROVIDER_SELECTOR = "provider_selector"
    LINUX_SIGNIN = "linux_signin"
    LINUX_ROLE = "linux_role"
    VIRTUAL_SIGNIN = "virtual_signin"
    VIRTUAL_ROLE = "virtual_role"
    SIGNOUT = "signout"
    KEYS_NAVIGATION = "keys_navigation"
    OWNED_NAME_VISIBLE = "owned_name_visible"
    KEYS_INTERACTIVE_READY = "keys_interactive_ready"
    OWNED_NAME_FILL = "owned_name_fill"
    KEY_CREATE_CLICK = "key_create_click"
    SECRET_VISIBLE = "secret_visible"
    SECRET_READ = "secret_read"
    KEY_API_CHECKS = "key_api_checks"
    SECRET_DISMISS = "secret_dismiss"
    SECRET_HIDDEN = "secret_hidden"
    KEYS_SCREENSHOT = "keys_screenshot"
    KEY_REVOKE_CLICK = "key_revoke_click"
    KEY_REVOKED_VISIBLE = "key_revoked_visible"
    KEY_REVOCATION_CHECK = "key_revocation_check"
    NONADMIN_SIGNIN = "nonadmin_signin"
    NONADMIN_ROLE = "nonadmin_role"
    PEOPLE_NAVIGATION = "people_navigation"
    PEOPLE_DENIED = "people_denied"
    WRONG_PASSWORD = "wrong_password"


def check(value):
    if not value:
        raise AssertionError("Acceptance condition failed")

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--web",default="https://127.0.0.1:28088")
    parser.add_argument("--api",default="https://127.0.0.1:28443")
    parser.add_argument("--output",required=True)
    args=parser.parse_args()
    check(Path("/.dockerenv").exists())
    for address in (args.web,args.api):
        parsed=urllib.parse.urlparse(address)
        check(parsed.scheme=="https" and parsed.hostname in ("127.0.0.1","localhost") and not parsed.username)
    output=Path(args.output)
    output.mkdir(parents=True,exist_ok=True)
    cases=[]
    phase=BrowserPhase.PREPARE
    page=None
    interactive_state={"ready":False}
    def safe_page_state():
        # Only fixed selectors and known route paths: never text, values, HTML,
        # query strings, headers, screenshots, or raw exception messages.
        state={"keys_interactive_ready":interactive_state["ready"]}
        if page is None:
            return state
        try:
            route=urllib.parse.urlparse(page.url).path
            state["path"]=route if route in ("/", "/signin", "/account/keys", "/people") else "<other>"
        except Exception:
            state["path"]="<unavailable>"
        try:
            state.update(page.evaluate(key_control_bindings_js))
        except Exception as error:
            state["control_binding_unavailable"]=type(error).__name__
        selectors={
            "signed_in_user": '[data-testid="signed-in-user"]',
            "sign_in": '[data-testid="sign-in"]',
            "owned_name": "#owned-name",
            "owned_secret_panel": '[data-testid="owned-key-secret"]',
            "owned_secret_input": "#owned-secret",
            "owned_key_rows": '[data-testid="owned-key"]',
            "role_denied": '[data-testid="role-denied"]',
            "blazor_error": "#blazor-error-ui",
            "blazor_reconnect": "#components-reconnect-modal",
        }
        for name, selector in selectors.items():
            try:
                locator=page.locator(selector)
                count=locator.count()
                state[name]={"count":count, "visible":locator.first.is_visible() if count else False}
            except Exception as error:
                state[name]={"unavailable":type(error).__name__}
        try:
            create=page.get_by_role("button",name="Create key",exact=True)
            state["create_key"]={"count":create.count(), "enabled":create.is_enabled() if create.count()==1 else False}
        except Exception as error:
            state["create_key"]={"unavailable":type(error).__name__}
        return state
    # Explicitly scoped test-only exception: SSH-forwarded loopback ports do not match the guest certificate name.
    context_ssl=ssl._create_unverified_context()
    def api(method,path,key):
        request=urllib.request.Request(args.api+"/api/v4.0/"+path,
            data=b"{}" if method=="POST" else None,headers={"X-Api-Key":key,"Content-Type":"application/json"},method=method)
        try:
            with urllib.request.urlopen(request,context=context_ssl,timeout=30) as response:
                response.read()
                return response.status
        except urllib.error.HTTPError as error:
            error.read()
            return error.code
    def record(name,fn):
        try:
            fn()
            item={"name":name,"passed":True,"step":phase.value}
        except Exception as error:
            item={"name":name,"passed":False,"error_type":type(error).__name__,"step":phase.value,"page_state":safe_page_state()}
        cases.append(item)
        print(json.dumps(item),flush=True)
        (output/"browser-report.json").write_text(json.dumps({"cases":cases},indent=2)+"\n")
    with sync_playwright() as playwright:
        browser=playwright.chromium.launch(headless=True,args=["--disable-dev-shm-usage"])
        context=browser.new_context(base_url=args.web,ignore_https_errors=True,viewport={"width":1440,"height":1000})
        page=context.new_page()
        page.set_default_timeout(30000)
        # Verified against the installed Blazor runtime: each live element has
        # _blazorEvents_<id> whose getHandler is an observational lookup. A first
        # render acknowledgment can precede this form's asynchronous initialization.
        key_control_bindings_js="""() => {
            const input=document.getElementById("owned-name");
            const create=Array.from(document.querySelectorAll("button"))
                .find(button => button.textContent.trim() === "Create key");
            const bound=(element,event) => !!element &&
                Object.getOwnPropertyNames(element).some(key => {
                    if (!key.startsWith("_blazorEvents_")) return false;
                    const info=element[key];
                    return !!(info && typeof info.getHandler === "function" && info.getHandler(event));
                });
            return {
                owned_name_change_handler:bound(input,"change"),
                create_click_handler:bound(create,"click"),
                bound_create_enabled:!!create && !create.disabled
            };
        }"""
        def wait_for_key_page_interactive():
            interactive_state["ready"]=False
            page.wait_for_function("""() => {
                const state=("""+key_control_bindings_js+""")();
                return state.owned_name_change_handler &&
                    state.create_click_handler && state.bound_create_enabled;
            }""",timeout=45000)
            interactive_state["ready"]=True
        def signin(provider,name="fixture-admin",password=PASSWORD):
            page.goto("/signin")
            page.locator("#signin-provider").select_option(provider)
            page.locator("#signin-name").fill(name)
            page.locator("#signin-password").fill(password)
            page.get_by_test_id("sign-in").click()
            expect(page.get_by_test_id("signed-in-user")).to_be_visible()
        def signout():
            page.get_by_test_id("sign-out").click()
            expect(page.get_by_test_id("sign-in")).to_be_visible()
        def shot(name):
            check(page.locator("#signin-password").count()==0 or page.locator("#signin-password").input_value()=="")
            check(page.get_by_test_id("owned-key-secret").count()==0)
            check(PASSWORD not in page.content())
            page.screenshot(path=str(output/name),full_page=True,animations="disabled")
        try:
            phase=BrowserPhase.PROVIDER_SELECTOR
            page.goto("/signin")
            record("installed_web_provider_selector",lambda:expect(page.locator("#signin-provider")).to_be_visible())
            shot("provider-signin-desktop.png")
            phase=BrowserPhase.LINUX_SIGNIN
            record("actual_linux_web_signin",lambda:signin("linux"))
            phase=BrowserPhase.LINUX_ROLE
            record("linux_web_admin_role",lambda:expect(page.get_by_test_id("signed-in-role")).to_have_text("Admin"))
            phase=BrowserPhase.SIGNOUT
            signout()
            phase=BrowserPhase.VIRTUAL_SIGNIN
            record("actual_virtual_web_signin",lambda:signin("virtual"))
            phase=BrowserPhase.VIRTUAL_ROLE
            record("virtual_web_admin_role",lambda:expect(page.get_by_test_id("signed-in-role")).to_have_text("Admin"))
            phase=BrowserPhase.KEYS_NAVIGATION
            page.goto("/account/keys")
            phase=BrowserPhase.OWNED_NAME_VISIBLE
            expect(page.locator("#owned-name")).to_be_visible()
            phase=BrowserPhase.KEYS_INTERACTIVE_READY
            wait_for_key_page_interactive()
            key_name="browser-read-"+uuid.uuid4().hex[:10]
            phase=BrowserPhase.OWNED_NAME_FILL
            page.locator("#owned-name").fill(key_name)
            phase=BrowserPhase.KEY_CREATE_CLICK
            page.get_by_role("button",name="Create key",exact=True).click()
            phase=BrowserPhase.SECRET_VISIBLE
            expect(page.get_by_test_id("owned-key-secret")).to_be_visible()
            phase=BrowserPhase.SECRET_READ
            secret=page.locator("#owned-secret").input_value()
            phase=BrowserPhase.KEY_API_CHECKS
            record("browser_created_scoped_key_reads",lambda:check(api("GET","RoofControl/Status",secret)==200))
            record("browser_created_key_configuration_denied",lambda:check(api("POST","RoofControl/Configuration",secret)==403))
            record("browser_created_key_stop_allowed",lambda:check(api("POST","RoofControl/Stop",secret)==200))
            phase=BrowserPhase.SECRET_DISMISS
            page.get_by_role("button",name="Done",exact=True).click()
            phase=BrowserPhase.SECRET_HIDDEN
            expect(page.get_by_test_id("owned-key-secret")).to_have_count(0)
            phase=BrowserPhase.KEYS_SCREENSHOT
            shot("provider-owned-keys-desktop.png")
            row=page.get_by_test_id("owned-key").filter(has_text=key_name)
            phase=BrowserPhase.KEY_REVOKE_CLICK
            row.get_by_role("button",name="Revoke",exact=True).click()
            phase=BrowserPhase.KEY_REVOKED_VISIBLE
            expect(row).to_contain_text("Revoked")
            phase=BrowserPhase.KEY_REVOCATION_CHECK
            record("browser_key_revocation_effective",lambda:check(api("GET","RoofControl/Status",secret)==401))
            phase=BrowserPhase.SIGNOUT
            signout()
            phase=BrowserPhase.NONADMIN_SIGNIN
            record("actual_nonadmin_linux_web_signin",lambda:signin("linux","fixture-user"))
            phase=BrowserPhase.NONADMIN_ROLE
            record("linux_nonadmin_web_role",lambda:expect(page.get_by_test_id("signed-in-role")).to_have_text("Viewer"))
            phase=BrowserPhase.PEOPLE_NAVIGATION
            page.goto("/people")
            phase=BrowserPhase.PEOPLE_DENIED
            record("linux_nonadmin_people_page_denied",lambda:expect(page.get_by_test_id("role-denied")).to_be_visible())
            phase=BrowserPhase.SIGNOUT
            signout()
            phase=BrowserPhase.WRONG_PASSWORD
            page.goto("/signin")
            page.locator("#signin-provider").select_option("linux")
            page.locator("#signin-name").fill("fixture-admin")
            page.locator("#signin-password").fill("public-wrong-fixture")
            page.get_by_test_id("sign-in").click()
            record("wrong_linux_browser_password_denied",lambda:check("message=failed" in page.url))
            record("wrong_linux_browser_password_has_no_session",lambda:check(page.get_by_test_id("signed-in-user").count()==0))
        except Exception as error:
            cases.append({"name":"browser_acceptance_prerequisite","passed":False,"error_type":type(error).__name__,"step":phase.value,"page_state":safe_page_state()})
        finally:
            context.close()
            browser.close()
    result={"phase":"browser","coverage":"actual installed website in Chromium and production API; no mock authentication",
        "https_validation":"test-only loopback forwarded endpoint exception; no host or application TLS setting changed",
        "passed":sum(c["passed"] for c in cases),"failed":sum(not c["passed"] for c in cases),"cases":cases,
        "screenshots":["provider-signin-desktop.png","provider-owned-keys-desktop.png"]}
    (output/"browser-report.json").write_text(json.dumps(result,indent=2)+"\n")
    print(json.dumps({k:result[k] for k in ("phase","passed","failed")}))
    return 0 if result["failed"]==0 else 1

if __name__=="__main__":
    sys.exit(main())
