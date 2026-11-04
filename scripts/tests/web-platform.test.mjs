import test from "node:test";
import assert from "node:assert/strict";
import { captureInstallPrompt, promptInstall, renewPersistence, saveFile, watchInstall, watchVisibility } from "../../web/NutriFlow.Web/wwwroot/js/platform.mjs";

// a window that dispatches the two install events of chrome
function browser() {
    const listeners = new Map();
    return {
        addEventListener(type, listener) {
            listeners.set(type, [...(listeners.get(type) ?? []), listener]);
        },
        dispatch(type, event = {}) {
            for (const listener of listeners.get(type) ?? []) listener(event);
        }
    };
}

function installEvent(outcome = "accepted") {
    return {
        prevented: false,
        prompted: 0,
        preventDefault() { this.prevented = true; },
        async prompt() { this.prompted++; },
        userChoice: Promise.resolve({ outcome })
    };
}

function watcher() {
    const calls = [];
    return { calls, invokeMethodAsync: (method, available) => calls.push([method, available]) };
}

test("the install prompt is kept instead of the browser infobar and reported to .NET", () => {
    const window = browser();
    captureInstallPrompt(window);
    const dotnet = watcher();
    assert.equal(watchInstall(dotnet), false);

    const event = installEvent();
    window.dispatch("beforeinstallprompt", event);

    assert.equal(event.prevented, true);
    assert.deepEqual(dotnet.calls, [["OnInstallAvailabilityChanged", true]]);
});

test("a prompt that came before .NET started is available at once", () => {
    const window = browser();
    captureInstallPrompt(window);
    window.dispatch("beforeinstallprompt", installEvent());

    assert.equal(watchInstall(watcher()), true);
});

test("installing shows the browser dialog once and returns its outcome", async () => {
    const window = browser();
    captureInstallPrompt(window);
    const dotnet = watcher();
    watchInstall(dotnet);
    const event = installEvent("dismissed");
    window.dispatch("beforeinstallprompt", event);

    assert.equal(await promptInstall(), "dismissed");
    assert.equal(await promptInstall(), "unavailable");
    assert.equal(event.prompted, 1);
    assert.deepEqual(dotnet.calls.at(-1), ["OnInstallAvailabilityChanged", false]);
});

test("an installed app hides the install button", () => {
    const window = browser();
    captureInstallPrompt(window);
    const dotnet = watcher();
    watchInstall(dotnet);
    window.dispatch("beforeinstallprompt", installEvent());

    window.dispatch("appinstalled");

    assert.deepEqual(dotnet.calls, [["OnInstallAvailabilityChanged", true], ["OnInstallAvailabilityChanged", false]]);
    assert.equal(watchInstall(dotnet), false);
});

test("a hidden page asks .NET to write unsaved input, a visible one does not", () => {
    const document = { ...browser(), visibilityState: "visible" };
    const dotnet = watcher();
    watchVisibility(dotnet, document);

    document.dispatch("visibilitychange");
    document.visibilityState = "hidden";
    document.dispatch("visibilitychange");

    assert.deepEqual(dotnet.calls, [["OnHidden", undefined]]);
});

// a page with File, blob urls and a share sheet, as safari on an iphone or chrome on a computer has them
function savingPage({ userAgent, maxTouchPoints = 0, share = null }) {
    const clicks = [];
    const revoked = [];
    const timers = [];
    const page = {
        File: class { constructor(parts, name, options) { this.parts = parts; this.name = name; this.type = options.type; } },
        URL: { createObjectURL: file => `blob:${file.name}`, revokeObjectURL: url => revoked.push(url) },
        setTimeout: (callback, delay) => timers.push({ callback, delay }),
        navigator: { userAgent, maxTouchPoints },
        document: {
            body: { append() { } },
            createElement: () => ({ click() { clicks.push({ href: this.href, download: this.download }); }, remove() { } })
        },
        clicks,
        revoked,
        timers
    };
    if (share) {
        page.navigator.canShare = ({ files }) => files.length === 1;
        page.navigator.share = share;
    }
    return page;
}

test("an iphone gets the share sheet, and closing it is reported as cancelled", async () => {
    const shared = [];
    const iphone = savingPage({
        userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X)",
        share: async ({ files }) => shared.push(files[0])
    });

    assert.equal(await saveFile("NutriFlow.zip", "application/zip", new Uint8Array([1]), iphone), "saved");
    assert.equal(shared[0].name, "NutriFlow.zip");
    assert.equal(shared[0].type, "application/zip");
    assert.deepEqual(iphone.clicks, []);

    const closed = savingPage({
        userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)",
        maxTouchPoints: 5,
        share: async () => { throw new DOMException("Share canceled", "AbortError"); }
    });
    assert.equal(await saveFile("NutriFlow.zip", "application/zip", new Uint8Array([1]), closed), "cancelled");
});

test("other browsers download the file and free its url later", async () => {
    const computer = savingPage({
        userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/142.0",
        share: async () => assert.fail("a computer downloads instead of sharing")
    });

    assert.equal(await saveFile("NutriFlow.zip", "application/zip", new Uint8Array([1]), computer), "saved");

    assert.deepEqual(computer.clicks, [{ href: "blob:NutriFlow.zip", download: "NutriFlow.zip" }]);
    assert.deepEqual(computer.revoked, []);
    computer.timers[0].callback();
    assert.deepEqual(computer.revoked, ["blob:NutriFlow.zip"]);
});

// the storage manager of a browser that answers persist() with the given decision
function storageBrowser(userAgent, { persisted = false, decision = false } = {}) {
    const browser = {
        userAgent,
        asked: 0,
        storage: {
            persisted: async () => persisted,
            persist: async () => {
                browser.asked++;
                return decision;
            }
        }
    };
    return browser;
}

test("a browser that decides silently is asked again at every start until it agrees", async () => {
    const chrome = storageBrowser("Mozilla/5.0 Chrome/146.0 Safari/537.36", { decision: true });
    assert.equal(await renewPersistence(chrome), true);
    assert.equal(chrome.asked, 1);

    const safari = storageBrowser("Mozilla/5.0 (iPhone) Version/27.0 Safari/605.1.15");
    assert.equal(await renewPersistence(safari), false);
    assert.equal(safari.asked, 1);
});

test("storage that is already persistent is not asked for again", async () => {
    const chrome = storageBrowser("Mozilla/5.0 Chrome/146.0", { persisted: true });
    assert.equal(await renewPersistence(chrome), true);
    assert.equal(chrome.asked, 0);
});

test("firefox shows the question to the user, so a start never asks it, unlike firefox on iphone", async () => {
    const firefox = storageBrowser("Mozilla/5.0 (Android 14; Mobile; rv:146.0) Gecko/146.0 Firefox/146.0", { decision: true });
    assert.equal(await renewPersistence(firefox), false);
    assert.equal(firefox.asked, 0);

    const firefoxOnIphone = storageBrowser("Mozilla/5.0 (iPhone) AppleWebKit/605.1.15 FxiOS/146.0 Mobile/15E148 Safari/605.1.15");
    await renewPersistence(firefoxOnIphone);
    assert.equal(firefoxOnIphone.asked, 1);
});

test("a browser without the storage manager or with a failing one simply stays not persistent", async () => {
    assert.equal(await renewPersistence({ userAgent: "Old" }), false);
    const failing = {
        userAgent: "Mozilla/5.0 Chrome/146.0",
        storage: {
            persisted: () => Promise.reject(new Error("blocked")),
            persist: () => Promise.reject(new Error("blocked"))
        }
    };
    assert.equal(await renewPersistence(failing), false);
});
