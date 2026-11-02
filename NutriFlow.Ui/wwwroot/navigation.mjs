// page helpers of NutriFlow.Ui: history entries for BackStack, layers over the shell, input blocking and the keyboard inset
let backStack = null;
const layers = new Map();

export function start(reference, target = globalThis, doc = globalThis.document) {
    backStack = reference;
    target.addEventListener("popstate", () => backStack?.invokeMethodAsync("OnPopStateAsync"));
    // ios safari applies :active only when the page listens to touches
    doc?.addEventListener("touchstart", () => { }, { passive: true });
    watchKeyboard(target.visualViewport, target, doc?.documentElement);
}

export function push(history = globalThis.history) {
    history.pushState({ nutriflow: true }, "");
}

export function back(history = globalThis.history) {
    history.back();
}

export function focus(element) {
    element?.focus({ preventScroll: true });
}

// a full-screen layer makes the rest of the page inert at every level up to the body, so tab and screen readers stay
// inside, and remembers the focused control to give the focus back when it closes. the live region, open dialogs and
// alerts stay reachable
export function enterLayer(key, element, activeElement = globalThis.document?.activeElement) {
    if (!element || layers.has(key)) return;
    const inerted = [];
    for (let node = element; node?.parentElement && node.tagName !== "BODY"; node = node.parentElement) {
        for (const sibling of node.parentElement.children) {
            if (sibling === node || sibling.inert || keepsReachable(sibling)) continue;
            sibling.inert = true;
            inerted.push(sibling);
        }
    }

    layers.set(key, { inerted, opener: activeElement });
}

// the layer element may already be gone from the page, so the layer is found by its key
export function leaveLayer(key) {
    const layer = layers.get(key);
    if (!layer) return;
    layers.delete(key);
    for (const sibling of layer.inerted) sibling.inert = false;
    if (layer.opener?.isConnected && !layer.opener.closest("[inert]")) layer.opener.focus({ preventScroll: true });
}

// the transparent blocker stops pointers; keyboard and screen reader activation reach controls directly,
// so while an action runs they are dropped too, except inside a dialog the action itself opened
const blockInput = event => {
    if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") return;
    if (event.target?.closest?.("dialog[open]")) return;
    event.preventDefault();
    event.stopPropagation();
};

export function setInputBlocked(blocked, doc = globalThis.document) {
    for (const type of ["click", "keydown"]) {
        if (blocked) doc.addEventListener(type, blockInput, true);
        else doc.removeEventListener(type, blockInput, true);
    }
}

// ios safari keeps fixed panels under the on-screen keyboard; the inset lets a form lift its pinned footer above it
export function keyboardInset(viewport, layoutHeight) {
    if (!viewport) return 0;
    return Math.max(0, Math.round(layoutHeight - viewport.height - viewport.offsetTop));
}

function watchKeyboard(viewport, target, root) {
    if (!viewport || !root) return;
    const update = () => root.style.setProperty("--keyboard-inset", `${keyboardInset(viewport, target.innerHeight)}px`);
    viewport.addEventListener("resize", update);
    viewport.addEventListener("scroll", update);
}

function keepsReachable(element) {
    return element.tagName === "DIALOG" || element.tagName === "SCRIPT" || element.matches("[aria-live], [role=alert]");
}
