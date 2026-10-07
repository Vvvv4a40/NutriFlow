import assert from "node:assert/strict";
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
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=", "base64");

function fixture(context, { change = () => undefined, jobs, inventory } = {}) {
    const directory = mkdtempSync(join(tmpdir(), "nutriflow-ios-smoke-"));
    context.after(() => rmSync(directory, { recursive: true, force: true }));
    const bundlePath = join(directory, "app with spaces.app");
    const outputDirectory = join(directory, "output with spaces");
    mkdirSync(bundlePath);
    const calls = [];
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
        if (command === "plutil") {
            stdout = args[1] === "CFBundleIdentifier" ? bundleId : "NutriFlow.Mobile";
        } else {
            assert.equal(command, "xcrun");
            assert.equal(args[0], "simctl");
            switch (args[1]) {
                case "list": stdout = JSON.stringify(data); break;
                case "create": stdout = deviceId; break;
                case "launch":
                    stdout = args.at(-1) === readinessBundleId ? `${readinessBundleId}: ${readinessPid}\n` : `${bundleId}: ${pid}\n`;
                    break;
                case "spawn":
                    if (args[3] === "launchctl") {
                        processChecks++;
                        stdout = jobs?.(processChecks) ?? `PID Status Label\n${pid}\t0\tUIKitApplication:${bundleId}[1234]\n`;
                    }
                    break;
                case "io": writeFileSync(args.at(-1), png); break;
                default: assert.ok(["bootstatus", "terminate", "install", "help", "shutdown", "delete"].includes(args[1]));
            }
        }
        return { status: 0, stdout, stderr: "" };
    }

    return {
        calls, pauses, outputDirectory, bundlePath,
        run: () => testIosSimulator({ bundlePath, outputDirectory, execute, pause: async milliseconds => pauses.push(milliseconds) }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

function assertOwnedCleanup(calls) {
    const cleanup = calls.filter(call => ["shutdown", "delete"].includes(call.args[1]));
    assert.deepEqual(cleanup.map(call => call.args), [
        ["simctl", "shutdown", deviceId], ["simctl", "delete", deviceId]
    ]);
}

test("runs an isolated iPhone, checks PID around a PNG and deletes only its own device", async context => {
    const sample = fixture(context);
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.runtimeIdentifier, runtimeId);
    assert.equal(report.deviceTypeIdentifier, deviceType);
    assert.equal(report.pid, pid);
    assert.equal(report.screenshot, "startup.png");
    assert.deepEqual(report.readiness, { bundleIdentifier: readinessBundleId, status: "passed", pid: readinessPid });
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
    assert.equal(commands[3].options.timeout, 30_000);
    assert.equal(commands[4].options.timeout, 30_000);
    assert.deepEqual(commands[5].args, ["simctl", "install", deviceId, sample.bundlePath]);
    assert.equal(commands[6].options.timeout, 60_000);
    assert.deepEqual(commands[6].args, ["simctl", "launch", "--terminate-running-process",
        `--stdout=${join(sample.outputDirectory, "app-stdout.log")}`,
        `--stderr=${join(sample.outputDirectory, "app-stderr.log")}`, deviceId, bundleId]);
    for (const call of sample.calls) {
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 240_000);
        assert.equal(call.options.shell, false);
        assert.equal(call.options.killSignal, "SIGKILL");
        assert.ok(!call.args.some(arg => ["booted", "all", "unavailable"].includes(arg)));
    }
    assertOwnedCleanup(sample.calls);
    assert.equal(sample.report().status, "passed");
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
        if (args[1] === "spawn" && args[3] === "log" && args.at(-1).includes("SpringBoard")) {
            return { status: 0, stdout: "SpringBoard readiness diagnostic" };
        }
    } });
    await assert.rejects(sample.run(), /not ready/);
    const logs = sample.calls.filter(call => call.args[1] === "spawn" && call.args[3] === "log");
    assert.equal(logs.length, 2);
    assert.equal(logs[0].args.at(-1), 'process == "NutriFlow.Mobile"');
    assert.deepEqual(logs[1].args.slice(0, -1), [
        "simctl", "spawn", deviceId, "log", "show", "--last", "10m", "--style", "compact", "--predicate"
    ]);
    assert.equal(logs[1].args.at(-1),
        `eventMessage CONTAINS[c] "${bundleId}" OR eventMessage CONTAINS[c] "${readinessBundleId}" OR ` +
        'process IN {"SpringBoard", "runningboardd", "launchd", "backboardd"}');
    assert.equal(logs[1].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(logs[1].options.timeout, 30_000);
    assertOwnedCleanup(sample.calls);
    assert.match(readFileSync(join(sample.outputDirectory, "launch-system.log"), "utf8"), /SpringBoard readiness diagnostic/);
});

for (const code of ["ETIMEDOUT", "ENOBUFS"]) {
    test(`launch-log ${code} preserves the app failure and any partial output`, async context => {
        const sample = fixture(context, { change: (_, args) => {
            if (args[1] === "launch" && args.at(-1) === bundleId) {
                return { status: 17, stderr: "original app error" };
            }
            if (args[1] === "spawn" && args[3] === "log" && args.at(-1).includes("SpringBoard")) {
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
        if (args[1] === "io") {
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

test("workflow time budget includes commands, pauses, failure PNG and cleanup reserve", async context => {
    const sample = fixture(context);
    await sample.run();
    const workflow = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    const limit = Number(/id: ios_smoke\s+timeout-minutes: (\d+)/.exec(workflow)?.[1]) * 60_000;
    const commandBudget = sample.calls.reduce((total, call) => total + call.options.timeout, 0);
    const pauseBudget = sample.pauses.reduce((total, milliseconds) => total + milliseconds, 0);
    assert.ok(Number.isFinite(limit));
    assert.ok(commandBudget + pauseBudget + 30_000 + 30_000 < limit);
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
