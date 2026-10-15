import assert from "node:assert/strict";
import { test } from "node:test";
import { configureAndroidEmulatorInput } from "../configure-android-emulator-input.mjs";

const serial = "emulator-5554";
const keys = ["stylus_handwriting_enabled", "show_ime_with_hard_keyboard"];

function fixture({ previous = ["1", "0"], qemu = "1", ready = "device", boot = "1", change = () => undefined, platform = "linux" } = {}) {
    const settings = new Map(keys.map((key, index) => [key, previous[index]]));
    const calls = [];
    const execute = (command, args, options) => {
        assert.deepEqual(args.slice(0, 2), ["-s", serial]);
        const operation = args.slice(2);
        calls.push({ command, args, operation, options });
        const changed = change(operation, calls, settings);
        if (changed !== undefined) return changed;
        let stdout = "";
        if (operation[0] === "get-state") stdout = ready;
        else if (operation[0] === "shell" && operation[1] === "getprop") stdout = operation[2] === "ro.kernel.qemu" ? qemu : boot;
        else if (operation[0] === "shell" && operation[1] === "settings") {
            assert.equal(operation[3], "secure");
            assert.ok(keys.includes(operation[4]));
            if (operation[2] === "get") stdout = settings.get(operation[4]) ?? "null";
            else if (operation[2] === "put") settings.set(operation[4], operation[5]);
            else if (operation[2] === "delete") settings.delete(operation[4]);
            else assert.fail(`Unexpected settings operation: ${operation}`);
        } else assert.fail(`Unexpected operation: ${operation}`);
        return { status: 0, stdout: `${stdout}\n`, stderr: "" };
    };
    const sdkDirectory = platform === "win32" ? "C:\\Android SDK" : "/synthetic Android SDK";
    return { settings, calls, run: extra => configureAndroidEmulatorInput({ serial, sdkDirectory, execute, platform, ...extra }) };
}

test("updates only the two input preferences and exposes exact previous-value restore commands", () => {
    const sample = fixture();
    const result = sample.run();
    assert.deepEqual(result.previousSettings, { stylus_handwriting_enabled: "1", show_ime_with_hard_keyboard: "0" });
    assert.deepEqual(result.settings, { stylus_handwriting_enabled: "0", show_ime_with_hard_keyboard: "1" });
    assert.equal(result.status, "configured");
    assert.deepEqual([...sample.settings.values()], ["0", "1"]);
    const writes = sample.calls.filter(call => call.operation[2] === "put");
    assert.deepEqual(writes.map(call => call.operation), keys.map((key, index) => ["shell", "settings", "put", "secure", key, String(index)]));
    assert.deepEqual(result.restoreCommands.map(command => command.args), keys.map((key, index) => ["-s", serial, "shell", "settings", "put", "secure", key, String(1 - index)]));
    assert.ok(sample.calls.every(call => call.command === "/synthetic Android SDK/platform-tools/adb" &&
        call.options.shell === false && call.options.timeout <= 10_000 && call.options.maxBuffer <= 64 * 1024));
    assert.equal(sample.calls.filter(call => call.operation[2] === "get").length, 4);
});

test("uses explicit Windows SDK adb.exe without a shell", () => {
    const sample = fixture({ platform: "win32" });
    sample.run();
    assert.ok(sample.calls.every(call => call.command === "C:\\Android SDK\\platform-tools\\adb.exe" && call.options.windowsHide));
});

test("refuses physical devices even if their serial looks like an emulator", () => {
    const sample = fixture({ qemu: "0" });
    assert.throws(() => sample.run(), /not an Android emulator/);
    assert.ok(sample.calls.every(call => call.operation[1] !== "settings"));
});

for (const [name, extra] of [["offline", { ready: "offline" }], ["still booting", { boot: "0" }]]) {
    test(`refuses an emulator that is ${name} without changing settings`, () => {
        const sample = fixture(extra);
        assert.throws(() => sample.run(), /not ready|finish booting/);
        assert.ok(sample.calls.every(call => call.operation[1] !== "settings"));
    });
}

for (const invalidSerial of [undefined, "", "0123abc", "emulator-", "emulator-5554; echo unsafe", "emulator-5554\n"]) {
    test(`rejects malformed or missing serial ${JSON.stringify(invalidSerial)} before invoking ADB`, () => {
        const sample = fixture();
        assert.throws(() => sample.run({ serial: invalidSerial }), /explicit emulator serial/);
        assert.equal(sample.calls.length, 0);
    });
}

test("requires an explicit absolute SDK directory before invoking ADB", () => {
    for (const sdkDirectory of [undefined, "", "relative-sdk"]) {
        const sample = fixture();
        assert.throws(() => sample.run({ sdkDirectory }), /absolute Android SDK/);
        assert.equal(sample.calls.length, 0);
    }
});

test("does not write anything when a settings read returns nonzero", () => {
    const sample = fixture({ change: operation => operation[2] === "get" ? { status: 1, stdout: "1", stderr: "synthetic failure" } : undefined });
    assert.throws(() => sample.run(), /ADB .* failed: 1/);
    assert.ok(sample.calls.every(call => call.operation[2] !== "put" && call.operation[2] !== "delete"));
});

test("does not write anything when a previous preference has an unexpected value", () => {
    const sample = fixture({ previous: ["unknown", "0"] });
    assert.throws(() => sample.run(), /Unexpected secure setting value/);
    assert.ok(sample.calls.every(call => call.operation[2] !== "put" && call.operation[2] !== "delete"));
});

test("rolls back both preferences when the second write fails even after taking effect", () => {
    let failed = false;
    const sample = fixture({ change: (operation, calls, settings) => {
        if (!failed && operation[2] === "put" && operation[4] === keys[1]) {
            failed = true;
            settings.set(keys[1], "1");
            return { status: 1, stdout: "", stderr: "synthetic failure" };
        }
    } });
    assert.throws(() => sample.run(), error => {
        assert.match(error.message, /Both previous settings were restored/);
        assert.deepEqual(error.rollbackErrors, []);
        assert.deepEqual(error.previousSettings, { stylus_handwriting_enabled: "1", show_ime_with_hard_keyboard: "0" });
        return true;
    });
    assert.deepEqual([...sample.settings.values()], ["1", "0"]);
    assert.equal(sample.calls.filter(call => call.operation[2] === "put").length, 4);
});

test("rolls back both preferences when a successful write fails readback verification", () => {
    let ignored = false;
    const sample = fixture({ change: operation => {
        if (!ignored && operation[2] === "put" && operation[4] === keys[0]) {
            ignored = true;
            return { status: 0, stdout: "", stderr: "" };
        }
    } });
    assert.throws(() => sample.run(), /did not retain.*Both previous settings were restored/);
    assert.deepEqual([...sample.settings.values()], ["1", "0"]);
});

test("restores absent preferences with delete rather than writing the word null", () => {
    let failed = false;
    const sample = fixture({ previous: [null, null], change: operation => {
        if (!failed && operation[2] === "put" && operation[4] === keys[1]) {
            failed = true;
            return { status: 1, stdout: "", stderr: "synthetic failure" };
        }
    } });
    assert.throws(() => sample.run(), error => {
        assert.deepEqual(error.previousSettings, { stylus_handwriting_enabled: null, show_ime_with_hard_keyboard: null });
        assert.ok(error.restoreCommands.every(command => command.args[4] === "delete" && command.args.length === 7));
        return true;
    });
    assert.equal(sample.settings.size, 0);
    assert.deepEqual(sample.calls.filter(call => call.operation[2] === "delete").map(call => call.operation[4]), keys);
});

test("surfaces rollback errors and still attempts the second preference", () => {
    let writingFailed = false;
    const sample = fixture({ change: operation => {
        if (operation[2] !== "put") return;
        if (!writingFailed && operation[4] === keys[1]) {
            writingFailed = true;
            return { status: 1, stdout: "", stderr: "synthetic write failure" };
        }
        if (writingFailed && operation[4] === keys[0]) return { status: 1, stdout: "", stderr: "synthetic rollback failure" };
    } });
    assert.throws(() => sample.run(), error => {
        assert.match(error.message, /Rollback failed/);
        assert.equal(error.rollbackErrors.length, 1);
        assert.match(error.rollbackErrors[0], /stylus_handwriting_enabled/);
        assert.equal(error.restoreCommands.length, 2);
        return true;
    });
    assert.equal(sample.settings.get(keys[1]), "0");
    assert.ok(sample.calls.some(call => call.operation[2] === "get" && call.operation[4] === keys[1]));
});

test("is idempotent and retains commands for restoring already configured preferences", () => {
    const sample = fixture({ previous: ["0", "1"] });
    const result = sample.run();
    assert.deepEqual(result.previousSettings, result.settings);
    assert.deepEqual([...sample.settings.values()], ["0", "1"]);
});

test("refuses a timed-out readiness check without changing any preferences", () => {
    const sample = fixture({ change: () => ({ status: null, stdout: "", stderr: "", error: { code: "ETIMEDOUT" } }) });
    assert.throws(() => sample.run(), /ETIMEDOUT/);
    assert.equal(sample.calls.length, 1);
});
