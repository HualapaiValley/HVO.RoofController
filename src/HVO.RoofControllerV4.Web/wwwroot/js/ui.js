// Keyboard behavior for the existing inline editors. Mutations still use the Blazor/client handlers.
(() => {
    "use strict";
    const selector = "[data-testid=setting-editor], .web-confirm, [data-testid=user-add-form], [data-testid=user-panel], [data-testid=key-add-form], [data-testid=key-panel], [data-testid=session-panel], [data-testid=linux-user]:has([data-testid=linux-role])";
    const panels = new Map();
    let errorNumber = 0;
    let lastAction;

    function originOf(element) {
        return {
            element,
            testId: element?.dataset.testid,
            text: element?.textContent.trim(),
            scope: element?.closest(".web-setting, tr, [data-testid=linux-user], section")
        };
    }
    // Capture before a circuit update can remove the button which opened an editor.
    document.addEventListener("click", event => {
        const action = event.target.closest("button");
        if (action) lastAction = panels.get(action.closest(selector)) || originOf(action);
    }, true);

    function update() {
        for (const [panel, origin] of panels) {
            if (!panel.isConnected || !panel.matches(selector)) {
                panels.delete(panel);
                // Conditional Blazor markup recreates row actions. Find the replacement inside the same record.
                const replacement = origin.testId && origin.scope?.isConnected
                    ? origin.scope.querySelector(`[data-testid="${CSS.escape(origin.testId)}"]`)
                    : [...(origin.scope?.querySelectorAll("button") || [])].find(button => button.textContent.trim() === origin.text);
                const opener = origin.element?.isConnected ? origin.element : replacement;
                if (opener?.isConnected && document.activeElement === document.body) opener.focus();
            }
        }
        for (const panel of document.querySelectorAll(selector)) {
            if (!panels.has(panel)) {
                panels.set(panel, lastAction || originOf(document.activeElement));
                if (!panel.hasAttribute("role")) panel.setAttribute("role", "group");
                const first = panel.querySelector("input:not([type=hidden]), select, textarea, button:not(:disabled)");
                first?.focus();
            }
            const error = panel.querySelector("[role=alert]");
            for (const field of panel.querySelectorAll("input:not([type=hidden]), select, textarea")) {
                if (error) {
                    if (!error.id) error.id = `web-field-error-${++errorNumber}`;
                    if (field.getAttribute("aria-describedby") !== error.id) field.setAttribute("aria-describedby", error.id);
                    if (field.getAttribute("aria-invalid") !== "true") field.setAttribute("aria-invalid", "true");
                } else if (field.hasAttribute("aria-invalid")) {
                    field.removeAttribute("aria-invalid");
                    field.removeAttribute("aria-describedby");
                }
            }
        }
    }
    new MutationObserver(update).observe(document.body, { childList: true, subtree: true });
    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") return;
        const panel = event.target.closest(selector);
        const cancel = panel && [...panel.querySelectorAll("button:not(:disabled)")].find(button => button.textContent.trim() === "Cancel");
        if (cancel) {
            event.preventDefault();
            cancel.click();
        }
    });
    update();
})();

// #220: the person's own input on the page (a click or tap, a key, a form, scrolling) keeps their operator lease from
// idling out. Only events the browser marks as the person's count, never a script's, a timer or a status update, and
// the live page hears of them at most once every 2 s (its heartbeat goes every 10 s).
window.hvoInput = (() => {
    "use strict";
    let page = null;
    let last = 0;
    function note(event) {
        if (!page || !event.isTrusted) return;
        const now = Date.now();
        if (now - last < 2000) return;
        last = now;
        page.invokeMethodAsync("NoteInput").catch(() => { });
    }
    for (const type of ["pointerdown", "keydown", "submit", "wheel"]) {
        document.addEventListener(type, note, { capture: true, passive: true });
    }
    return {
        watch(reference) {
            page = reference;
            last = 0;
        }
    };
})();
