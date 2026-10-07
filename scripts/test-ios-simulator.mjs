import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { appendFileSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { setTimeout } from "node:timers/promises";
import { fileURLToPath } from "node:url";

export async function testIosSimulator({ bundlePath, outputDirectory, execute = spawnSync, pause = setTimeout }) {
    bundlePath = resolve(bundlePath);
    outputDirectory = resolve(outputDirectory);
    if (!statSync(bundlePath).isDirectory()) {
        throw new Error("The app bundle must be a directory.");
    }
    mkdirSync(outputDirectory);

    const report = { status: "running", startedAt: new Date().toISOString() };
    const commandLog = join(outputDirectory, "commands.log");
    let deviceId;
    let failure;
    let recordingFailure;

    function record(path, content, append = false) {
        try {
            if (append) {
                appendFileSync(path, content);
            } else {
                writeFileSync(path, content);
            }
        } catch (error) {
            if (!recordingFailure) {
                console.error(`Could not save simulator diagnostics: ${error.message}`);
                recordingFailure = error;
            }
        }
    }

    function invoke(command, args, { timeout = 30_000, file } = {}) {
        record(commandLog, `${JSON.stringify([command, ...args])}\n`, true);
        const result = execute(command, args, {
            encoding: "utf8", timeout, killSignal: "SIGKILL", maxBuffer: 4 * 1024 * 1024, shell: false
        });
        const stdout = result.stdout ?? "";
        const stderr = result.stderr ?? "";
        record(commandLog, `${stdout}\n${stderr}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`, true);
        if (file) {
            record(join(outputDirectory, file), `${stdout}\n${stderr}`);
        }
        if (result.error || result.status !== 0) {
            throw new Error(`${command} ${args.join(" ")} failed: ${result.error?.code ?? result.status}; ${stderr.trim()}`);
        }
        return stdout.trim();
    }

    function simctl(args, options) {
        return invoke("xcrun", ["simctl", ...args], options);
    }

    function diagnostic(action) {
        try {
            action();
        } catch (error) {
            record(join(outputDirectory, "diagnostic-errors.log"), `${error.message}\n`, true);
        }
    }

    function readLaunchPid(output, bundleIdentifier) {
        const prefix = `${bundleIdentifier}: `;
        const pidText = output.startsWith(prefix) ? output.slice(prefix.length).trim() : "";
        if (!/^[1-9][0-9]*$/.test(pidText) || !Number.isSafeInteger(Number(pidText))) {
            throw new Error(`simctl launch did not return the app's positive PID for ${bundleIdentifier}.`);
        }
        return Number(pidText);
    }

    function checkProcess(file) {
        const jobs = simctl(["spawn", deviceId, "launchctl", "list"], { file });
        const alive = jobs.split(/\r?\n/).some(line => {
            const [pid, , label] = line.trim().split(/\s+/);
            return pid === String(report.pid) &&
                (label === `UIKitApplication:${report.bundleIdentifier}` ||
                    label?.startsWith(`UIKitApplication:${report.bundleIdentifier}[`));
        });
        if (!alive) {
            throw new Error("The launched app process is no longer running in this simulator.");
        }
    }

    try {
        report.phase = "read-bundle";
        const plist = join(bundlePath, "Info.plist");
        report.bundleIdentifier = invoke("plutil", ["-extract", "CFBundleIdentifier", "raw", "-o", "-", plist]);
        report.executable = invoke("plutil", ["-extract", "CFBundleExecutable", "raw", "-o", "-", plist]);
        if (!/^[A-Za-z0-9][A-Za-z0-9.-]+$/.test(report.bundleIdentifier) || !report.executable) {
            throw new Error("The built Info.plist has an invalid bundle identifier or executable.");
        }

        report.phase = "select-runtime";
        const inventory = JSON.parse(simctl(["list", "--json"], { file: "inventory.json" }));
        const runtime = inventory.runtimes.find(item =>
            item.identifier === "com.apple.CoreSimulator.SimRuntime.iOS-26-0" && item.isAvailable === true);
        if (!runtime) {
            throw new Error("An available iOS 26.0 simulator runtime is required; no runtime will be downloaded automatically.");
        }
        const iphone = inventory.devices[runtime.identifier]?.find(item =>
            item.isAvailable === true &&
            item.deviceTypeIdentifier?.startsWith("com.apple.CoreSimulator.SimDeviceType.iPhone-"));
        if (!iphone) {
            throw new Error("No available iPhone for the iOS 26.0 runtime was found.");
        }
        report.runtimeIdentifier = runtime.identifier;
        report.runtimeVersion = runtime.version;
        report.deviceTypeIdentifier = iphone.deviceTypeIdentifier;

        report.phase = "create-device";
        const createdId = simctl(["create", `NutriFlow smoke ${randomUUID()}`, iphone.deviceTypeIdentifier, runtime.identifier]);
        if (!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(createdId)) {
            throw new Error("simctl create did not return a valid device UUID.");
        }
        deviceId = createdId;
        report.deviceId = deviceId;

        report.phase = "boot";
        simctl(["bootstatus", deviceId, "-b"], { timeout: 240_000, file: "boot.log" });
        report.phase = "readiness-launch";
        report.readiness = { bundleIdentifier: "com.apple.Preferences", status: "running" };
        const readinessLaunch = simctl(["launch", deviceId, report.readiness.bundleIdentifier], { file: "readiness-launch.log" });
        report.readiness.pid = readLaunchPid(readinessLaunch, report.readiness.bundleIdentifier);
        await pause(700);
        report.phase = "readiness-terminate";
        simctl(["terminate", deviceId, report.readiness.bundleIdentifier], { file: "readiness-terminate.log" });
        report.readiness.status = "passed";
        report.phase = "install";
        simctl(["install", deviceId, bundlePath], { timeout: 60_000, file: "install.log" });
        report.phase = "launch";
        const launch = simctl(["launch", "--terminate-running-process",
            `--stdout=${join(outputDirectory, "app-stdout.log")}`,
            `--stderr=${join(outputDirectory, "app-stderr.log")}`,
            deviceId, report.bundleIdentifier], { timeout: 60_000, file: "launch.log" });
        report.pid = readLaunchPid(launch, report.bundleIdentifier);

        report.phase = "check-startup";
        checkProcess("process-after-launch.log");
        await pause(10_000);
        checkProcess("process-before-screenshot.log");
        report.phase = "screenshot";
        const screenshot = join(outputDirectory, "startup.png");
        simctl(["io", deviceId, "screenshot", "--type=png", screenshot], { file: "screenshot.log" });
        const png = readFileSync(screenshot);
        if (png.length <= 8 || !png.subarray(0, 8).equals(Buffer.from("89504e470d0a1a0a", "hex"))) {
            throw new Error("simctl did not produce a nonempty PNG screenshot.");
        }
        report.screenshot = "startup.png";
        await pause(5_000);
        report.phase = "check-after-screenshot";
        checkProcess("process-after-screenshot.log");
    } catch (error) {
        failure = error;
        if (report.readiness?.status === "running") {
            report.readiness.status = "failed";
            report.readiness.failure = error.message;
        }
    } finally {
        for (const command of ["bootstatus", "launch", "io"]) {
            diagnostic(() => simctl(["help", command], { timeout: 15_000, file: `help-${command}.log` }));
        }
        if (deviceId) {
            diagnostic(() => simctl(["list", "devices", "--json"], { file: "devices-after-run.json" }));
            diagnostic(() => simctl(["spawn", deviceId, "launchctl", "list"], { file: "process-final.log" }));
            diagnostic(() => simctl(["spawn", deviceId, "log", "show", "--last", "3m", "--style", "compact",
                "--predicate", `process == ${JSON.stringify(report.executable)}`], { file: "app-system.log" }));
            const launchPredicate = [
                `eventMessage CONTAINS[c] ${JSON.stringify(report.bundleIdentifier)}`,
                'eventMessage CONTAINS[c] "com.apple.Preferences"',
                'process IN {"SpringBoard", "runningboardd", "launchd", "backboardd"}'
            ].join(" OR ");
            diagnostic(() => simctl(["spawn", deviceId, "log", "show", "--last", "10m", "--style", "compact",
                "--predicate", launchPredicate], { file: "launch-system.log" }));
            if (failure) {
                diagnostic(() => simctl(["io", deviceId, "screenshot", "--type=png",
                    join(outputDirectory, "failure.png")], { file: "failure-screenshot.log" }));
            }
            for (const command of ["shutdown", "delete"]) {
                try {
                    simctl([command, deviceId], { timeout: 15_000 });
                } catch (error) {
                    failure ??= error;
                }
            }
        }
        failure ??= recordingFailure;
        report.status = failure ? "failed" : "passed";
        report.failure = failure?.message ?? null;
        report.finishedAt = new Date().toISOString();
        record(join(outputDirectory, "result.json"), `${JSON.stringify(report, null, 2)}\n`);
        failure ??= recordingFailure;
    }

    if (failure) {
        throw failure;
    }
    return report;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        if (process.argv.length !== 4) {
            throw new Error("Usage: node scripts/test-ios-simulator.mjs <built.app> <new-output-directory>");
        }
        if (process.platform !== "darwin") {
            throw new Error("This smoke test requires macOS and Xcode. Use the iOS simulator workflow from Windows.");
        }
        const report = await testIosSimulator({ bundlePath: process.argv[2], outputDirectory: process.argv[3] });
        console.log(JSON.stringify(report, null, 2));
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
