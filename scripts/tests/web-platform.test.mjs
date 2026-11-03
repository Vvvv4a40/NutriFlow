import test from "node:test";
import assert from "node:assert/strict";
import { captureInstallPrompt, promptInstall, watchInstall, watchVisibility } from "../../web/NutriFlow.Web/wwwroot/js/platform.mjs";

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
