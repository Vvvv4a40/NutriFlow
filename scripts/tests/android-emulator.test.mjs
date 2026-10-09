import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import { testAndroidEmulator } from "../test-android-emulator.mjs";

const packageId = "com.nutriflow.app";
const serial = "emulator-5556";
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=", "base64");

function node(text, extra = "") {
    return `<node text="${text}" package="${packageId}" class="android.widget.TextView" enabled="true" bounds="[20,200][300,250]" ${extra} />`;
}
function hierarchy(screen) {
    const diary = node("Дневник питания") + node("Съедено за день") +
        node("Smoke", 'clickable="true"').replace("android.widget.TextView", "android.widget.EditText") +
        node("Добавить еду", 'clickable="true"');
    const screens = {
        welcome: node("Питание под вашим контролем") + node("СОЗДАТЬ ПРОФИЛЬ", 'clickable="true"'),
        form: node("Локальный профиль") + node("Создать", 'clickable="true"') + node("Имя профиля").replace("android.widget.TextView", "android.widget.EditText"),
        filled: node("Локальный профиль") + node("Создать", 'clickable="true"') + node("Smoke").replace("android.widget.TextView", "android.widget.EditText"),
        diary,
        "busy-diary": diary + node("Выполняется…"),
        "disabled-diary": diary.replaceAll('enabled="true"', 'enabled="false"'),
        splash: node("NutriFlow")
    };
    return `<?xml version="1.0"?><hierarchy rotation="0">${screens[screen]}</hierarchy>`;
}

function fixture(context, { change = () => undefined, startFailure = false, welcome = "welcome", persisted = true,
    consoleLineEnding = "\n", extraConsoleLine = "", diaryScreen = "diary", diarySequence = [] } = {}) {
    const runnerTemp = mkdtempSync(join(tmpdir(), "nutriflow-android-smoke-test-"));
    context.after(() => rmSync(runnerTemp, { recursive: true, force: true }));
    const apkPath = join(runnerTemp, "verified APK.apk");
    const outputDirectory = join(runnerTemp, "output with spaces");
    const sdkDirectory = join(runnerTemp, "Android SDK");
    mkdirSync(sdkDirectory);
    writeFileSync(apkPath, "fixture APK, not an actual Android package");
    const calls = [];
    const starts = [];
    const diarySnapshots = [];
    const screenshots = [];
    const child = new EventEmitter();
    Object.assign(child, { pid: startFailure ? undefined : 4000, exitCode: null, signalCode: null,
        stdout: new PassThrough(), stderr: new PassThrough(), signals: [], unref: () => {} });
    child.kill = signal => {
        child.signals.push(signal);
        queueMicrotask(() => { child.signalCode = signal; child.emit("exit", null, signal); });
        return true;
    };
    let avdName;
    let screen = welcome;
    let launches = 0;
    let time = 0;
    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, options);
        if (changed) return changed;
        let stdout = "";
        if (command.endsWith("avdmanager")) avdName = args[args.indexOf("-n") + 1];
        else if (command.endsWith("adb")) {
            if (args[0] === "devices") stdout = "List of devices attached\n";
            else {
                assert.deepEqual(args.slice(0, 2), ["-s", serial]);
                const operation = args.slice(2);
                if (operation[0] === "emu") stdout = `${avdName}${consoleLineEnding}OK${consoleLineEnding}${extraConsoleLine}`;
                else if (operation[0] === "install") stdout = "Success\n";
                else if (operation[0] === "exec-out") {
                    screenshots.push({ screen, diarySnapshots: [...diarySnapshots] });
                    stdout = png;
                }
                else if (operation[0] === "shell") {
                    if (operation[1] === "getprop") stdout = "1";
                    else if (operation[1] === "pidof") stdout = String(5000 + launches);
                    else if (operation[1] === "cmd") stdout = `priority=0 preferredOrder=0 match=0x108000\n${packageId}/crc123.MainActivity\n`;
                    else if (operation[1] === "am" && operation[2] === "start") {
                        launches++;
                        if (launches > 1) screen = persisted ? "diary" : "welcome";
                        stdout = "Starting: Intent {}\nStatus: ok\nActivity: com.nutriflow.app/crc123.MainActivity\n";
                    } else if (operation[1] === "cat") {
                        const snapshot = screen === "diary" ? diarySequence.shift() ?? diaryScreen : screen;
                        if (screen === "diary") diarySnapshots.push(snapshot);
                        stdout = hierarchy(snapshot);
                    }
                    else if (operation[1] === "input" && operation[2] === "tap") {
                        assert.deepEqual(operation.slice(3), ["160", "225"]);
                        if (screen === "welcome") screen = "form";
                        else if (screen === "filled") screen = "diary";
                    } else if (operation[1] === "input" && operation[2] === "text") {
                        assert.equal(screen, "form");
                        assert.equal(operation[3], "Smoke");
                        screen = "filled";
                    }
                }
            }
        } else assert.ok(command.endsWith("emulator"));
        return { status: 0, stdout, stderr: "" };
    }
    return {
        runnerTemp, outputDirectory, apkPath, sdkDirectory, calls, starts, child, diarySnapshots, screenshots,
        run: options => testAndroidEmulator({ apkPath, outputDirectory, sdkDirectory, runnerTemp, execute,
            start: (command, args, settings) => {
                starts.push({ command, args, settings });
                queueMicrotask(() => child.emit(startFailure ? "error" : "spawn", ...(startFailure ? [new Error("emulator ENOENT")] : [])));
                return child;
            },
            pause: async milliseconds => { time += milliseconds; }, now: () => time, ...options }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

test("installs and launches the selected emulator, creates a profile by UI bounds and verifies it after restart", async context => {
    const sample = fixture(context);
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.installation, "passed");
    assert.equal(report.launch, "passed");
    assert.deepEqual(report.profile, { name: "Smoke", creation: "passed", persistence: "passed" });
    assert.match(report.apkSha256, /^[a-f0-9]{64}$/);
    assert.equal(report.cleanup, "passed");
    assert.equal(sample.starts.length, 1);
    assert.equal(sample.starts[0].settings.detached, false);
    assert.ok(sample.starts[0].args.includes("-no-window"));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    assert.ok(!readdirSync(sample.runnerTemp).some(name => name.startsWith("nutriflow-android-emulator-")));
    for (const call of sample.calls) {
        assert.equal(call.options.shell, false);
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 150_000);
    }
    for (const file of ["welcome.png", "diary.png", "diary-after-restart.png", "diary-after-restart.xml", "logcat.log"]) {
        assert.ok(existsSync(join(sample.outputDirectory, file)));
    }
});

test("rejects am start failure even when adb exits zero and still stops its owned emulator", async context => {
    const sample = fixture(context, { change: (command, args) => command.endsWith("adb") && args.includes("start") ?
        { status: 0, stdout: "Error: Activity class does not exist.\n", stderr: "" } : undefined });
    await assert.rejects(sample.run(), /successful NutriFlow activity launch/);
    assert.equal(sample.report().phase, "launch");
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    assert.ok(existsSync(join(sample.outputDirectory, "failure.png")));
});

test("accepts the owned emulator console identity with actual CRLF line endings", async context => {
    const sample = fixture(context, { consoleLineEnding: "\r\n" });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.installation, "passed");
    assert.equal(report.profile.persistence, "passed");
});

test("rejects extra emulator console identity lines even when the owned name and OK are present", async context => {
    const sample = fixture(context, { consoleLineEnding: "\r\n", extraConsoleLine: "unexpected" });
    await assert.rejects(sample.run(), /does not belong/);
    assert.ok(!sample.calls.some(call => call.args.includes("install")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("an app process and splash alone do not pass the native welcome screen check", async context => {
    const sample = fixture(context, { welcome: "splash" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*welcome.xml/);
    assert.equal(sample.report().launch, undefined);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("fails when the profile and diary disappear after restarting", async context => {
    const sample = fixture(context, { persisted: false });
    await assert.rejects(sample.run(), /diary-after-restart.xml/);
    assert.equal(sample.report().profile.creation, "passed");
    assert.equal(sample.report().profile.persistence, "not-tested");
    assert.equal(sample.report().status, "failed");
});

test("does not accept a busy diary even when its content and enabled controls are already visible", async context => {
    const sample = fixture(context, { diaryScreen: "busy-diary" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("does not accept diary content while the profile and action controls are disabled", async context => {
    const sample = fixture(context, { diaryScreen: "disabled-diary" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
});

test("waits through a brief ready-to-busy transition and captures PNG only after two consecutive idle diary snapshots", async context => {
    const sample = fixture(context, { diarySequence: ["busy-diary", "diary", "busy-diary", "diary", "diary"] });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.deepEqual(sample.diarySnapshots, ["busy-diary", "diary", "busy-diary", "diary", "diary", "diary", "diary"]);
    assert.deepEqual(sample.screenshots.filter(item => item.screen === "diary").map(item => item.diarySnapshots), [
        ["busy-diary", "diary", "busy-diary", "diary", "diary"],
        ["busy-diary", "diary", "busy-diary", "diary", "diary", "diary", "diary"]
    ]);
});

test("does not reuse or send device commands to an existing selected serial", async context => {
    const sample = fixture(context, { change: (command, args) => args[0] === "devices" ?
        { status: 0, stdout: `List of devices attached\n${serial}\tdevice\n`, stderr: "" } : undefined });
    await assert.rejects(sample.run(), /already exists/);
    assert.equal(sample.starts.length, 0);
    assert.equal(sample.calls.length, 1);
    assert.equal(sample.report().cleanup, "passed");
});

test("rejects a serial reporting another AVD and never installs or captures its screen", async context => {
    const sample = fixture(context, { change: (command, args) => args.includes("emu") ?
        { status: 0, stdout: "someone-elses-avd\nOK", stderr: "" } : undefined });
    await assert.rejects(sample.run(), /does not belong/);
    assert.ok(!sample.calls.some(call => call.args.includes("install") || call.args.includes("screencap") || call.args.includes("logcat")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("records emulator startup failure and cleans the temporary AVD directory", async context => {
    const sample = fixture(context, { startFailure: true });
    await assert.rejects(sample.run(), /emulator ENOENT/);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().cleanup, "passed");
    assert.ok(!sample.calls.some(call => call.args.includes("install")));
});

test("refuses to create diagnostic output outside the runner temporary directory", async context => {
    const sample = fixture(context);
    await assert.rejects(sample.run({ outputDirectory: join(sample.runnerTemp, "..", "outside-output") }), /inside RUNNER_TEMP/);
    assert.equal(sample.calls.length, 0);
});
