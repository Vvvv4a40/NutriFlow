import { spawn, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { appendFileSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";
import { setImmediate, setTimeout } from "node:timers/promises";
import { fileURLToPath } from "node:url";

export async function testIosSimulator({
    bundlePath, outputDirectory, execute = spawnSync, pause = setTimeout,
    start = spawn, developerDirectory = process.env.DEVELOPER_DIR
}) {
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
    let simulatorUi;
    let simulatorUiFailure;
    let simulatorUiExited = false;

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

    function invoke(command, args, { timeout = 30_000, file, includeStderr = false } = {}) {
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
            throw Object.assign(new Error(`${command} ${args.join(" ")} failed: ${result.error?.code ?? result.status}; ${stderr.trim()}`), {
                exitStatus: result.status, stderr, commandError: result.error ?? null
            });
        }
        return (includeStderr ? `${stdout}\n${stderr}` : stdout).trim();
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

    function simulatorUiHasExited() {
        return simulatorUiExited || simulatorUi.exitCode !== null || simulatorUi.signalCode !== null;
    }

    async function startSimulatorUi() {
        const args = ["-CurrentDeviceUDID", deviceId, "-StartLastDeviceOnLaunch", "0",
            "-ConnectHardwareKeyboard", "0", "-PasteboardAutomaticSync", "0"];
        record(commandLog, `${JSON.stringify([report.simulatorUi.executable, ...args])}\n`, true);
        try {
            simulatorUi = start(report.simulatorUi.executable, args, { stdio: "ignore", shell: false, detached: false });
            simulatorUi.on("error", error => {
                simulatorUiFailure ??= error;
                record(join(outputDirectory, "simulator-ui.log"), `error=${error.message}\n`, true);
            });
            simulatorUi.on("exit", (code, signal) => {
                simulatorUiExited = true;
                report.simulatorUi.exitCode = code;
                report.simulatorUi.signal = signal;
                record(join(outputDirectory, "simulator-ui.log"), `exitCode=${code}; signal=${signal}\n`, true);
            });
            await new Promise((resolveStarted, rejectStarted) => {
                function finish(error) {
                    globalThis.clearTimeout(timer);
                    simulatorUi.removeListener("spawn", onSpawn);
                    simulatorUi.removeListener("error", onError);
                    simulatorUi.removeListener("exit", onExit);
                    if (error) rejectStarted(error);
                    else resolveStarted();
                }
                function onSpawn() {
                    if (!Number.isSafeInteger(simulatorUi.pid) || simulatorUi.pid <= 0) {
                        finish(new Error("Simulator UI did not return a positive PID."));
                        return;
                    }
                    report.simulatorUi.pid = simulatorUi.pid;
                    report.simulatorUi.status = "running";
                    record(join(outputDirectory, "simulator-ui.log"), `pid=${simulatorUi.pid}\n`, true);
                    finish();
                }
                function onError(error) { finish(error); }
                function onExit() { finish(new Error("Simulator UI exited before startup.")); }
                const timer = globalThis.setTimeout(() => finish(new Error("Simulator UI startup timed out after 10000ms.")), 10_000);
                simulatorUi.once("spawn", onSpawn);
                simulatorUi.once("error", onError);
                simulatorUi.once("exit", onExit);
            });
        } catch (error) {
            report.simulatorUi.status = "failed";
            report.simulatorUi.failure = error.message;
            throw error;
        }
    }

    async function checkSimulatorUi() {
        await setImmediate();
        const error = simulatorUiFailure ?? (simulatorUiHasExited() ? new Error("Simulator UI exited unexpectedly; it is no longer running.") : null);
        if (error) {
            report.simulatorUi.status = "failed";
            report.simulatorUi.failure = error.message;
            throw error;
        }
    }

    function signalSimulatorUi(signal) {
        return new Promise((resolveStopped, rejectStopped) => {
            function finish(error, stopped = false) {
                globalThis.clearTimeout(timer);
                simulatorUi.removeListener("exit", onExit);
                if (error) rejectStopped(error);
                else resolveStopped(stopped);
            }
            function onExit() { finish(null, true); }
            const timer = globalThis.setTimeout(() => finish(null, false), 5_000);
            simulatorUi.once("exit", onExit);
            try {
                record(join(outputDirectory, "simulator-ui.log"), `signal=${signal}\n`, true);
                simulatorUi.kill(signal);
            } catch (error) {
                finish(error);
            }
        });
    }

    async function stopSimulatorUi() {
        if (!simulatorUi) return;
        await setImmediate();
        let terminationFailure;
        for (const signal of ["SIGTERM", "SIGKILL"]) {
            if (simulatorUiHasExited() || simulatorUi.pid === undefined) {
                report.simulatorUi.cleanup = { status: "passed", signal: report.simulatorUi.signal ?? null };
                if (report.simulatorUi.status === "running") {
                    report.simulatorUi.status = "failed";
                    report.simulatorUi.failure = simulatorUiFailure?.message ?? "Simulator UI exited unexpectedly; it is no longer running.";
                }
                return;
            }
            try {
                if (await signalSimulatorUi(signal)) {
                    report.simulatorUi.cleanup = { status: "passed", signal };
                    if (report.simulatorUi.status === "running") report.simulatorUi.status = "stopped";
                    return;
                }
                terminationFailure ??= new Error("Simulator UI termination timed out.");
            } catch (error) {
                terminationFailure ??= error;
            }
        }
        report.simulatorUi.cleanup = { status: "failed", failure: terminationFailure.message };
        report.simulatorUi.status = "failed";
        simulatorUi.unref();
        throw terminationFailure;
    }

    function shutdownSimulator() {
        const cleanup = report.deviceCleanup.shutdown;
        try {
            simctl(["shutdown", deviceId], { timeout: 15_000, file: "cleanup-shutdown.log" });
            Object.assign(cleanup, { status: "passed", alreadyShutdown: false });
        } catch (error) {
            const alreadyShutdownMessage = /^An error was encountered processing the command \(domain=com\.apple\.CoreSimulator\.SimError, code=405\):\r?\nUnable to shutdown device in current state: Shutdown$/;
            if (error.exitStatus === 149 && error.commandError === null && alreadyShutdownMessage.test(error.stderr.trim())) {
                try {
                    const inventory = JSON.parse(simctl(["list", "devices", "--json"], {
                        timeout: 15_000, file: "cleanup-device-state.json"
                    }));
                    const groups = Object.values(inventory.devices);
                    if (Array.isArray(inventory.devices) || !groups.every(Array.isArray)) {
                        throw new Error("The simulator inventory must contain device arrays grouped by runtime.");
                    }
                    const devices = groups.flat();
                    const owned = devices.filter(device => device?.udid === deviceId);
                    if (owned.length !== 1 || owned[0].state !== "Shutdown") {
                        throw new Error("The owned simulator's Shutdown state could not be verified uniquely.");
                    }
                    Object.assign(cleanup, { status: "passed", alreadyShutdown: true,
                        verifiedState: "Shutdown", commandFailure: error.message });
                    return;
                } catch (verificationError) {
                    cleanup.verificationFailure = verificationError.message;
                }
            }
            Object.assign(cleanup, { status: "failed", failure: error.message });
            throw error;
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

    function readBootStatus(output) {
        const updates = [...output.matchAll(/^(?:\[[^\]\r\n]+\][ \t]*)?Status=(\d+),[ \t]*isTerminal=(YES|NO),[^\r\n]*$/gm)];
        const last = updates.at(-1);
        const terminalStatus = last?.[2] === "YES" ? last[1] : null;
        const summary = last ? /^\r?\n([^\r\n]*)/.exec(output.slice(last.index + last[0].length))?.[1].trim() ?? null : null;
        let status = "unrecognized";
        if (last?.[2] === "YES" && updates.filter(update => update[2] === "YES").length === 1) {
            if (terminalStatus === "4294967295" && summary === "Finished" && !/^[ \t]*Data Migration Failed[ \t]*\r?$/m.test(output)) {
                status = "passed";
            } else if (terminalStatus === "3" && summary === "Data Migration Failed") {
                status = "migration-failed";
            }
        }
        return { status, terminalStatus, summary };
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

        report.phase = "select-simulator-ui";
        const selectedDeveloperDirectory = developerDirectory ?? invoke("xcode-select", ["--print-path"]);
        if (!isAbsolute(selectedDeveloperDirectory)) {
            throw new Error("The selected Xcode Developer directory must be an absolute path.");
        }
        const simulatorExecutable = resolve(selectedDeveloperDirectory, "Applications/Simulator.app/Contents/MacOS/Simulator");
        if (!statSync(simulatorExecutable).isFile()) {
            throw new Error("The selected Xcode Simulator executable must be a file.");
        }
        report.simulatorUi = { executable: simulatorExecutable, status: "not-started" };

        report.phase = "create-device";
        const createdId = simctl(["create", `NutriFlow smoke ${randomUUID()}`, iphone.deviceTypeIdentifier, runtime.identifier]);
        if (!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(createdId)) {
            throw new Error("simctl create did not return a valid device UUID.");
        }
        deviceId = createdId;
        report.deviceId = deviceId;

        report.phase = "start-simulator-ui";
        await startSimulatorUi();
        await checkSimulatorUi();

        report.bootAttempts = [];
        for (let number = 1; number <= 2; number++) {
            report.phase = number === 1 ? "boot" : "boot-retry";
            const attempt = { number, log: number === 1 ? "boot.log" : "boot-retry.log", status: "running" };
            report.bootAttempts.push(attempt);
            try {
                const output = simctl(["bootstatus", deviceId, "-b"], { timeout: 240_000, file: attempt.log, includeStderr: true });
                Object.assign(attempt, readBootStatus(output));
            } catch (error) {
                attempt.status = "command-failed";
                attempt.failure = error.message;
                throw error;
            }
            if (attempt.status === "passed") {
                break;
            }
            if (number === 1 && attempt.status === "migration-failed") {
                report.phase = "restart-after-migration-failure";
                simctl(["shutdown", deviceId], { timeout: 15_000, file: "boot-restart.log" });
                continue;
            }
            throw new Error(`The iOS simulator boot did not finish successfully (terminal status ${attempt.terminalStatus ?? "missing"}; ${attempt.summary ?? "missing summary"}).`);
        }
        await checkSimulatorUi();
        report.phase = "readiness-launch";
        report.readiness = { bundleIdentifier: "com.apple.Preferences", status: "running" };
        const readinessLaunch = simctl(["launch", deviceId, report.readiness.bundleIdentifier], { timeout: 120_000, file: "readiness-launch.log" });
        report.readiness.pid = readLaunchPid(readinessLaunch, report.readiness.bundleIdentifier);
        await pause(700);
        report.phase = "readiness-terminate";
        simctl(["terminate", deviceId, report.readiness.bundleIdentifier], { file: "readiness-terminate.log" });
        report.readiness.status = "passed";
        await checkSimulatorUi();
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
        await checkSimulatorUi();
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
            diagnostic(() => simctl(["io", deviceId, "enumerate"], { file: "display-ports.log" }));
            diagnostic(() => simctl(["spawn", deviceId, "log", "show", "--last", "3m", "--style", "compact",
                "--predicate", `process == ${JSON.stringify(report.executable)}`], { file: "app-system.log" }));
            const launchPredicate = [
                `eventMessage CONTAINS[c] ${JSON.stringify(report.bundleIdentifier)}`,
                'eventMessage CONTAINS[c] "com.apple.Preferences"',
                '(process == "com.apple.datamigrator" AND (eventMessage CONTAINS[c] "error" OR eventMessage CONTAINS[c] "failed" OR eventMessage CONTAINS[c] "watchdog"))'
            ].join(" OR ");
            diagnostic(() => simctl(["spawn", deviceId, "log", "show", "--last", "20m", "--style", "compact",
                "--predicate", launchPredicate], { file: "launch-system.log" }));
            const systemFailurePredicate = '(eventMessage CONTAINS[c] "error" OR eventMessage CONTAINS[c] "failed" OR eventMessage CONTAINS[c] "watchdog" OR eventMessage CONTAINS[c] "display")';
            diagnostic(() => simctl(["spawn", deviceId, "log", "show", "--last", "20m", "--style", "compact",
                "--predicate", `(process IN {"backboardd", "SpringBoard"} AND ${systemFailurePredicate})`], { file: "boot-system.log" }));
            if (report.simulatorUi?.pid) {
                diagnostic(() => invoke("log", ["show", "--last", "20m", "--style", "compact",
                    "--predicate", `(process == "Simulator" AND processID == ${report.simulatorUi.pid} AND ${systemFailurePredicate})`], { file: "simulator-host.log" }));
            }
            if (failure) {
                diagnostic(() => simctl(["io", deviceId, "screenshot", "--type=png",
                    join(outputDirectory, "failure.png")], { file: "failure-screenshot.log" }));
            }
            try {
                if (report.simulatorUi?.status === "running" && !failure) await checkSimulatorUi();
            } catch (error) {
                failure ??= error;
            }
            try {
                await stopSimulatorUi();
            } catch (error) {
                failure ??= error;
            }
            report.deviceCleanup = { deviceId, shutdown: { status: "pending" }, delete: { status: "pending" } };
            try {
                shutdownSimulator();
            } catch (error) {
                failure ??= error;
            }
            try {
                simctl(["delete", deviceId], { timeout: 15_000, file: "cleanup-delete.log" });
                report.deviceCleanup.delete.status = "passed";
            } catch (error) {
                Object.assign(report.deviceCleanup.delete, { status: "failed", failure: error.message });
                failure ??= error;
            }
            report.deviceCleanup.status = report.deviceCleanup.shutdown.status === "passed" &&
                report.deviceCleanup.delete.status === "passed" ? "passed" : "failed";
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
