import { isAppleMobile, isStandalone, movedToAppKey, windowsChannelName } from "./startup.mjs";

// a worker that never takes over (a newer one replaced it) must not leave the page waiting
const updateTakeoverTimeoutInMilliseconds = 5000;

let installPrompt = null;
let installWatcher = null;
let workerRegistration = null;
let updateAvailable = false;
let updateWatcher = null;

export async function requestPersistence() {
    return await navigator.storage?.persist?.().catch(() => false) ?? false;
}

export async function isPersisted() {
    return await navigator.storage?.persisted?.().catch(() => false) ?? false;
}

// a browser that refused at the first profile may agree after the installation or more visits, so every start asks
// again. firefox puts this question to the user instead of deciding silently: there it stays the one at the first profile
export async function renewPersistence(browser = globalThis.navigator) {
    const storage = browser.storage;
    if (await storage?.persisted?.().catch(() => false) ?? false) return true;
    if (/\bFirefox\//.test(browser.userAgent ?? "")) return false;
    return await storage?.persist?.().catch(() => false) ?? false;
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

// the browser tab gives way to the installed app: .NET saves the input first and may refuse when it could not.
// the tab comes back with a screen that does not take the lock (chooseStartupScreen)
export function watchInstalledApp(watcher, environment = globalThis) {
    if (isStandalone(environment) || typeof environment.BroadcastChannel !== "function") return;
    const channel = new environment.BroadcastChannel(windowsChannelName);
    channel.addEventListener("message", async () => {
        if (await watcher.invokeMethodAsync("OnLeaveRequested").catch(() => false)) {
            environment.sessionStorage.setItem(movedToAppKey, "1");
            environment.location.reload();
        }
    });
}

// registers the offline worker of the running app. a new version installs in the background and waits;
// only the user applies it, so it never replaces the page in the middle of work
export async function startUpdates(environment = globalThis) {
    workerRegistration = null;
    updateAvailable = false;
    const container = environment.navigator.serviceWorker;
    if (!container) return;
    let registration;
    try {
        registration = await container.register("service-worker.js", { updateViaCache: "none" });
    } catch (error) {
        console.warn("service worker registration failed", error);
        return;
    }

    workerRegistration = registration;
    const report = () => {
        // a page without a controller has just seen the first worker fill the cache: no older version runs
        const available = Boolean(registration.waiting) && Boolean(container.controller);
        if (available !== updateAvailable) {
            updateAvailable = available;
            updateWatcher?.invokeMethodAsync("OnUpdateAvailabilityChanged", available);
        }
    };
    registration.addEventListener("updatefound", () => {
        registration.installing?.addEventListener("statechange", report);
    });
    report();
    environment.document.addEventListener("visibilitychange", () => {
        if (environment.document.visibilityState === "visible") {
            // rejects without a network; the next return to the app tries again
            registration.update().catch(() => { });
        }
    });
}

export function watchUpdates(watcher) {
    updateWatcher = watcher;
    return updateAvailable;
}

// never resolves when it works: the page reloads into the new version, and .NET keeps the input blocked until then
export function applyUpdate(environment = globalThis) {
    const waiting = workerRegistration?.waiting;
    let reloading = false;
    const reload = () => {
        if (!reloading) {
            reloading = true;
            environment.location.reload();
        }
    };
    if (!waiting) {
        reload();
    } else {
        environment.navigator.serviceWorker.addEventListener("controllerchange", reload);
        environment.setTimeout(reload, updateTakeoverTimeoutInMilliseconds);
        waiting.postMessage({ type: "SKIP_WAITING" });
    }

    return new Promise(() => { });
}

// a phone may freeze or discard a hidden page without another event, so unsaved input is written when it hides
export function watchVisibility(watcher, target = globalThis.document) {
    target.addEventListener("visibilitychange", () => {
        if (target.visibilityState === "hidden") {
            watcher.invokeMethodAsync("OnHidden");
        }
    });
}

// "saved", "cancelled": an installed app on iphone has no downloads, its share sheet offers «Save to Files»;
// everywhere else the browser downloads the file. sharing needs the tap that called this, so it must come first
export async function saveFile(name, type, bytes, environment = globalThis) {
    const file = new environment.File([bytes], name, { type });
    const navigator = environment.navigator;
    if (isAppleMobile(navigator) && navigator.canShare?.({ files: [file] })) {
        try {
            await navigator.share({ files: [file] });
            return "saved";
        } catch (error) {
            if (error?.name === "AbortError") return "cancelled";
            throw error;
        }
    }

    const url = environment.URL.createObjectURL(file);
    const link = environment.document.createElement("a");
    link.href = url;
    link.download = name;
    link.hidden = true;
    environment.document.body.append(link);
    link.click();
    link.remove();
    // the download reads the blob after this returns
    environment.setTimeout(() => environment.URL.revokeObjectURL(url), 60000);
    return "saved";
}

function notifyInstall() {
    installWatcher?.invokeMethodAsync("OnInstallAvailabilityChanged", installPrompt !== null);
}
