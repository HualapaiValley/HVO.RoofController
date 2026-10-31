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

// #220: the person's deliberate input on the page, a click (or tap) or a key, keeps their operator lease from idling out,
// and takes it back after they stepped away. Scrolling does not count, nor a lone modifier key, nor anything the browser
// does not mark as the person's (a script's event, a timer, a status update). The control that sends a grant
// (data-alpaca-step-away) is the person stepping away, never input after it. The live page hears of input at most once
// a second (its heartbeat goes every 10 s).
window.hvoInput = (() => {
    "use strict";
    const modifiers = new Set(["Shift", "Control", "Alt", "Meta", "AltGraph", "CapsLock"]);
    let page = null;
    let last = 0;
    function note(event) {
        if (!page || !event.isTrusted) return;
        if (event.type === "keydown" && modifiers.has(event.key)) return;
        if (event.target instanceof Element && event.target.closest("[data-alpaca-step-away]")) {
            // The next click or key after the grant counts at once.
            last = 0;
            return;
        }
        const now = Date.now();
        if (now - last < 1000) return;
        last = now;
        page.invokeMethodAsync("NoteInput").catch(() => { });
    }
    for (const type of ["click", "keydown"]) {
        document.addEventListener(type, note, { capture: true, passive: true });
    }
    return {
        watch(reference) {
            page = reference;
            last = 0;
        }
    };
})();
