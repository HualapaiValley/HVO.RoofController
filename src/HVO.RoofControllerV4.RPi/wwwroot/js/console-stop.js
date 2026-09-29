// Stop from the reconnect dialog (App.razor). The form is posted with fetch, so Stop works while the Blazor circuit is
// down, and the dialog shows what the controller answered. The console cookie and the form's antiforgery field
// authenticate the request (POST /console/stop). Every text comes from the form's data-console-stop-texts, which the
// server renders from RoofStopText and RoofText (RoofConsoleStopTexts), so this script has no wording of its own and
// every client says the same.
(function () {
    "use strict";

    const timeoutMilliseconds = 5000;

    function has(object, key) {
        return Object.prototype.hasOwnProperty.call(object, key);
    }

    function readTexts(form) {
        try {
            const texts = JSON.parse(form.dataset.consoleStopTexts);
            return texts && typeof texts === "object" && texts.codes && texts.statuses ? texts : null;
        } catch {
            return null;
        }
    }

    function show(output, state, text) {
        output.dataset.state = state;
        output.textContent = text;
    }

    async function readResult(response, texts) {
        if (response.status === 401) {
            return ["failed", texts.signedOut];
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

        // A refusal before the endpoint (the origin or HTTPS check) carries a code; a proxy's error page only a status.
        const status = String(response.status);
        const code = body && typeof body.code === "string" && has(texts.codes, body.code) ? body.code : null;
        const text = code !== null ? texts.codes[code]
            : has(texts.statuses, status) ? texts.statuses[status]
            : response.status >= 500 ? texts.serverError
            : texts.other.replace("{status}", status);
        return ["failed", text];
    }

    async function sendStop(form, texts) {
        const button = form.querySelector("button[type=submit]");
        const output = form.querySelector("[data-console-stop-result]");
        const abort = new AbortController();
        const timer = setTimeout(() => abort.abort(), timeoutMilliseconds);

        button.disabled = true;
        show(output, "sent", texts.sending);
        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                cache: "no-store",
                signal: abort.signal
            });
            const [state, text] = await readResult(response, texts);
            show(output, state, text);
        } catch {
            show(output, "failed", abort.signal.aborted ? texts.timedOut : texts.unreachable);
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

        // Without its texts the dialog cannot describe the answer, so the browser posts the form itself: Stop is still sent,
        // and the controller's answer is shown as the page.
        const texts = readTexts(form);
        if (texts === null) {
            return;
        }

        event.preventDefault();
        sendStop(form, texts);
    });
})();
