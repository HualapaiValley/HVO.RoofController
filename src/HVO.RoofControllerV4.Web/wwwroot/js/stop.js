// Stop, on every page (App.razor): the Stop bar and the reconnect dialog. Each form is posted with fetch, so Stop works
// with or without the page's live connection, and the form shows what the controller answered. The sign-in cookie and
// the form's antiforgery field authenticate the request (POST /stop). Every text, and how long to wait, comes from the
// form's data-web-stop-texts, which the server renders from RoofStopText and RoofText (WebStopTexts), so this script
// has no wording of its own and every client says the same. The button is never disabled: each press sends Stop again, and
// only the newest answer is shown.
(function () {
    "use strict";

    let sequence = 0;

    function has(object, key) {
        return Object.prototype.hasOwnProperty.call(object, key);
    }

    function readTexts(form) {
        try {
            const texts = JSON.parse(form.dataset.webStopTexts);
            return texts && typeof texts === "object" && texts.codes && texts.statuses
                && Number.isFinite(texts.timeoutMilliseconds) && texts.timeoutMilliseconds > 0 ? texts : null;
        } catch {
            return null;
        }
    }

    function show(state, text) {
        // Every Stop form shows the newest answer, so the bar and the reconnect dialog agree.
        for (const output of document.querySelectorAll("[data-web-stop-result]")) {
            output.dataset.state = state;
            output.textContent = text;
        }
    }

    async function readResult(response, texts) {
        let body = null;
        try {
            body = await response.json();
        } catch {
            // Not JSON (for example a proxy's error page).
        }

        if (body && typeof body.message === "string" && typeof body.outcome === "string") {
            const state = body.outcome === "Acknowledged" ? "ok" : body.outcome === "RelayUnverified" ? "warn" : "failed";
            return [state, body.message];
        }

        if (response.status === 401) {
            return ["failed", texts.signedOut];
        }

        // A refusal before the endpoint (the origin check) carries a code; a proxy's error page only a status.
        const status = String(response.status);
        const code = body && typeof body.code === "string" && has(texts.codes, body.code) ? body.code : null;
        const text = code !== null ? texts.codes[code]
            : has(texts.statuses, status) ? texts.statuses[status]
            : response.status >= 500 ? texts.serverError
            : texts.other.replace("{status}", status);
        return ["failed", text];
    }

    async function sendStop(form, texts) {
        const mine = ++sequence;
        const abort = new AbortController();
        // Longer than the web UI waits for the controller, so the controller's answer is what the page shows.
        const timer = setTimeout(() => abort.abort(), texts.timeoutMilliseconds);

        show("sent", texts.sending);
        let state;
        let text;
        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                cache: "no-store",
                signal: abort.signal
            });
            [state, text] = await readResult(response, texts);
        } catch {
            [state, text] = ["failed", abort.signal.aborted ? texts.timedOut : texts.unreachable];
        } finally {
            clearTimeout(timer);
        }

        // An older Stop's answer says nothing about a newer one, which may have failed or been confirmed.
        if (mine === sequence) {
            show(state, text);
        }
    }

    // The page leaves room at its end for the Stop bar (app.css, --web-stop-bar-space). An answer can make the bar taller
    // than the room the stylesheet leaves, so the room follows the bar's height.
    function keepRoomForTheBar() {
        const bar = document.querySelector(".web-stop-bar");
        if (bar === null || typeof ResizeObserver !== "function") {
            return;
        }

        new ResizeObserver(() => {
            const height = Math.ceil(bar.getBoundingClientRect().height);
            document.documentElement.style.setProperty("--web-stop-bar-space", `${height}px`);
        }).observe(bar);
    }

    keepRoomForTheBar();

    document.addEventListener("submit", event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-web-stop")) {
            return;
        }

        // Without its texts the page cannot describe the answer, so the browser posts the form itself: Stop is still
        // sent, and the controller's answer is shown as the page.
        const texts = readTexts(form);
        if (texts === null) {
            return;
        }

        event.preventDefault();
        sendStop(form, texts);
    });
})();
