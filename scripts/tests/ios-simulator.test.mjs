import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtempSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { testIosSimulator } from "../test-ios-simulator.mjs";

const runtimeId = "com.apple.CoreSimulator.SimRuntime.iOS-26-0";
const deviceType = "com.apple.CoreSimulator.SimDeviceType.iPhone-16";
const deviceId = "11111111-2222-3333-4444-555555555555";
const bundleId = "com.nutriflow.app";
const pid = 1234;
const readinessBundleId = "com.apple.Preferences";
const readinessPid = 4321;
const simulatorUiPid = 9876;
const alreadyShutdown = { status: 149, stdout: "", stderr: "An error was encountered processing the command (domain=com.apple.CoreSimulator.SimError, code=405):\nUnable to shutdown device in current state: Shutdown\n" };
const shutdownInventory = { devices: { [runtimeId]: [{ udid: deviceId, state: "Shutdown" }] } };
const bootPassed = "[2026-10-07 16:32:21 +0000] Status=4294967295, isTerminal=YES, Elapsed=01:26.\n\tFinished\n";
const bootMigrationFailed = "[2026-10-07 18:17:38 +0000] Status=3, isTerminal=YES, Elapsed=01:57.\n\tData Migration Failed\n";
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=", "base64");

function fixture(context, {
    change = () => undefined, startChange = () => undefined, pauseChange = () => undefined,
    developerDirectory: selectedDeveloperDirectory = null, jobs, inventory
} = {}) {
    const directory = mkdtempSync(join(tmpdir(), "nutriflow-ios-smoke-"));
    context.after(() => rmSync(directory, { recursive: true, force: true }));
    const bundlePath = join(directory, "app with spaces.app");
    const outputDirectory = join(directory, "output with spaces");
    const developerDirectory = join(directory, "Xcode with spaces.app", "Contents", "Developer");
    const simulatorExecutable = join(developerDirectory, "Applications", "Simulator.app", "Contents", "MacOS", "Simulator");
    mkdirSync(bundlePath);
    mkdirSync(join(simulatorExecutable, ".."), { recursive: true });
    writeFileSync(simulatorExecutable, "synthetic simulator executable");
    const calls = [];
    const starts = [];
    const uiProcesses = [];
    const pauses = [];
    let processChecks = 0;
    const data = inventory ?? {
        runtimes: [
            { identifier: "com.apple.CoreSimulator.SimRuntime.iOS-18-5", version: "18.5", isAvailable: true },
            { identifier: runtimeId, version: "26.0", isAvailable: true }
        ],
        devices: { [runtimeId]: [
            { name: "iPad", isAvailable: true, deviceTypeIdentifier: "com.apple.CoreSimulator.SimDeviceType.iPad-Air" },
            { name: "iPhone 16", isAvailable: true, deviceTypeIdentifier: deviceType }
        ] }
    };

    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, calls);
        if (changed) {
            return changed;
        }
        let stdout = "";
        if (command === "xcode-select") {
            assert.deepEqual(args, ["--print-path"]);
            stdout = `${developerDirectory}\n`;
        } else if (command === "log") {
            stdout = "Simulator host diagnostics";
        } else if (command === "plutil") {
            stdout = args[1] === "CFBundleIdentifier" ? bundleId : "NutriFlow.Mobile";
        } else {
            assert.equal(command, "xcrun");
            assert.equal(args[0], "simctl");
            switch (args[1]) {
                case "list": stdout = JSON.stringify(data); break;
                case "create": stdout = deviceId; break;
                case "bootstatus": stdout = bootPassed; break;
                case "launch":
                    stdout = args.at(-1) === readinessBundleId ? `${readinessBundleId}: ${readinessPid}\n` : `${bundleId}: ${pid}\n`;
                    break;
                case "spawn":
                    if (args[3] === "launchctl") {
                        processChecks++;
                        stdout = jobs?.(processChecks) ?? `PID Status Label\n${pid}\t0\tUIKitApplication:${bundleId}[1234]\n`;
                    }
                    break;
                case "io":
                    if (args[3] === "screenshot") {
                        writeFileSync(args.at(-1), png);
                    } else {
                        assert.equal(args[3], "enumerate");
                        stdout = "Display ports for owned simulator";
                    }
                    break;
                default: assert.ok(["terminate", "install", "help", "shutdown", "delete"].includes(args[1]));
            }
        }
        return { status: 0, stdout, stderr: "" };
    }

    function start(command, args, options) {
        starts.push({ command, args, options });
        const child = new EventEmitter();
        child.pid = simulatorUiPid;
        child.exitCode = null;
        child.signalCode = null;
        child.signals = [];
        child.unrefCalls = 0;
        child.unref = () => child.unrefCalls++;
        child.kill = signal => {
            child.signals.push(signal);
            queueMicrotask(() => {
                child.signalCode = signal;
                child.emit("exit", null, signal);
            });
            return true;
        };
        uiProcesses.push(child);
        const changed = startChange(child, command, args, options);
        if (changed !== undefined) {
            return changed;
        }
        queueMicrotask(() => child.emit("spawn"));
        return child;
    }

    return {
        calls, starts, uiProcesses, pauses, outputDirectory, bundlePath, developerDirectory, simulatorExecutable,
        run: (options = {}) => testIosSimulator({
            bundlePath, outputDirectory, execute, start, developerDirectory: selectedDeveloperDirectory,
            pause: async milliseconds => {
                pauses.push(milliseconds);
                await pauseChange(milliseconds);
            },
            ...options
        }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

function assertOwnedCleanup(calls, recoveryAttempted = false) {
    const cleanup = calls.filter(call => ["shutdown", "delete"].includes(call.args[1]));
    assert.deepEqual(cleanup.map(call => call.args), [
        ...(recoveryAttempted ? [["simctl", "shutdown", deviceId]] : []),
        ["simctl", "shutdown", deviceId], ["simctl", "delete", deviceId]
    ]);
}

function isCleanupStateQuery(command, args, calls) {
    return command === "xcrun" && args[1] === "list" && args[2] === "devices" &&
        calls.some(call => call.args[1] === "shutdown");
}

async function waitForUiSignal(sample, signal) {
    for (let turn = 0; turn < 10 && !sample.uiProcesses[0]?.signals.includes(signal); turn++) {
        await new Promise(resolve => setImmediate(resolve));
    }
    assert.ok(sample.uiProcesses[0]?.signals.includes(signal), `Expected the owned UI process to receive ${signal}`);
}

test("runs an isolated iPhone, checks PID around a PNG and deletes only its own device", async context => {
    const sample = fixture(context);
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.runtimeIdentifier, runtimeId);
    assert.equal(report.deviceTypeIdentifier, deviceType);
    assert.equal(report.pid, pid);
    assert.equal(report.screenshot, "startup.png");
    assert.deepEqual(report.bootAttempts, [{ number: 1, log: "boot.log", status: "passed", terminalStatus: "4294967295", summary: "Finished" }]);
    assert.deepEqual(report.readiness, { bundleIdentifier: readinessBundleId, status: "passed", pid: readinessPid });
    assert.equal(report.simulatorUi.executable, sample.simulatorExecutable);
    assert.equal(report.simulatorUi.pid, simulatorUiPid);
    assert.equal(report.simulatorUi.status, "stopped");
    assert.deepEqual(sample.starts, [{ command: sample.simulatorExecutable,
        args: ["-CurrentDeviceUDID", deviceId, "-StartLastDeviceOnLaunch", "0", "-ConnectHardwareKeyboard", "0", "-PasteboardAutomaticSync", "0"],
        options: { stdio: "ignore", shell: false, detached: false } }]);
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
    assert.deepEqual(report.deviceCleanup, {
        deviceId, shutdown: { status: "passed", alreadyShutdown: false }, delete: { status: "passed" }, status: "passed"
    });
    assert.deepEqual(sample.pauses, [700, 10_000, 5_000]);
    const commands = sample.calls.filter(call => call.command === "xcrun");
    assert.deepEqual(commands.slice(0, 11).map(call => call.args[1]), [
        "list", "create", "bootstatus", "launch", "terminate", "install", "launch", "spawn", "spawn", "io", "spawn"
    ]);
    assert.deepEqual(commands[1].args.slice(3), [deviceType, runtimeId]);
    assert.deepEqual(commands[2].args, ["simctl", "bootstatus", deviceId, "-b"]);
    assert.equal(commands[2].options.timeout, 240_000);
    assert.deepEqual(commands[3].args, ["simctl", "launch", deviceId, readinessBundleId]);
    assert.deepEqual(commands[4].args, ["simctl", "terminate", deviceId, readinessBundleId]);
    assert.equal(commands[3].options.timeout, 120_000);
    assert.equal(commands[4].options.timeout, 30_000);
    assert.deepEqual(commands[5].args, ["simctl", "install", deviceId, sample.bundlePath]);
    assert.equal(commands[5].options.timeout, 60_000);
    assert.equal(commands[6].options.timeout, 60_000);
    assert.deepEqual(commands[6].args, ["simctl", "launch", "--terminate-running-process",
        `--stdout=${join(sample.outputDirectory, "app-stdout.log")}`,
        `--stderr=${join(sample.outputDirectory, "app-stderr.log")}`, deviceId, bundleId]);
    for (const call of sample.calls) {
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 240_000);
        assert.equal(call.options.shell, false);
        assert.equal(call.options.killSignal, "SIGKILL");
        assert.ok(!call.args.some(arg => ["booted", "all", "unavailable"].includes(arg)));
        assert.ok(!["killall", "pkill", "open", "ps"].includes(call.command));
    }
    assertOwnedCleanup(sample.calls);
    assert.equal(sample.report().status, "passed");
});

test("selects Simulator from the active Xcode path without shell interpolation", async context => {
    const sample = fixture(context);
    await sample.run();
    assert.ok(sample.simulatorExecutable.includes("Xcode with spaces.app"));
    const selected = sample.calls.filter(call => call.command === "xcode-select");
    assert.equal(selected.length, 1);
    assert.deepEqual(selected[0].args, ["--print-path"]);
    assert.equal(sample.starts[0].command, sample.simulatorExecutable);
    assert.ok(!sample.starts[0].command.includes('"'));
    assert.equal(sample.starts[0].options.shell, false);
    assert.equal(sample.report().simulatorUi.cleanup.status, "passed");
    assert.equal(sample.report().simulatorUi.cleanup.signal, "SIGTERM");
});

test("uses explicit DEVELOPER_DIR instead of another system Xcode selection", async context => {
    const sample = fixture(context, { change: command => command === "xcode-select" ?
        { status: 17, stderr: "the system Xcode must not be selected" } : undefined });
    await sample.run({ developerDirectory: sample.developerDirectory });
    assert.equal(sample.starts[0].command, sample.simulatorExecutable);
    assert.ok(!sample.calls.some(call => call.command === "xcode-select"));
});

for (const [name, selectedPath] of [["empty", ""], ["relative", "relative/Xcode/Developer"]]) {
    test(`rejects ${name} Xcode selection before creating a device`, async context => {
        const sample = fixture(context, { change: command => command === "xcode-select" ?
            { status: 0, stdout: selectedPath } : undefined });
        await assert.rejects(sample.run(), /absolute|Simulator|Xcode|Developer/i);
        assert.equal(sample.report().phase, "select-simulator-ui");
        assert.equal(sample.starts.length, 0);
        assert.ok(!sample.calls.some(call => ["create", "shutdown", "delete"].includes(call.args[1])));
    });
}

test("a failed xcode-select command stops before creating a device", async context => {
    const sample = fixture(context, { change: command => command === "xcode-select" ?
        { status: 17, stderr: "Xcode selection unavailable" } : undefined });
    await assert.rejects(sample.run(), /Xcode selection unavailable/);
    assert.equal(sample.report().phase, "select-simulator-ui");
    assert.equal(sample.starts.length, 0);
    assert.ok(!sample.calls.some(call => call.args[1] === "create"));
});

for (const kind of ["missing", "directory"]) {
    test(`rejects a ${kind} Simulator executable before creating a device`, async context => {
        const sample = fixture(context);
        rmSync(sample.simulatorExecutable);
        if (kind === "directory") {
            mkdirSync(sample.simulatorExecutable);
        }
        await assert.rejects(sample.run(), /ENOENT|Simulator|executable/i);
        assert.equal(sample.report().phase, "select-simulator-ui");
        assert.equal(sample.starts.length, 0);
        assert.ok(!sample.calls.some(call => ["create", "shutdown", "delete"].includes(call.args[1])));
    });
}

test("a thrown UI spawn error keeps its message and cleans only the created device", async context => {
    const sample = fixture(context, { startChange: () => { throw new Error("synthetic UI spawn error"); } });
    await assert.rejects(sample.run(), /synthetic UI spawn error/);
    assert.equal(sample.report().phase, "start-simulator-ui");
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().simulatorUi.pid, undefined);
    assert.ok(!sample.calls.some(call => call.command === "log"));
    assert.ok(!sample.calls.some(call => ["bootstatus", "install", "launch"].includes(call.args[1])));
    assertOwnedCleanup(sample.calls);
});

test("a child-process error event is a failed UI startup and does not boot NutriFlow", async context => {
    const sample = fixture(context, { startChange: child => {
        queueMicrotask(() => child.emit("error", Object.assign(new Error("synthetic Simulator ENOENT"), { code: "ENOENT" })));
    } });
    await assert.rejects(sample.run(), /synthetic Simulator ENOENT/);
    assert.equal(sample.report().phase, "start-simulator-ui");
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().simulatorUi.pid, undefined);
    assert.ok(!sample.calls.some(call => call.command === "log"));
    assert.ok(!sample.calls.some(call => ["bootstatus", "install", "launch"].includes(call.args[1])));
    assertOwnedCleanup(sample.calls);
});

for (const invalidPid of [undefined, 0, -1, 9007199254740993]) {
    test(`rejects an invalid Simulator UI PID before boot: ${invalidPid}`, async context => {
        const sample = fixture(context, { startChange: child => { child.pid = invalidPid; } });
        await assert.rejects(sample.run(), /positive PID/);
        assert.equal(sample.report().phase, "start-simulator-ui");
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => ["bootstatus", "install", "launch"].includes(call.args[1])));
        assertOwnedCleanup(sample.calls);
    });
}

test("a Simulator UI exit before the spawn event fails without booting", async context => {
    const sample = fixture(context, { startChange: child => {
        queueMicrotask(() => {
            child.exitCode = 9;
            child.emit("exit", 9, null);
        });
        return child;
    } });
    await assert.rejects(sample.run(), /exited before startup/);
    assert.equal(sample.report().phase, "start-simulator-ui");
    assert.equal(sample.report().simulatorUi.exitCode, 9);
    assert.ok(!sample.calls.some(call => ["bootstatus", "install", "launch"].includes(call.args[1])));
    assert.deepEqual(sample.uiProcesses[0].signals, []);
    assertOwnedCleanup(sample.calls);
});

test("a Simulator child-process error after startup stops before Settings launch", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "bootstatus") {
            queueMicrotask(() => sample.uiProcesses[0].emit("error", new Error("late Simulator process error")));
        }
    } });
    await assert.rejects(sample.run(), /late Simulator process error/);
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
    assertOwnedCleanup(sample.calls);
});

test("an exited UI process after boot prevents Settings and NutriFlow launch", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "bootstatus") {
            queueMicrotask(() => {
                sample.uiProcesses[0].exitCode = 2;
                sample.uiProcesses[0].emit("exit", 2, null);
            });
        }
    } });
    await assert.rejects(sample.run(), /Simulator.*(exit|running)|UI.*(exit|running)/i);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().simulatorUi.exitCode, 2);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assert.deepEqual(sample.uiProcesses[0].signals, []);
    assertOwnedCleanup(sample.calls);
});

test("a simultaneous boot timeout and UI exit keeps the boot error and records the exited UI", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "bootstatus") {
            queueMicrotask(() => {
                sample.uiProcesses[0].exitCode = 6;
                sample.uiProcesses[0].emit("exit", 6, null);
            });
            return { status: null, error: Object.assign(new Error("original boot timeout"), { code: "ETIMEDOUT" }) };
        }
    } });
    await assert.rejects(sample.run(), /simctl bootstatus.*ETIMEDOUT/);
    const report = sample.report();
    assert.equal(report.phase, "boot");
    assert.match(report.failure, /simctl bootstatus.*ETIMEDOUT/);
    assert.equal(report.simulatorUi.status, "failed");
    assert.equal(report.simulatorUi.exitCode, 6);
    assert.match(report.simulatorUi.failure, /exited|running/);
    assert.deepEqual(sample.uiProcesses[0].signals, []);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assertOwnedCleanup(sample.calls);
});

test("an exited UI after Settings readiness prevents installing NutriFlow", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "terminate" && args.at(-1) === readinessBundleId) {
            sample.uiProcesses[0].exitCode = 3;
            sample.uiProcesses[0].emit("exit", 3, null);
        }
    } });
    await assert.rejects(sample.run(), /Simulator.*(exit|running)|UI.*(exit|running)/i);
    assert.equal(sample.report().readiness.status, "passed");
    assert.equal(sample.report().simulatorUi.exitCode, 3);
    assert.ok(!sample.calls.some(call => call.args[1] === "install"));
    assertOwnedCleanup(sample.calls);
});

test("an exited UI during final stabilization cannot yield a successful smoke", async context => {
    const sample = fixture(context, { pauseChange: milliseconds => {
        if (milliseconds === 5_000) {
            sample.uiProcesses[0].exitCode = 4;
            sample.uiProcesses[0].emit("exit", 4, null);
        }
    } });
    await assert.rejects(sample.run(), /Simulator.*(exit|running)|UI.*(exit|running)/i);
    assert.equal(sample.report().simulatorUi.exitCode, 4);
    assert.equal(sample.report().status, "failed");
    assertOwnedCleanup(sample.calls);
});

test("the Simulator UI PID cannot substitute for a live NutriFlow process", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "launch" && args.at(-1) === bundleId ?
        { status: 0, stdout: `${bundleId}: ${simulatorUiPid}` } : undefined });
    await assert.rejects(sample.run(), /no longer running/);
    assert.equal(sample.report().simulatorUi.pid, simulatorUiPid);
    assert.equal(sample.report().status, "failed");
    assertOwnedCleanup(sample.calls);
});

test("UI cleanup failure does not hide the primary NutriFlow install error", async context => {
    const sample = fixture(context, {
        change: (_, args) => args[1] === "install" ? { status: 17, stderr: "primary install failure" } : undefined,
        startChange: child => {
            child.kill = signal => {
                child.signals.push(signal);
                throw new Error("owned UI termination failure");
            };
        }
    });
    await assert.rejects(sample.run(), /primary install failure/);
    assert.match(sample.report().failure, /primary install failure/);
    assert.equal(sample.report().simulatorUi.cleanup.status, "failed");
    assert.ok(sample.uiProcesses[0].signals.length > 0);
    assertOwnedCleanup(sample.calls);
});

test("failed UI cleanup cannot make an otherwise successful smoke green", async context => {
    const sample = fixture(context, { startChange: child => {
        child.kill = signal => {
            child.signals.push(signal);
            throw new Error("owned UI termination failure");
        };
    } });
    await assert.rejects(sample.run(), /owned UI termination failure/);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().simulatorUi.cleanup.status, "failed");
    assertOwnedCleanup(sample.calls);
});

test("UI spawn has a ten-second deadline and cleanup still stops the owned child", { timeout: 5_000 }, async context => {
    context.mock.timers.enable({ apis: ["setTimeout"] });
    const sample = fixture(context, { startChange: child => child });
    const rejected = assert.rejects(sample.run(), /Simulator|UI|spawn|timed/i);
    await new Promise(resolve => setImmediate(resolve));
    context.mock.timers.tick(10_000);
    await rejected;
    assert.equal(sample.report().phase, "start-simulator-ui");
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => ["bootstatus", "install", "launch"].includes(call.args[1])));
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
    assertOwnedCleanup(sample.calls);
});

test("a UI ignoring SIGTERM receives only an owned SIGKILL after five seconds", { timeout: 5_000 }, async context => {
    context.mock.timers.enable({ apis: ["setTimeout"] });
    const sample = fixture(context, { startChange: child => {
        child.kill = signal => {
            child.signals.push(signal);
            if (signal === "SIGKILL") {
                queueMicrotask(() => {
                    child.signalCode = signal;
                    child.emit("exit", null, signal);
                });
            }
            return true;
        };
    } });
    const running = sample.run();
    await waitForUiSignal(sample, "SIGTERM");
    context.mock.timers.tick(5_000);
    const report = await running;
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM", "SIGKILL"]);
    assert.equal(report.simulatorUi.status, "stopped");
    assert.equal(report.simulatorUi.cleanup.status, "passed");
    assert.equal(report.simulatorUi.cleanup.signal, "SIGKILL");
    assert.equal(report.status, "passed");
    assertOwnedCleanup(sample.calls);
});

test("a UI ignoring both termination deadlines fails without broad process cleanup", { timeout: 5_000 }, async context => {
    context.mock.timers.enable({ apis: ["setTimeout"] });
    const sample = fixture(context, { startChange: child => {
        child.kill = signal => {
            child.signals.push(signal);
            return true;
        };
    } });
    const rejected = assert.rejects(sample.run(), /Simulator|UI|stop|terminat|timed/i);
    await waitForUiSignal(sample, "SIGTERM");
    context.mock.timers.tick(5_000);
    await waitForUiSignal(sample, "SIGKILL");
    context.mock.timers.tick(5_000);
    await rejected;
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM", "SIGKILL"]);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().simulatorUi.cleanup.status, "failed");
    assert.ok(!sample.calls.some(call => ["killall", "pkill", "open", "ps"].includes(call.command)));
    assertOwnedCleanup(sample.calls);
});

for (const inventory of [
    { runtimes: [], devices: {} },
    { runtimes: [{ identifier: runtimeId, isAvailable: false }], devices: {} },
    { runtimes: [{ identifier: "com.apple.CoreSimulator.SimRuntime.iOS-18-5", isAvailable: true }], devices: {} }
]) {
    test(`rejects missing or unavailable pinned runtime: ${JSON.stringify(inventory.runtimes)}`, async context => {
        const sample = fixture(context, { inventory });
        await assert.rejects(sample.run(), /available iOS 26.0/);
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => ["create", "shutdown", "delete"].includes(call.args[1])));
    });
}

test("rejects inventory without an available compatible iPhone", async context => {
    const sample = fixture(context, { inventory: {
        runtimes: [{ identifier: runtimeId, isAvailable: true }],
        devices: { [runtimeId]: [{ deviceTypeIdentifier: deviceType, isAvailable: false }] }
    } });
    await assert.rejects(sample.run(), /No available iPhone/);
    assert.ok(!sample.calls.some(call => call.args[1] === "create"));
});

test("rejects malformed inventory JSON", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "list" ? { status: 0, stdout: "not json" } : undefined });
    await assert.rejects(sample.run(), SyntaxError);
    assert.equal(sample.report().phase, "select-runtime");
});

test("does not delete an invalid UUID returned by create", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "create" ? { status: 0, stdout: "all" } : undefined });
    await assert.rejects(sample.run(), /valid device UUID/);
    assert.ok(!sample.calls.some(call => ["shutdown", "delete"].includes(call.args[1])));
});

test("restarts only its own device once after a migration failure despite exit zero", async context => {
    const sample = fixture(context, { change: (_, args, calls) => {
        if (args[1] === "bootstatus" && calls.filter(call => call.args[1] === "bootstatus").length === 1) {
            return { status: 0, stdout: bootMigrationFailed };
        }
        if (args[1] === "shutdown") {
            return { status: 0, stdout: "shutdown completed" };
        }
    } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.readiness.status, "passed");
    assert.deepEqual(report.bootAttempts, [
        { number: 1, log: "boot.log", status: "migration-failed", terminalStatus: "3", summary: "Data Migration Failed" },
        { number: 2, log: "boot-retry.log", status: "passed", terminalStatus: "4294967295", summary: "Finished" }
    ]);
    const commands = sample.calls.filter(call => call.command === "xcrun");
    assert.deepEqual(commands.slice(0, 9).map(call => call.args[1]), [
        "list", "create", "bootstatus", "shutdown", "bootstatus", "launch", "terminate", "install", "launch"
    ]);
    for (const index of [2, 4]) {
        assert.deepEqual(commands[index].args, ["simctl", "bootstatus", deviceId, "-b"]);
        assert.equal(commands[index].options.timeout, 240_000);
    }
    assert.equal(commands[3].options.timeout, 15_000);
    assert.match(readFileSync(join(sample.outputDirectory, "boot.log"), "utf8"), /Data Migration Failed/);
    assert.match(readFileSync(join(sample.outputDirectory, "boot-retry.log"), "utf8"), /Finished/);
    assert.match(readFileSync(join(sample.outputDirectory, "boot-restart.log"), "utf8"), /shutdown completed/);
    assertOwnedCleanup(sample.calls, true);
    assert.equal(sample.calls.filter(call => call.args[1] === "create").length, 1);
    assert.ok(!sample.calls.some(call => call.args.includes("erase")));
});

for (const ending of ["\n", "\r\n"]) {
    test(`accepts the final successful boot status with ${JSON.stringify(ending)} line endings`, async context => {
        const output = ("[2026-10-07 16:32:14 +0000] Status=4, isTerminal=NO, Elapsed=01:19.\n\tWaiting on System App\n\n" + bootPassed).replaceAll("\n", ending);
        const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? { status: 0, stdout: output } : undefined });
        await sample.run();
        assert.equal(sample.report().bootAttempts[0].status, "passed");
        assertOwnedCleanup(sample.calls);
    });
}

for (const [name, output] of [
    ["empty", ""],
    ["bare Finished", "Finished"],
    ["bare migration failure", "Data Migration Failed"],
    ["unknown terminal status", bootPassed.replace("4294967295", "5")],
    ["failure code with success summary", bootMigrationFailed.replace("Data Migration Failed", "Finished")],
    ["success code with failure summary", bootPassed.replace("Finished", "Data Migration Failed")],
    ["nonterminal success code", bootPassed.replace("isTerminal=YES", "isTerminal=NO")],
    ["missing summary", bootPassed.replace("\tFinished\n", "")],
    ["trailing nonterminal status", bootPassed + "[2026-10-07 16:32:22 +0000] Status=4, isTerminal=NO, Elapsed=01:27."],
    ["conflicting failure text", bootPassed + "Data Migration Failed\n"],
    ["conflicting terminal results", bootMigrationFailed + bootPassed],
    ["reverse conflicting terminal results", bootPassed + bootMigrationFailed],
    ["duplicate terminal result", bootPassed + bootPassed],
    ["unknown terminal result before success", bootPassed.replace("4294967295", "5") + bootPassed],
    ["unknown terminal result before migration failure", bootPassed.replace("4294967295", "5") + bootMigrationFailed]
]) {
    test(`rejects ${name} boot output without recovery or installation`, async context => {
        const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? { status: 0, stdout: output } : undefined });
        await assert.rejects(sample.run(), /boot did not finish successfully/);
        const report = sample.report();
        assert.equal(report.phase, "boot");
        assert.equal(report.bootAttempts.length, 1);
        assert.equal(report.bootAttempts[0].status, "unrecognized");
        assert.equal(report.readiness, undefined);
        assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
        assertOwnedCleanup(sample.calls);
    });
}

test("stops after a second migration failure without a third boot", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? { status: 0, stdout: bootMigrationFailed } : undefined });
    await assert.rejects(sample.run(), /Data Migration Failed/);
    const report = sample.report();
    assert.equal(report.phase, "boot-retry");
    assert.deepEqual(report.bootAttempts.map(attempt => attempt.status), ["migration-failed", "migration-failed"]);
    assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 2);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assertOwnedCleanup(sample.calls, true);
});

for (const [name, result, status, error] of [
    ["nonzero exit", { status: 17, stderr: "retry failed" }, "command-failed", /retry failed/],
    ["timeout", { status: null, error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" }) }, "command-failed", /ETIMEDOUT/],
    ["unrecognized output", { status: 0, stdout: "unknown boot output" }, "unrecognized", /boot did not finish successfully/]
]) {
    test(`does not retry a failed recovery boot: ${name}`, async context => {
        const sample = fixture(context, { change: (_, args, calls) => {
            if (args[1] === "bootstatus") {
                return calls.filter(call => call.args[1] === "bootstatus").length === 1 ? { status: 0, stdout: bootMigrationFailed } : result;
            }
        } });
        await assert.rejects(sample.run(), error);
        assert.equal(sample.report().phase, "boot-retry");
        assert.deepEqual(sample.report().bootAttempts.map(attempt => attempt.status), ["migration-failed", status]);
        assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 2);
        assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
        assertOwnedCleanup(sample.calls, true);
    });
}

test("does not recover a nonzero command exit even if stdout reports migration failure", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? { status: 17, stdout: bootMigrationFailed } : undefined });
    await assert.rejects(sample.run(), /failed: 17/);
    assert.equal(sample.report().bootAttempts[0].status, "command-failed");
    assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 1);
    assertOwnedCleanup(sample.calls);
});

test("recognizes a migration failure written only to stderr", async context => {
    const sample = fixture(context, { change: (_, args, calls) =>
        args[1] === "bootstatus" && calls.filter(call => call.args[1] === "bootstatus").length === 1 ?
            { status: 0, stdout: "", stderr: bootMigrationFailed } : undefined });
    await sample.run();
    assert.deepEqual(sample.report().bootAttempts.map(attempt => attempt.status), ["migration-failed", "passed"]);
    assert.match(readFileSync(join(sample.outputDirectory, "boot.log"), "utf8"), /Data Migration Failed/);
    assertOwnedCleanup(sample.calls, true);
});

test("recognizes successful boot written only to stderr without changing launch PID parsing", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? { status: 0, stdout: "", stderr: bootPassed } : undefined });
    const report = await sample.run();
    assert.equal(report.bootAttempts[0].status, "passed");
    assert.equal(report.pid, pid);
    assertOwnedCleanup(sample.calls);
});

test("a failed recovery shutdown stops before another boot and still cleans up", async context => {
    const sample = fixture(context, { change: (_, args, calls) => {
        if (args[1] === "bootstatus") {
            return { status: 0, stdout: bootMigrationFailed };
        }
        if (args[1] === "shutdown" && calls.filter(call => call.args[1] === "shutdown").length === 1) {
            return { status: 17, stderr: "recovery shutdown failed" };
        }
    } });
    await assert.rejects(sample.run(), /recovery shutdown failed/);
    assert.equal(sample.report().phase, "restart-after-migration-failure");
    assert.equal(sample.report().bootAttempts.length, 1);
    assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 1);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assertOwnedCleanup(sample.calls, true);
});

test("a readiness failure after recovered boot does not trigger another restart", async context => {
    const sample = fixture(context, { change: (_, args, calls) => {
        if (args[1] === "bootstatus" && calls.filter(call => call.args[1] === "bootstatus").length === 1) {
            return { status: 0, stdout: bootMigrationFailed };
        }
        if (args[1] === "launch" && args.at(-1) === readinessBundleId) {
            return { status: 17, stderr: "Settings failed" };
        }
    } });
    await assert.rejects(sample.run(), /Settings failed/);
    assert.equal(sample.report().phase, "readiness-launch");
    assert.equal(sample.report().readiness.status, "failed");
    assert.deepEqual(sample.report().bootAttempts.map(attempt => attempt.status), ["migration-failed", "passed"]);
    assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 2);
    assert.ok(!sample.calls.some(call => call.args[1] === "install"));
    assertOwnedCleanup(sample.calls, true);
});

for (const command of ["bootstatus", "install", "launch", "io"]) {
    test(`preserves ${command} failure and cleans up owned simulator`, async context => {
        const sample = fixture(context, { change: (_, args) =>
            args[1] === command && (command !== "launch" || args.at(-1) === bundleId) ?
                { status: 17, stdout: "", stderr: `${command} failed` } : undefined });
        await assert.rejects(sample.run(), new RegExp(`${command}.*failed`));
        assert.equal(sample.report().status, "failed");
        assertOwnedCleanup(sample.calls);
        assert.ok(sample.report().failure.includes("17"));
    });
}

for (const command of ["launch", "terminate"]) {
    test(`stops before install when Settings ${command} fails`, async context => {
        const sample = fixture(context, { change: (_, args) =>
            args[1] === command && args.at(-1) === readinessBundleId ? { status: 17, stderr: "Settings failed" } : undefined });
        await assert.rejects(sample.run(), /Settings failed/);
        const report = sample.report();
        assert.equal(report.phase, `readiness-${command}`);
        assert.equal(report.readiness.status, "failed");
        assert.equal(report.readiness.failure, report.failure);
        assert.ok(!sample.calls.some(call => call.args[1] === "install"));
        assert.ok(!sample.calls.some(call => call.args[1] === "launch" && call.args.at(-1) === bundleId));
        assertOwnedCleanup(sample.calls);
        assert.ok(readFileSync(join(sample.outputDirectory, `readiness-${command}.log`), "utf8").includes("Settings failed"));
    });

    test(`reports Settings ${command} timeout as a readiness failure`, async context => {
        const sample = fixture(context, { change: (_, args) =>
            args[1] === command && args.at(-1) === readinessBundleId ? {
                status: null, error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" })
            } : undefined });
        await assert.rejects(sample.run(), /ETIMEDOUT/);
        assert.equal(sample.report().phase, `readiness-${command}`);
        assert.equal(sample.report().readiness.status, "failed");
        assert.ok(!sample.calls.some(call => call.args[1] === "install"));
        assertOwnedCleanup(sample.calls);
        assert.ok(sample.calls.some(call => call.args.at(-1) === join(sample.outputDirectory, "failure.png")));
    });
}

for (const text of [`${readinessBundleId}: 0`, `${readinessBundleId}: -1`, `${bundleId}: ${pid}`, `${readinessBundleId}: 9007199254740993`]) {
    test(`rejects invalid Settings launch PID before install: ${text}`, async context => {
        const sample = fixture(context, { change: (_, args) =>
            args[1] === "launch" && args.at(-1) === readinessBundleId ? { status: 0, stdout: text } : undefined });
        await assert.rejects(sample.run(), /positive PID/);
        assert.equal(sample.report().phase, "readiness-launch");
        assert.equal(sample.report().readiness.status, "failed");
        assert.ok(!sample.calls.some(call => ["install", "terminate"].includes(call.args[1])));
        assertOwnedCleanup(sample.calls);
    });
}

test("keeps successful readiness separate from a NutriFlow launch timeout", async context => {
    const sample = fixture(context, { change: (_, args) =>
        args[1] === "launch" && args.at(-1) === bundleId ? {
            status: null, error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" })
        } : undefined });
    await assert.rejects(sample.run(), /ETIMEDOUT/);
    const report = sample.report();
    assert.equal(report.phase, "launch");
    assert.equal(report.status, "failed");
    assert.equal(report.readiness.status, "passed");
    assert.equal(report.pid, undefined);
    assertOwnedCleanup(sample.calls);
});

test("captures bundle and system launch diagnostics even when Settings never opens", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "launch" && args.at(-1) === readinessBundleId) {
            return { status: 17, stderr: "not ready" };
        }
        if (args[1] === "spawn" && args[3] === "log" && args.at(-1).includes("com.apple.datamigrator")) {
            return { status: 0, stdout: "SpringBoard: com.apple.Preferences readiness diagnostic" };
        }
    } });
    await assert.rejects(sample.run(), /not ready/);
    const logs = sample.calls.filter(call => call.args[1] === "spawn" && call.args[3] === "log");
    assert.equal(logs.length, 3);
    assert.equal(logs[0].args.at(-1), 'process == "NutriFlow.Mobile"');
    assert.deepEqual(logs[1].args.slice(0, -1), [
        "simctl", "spawn", deviceId, "log", "show", "--last", "20m", "--style", "compact", "--predicate"
    ]);
    assert.equal(logs[1].args.at(-1),
        `eventMessage CONTAINS[c] "${bundleId}" OR eventMessage CONTAINS[c] "${readinessBundleId}" OR ` +
        '(process == "com.apple.datamigrator" AND (eventMessage CONTAINS[c] "error" OR eventMessage CONTAINS[c] "failed" OR eventMessage CONTAINS[c] "watchdog"))');
    assert.ok(!logs[1].args.at(-1).includes("process IN"));
    assert.equal(logs[1].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(logs[1].options.timeout, 30_000);
    assert.deepEqual(logs[2].args.slice(0, -1), [
        "simctl", "spawn", deviceId, "log", "show", "--last", "20m", "--style", "compact", "--predicate"
    ]);
    assert.match(logs[2].args.at(-1), /backboardd/);
    assert.match(logs[2].args.at(-1), /SpringBoard/);
    assert.match(logs[2].args.at(-1), /display/);
    assertOwnedCleanup(sample.calls);
    assert.match(readFileSync(join(sample.outputDirectory, "launch-system.log"), "utf8"), /com.apple.Preferences readiness diagnostic/);
});

test("captures owned display ports, bounded boot services and filtered Simulator host diagnostics", async context => {
    const sample = fixture(context);
    await sample.run();
    const displays = sample.calls.filter(call => call.args[1] === "io" && call.args[3] === "enumerate");
    assert.equal(displays.length, 1);
    assert.deepEqual(displays[0].args, ["simctl", "io", deviceId, "enumerate"]);
    assert.match(readFileSync(join(sample.outputDirectory, "display-ports.log"), "utf8"), /owned simulator/);
    const bootLog = sample.calls.find(call => call.args[1] === "spawn" && call.args[3] === "log" && call.args.at(-1).includes("backboardd"));
    assert.ok(bootLog);
    assert.equal(bootLog.args[2], deviceId);
    const failures = '(eventMessage CONTAINS[c] "error" OR eventMessage CONTAINS[c] "failed" OR eventMessage CONTAINS[c] "watchdog" OR eventMessage CONTAINS[c] "display")';
    assert.equal(bootLog.args.at(-1), `(process IN {"backboardd", "SpringBoard"} AND ${failures})`);
    const hostLogs = sample.calls.filter(call => call.command === "log");
    assert.equal(hostLogs.length, 1);
    assert.deepEqual(hostLogs[0].args.slice(0, -1), ["show", "--last", "20m", "--style", "compact", "--predicate"]);
    assert.equal(hostLogs[0].args.at(-1), `(process == "Simulator" AND processID == ${simulatorUiPid} AND ${failures})`);
    assert.match(readFileSync(join(sample.outputDirectory, "simulator-host.log"), "utf8"), /Simulator host diagnostics/);
    for (const call of sample.calls.filter(call => call.args[1] === "spawn" || call.args[1] === "io")) {
        assert.equal(call.args[2], deviceId);
    }
    assertOwnedCleanup(sample.calls);
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
});

for (const [name, file, matches] of [
    ["display ports", "display-ports.log", (command, args) => command === "xcrun" && args[1] === "io" && args[3] === "enumerate"],
    ["boot services", "boot-system.log", (command, args) => command === "xcrun" && args[1] === "spawn" && args[3] === "log" && args.at(-1).includes("backboardd")],
    ["Simulator host", "simulator-host.log", command => command === "log"]
]) {
    test(`partial ${name} diagnostic failure preserves the original error and both owned cleanups`, async context => {
        const sample = fixture(context, { change: (command, args) => {
            if (args[1] === "install") {
                return { status: 17, stderr: "primary app install error" };
            }
            if (matches(command, args)) {
                return { status: null, stdout: `partial ${name} diagnostic`, error: Object.assign(new Error("capture buffer exhausted"), { code: "ENOBUFS" }) };
            }
        } });
        await assert.rejects(sample.run(), /primary app install error/);
        assert.match(sample.report().failure, /primary app install error/);
        assert.match(readFileSync(join(sample.outputDirectory, file), "utf8"), new RegExp(`partial ${name} diagnostic`));
        assert.match(readFileSync(join(sample.outputDirectory, "diagnostic-errors.log"), "utf8"), /ENOBUFS/);
        assertOwnedCleanup(sample.calls);
        assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
    });
}

for (const code of ["ETIMEDOUT", "ENOBUFS"]) {
    test(`launch-log ${code} preserves the app failure and any partial output`, async context => {
        const sample = fixture(context, { change: (_, args) => {
            if (args[1] === "launch" && args.at(-1) === bundleId) {
                return { status: 17, stderr: "original app error" };
            }
            if (args[1] === "spawn" && args[3] === "log" && args.at(-1).includes("com.apple.datamigrator")) {
                return { status: null, stdout: "partial launch diagnostics", error: Object.assign(new Error("capture failed"), { code }) };
            }
        } });
        await assert.rejects(sample.run(), /original app error/);
        assert.match(sample.report().failure, /original app error/);
        assert.match(readFileSync(join(sample.outputDirectory, "launch-system.log"), "utf8"), /partial launch diagnostics/);
        assert.match(readFileSync(join(sample.outputDirectory, "diagnostic-errors.log"), "utf8"), new RegExp(code));
        assertOwnedCleanup(sample.calls);
    });
}

test("reports a command timeout and still attempts bounded cleanup", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "bootstatus" ? {
        status: null, stdout: "", error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" })
    } : undefined });
    await assert.rejects(sample.run(), /ETIMEDOUT/);
    assertOwnedCleanup(sample.calls);
    assert.ok(sample.calls.some(call => call.args.at(-1) === join(sample.outputDirectory, "failure.png")));
    assert.ok(sample.calls.some(call => call.args[1] === "spawn" && call.args[3] === "log"));
    assert.equal(sample.calls.find(call => call.args[1] === "bootstatus").options.timeout, 240_000);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
});

for (const text of [`${bundleId}: 0`, `${bundleId}: -1`, "another.bundle: 1234", `${bundleId}: 9007199254740993`]) {
    test(`rejects invalid launch PID: ${text}`, async context => {
        const sample = fixture(context, { change: (_, args) =>
            args[1] === "launch" && args.at(-1) === bundleId ? { status: 0, stdout: text } : undefined });
        await assert.rejects(sample.run(), /positive PID/);
        assertOwnedCleanup(sample.calls);
    });
}

for (const deadAt of [1, 2, 3]) {
    test(`fails if the app exits at process check ${deadAt}`, async context => {
        const sample = fixture(context, { jobs: count => count >= deadAt ? `-\t1\tUIKitApplication:${bundleId}[1234]\n` : undefined });
        await assert.rejects(sample.run(), /no longer running/);
        assert.equal(sample.report().status, "failed");
        assertOwnedCleanup(sample.calls);
        assert.ok(sample.calls.some(call => call.args.at(-1) === join(sample.outputDirectory, "failure.png")));
    });
}

for (const jobs of [`999\t0\tUIKitApplication:${bundleId}[1234]`, `${pid}\t0\tUIKitApplication:${bundleId}.other[1234]`]) {
    test(`does not accept a different process or application: ${jobs}`, async context => {
        const sample = fixture(context, { jobs: () => jobs });
        await assert.rejects(sample.run(), /no longer running/);
    });
}

test("rejects an invalid screenshot despite successful simctl exit", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "io" && args[3] === "screenshot") {
            writeFileSync(args.at(-1), "not a PNG");
            return { status: 0, stdout: "" };
        }
    } });
    await assert.rejects(sample.run(), /PNG screenshot/);
    assertOwnedCleanup(sample.calls);
});

test("diagnostic and cleanup failures do not hide the original install failure", async context => {
    const sample = fixture(context, { change: (_, args) => {
        if (["install", "help", "spawn", "shutdown", "delete"].includes(args[1])) {
            return { status: 17, stdout: "", stderr: `${args[1]} failed` };
        }
    } });
    await assert.rejects(sample.run(), /simctl install/);
    assert.match(sample.report().failure, /simctl install/);
    assert.match(readFileSync(join(sample.outputDirectory, "diagnostic-errors.log"), "utf8"), /simctl help/);
    assertOwnedCleanup(sample.calls);
});

test("cleanup failure also fails an otherwise successful smoke", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "delete" ? { status: 7, stderr: "delete failed" } : undefined });
    await assert.rejects(sample.run(), /simctl delete/);
    assert.equal(sample.report().status, "failed");
});

test("accepts an already-shutdown response only after uniquely verifying the owned device state", async context => {
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "shutdown") {
            assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
            assert.equal(sample.uiProcesses[0].signalCode, "SIGTERM");
            return alreadyShutdown;
        }
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(shutdownInventory) };
    } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.deviceCleanup.status, "passed");
    assert.equal(report.deviceCleanup.deviceId, deviceId);
    assert.equal(report.deviceCleanup.shutdown.status, "passed");
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, true);
    assert.equal(report.deviceCleanup.shutdown.verifiedState, "Shutdown");
    assert.match(report.deviceCleanup.shutdown.commandFailure, /149/);
    assert.match(report.deviceCleanup.shutdown.commandFailure, /code=405/);
    assert.deepEqual(report.deviceCleanup.delete, { status: "passed" });
    const queries = sample.calls.filter(call => isCleanupStateQuery(call.command, call.args, sample.calls.slice(0, sample.calls.indexOf(call))));
    assert.equal(queries.length, 1);
    assert.deepEqual(queries[0].args, ["simctl", "list", "devices", "--json"]);
    assert.equal(queries[0].options.timeout, 15_000);
    assert.ok(sample.calls.findIndex(call => call.args[1] === "shutdown") < sample.calls.indexOf(queries[0]));
    assert.ok(sample.calls.indexOf(queries[0]) < sample.calls.findIndex(call => call.args[1] === "delete"));
    assert.deepEqual(JSON.parse(readFileSync(join(sample.outputDirectory, "cleanup-device-state.json"), "utf8")), shutdownInventory);
    assert.match(readFileSync(join(sample.outputDirectory, "cleanup-shutdown.log"), "utf8"), /current state: Shutdown/);
    assert.ok(readFileSync(join(sample.outputDirectory, "cleanup-delete.log"), "utf8").length > 0);
    assertOwnedCleanup(sample.calls);
    assert.deepEqual(sample.uiProcesses[0].signals, ["SIGTERM"]);
});

test("accepts CRLF already-shutdown stderr and ignores unrelated device states", async context => {
    const inventory = { devices: {
        [runtimeId]: [{ udid: deviceId, state: "Shutdown" }, { udid: "99999999-2222-3333-4444-555555555555", state: "Booted" }],
        anotherRuntime: [{ udid: "88888888-2222-3333-4444-555555555555", state: "Shutdown" }]
    } };
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "shutdown") return { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replaceAll("\n", "\r\n") };
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(inventory) };
    } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, true);
    assert.equal(report.deviceCleanup.shutdown.verifiedState, "Shutdown");
    assertOwnedCleanup(sample.calls);
});

for (const [name, response] of [
    ["another exit status", { ...alreadyShutdown, status: 17 }],
    ["another error domain", { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replace("com.apple.CoreSimulator.SimError", "com.apple.OtherError") }],
    ["domain suffix", { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replace("SimError", "SimErrorExtra") }],
    ["missing error domain", { ...alreadyShutdown, stderr: "Unable to shutdown device in current state: Shutdown" }],
    ["another error code", { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replace("code=405", "code=406") }],
    ["error code suffix", { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replace("code=405", "code=4050") }],
    ["another current state", { ...alreadyShutdown, stderr: alreadyShutdown.stderr.replace("state: Shutdown", "state: Booted") }],
    ["missing state error", { ...alreadyShutdown, stderr: "An error was encountered processing the command (domain=com.apple.CoreSimulator.SimError, code=405):\nAn unrelated shutdown failure" }],
    ["stdout only", { ...alreadyShutdown, stdout: alreadyShutdown.stderr, stderr: "" }],
    ["extra stderr prefix", { ...alreadyShutdown, stderr: `unexpected failure\n${alreadyShutdown.stderr}` }],
    ["extra stderr suffix", { ...alreadyShutdown, stderr: `${alreadyShutdown.stderr}another failure\n` }],
    ["subprocess error alongside status 149", { ...alreadyShutdown, error: Object.assign(new Error("subprocess timed out"), { code: "ETIMEDOUT" }) }]
]) {
    test(`does not reinterpret a shutdown failure with ${name}`, async context => {
        const sample = fixture(context, { change: (_, args) => args[1] === "shutdown" ? response : undefined });
        await assert.rejects(sample.run(), error => {
            assert.match(error.message, /simctl shutdown/);
            assert.equal(error.exitStatus, response.status);
            assert.equal(error.stderr, response.stderr);
            assert.equal(error.commandError, response.error ?? null);
            return true;
        });
        const report = sample.report();
        assert.equal(report.deviceCleanup.status, "failed");
        assert.equal(report.deviceCleanup.shutdown.status, "failed");
        assert.match(report.deviceCleanup.shutdown.failure, /simctl shutdown/);
        assert.deepEqual(report.deviceCleanup.delete, { status: "passed" });
        assert.ok(!sample.calls.some((call, index) => isCleanupStateQuery(call.command, call.args, sample.calls.slice(0, index))));
        assertOwnedCleanup(sample.calls);
    });
}

test("a successful shutdown does not need state verification despite warning text", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "shutdown" ? { ...alreadyShutdown, status: 0 } : undefined });
    const report = await sample.run();
    assert.deepEqual(report.deviceCleanup.shutdown, { status: "passed", alreadyShutdown: false });
    assert.ok(!sample.calls.some((call, index) => isCleanupStateQuery(call.command, call.args, sample.calls.slice(0, index))));
    assertOwnedCleanup(sample.calls);
});

for (const [name, inventory] of [
    ["owned device still Booted", { devices: { [runtimeId]: [{ udid: deviceId, state: "Booted" }] } }],
    ["owned device state missing", { devices: { [runtimeId]: [{ udid: deviceId }] } }],
    ["owned device state lowercased", { devices: { [runtimeId]: [{ udid: deviceId, state: "shutdown" }] } }],
    ["another shutdown UUID", { devices: { [runtimeId]: [{ udid: "99999999-2222-3333-4444-555555555555", state: "Shutdown" }] } }],
    ["missing device", { devices: { [runtimeId]: [] } }],
    ["duplicate owned UUID", { devices: { [runtimeId]: [{ udid: deviceId, state: "Shutdown" }, { udid: deviceId, state: "Shutdown" }] } }],
    ["duplicate UUID across runtimes", { devices: { [runtimeId]: [{ udid: deviceId, state: "Shutdown" }], other: [{ udid: deviceId, state: "Shutdown" }] } }],
    ["missing device inventory", {}],
    ["array device inventory", { devices: [{ udid: deviceId, state: "Shutdown" }] }],
    ["malformed device inventory", { devices: { [runtimeId]: "not an array" } }]
]) {
    test(`refuses already-shutdown acceptance for ${name}`, async context => {
        const sample = fixture(context, { change: (command, args, calls) => {
            if (args[1] === "shutdown") return alreadyShutdown;
            if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(inventory) };
        } });
        await assert.rejects(sample.run(), /simctl shutdown/);
        const report = sample.report();
        assert.equal(report.deviceCleanup.shutdown.status, "failed");
        assert.match(report.deviceCleanup.shutdown.failure, /149/);
        assert.ok(report.deviceCleanup.shutdown.verificationFailure.length > 0);
        assert.equal(report.deviceCleanup.status, "failed");
        assert.deepEqual(report.deviceCleanup.delete, { status: "passed" });
        assertOwnedCleanup(sample.calls);
    });
}

for (const [name, response, expected] of [
    ["invalid JSON", { status: 0, stdout: "not JSON" }, /JSON|Unexpected|token/i],
    ["failed state query", { status: 17, stderr: "verification unavailable" }, /verification unavailable/],
    ["timed-out state query", { status: null, error: Object.assign(new Error("verification timed out"), { code: "ETIMEDOUT" }) }, /ETIMEDOUT/]
]) {
    test(`keeps shutdown failure and records ${name} without retrying cleanup`, async context => {
        const sample = fixture(context, { change: (command, args, calls) => {
            if (args[1] === "shutdown") return alreadyShutdown;
            if (isCleanupStateQuery(command, args, calls)) return response;
        } });
        await assert.rejects(sample.run(), /simctl shutdown/);
        const report = sample.report();
        assert.equal(report.deviceCleanup.shutdown.status, "failed");
        assert.match(report.deviceCleanup.shutdown.verificationFailure, expected);
        assert.match(report.failure, /simctl shutdown/);
        assert.deepEqual(report.deviceCleanup.delete, { status: "passed" });
        assertOwnedCleanup(sample.calls);
    });
}

test("confirmed shutdown preserves an earlier NutriFlow failure", async context => {
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "install") return { status: 17, stderr: "original NutriFlow install error" };
        if (args[1] === "shutdown") return alreadyShutdown;
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(shutdownInventory) };
    } });
    await assert.rejects(sample.run(), /original NutriFlow install error/);
    const report = sample.report();
    assert.equal(report.status, "failed");
    assert.match(report.failure, /original NutriFlow install error/);
    assert.equal(report.deviceCleanup.status, "passed");
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, true);
    assertOwnedCleanup(sample.calls);
});

test("unverified shutdown does not overwrite an earlier readiness failure", async context => {
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "launch" && args.at(-1) === readinessBundleId) return { status: 17, stderr: "original Settings readiness error" };
        if (args[1] === "shutdown") return alreadyShutdown;
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify({ devices: {} }) };
    } });
    await assert.rejects(sample.run(), /original Settings readiness error/);
    const report = sample.report();
    assert.equal(report.deviceCleanup.status, "failed");
    assert.equal(report.deviceCleanup.shutdown.status, "failed");
    assert.ok(report.deviceCleanup.shutdown.verificationFailure.length > 0);
    assert.match(report.failure, /original Settings readiness error/);
    assertOwnedCleanup(sample.calls);
});

test("a failed delete remains fatal even after confirming the device was already shut down", async context => {
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "shutdown") return alreadyShutdown;
        if (args[1] === "delete") return { status: 17, stderr: "owned device delete error" };
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(shutdownInventory) };
    } });
    await assert.rejects(sample.run(), /owned device delete error/);
    const report = sample.report();
    assert.equal(report.deviceCleanup.shutdown.status, "passed");
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, true);
    assert.equal(report.deviceCleanup.delete.status, "failed");
    assert.match(report.deviceCleanup.delete.failure, /owned device delete error/);
    assert.equal(report.deviceCleanup.status, "failed");
    assert.match(readFileSync(join(sample.outputDirectory, "cleanup-delete.log"), "utf8"), /owned device delete error/);
    assertOwnedCleanup(sample.calls);
});

test("does not reinterpret the same status149 error from delete as successful cleanup", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "delete" ? alreadyShutdown : undefined });
    await assert.rejects(sample.run(), /simctl delete.*149/);
    const report = sample.report();
    assert.equal(report.status, "failed");
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, false);
    assert.equal(report.deviceCleanup.delete.status, "failed");
    assert.match(report.deviceCleanup.delete.failure, /simctl delete.*149/);
    assert.ok(!sample.calls.some((call, index) => isCleanupStateQuery(call.command, call.args, sample.calls.slice(0, index))));
    assertOwnedCleanup(sample.calls);
});

test("already-shutdown verification is not applied to a failed migration-recovery shutdown", async context => {
    const sample = fixture(context, { change: (_, args, calls) => {
        if (args[1] === "bootstatus") return { status: 0, stdout: bootMigrationFailed };
        if (args[1] === "shutdown" && calls.filter(call => call.args[1] === "shutdown").length === 1) return alreadyShutdown;
    } });
    await assert.rejects(sample.run(), /simctl shutdown.*149/);
    const report = sample.report();
    assert.equal(report.phase, "restart-after-migration-failure");
    assert.equal(report.bootAttempts.length, 1);
    assert.equal(report.deviceCleanup.shutdown.alreadyShutdown, false);
    assert.ok(!sample.calls.some(call => ["install", "launch"].includes(call.args[1])));
    assert.equal(sample.calls.filter(call => call.args[1] === "bootstatus").length, 1);
    assertOwnedCleanup(sample.calls, true);
});

test("refuses an existing output directory instead of reusing an old screenshot", async context => {
    const sample = fixture(context);
    mkdirSync(sample.outputDirectory);
    await assert.rejects(sample.run(), /EEXIST/);
    assert.equal(sample.calls.length, 0);
});

test("does not clean up any device if create fails", async context => {
    const sample = fixture(context, { change: (_, args) => args[1] === "create" ? { status: 17, stderr: "create failed" } : undefined });
    await assert.rejects(sample.run(), /simctl create/);
    assert.ok(!sample.calls.some(call => ["shutdown", "delete"].includes(call.args[1])));
});

test("rejects an empty executable in the built plist before creating a device", async context => {
    const sample = fixture(context, { change: (command, args) =>
        command === "plutil" && args[1] === "CFBundleExecutable" ? { status: 0, stdout: "" } : undefined });
    await assert.rejects(sample.run(), /invalid bundle identifier or executable/);
    assert.ok(!sample.calls.some(call => call.args[1] === "create"));
});

test("a failed process-list command is not treated as a successful launch", async context => {
    const sample = fixture(context, { change: (_, args) =>
        args[1] === "spawn" && args[3] === "launchctl" ? { status: 9, stderr: "spawn failed" } : undefined });
    await assert.rejects(sample.run(), /simctl spawn/);
    assertOwnedCleanup(sample.calls);
});

test("lost diagnostic directory neither blocks cleanup nor hides the install error", async context => {
    context.mock.method(console, "error", () => {});
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "install") {
            renameSync(sample.outputDirectory, `${sample.outputDirectory}-moved`);
            return { status: 17, stderr: "original install error" };
        }
    } });
    await assert.rejects(sample.run(), /original install error/);
    assertOwnedCleanup(sample.calls);
});

test("a diagnostic write failure cannot make an otherwise successful run green", async context => {
    context.mock.method(console, "error", () => {});
    const sample = fixture(context, { change: (_, args) => {
        if (args[1] === "install") {
            mkdirSync(join(sample.outputDirectory, "app-system.log"));
        }
    } });
    await assert.rejects(sample.run(), /EISDIR/);
    assert.equal(sample.report().status, "failed");
    assertOwnedCleanup(sample.calls);
});

test("workflow time budget includes recovery boot, verified already-shutdown cleanup and reserve", async context => {
    const sample = fixture(context, { change: (command, args, calls) => {
        if (args[1] === "bootstatus" && calls.filter(call => call.args[1] === "bootstatus").length === 1) {
            return { status: 0, stdout: bootMigrationFailed };
        }
        if (args[1] === "shutdown" && calls.filter(call => call.args[1] === "shutdown").length === 2) return alreadyShutdown;
        if (isCleanupStateQuery(command, args, calls)) return { status: 0, stdout: JSON.stringify(shutdownInventory) };
    } });
    await sample.run();
    assert.equal(sample.report().bootAttempts.length, 2);
    assert.equal(sample.report().deviceCleanup.shutdown.alreadyShutdown, true);
    const workflow = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    const limit = Number(/id: ios_smoke\s+timeout-minutes: (\d+)/.exec(workflow)?.[1]) * 60_000;
    const commandBudget = sample.calls.reduce((total, call) => total + call.options.timeout, 0);
    const pauseBudget = sample.pauses.reduce((total, milliseconds) => total + milliseconds, 0);
    assert.ok(Number.isFinite(limit));
    const totalBudget = commandBudget + pauseBudget + 10_000 + 5_000 + 5_000 + 30_000 + 30_000;
    assert.ok(totalBudget < limit, `Required budget ${totalBudget}ms exceeds workflow limit ${limit}ms`);
});

test("workflow invokes the tested script and uploads startup diagnostics even after smoke failure", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    assert.ok(workflow.includes("node --test scripts/tests/ios-simulator.test.mjs"));
    assert.ok(workflow.includes("node scripts/test-ios-simulator.mjs"));
    assert.ok(workflow.includes("steps.ios_smoke.outcome == 'success' || steps.ios_smoke.outcome == 'failure'"));
    assert.ok(workflow.includes("!cancelled()"));
    assert.ok(workflow.includes("path: ${{ runner.temp }}/ios-smoke/"));
    assert.ok(workflow.includes("name: nutriflow-ios-startup-${{ github.run_id }}-${{ github.run_attempt }}"));
});
