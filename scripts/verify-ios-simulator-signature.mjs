import { spawnSync } from "node:child_process";
import { appendFileSync, closeSync, lstatSync, mkdirSync, openSync, readSync, readdirSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export function verifyIosSimulatorSignature({
    bundlePath, outputDirectory, observeOnly = false, execute = spawnSync, now = Date.now
}) {
    if (typeof observeOnly !== "boolean") {
        throw new Error("The observation mode must be a boolean.");
    }
    bundlePath = resolve(bundlePath);
    if (!lstatSync(bundlePath).isDirectory()) {
        throw new Error("The app bundle must be a regular directory.");
    }
    bundlePath = realpathSync(bundlePath);
    outputDirectory = resolve(outputDirectory);
    outputDirectory = join(realpathSync(dirname(outputDirectory)), basename(outputDirectory));
    const outputRelativePath = relative(bundlePath, outputDirectory);
    if (outputRelativePath === "" || (!isAbsolute(outputRelativePath) && outputRelativePath !== ".." && !outputRelativePath.startsWith(`..${sep}`))) {
        throw new Error("Signature diagnostics must be outside the app bundle.");
    }
    mkdirSync(outputDirectory);

    const report = {
        status: "running", mode: observeOnly ? "observe" : "require-valid",
        startedAt: new Date().toISOString(), nativeCode: []
    };
    const deadline = now() + 120_000;
    let failure;

    function checkDeadline() {
        const remaining = deadline - now();
        if (remaining <= 0) {
            throw new Error("The signature verification deadline was exceeded.");
        }
        return remaining;
    }

    function invoke(command, args, file, allowInvalid = false) {
        const timeout = Math.min(15_000, checkDeadline());
        appendFileSync(join(outputDirectory, "commands.log"), `${JSON.stringify([command, ...args])}\n`);
        const result = execute(command, args, {
            encoding: "utf8", timeout, killSignal: "SIGKILL", maxBuffer: 1024 * 1024, shell: false
        });
        const stdout = result.stdout ?? "";
        const stderr = result.stderr ?? "";
        let commandFailure = result.error || !Number.isInteger(result.status) || (result.status !== 0 && !allowInvalid)
            ? new Error(`${command} ${args.join(" ")} failed: ${result.error?.code ?? result.status}; ${stderr.trim()}`)
            : null;
        try {
            checkDeadline();
        } catch (error) {
            commandFailure ??= error;
        }
        try {
            writeFileSync(join(outputDirectory, file), `${stdout}\n${stderr}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`);
        } catch (error) {
            throw commandFailure ?? error;
        }
        if (commandFailure) throw commandFailure;
        return { status: result.status, stdout, stderr };
    }

    function findNativeCode(directory) {
        const paths = [];
        for (const entry of readdirSync(directory, { withFileTypes: true }).sort((left, right) => left.name.localeCompare(right.name))) {
            checkDeadline();
            const path = join(directory, entry.name);
            if (entry.isSymbolicLink()) {
                throw new Error(`Symbolic links are not supported in this simulator bundle: ${relative(bundlePath, path)}`);
            }
            if (entry.isDirectory()) {
                paths.push(...findNativeCode(path));
            } else if (entry.isFile()) {
                const header = Buffer.alloc(4);
                const descriptor = openSync(path, "r");
                let bytesRead;
                try {
                    bytesRead = readSync(descriptor, header, 0, 4, 0);
                } finally {
                    closeSync(descriptor);
                }
                if (bytesRead === 4 && [0xfeedface, 0xfeedfacf, 0xcefaedfe, 0xcffaedfe, 0xcafebabe, 0xbebafeca, 0xcafebabf, 0xbfbafeca].includes(header.readUInt32BE())) {
                    paths.push(path);
                }
            } else {
                throw new Error(`Unsupported bundle entry: ${relative(bundlePath, path)}`);
            }
        }
        return paths;
    }

    try {
        report.phase = "manifest";
        const manifestPath = join(bundlePath, "Info.plist");
        if (!lstatSync(manifestPath).isFile()) {
            throw new Error("The app manifest must be a regular file.");
        }
        const manifest = JSON.parse(invoke("plutil", ["-convert", "json", "-o", "-", manifestPath], "manifest.log").stdout);
        if (manifest.CFBundleIdentifier !== "com.nutriflow.app" ||
            !Array.isArray(manifest.CFBundleSupportedPlatforms) ||
            manifest.CFBundleSupportedPlatforms.length !== 1 || manifest.CFBundleSupportedPlatforms[0] !== "iPhoneSimulator") {
            throw new Error("Expected the NutriFlow iOS simulator bundle.");
        }
        const executable = manifest.CFBundleExecutable;
        if (typeof executable !== "string" || executable.length === 0 || executable === "." || executable === ".." || /[\\/:\0]/.test(executable)) {
            throw new Error("The bundle executable must be a single safe filename.");
        }
        const executablePath = join(bundlePath, executable);
        if (!lstatSync(executablePath).isFile()) {
            throw new Error("The bundle executable must be a regular file.");
        }
        report.bundleIdentifier = manifest.CFBundleIdentifier;
        report.executable = executable;
        report.phase = "native-inventory";
        const nativePaths = findNativeCode(bundlePath).sort();
        if (!nativePaths.includes(executablePath)) {
            throw new Error("The bundle executable must be Mach-O native code.");
        }

        report.phase = "simulator-platform";
        const architecture = invoke("lipo", ["-archs", executablePath], "architecture.log").stdout.trim();
        if (architecture !== "arm64") {
            throw new Error("Expected an ARM64 simulator executable.");
        }
        const platformOutput = invoke("xcrun", ["vtool", "-show-build", executablePath], "platform.log").stdout;
        const platforms = [...platformOutput.matchAll(/^\s*platform\s+(\S+)\s*$/gm)].map(match => match[1]);
        if (platforms.length !== 1 || platforms[0] !== "IOSSIMULATOR") {
            throw new Error("The native executable must target IOSSIMULATOR, not an iPhone or macOS.");
        }

        report.phase = "signature-display";
        const display = invoke("codesign", ["--display", "--verbose=4", bundlePath], "signature-display.log", true);
        report.signature = display.status === 0 && /^Signature=adhoc\s*$/m.test(`${display.stdout}\n${display.stderr}`) ? "adhoc" : "unknown";
        report.phase = "verify-native";
        for (const [index, path] of nativePaths.entries()) {
            const result = invoke("codesign", ["--verify", "--strict", "--verbose=4", path], `native-${String(index + 1).padStart(3, "0")}.log`, true);
            report.nativeCode.push({
                path: relative(bundlePath, path).split(sep).join("/"),
                status: result.status === 0 ? "passed" : "invalid", exitStatus: result.status
            });
        }
        report.phase = "verify-bundle";
        const verification = invoke("codesign", ["--verify", "--deep", "--strict", "--verbose=4", bundlePath], "bundle-verification.log", true);
        report.bundleVerification = { status: verification.status === 0 ? "passed" : "invalid", exitStatus: verification.status };
        const valid = report.signature === "adhoc" && report.nativeCode.every(item => item.status === "passed") && verification.status === 0;
        report.status = valid ? "passed" : "invalid";
        if (!valid) {
            report.failure = "The simulator bundle does not have a valid ad-hoc signature. Inspect the signature diagnostics.";
            if (!observeOnly) failure = new Error(report.failure);
        }
        report.phase = "complete";
    } catch (error) {
        failure = error;
        report.status = "failed";
        report.failure = error.message;
    } finally {
        report.finishedAt = new Date().toISOString();
        try {
            writeFileSync(join(outputDirectory, "result.json"), `${JSON.stringify(report, null, 2)}\n`);
        } catch (error) {
            failure ??= error;
        }
    }
    if (failure) throw failure;
    return report;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        if ((process.argv.length !== 4 && process.argv.length !== 5) || (process.argv.length === 5 && process.argv[4] !== "--observe")) {
            throw new Error("Usage: node scripts/verify-ios-simulator-signature.mjs <built.app> <new-output-directory> [--observe]");
        }
        if (process.platform !== "darwin") {
            throw new Error("Signature verification requires macOS and Apple build tools.");
        }
        console.log(JSON.stringify(verifyIosSimulatorSignature({
            bundlePath: process.argv[2], outputDirectory: process.argv[3], observeOnly: process.argv[4] === "--observe"
        }), null, 2));
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
