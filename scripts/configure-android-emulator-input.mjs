import { spawnSync } from "node:child_process";
import { posix, resolve, win32 } from "node:path";
import { fileURLToPath } from "node:url";

const desiredSettings = { stylus_handwriting_enabled: "0", show_ime_with_hard_keyboard: "1" };

export function configureAndroidEmulatorInput({ serial, sdkDirectory, execute = spawnSync, platform = process.platform }) {
    if (typeof serial !== "string" || !/^emulator-\d+$/.test(serial)) {
        throw new Error("An explicit emulator serial is required, for example emulator-5554. Physical devices are not supported.");
    }
    const paths = platform === "win32" ? win32 : posix;
    if (typeof sdkDirectory !== "string" || !paths.isAbsolute(sdkDirectory)) {
        throw new Error("An explicit absolute Android SDK directory is required.");
    }
    const adbPath = paths.join(paths.normalize(sdkDirectory), "platform-tools", platform === "win32" ? "adb.exe" : "adb");
    function invoke(args) {
        const result = execute(adbPath, ["-s", serial, ...args], {
            encoding: "utf8", timeout: 10_000, killSignal: "SIGKILL", maxBuffer: 64 * 1024, shell: false, windowsHide: true
        });
        if (result.error || result.status !== 0 || result.signal) {
            throw new Error(`ADB ${args.join(" ")} failed: ${result.error?.code ?? result.signal ?? result.status}.`);
        }
        return String(result.stdout ?? "").trim();
    }
    if (invoke(["get-state"]) !== "device") throw new Error("The selected Android emulator is not ready.");
    if (invoke(["shell", "getprop", "ro.kernel.qemu"]) !== "1") throw new Error("The selected device is not an Android emulator. Nothing was changed.");
    if (invoke(["shell", "getprop", "sys.boot_completed"]) !== "1") throw new Error("Wait for the Android emulator to finish booting. Nothing was changed.");

    function readSetting(key) {
        const value = invoke(["shell", "settings", "get", "secure", key]);
        if (!/^(?:0|1|null)$/.test(value)) throw new Error(`Unexpected secure setting value for ${key}.`);
        return value === "null" ? null : value;
    }
    const previousSettings = Object.fromEntries(Object.keys(desiredSettings).map(key => [key, readSetting(key)]));
    const restoreCommands = Object.entries(previousSettings).map(([key, value]) => ({
        command: adbPath,
        args: ["-s", serial, "shell", "settings", value === null ? "delete" : "put", "secure", key, ...(value === null ? [] : [value])]
    }));
    try {
        for (const [key, value] of Object.entries(desiredSettings)) {
            invoke(["shell", "settings", "put", "secure", key, value]);
            if (readSetting(key) !== value) throw new Error(`The Android emulator did not retain ${key}=${value}.`);
        }
    } catch (cause) {
        const rollbackErrors = [];
        for (const [index, key] of Object.keys(previousSettings).entries()) {
            try {
                invoke(restoreCommands[index].args.slice(2));
                if (readSetting(key) !== previousSettings[key]) throw new Error(`Could not verify restored ${key}.`);
            } catch (error) {
                rollbackErrors.push(error.message);
            }
        }
        throw Object.assign(new Error(`${cause.message} ${rollbackErrors.length ? "Rollback failed; use restoreCommands to restore the previous settings." : "Both previous settings were restored."}`, { cause }), {
            previousSettings, restoreCommands, rollbackErrors
        });
    }
    return { status: "configured", serial, previousSettings, settings: { ...desiredSettings }, restoreCommands };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        const args = process.argv.slice(2);
        const options = new Map([[args[0], args[1]], [args[2], args[3]]]);
        if (args.length !== 4 || options.size !== 2 || !options.has("--serial") || !options.has("--sdk")) {
            throw new Error("Usage: node scripts/configure-android-emulator-input.mjs --serial emulator-5554 --sdk <absolute-Android-SDK-directory>");
        }
        console.log(JSON.stringify(configureAndroidEmulatorInput({ serial: options.get("--serial"), sdkDirectory: options.get("--sdk") }), null, 2));
    } catch (error) {
        console.error(error.message);
        if (error.previousSettings) console.error(JSON.stringify({ previousSettings: error.previousSettings, restoreCommands: error.restoreCommands, rollbackErrors: error.rollbackErrors }, null, 2));
        process.exitCode = 1;
    }
}
