// Stop from the reconnect dialog (App.razor). The form is posted with fetch, so Stop works while the Blazor circuit is
// down, and the dialog shows what the controller answered. The console cookie and the form's antiforgery field
// authenticate the request (POST /console/stop). The wording is RoofStopText's (HVO.RoofControllerV4.Client), which
// a test pins, so every client says the same.
(function () {
    "use strict";

    const timeoutMilliseconds = 5000;
    const useRoofStop = " Use the stop control at the roof.";

    function show(output, state, text) {
        output.dataset.state = state;
        output.textContent = text;
    }

    async function readResult(response) {
        if (response.status === 401) {
            return ["failed", "Stop was not sent because the session is signed out. Sign in again, or use the stop control at the roof."];
        }

        let body = null;
        try {
            body = await response.json();
        } catch {
            // Not JSON (for example a proxy error page).
        }

        if (body && typeof body.message === "string" && typeof body.outcome === "string") {
            const state = body.outcome === "Acknowledged" ? "ok" : body.outcome === "RelayUnverified" ? "warn" : "failed";
            return [state, body.message];
        }

        return ["failed", `Stop failed: The controller refused the request (HTTP ${response.status}).${useRoofStop}`];
    }

    async function sendStop(form) {
        const button = form.querySelector("button[type=submit]");
        const output = form.querySelector("[data-console-stop-result]");
        const abort = new AbortController();
        const timer = setTimeout(() => abort.abort(), timeoutMilliseconds);

        button.disabled = true;
        show(output, "sent", "Stop sent. Waiting for the controller…");
        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                cache: "no-store",
                signal: abort.signal
            });
            const [state, text] = await readResult(response);
            show(output, state, text);
        } catch {
            const reason = abort.signal.aborted ? "The controller did not answer in time." : "The controller could not be reached.";
            show(output, "failed", `Stop failed: ${reason}${useRoofStop}`);
        } finally {
            clearTimeout(timer);
            button.disabled = false;
        }
    }

    document.addEventListener("submit", event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-console-stop")) {
            return;
        }

        event.preventDefault();
        sendStop(form);
    });
})();
