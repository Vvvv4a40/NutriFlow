import test from "node:test";
import assert from "node:assert/strict";
import {
    acquireInstanceLock,
    chooseStartupScreen,
    installedAppLockTimeoutInMilliseconds,
    instanceLockName,
    isAppleMobile,
    isStandalone,
    isSupported,
    movedToAppKey,
    windowsChannelName
} from "../../web/NutriFlow.Web/wwwroot/js/startup.mjs";

const iPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1";
const iPadDesktopMode = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15";
const android = "Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36";
const windows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

function createLocks({ grant = true } = {}) {
    const requests = [];
    return {
        requests,
        request(name, options, callback) {
            requests.push({ name, options });
            if (grant) {
                callback();
                return new Promise(() => { });
            }

            return new Promise((_, reject) => {
                options.signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
            });
        }
    };
}

function createEnvironment({
    userAgent = windows,
    maxTouchPoints = 0,
    standalone = false,
    locks = createLocks(),
    webAssembly = true,
    getDirectory = async () => ({})
} = {}) {
    return {
        WebAssembly: webAssembly ? { instantiate() { } } : undefined,
        matchMedia: query => ({ matches: standalone && query === "(display-mode: standalone)" }),
        navigator: {
            userAgent,
            maxTouchPoints,
            locks,
            storage: getDirectory ? { getDirectory } : {}
        }
    };
}

function immediateTimers() {
    const timers = new Map();
    return {
        timers,
        setTimeout(callback, duration) {
            const id = timers.size + 1;
            timers.set(id, { callback, duration });
            return id;
        },
        clearTimeout: id => timers.delete(id)
    };
}

test("detects iPhone and iPad, including iPad with a desktop user agent", () => {
    assert.equal(isAppleMobile({ userAgent: iPhone, maxTouchPoints: 5 }), true);
    assert.equal(isAppleMobile({ userAgent: iPadDesktopMode, maxTouchPoints: 5 }), true);
    assert.equal(isAppleMobile({ userAgent: iPadDesktopMode, maxTouchPoints: 0 }), false);
    assert.equal(isAppleMobile({ userAgent: android, maxTouchPoints: 5 }), false);
    assert.equal(isAppleMobile({ userAgent: windows, maxTouchPoints: 10 }), false);
});

test("standalone comes from the display mode or from the legacy safari flag", () => {
    assert.equal(isStandalone(createEnvironment({ standalone: true })), true);
    const legacy = createEnvironment();
    legacy.navigator.standalone = true;
    assert.equal(isStandalone(legacy), true);
    assert.equal(isStandalone(createEnvironment()), false);
});

test("support requires webassembly, web locks and a usable origin private file system", async () => {
    assert.equal(await isSupported(createEnvironment()), true);
    assert.equal(await isSupported(createEnvironment({ webAssembly: false })), false);
    assert.equal(await isSupported(createEnvironment({ getDirectory: null })), false);
    assert.equal(await isSupported(createEnvironment({
        getDirectory: async () => { throw new DOMException("Denied", "SecurityError"); }
    })), false);
    const withoutLocks = createEnvironment();
    delete withoutLocks.navigator.locks;
    assert.equal(await isSupported(withoutLocks), false);
});

test("instance lock is requested exclusively and held after it is granted", async () => {
    const locks = createLocks();
    const clock = immediateTimers();

    assert.equal(await acquireInstanceLock(locks, clock), true);
    assert.equal(locks.requests.length, 1);
    assert.equal(locks.requests[0].name, instanceLockName);
    assert.equal(locks.requests[0].options.mode, "exclusive");
    assert.equal(clock.timers.size, 0);
});

test("instance lock gives up when another page keeps it past the timeout", async () => {
    const locks = createLocks({ grant: false });
    const clock = immediateTimers();

    const pending = acquireInstanceLock(locks, clock);
    const [{ callback, duration }] = clock.timers.values();
    assert.equal(duration, 3000);
    callback();

    assert.equal(await pending, false);
});

test("startup screens follow the order: unsupported, install on apple devices, second instance, app", async () => {
    let lockRequests = 0;
    const lock = granted => async () => { lockRequests++; return granted; };

    assert.equal(await chooseStartupScreen(createEnvironment({ webAssembly: false }), lock(true)), "unsupported");
    assert.equal(await chooseStartupScreen(createEnvironment({ userAgent: iPhone, maxTouchPoints: 5 }), lock(true)), "install");
    assert.equal(lockRequests, 0);

    assert.equal(await chooseStartupScreen(createEnvironment({ userAgent: iPhone, maxTouchPoints: 5, standalone: true }), lock(true)), "app");
    assert.equal(await chooseStartupScreen(createEnvironment({ userAgent: android }), lock(false)), "second-instance");
    assert.equal(await chooseStartupScreen(createEnvironment(), lock(true)), "app");
    assert.equal(lockRequests, 3);
});

test("a tab that gave way to the installed app does not take the lock again", async () => {
    let lockRequests = 0;
    const lock = async () => { lockRequests++; return true; };
    const environment = createEnvironment();
    environment.sessionStorage = { getItem: key => key === movedToAppKey ? "1" : null };

    assert.equal(await chooseStartupScreen(environment, lock), "moved");
    assert.equal(lockRequests, 0);

    environment.sessionStorage = { getItem: () => null };
    assert.equal(await chooseStartupScreen(environment, lock), "app");
});

test("the installed app asks a browser tab to leave and waits longer for its lock", async () => {
    const messages = [];
    const environment = createEnvironment({ standalone: true });
    environment.sessionStorage = { getItem: () => "1" };
    environment.BroadcastChannel = class {
        constructor(name) { this.name = name; }
        postMessage(message) { messages.push([this.name, message]); }
        close() { }
    };
    let options;

    const screen = await chooseStartupScreen(environment, async (_, lockOptions) => { options = lockOptions; return true; });

    assert.equal(screen, "app");
    assert.deepEqual(messages, [[windowsChannelName, "installed-app-opened"]]);
    assert.equal(options.timeoutInMilliseconds, installedAppLockTimeoutInMilliseconds);
});
