let installPrompt = null;
let installWatcher = null;

export async function requestPersistence() {
    return await navigator.storage?.persist?.().catch(() => false) ?? false;
}

export async function isPersisted() {
    return await navigator.storage?.persisted?.().catch(() => false) ?? false;
}

// chrome and edge offer installation through beforeinstallprompt, often before .NET has started, so boot.mjs
// registers this first; keeping the event lets the app show its own button instead of the browser infobar
export function captureInstallPrompt(target) {
    installPrompt = null;
    installWatcher = null;
    target.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        installPrompt = event;
        notifyInstall();
    });
    target.addEventListener("appinstalled", () => {
        installPrompt = null;
        notifyInstall();
    });
}

export function watchInstall(watcher) {
    installWatcher = watcher;
    return installPrompt !== null;
}

// "accepted", "dismissed" or "unavailable"; one event shows the browser dialog only once
export async function promptInstall() {
    const prompt = installPrompt;
    if (prompt === null) return "unavailable";
    installPrompt = null;
    notifyInstall();
    await prompt.prompt();
    const choice = await prompt.userChoice;
    return choice.outcome;
}

// a phone may freeze or discard a hidden page without another event, so unsaved input is written when it hides
export function watchVisibility(watcher, target = globalThis.document) {
    target.addEventListener("visibilitychange", () => {
        if (target.visibilityState === "hidden") {
            watcher.invokeMethodAsync("OnHidden");
        }
    });
}

function notifyInstall() {
    installWatcher?.invokeMethodAsync("OnInstallAvailabilityChanged", installPrompt !== null);
}
