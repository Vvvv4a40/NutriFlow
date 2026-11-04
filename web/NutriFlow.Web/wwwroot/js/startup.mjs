// decides what the page shows before .NET starts; kept free of DOM access so node tests can run it

export const instanceLockName = "nutriflow-app";
// a reloaded or updated page releases the lock of the previous one with a delay
export const instanceLockTimeoutInMilliseconds = 3000;
// the installed app also waits for a browser tab that saves its input before it gives way
export const installedAppLockTimeoutInMilliseconds = 6000;
export const windowsChannelName = "nutriflow-windows";
export const movedToAppKey = "nutriflow.moved-to-app";

export async function isSupported(environment = globalThis) {
    if (typeof environment.WebAssembly?.instantiate !== "function") return false;
    if (typeof environment.navigator?.locks?.request !== "function") return false;
    if (typeof environment.navigator?.storage?.getDirectory !== "function") return false;
    try {
        // private windows expose the api but refuse the directory
        await environment.navigator.storage.getDirectory();
        return true;
    } catch {
        return false;
    }
}

export function isAppleMobile({ userAgent = "", maxTouchPoints = 0 } = {}) {
    // ipados reports a desktop safari user agent, the touch screen gives it away
    return /\b(iPhone|iPad|iPod)\b/.test(userAgent) || (/\bMacintosh\b/.test(userAgent) && maxTouchPoints > 1);
}

export function isStandalone(environment = globalThis) {
    return environment.matchMedia?.("(display-mode: standalone)").matches === true ||
        environment.navigator?.standalone === true;
}

export function acquireInstanceLock(locks, {
    name = instanceLockName,
    timeoutInMilliseconds = instanceLockTimeoutInMilliseconds,
    setTimeout: scheduleTimeout = globalThis.setTimeout,
    clearTimeout: cancelTimeout = globalThis.clearTimeout
} = {}) {
    return new Promise(resolve => {
        const controller = new AbortController();
        const timeoutId = scheduleTimeout(() => controller.abort(), timeoutInMilliseconds);
        locks.request(name, { mode: "exclusive", signal: controller.signal }, () => {
            cancelTimeout(timeoutId);
            resolve(true);
            // the lock stays held until the page goes away
            return new Promise(() => { });
        }).catch(() => {
            cancelTimeout(timeoutId);
            resolve(false);
        });
    });
}

export async function chooseStartupScreen(environment = globalThis, acquireLock = acquireInstanceLock) {
    if (!await isSupported(environment)) return "unsupported";
    const standalone = isStandalone(environment);
    if (isAppleMobile(environment.navigator) && !standalone) return "install";
    if (!standalone && environment.sessionStorage?.getItem(movedToAppKey)) return "moved";
    if (standalone) askBrowserTabToLeave(environment);
    const lockOptions = standalone ? { timeoutInMilliseconds: installedAppLockTimeoutInMilliseconds } : undefined;
    if (!await acquireLock(environment.navigator.locks, lockOptions)) return "second-instance";
    return "app";
}

// after the installation the tab that installed the app still holds the profile; a live tab answers through
// watchInstalledApp in platform.mjs, a frozen one does not, and the app shows the second instance screen
function askBrowserTabToLeave(environment) {
    if (typeof environment.BroadcastChannel !== "function") return;
    const channel = new environment.BroadcastChannel(windowsChannelName);
    channel.postMessage("installed-app-opened");
    channel.close();
}
