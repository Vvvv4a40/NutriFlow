import test from "node:test";
import assert from "node:assert/strict";
import { captureInstallPrompt, promptInstall, saveFile, watchInstall, watchVisibility } from "../../web/NutriFlow.Web/wwwroot/js/platform.mjs";

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
