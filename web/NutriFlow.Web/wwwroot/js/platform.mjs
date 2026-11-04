import { isAppleMobile } from "./startup.mjs";

let installPrompt = null;
let installWatcher = null;

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
