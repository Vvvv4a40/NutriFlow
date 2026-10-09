import { spawn, spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { appendFileSync, closeSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from "node:fs";
import { basename, isAbsolute, join, relative, resolve } from "node:path";
import { setImmediate, setTimeout } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const packageId = "com.nutriflow.app";
const systemImage = "system-images;android-35;google_apis;x86_64";
const serial = "emulator-5556";

function uiNodes(xml) {
    if (!xml.includes("<hierarchy") || !xml.includes("</hierarchy>")) throw new Error("No complete Android UI hierarchy was returned.");
    return [...xml.matchAll(/<node\b([^>]+)>/g)].map(match => Object.fromEntries(
        [...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(attribute => [attribute[1], attribute[2].replace(
            /&(?:quot|apos|lt|gt|amp);/g, entity => ({ "&quot;": '"', "&apos;": "'", "&lt;": "<", "&gt;": ">", "&amp;": "&" })[entity])])));
}

export async function testAndroidEmulator({
    apkPath, outputDirectory, sdkDirectory = process.env.ANDROID_HOME, runnerTemp = process.env.RUNNER_TEMP,
    execute = spawnSync, start = spawn, pause = setTimeout, now = Date.now
}) {
    if (!sdkDirectory || !runnerTemp) throw new Error("The cloud runner Android SDK and temporary directory are required.");
    apkPath = realpathSync(resolve(apkPath));
    if (!statSync(apkPath).isFile() || statSync(apkPath).size === 0) throw new Error("A nonempty verified APK is required.");
    runnerTemp = realpathSync(resolve(runnerTemp));
    sdkDirectory = realpathSync(resolve(sdkDirectory));
    outputDirectory = resolve(outputDirectory);
    const outputRelative = relative(runnerTemp, outputDirectory);
    if (!outputRelative || outputRelative.startsWith("..") || isAbsolute(outputRelative)) throw new Error("Diagnostics must be a new directory inside RUNNER_TEMP.");
    mkdirSync(outputDirectory);
    const ownedDirectory = mkdtempSync(join(runnerTemp, "nutriflow-android-emulator-"));
    const avdHome = join(ownedDirectory, "avd");
    mkdirSync(avdHome);
    const avdName = `nutriflow-smoke-${randomUUID()}`;
    const environment = { ...process.env, ANDROID_HOME: sdkDirectory, ANDROID_USER_HOME: ownedDirectory, ANDROID_AVD_HOME: avdHome };
    const adbPath = join(sdkDirectory, "platform-tools", "adb");
    const emulatorPath = join(sdkDirectory, "emulator", "emulator");
    const avdmanager = join(sdkDirectory, "cmdline-tools", "19.0", "bin", "avdmanager");
    const report = {
        status: "running", startedAt: new Date().toISOString(), packageId, serial, avdName, systemImage,
        apkSha256: createHash("sha256").update(readFileSync(apkPath)).digest("hex"),
        profile: { name: "Smoke", creation: "not-tested", persistence: "not-tested" },
        scope: "cloud emulator; no phone, Groq key or AI network requests"
    };
    const deadline = now() + 420_000;
    let child;
    let emulatorError;
    let emulatorExited = false;
    let deviceOwned = false;
    let failure;

    function record(file, content, append = false) {
        (append ? appendFileSync : writeFileSync)(join(outputDirectory, file), content);
    }

    function invoke(command, args, { timeout = 15_000, file, input, binary = false, diagnostic = false } = {}) {
        const remaining = deadline - now();
        if (!diagnostic && remaining <= 0) throw new Error("The Android emulator smoke deadline was exceeded.");
        record("commands.log", `${JSON.stringify([command, ...args])}\n`, true);
        const result = execute(command, args, {
            encoding: binary ? undefined : "utf8", timeout: diagnostic ? timeout : Math.min(timeout, remaining),
            killSignal: "SIGKILL", maxBuffer: 8 * 1024 * 1024, shell: false, env: environment,
            ...(input === undefined ? {} : { input })
        });
        const stdout = result.stdout ?? (binary ? Buffer.alloc(0) : "");
        const stderr = String(result.stderr ?? "");
        record("commands.log", `${binary ? `[${stdout.length} binary bytes]` : stdout}\n${stderr}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`, true);
        if (file) record(file, binary ? stdout : `${stdout}\n${stderr}`);
        if (result.error || result.status !== 0) throw new Error(`${basename(command)} failed: ${result.error?.code ?? result.status}; ${stderr.trim()}`);
        return binary ? stdout : String(stdout).trim();
    }

    function adb(args, options) { return invoke(adbPath, ["-s", serial, ...args], options); }
    function checkEmulator() {
        if (emulatorError || emulatorExited || child?.exitCode !== null || child?.signalCode !== null) {
            throw emulatorError ?? new Error("The owned Android emulator process exited unexpectedly.");
        }
    }
    function checkApp() {
        checkEmulator();
        const pid = adb(["shell", "pidof", packageId]);
        if (!/^[1-9]\d*$/.test(pid)) throw new Error("The NutriFlow app process is not running.");
        report.pid = Number(pid);
    }
    function captureUi(file) {
        adb(["shell", "uiautomator", "dump", "/sdcard/nutriflow-smoke.xml"], { timeout: 20_000 });
        const xml = adb(["shell", "cat", "/sdcard/nutriflow-smoke.xml"]);
        record(file, xml);
        return uiNodes(xml).filter(node => node.package === packageId);
    }
    function hasText(nodes, text) { return nodes.some(node => node.text?.toLocaleLowerCase("ru") === text.toLocaleLowerCase("ru")); }
    async function waitUi(file, predicate) {
        const expires = Math.min(deadline, now() + 60_000);
        while (now() < expires) {
            checkApp();
            const nodes = captureUi(file);
            if (predicate(nodes)) return nodes;
            await pause(1_000);
        }
        throw new Error(`The expected NutriFlow screen did not appear (${file}).`);
    }
    function tap(nodes, predicate) {
        const candidates = nodes.filter(node => predicate(node) && node.enabled === "true");
        if (candidates.length !== 1) throw new Error("Expected one enabled UI target in the current NutriFlow hierarchy.");
        const bounds = /^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$/.exec(candidates[0].bounds ?? "");
        if (!bounds || Number(bounds[3]) <= Number(bounds[1]) || Number(bounds[4]) <= Number(bounds[2])) throw new Error("The UI target has invalid bounds.");
        adb(["shell", "input", "tap", String(Math.floor((Number(bounds[1]) + Number(bounds[3])) / 2)),
            String(Math.floor((Number(bounds[2]) + Number(bounds[4])) / 2))]);
    }
    function screenshot(file, diagnostic = false) {
        const png = adb(["exec-out", "screencap", "-p"], { file, binary: true, diagnostic });
        if (png.length <= 8 || !png.subarray(0, 8).equals(Buffer.from("89504e470d0a1a0a", "hex"))) throw new Error("Android did not return a nonempty PNG screenshot.");
    }
    function launch(file) {
        const output = adb(["shell", "am", "start", "-W", "-n", report.launcherActivity], { timeout: 45_000, file });
        if (!/^Status:\s*ok\s*$/m.test(output) || /\b(?:Error|Exception):/.test(output)) throw new Error("Android did not report a successful NutriFlow activity launch.");
    }
    async function stopEmulator() {
        if (!child) return;
        for (const signal of ["SIGTERM", "SIGKILL"]) {
            if (emulatorExited || child.exitCode !== null || child.signalCode !== null || !child.pid) return;
            await new Promise(resolveStopped => {
                const timer = globalThis.setTimeout(finish, 3_000);
                function finish() { globalThis.clearTimeout(timer); child.removeListener("exit", finish); resolveStopped(); }
                child.once("exit", finish);
                child.kill(signal);
            });
        }
        if (!emulatorExited && child.exitCode === null && child.signalCode === null) {
            child.unref();
            throw new Error("The owned Android emulator could not be stopped.");
        }
    }

    try {
        report.phase = "create-emulator";
        const devices = invoke(adbPath, ["devices"], { file: "devices-before.log" });
        if (devices.split(/\r?\n/).some(line => line.startsWith(`${serial}\t`))) throw new Error("The selected emulator serial already exists; it will not be reused.");
        invoke(emulatorPath, ["-accel-check"], { file: "acceleration.log" });
        invoke(avdmanager, ["create", "avd", "-n", avdName, "-k", systemImage, "-p", join(avdHome, `${avdName}.avd`), "-d", "pixel_6"],
            { timeout: 30_000, input: "no\n", file: "avd-create.log" });
        const emulatorArgs = ["-avd", avdName, "-port", "5556", "-no-window", "-no-audio", "-no-boot-anim", "-no-snapshot",
            "-gpu", "swiftshader", "-accel", "on", "-memory", "2048", "-cores", "2", "-camera-back", "none", "-camera-front", "none"];
        record("commands.log", `${JSON.stringify([emulatorPath, ...emulatorArgs])}\n`, true);
        const emulatorLog = openSync(join(outputDirectory, "emulator.log"), "wx");
        try {
            child = start(emulatorPath, emulatorArgs, { env: environment, shell: false, detached: false, stdio: ["ignore", emulatorLog, emulatorLog] });
        } finally { closeSync(emulatorLog); }
        child.on("error", error => { emulatorError = error; });
        child.on("exit", (code, signal) => { emulatorExited = true; report.emulatorExit = { code, signal }; });
        await new Promise((resolveStarted, rejectStarted) => {
            const timer = globalThis.setTimeout(() => finish(new Error("Android emulator process startup timed out.")), 5_000);
            function finish(error) {
                globalThis.clearTimeout(timer);
                child.removeListener("spawn", onSpawn);
                child.removeListener("error", onError);
                child.removeListener("exit", onExit);
                error ? rejectStarted(error) : resolveStarted();
            }
            function onSpawn() { finish(Number.isSafeInteger(child.pid) && child.pid > 0 ? null : new Error("Android emulator did not return a positive PID.")); }
            function onError(error) { finish(error); }
            function onExit() { finish(new Error("Android emulator exited before startup.")); }
            child.once("spawn", onSpawn); child.once("error", onError); child.once("exit", onExit);
        });
        report.emulatorPid = child.pid;
        report.phase = "boot";
        adb(["wait-for-device"], { timeout: 150_000 });
        const identity = adb(["emu", "avd", "name"]);
        if (identity !== `${avdName}\nOK`) throw new Error("The selected serial does not belong to the newly created emulator.");
        deviceOwned = true;
        const bootDeadline = Math.min(deadline, now() + 120_000);
        let booted = false;
        while (now() < bootDeadline) {
            await setImmediate(); checkEmulator();
            if (adb(["shell", "getprop", "sys.boot_completed"]) === "1") { booted = true; break; }
            await pause(1_000);
        }
        if (!booted) throw new Error("Android boot did not finish within its deadline.");
        report.boot = "passed";
        adb(["shell", "input", "keyevent", "82"]);
        for (const setting of ["window_animation_scale", "transition_animation_scale", "animator_duration_scale"]) {
            adb(["shell", "settings", "put", "global", setting, "0"]);
        }
        report.phase = "install";
        const installed = adb(["install", "--no-streaming", apkPath], { timeout: 60_000, file: "install.log" });
        if (!/^Success\s*$/m.test(installed)) throw new Error("Android did not confirm APK installation.");
        report.installation = "passed";
        const resolved = adb(["shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN",
            "-c", "android.intent.category.LAUNCHER", packageId], { file: "launcher.log" });
        const activities = resolved.split(/\r?\n/).filter(line => /^com\.nutriflow\.app\/[\w.$]+$/.test(line));
        if (activities.length !== 1) throw new Error("Expected one NutriFlow launcher activity.");
        report.launcherActivity = activities[0];
        adb(["logcat", "-c"]);
        report.phase = "launch";
        launch("launch.log");
        const welcome = await waitUi("welcome.xml", nodes => hasText(nodes, "Питание под вашим контролем") && hasText(nodes, "Создать профиль"));
        screenshot("welcome.png");
        report.launch = "passed";
        report.phase = "create-profile";
        tap(welcome, node => node.text?.toLocaleLowerCase("ru") === "создать профиль" && node.clickable === "true");
        const form = await waitUi("profile-form.xml", nodes => hasText(nodes, "Локальный профиль") && hasText(nodes, "Создать"));
        tap(form, node => node.class === "android.widget.EditText");
        adb(["shell", "input", "text", "Smoke"]);
        adb(["shell", "input", "keyevent", "4"]);
        const filled = await waitUi("profile-filled.xml", nodes => nodes.some(node => node.class === "android.widget.EditText" && node.text === "Smoke"));
        tap(filled, node => node.text?.toLocaleLowerCase("ru") === "создать" && node.clickable === "true");
        const diaryPredicate = nodes => hasText(nodes, "Дневник питания") && hasText(nodes, "Съедено за день") && hasText(nodes, "Smoke");
        await waitUi("diary.xml", diaryPredicate);
        screenshot("diary.png");
        report.profile.creation = "passed";
        report.phase = "restart";
        adb(["shell", "am", "force-stop", packageId]);
        launch("restart-launch.log");
        await waitUi("diary-after-restart.xml", diaryPredicate);
        screenshot("diary-after-restart.png");
        checkApp();
        report.profile.persistence = "passed";
    } catch (error) {
        failure = error;
    } finally {
        function diagnostic(action) {
            try { action(); } catch (error) { record("diagnostic-errors.log", `${error.message}\n`, true); }
        }
        if (deviceOwned) {
            diagnostic(() => adb(["logcat", "-d", "-t", "2000", "-v", "threadtime"], { file: "logcat.log", diagnostic: true }));
            diagnostic(() => adb(["shell", "dumpsys", "activity", "activities"], { file: "activities.log", diagnostic: true }));
            if (failure) diagnostic(() => screenshot("failure.png", true));
        }
        try {
            await stopEmulator();
            const ownedRelative = relative(runnerTemp, realpathSync(ownedDirectory));
            if (!ownedRelative || ownedRelative.startsWith("..") || isAbsolute(ownedRelative)) throw new Error("The owned emulator directory escaped RUNNER_TEMP.");
            rmSync(ownedDirectory, { recursive: true });
            report.cleanup = "passed";
        } catch (error) { report.cleanup = "failed"; report.cleanupFailure = error.message; failure ??= error; }
        report.status = failure ? "failed" : "passed";
        report.failure = failure?.message ?? null;
        report.finishedAt = new Date().toISOString();
        record("result.json", `${JSON.stringify(report, null, 2)}\n`);
    }
    if (failure) throw failure;
    return report;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        if (process.argv.length !== 4) throw new Error("Usage: node scripts/test-android-emulator.mjs <verified.apk> <new-output-directory>");
        if (process.platform !== "linux" || process.env.GITHUB_ACTIONS !== "true") throw new Error("Run this smoke test on the GitHub cloud runner; no local Android SDK installation is required.");
        console.log(JSON.stringify(await testAndroidEmulator({ apkPath: process.argv[2], outputDirectory: process.argv[3] }), null, 2));
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
