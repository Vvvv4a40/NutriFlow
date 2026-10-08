import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync, lstatSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export function verifyIosDeviceBundle({ bundlePath, buildPropertiesPath, outputDirectory, execute = spawnSync, now = Date.now }) {
    bundlePath = resolve(bundlePath);
    if (!lstatSync(bundlePath).isDirectory()) throw new Error("The app bundle must be a regular directory.");
    bundlePath = realpathSync(bundlePath);
    outputDirectory = resolve(outputDirectory);
    outputDirectory = join(realpathSync(dirname(outputDirectory)), basename(outputDirectory));
    const outputRelativePath = relative(bundlePath, outputDirectory);
    if (outputRelativePath === "" || (!isAbsolute(outputRelativePath) && outputRelativePath !== ".." && !outputRelativePath.startsWith(`..${sep}`))) {
        throw new Error("Device diagnostics must be outside the app bundle.");
    }
    mkdirSync(outputDirectory);

    const report = {
        status: "running", startedAt: new Date().toISOString(), runtimeIdentifier: "ios-arm64",
        deployment: { signing: "not-verified", installation: "not-tested", launch: "not-tested" }
    };
    const deadline = now() + 60_000;
    let failure;

    function checkDeadline() {
        const remaining = deadline - now();
        if (remaining <= 0) throw new Error("The device verification deadline was exceeded.");
        return remaining;
    }

    function invoke(command, args, file) {
        const timeout = Math.min(15_000, checkDeadline());
        appendFileSync(join(outputDirectory, "commands.log"), `${JSON.stringify([command, ...args])}\n`);
        const result = execute(command, args, {
            encoding: "utf8", timeout, maxBuffer: 1024 * 1024, killSignal: "SIGKILL", shell: false
        });
        let commandFailure = result.error || !Number.isInteger(result.status) || result.status !== 0
            ? new Error(`${command} ${args.join(" ")} failed: ${result.error?.code ?? result.status}; ${result.stderr?.trim() ?? ""}`)
            : null;
        try { checkDeadline(); } catch (error) { commandFailure ??= error; }
        try {
            writeFileSync(join(outputDirectory, file), `${result.stdout ?? ""}\n${result.stderr ?? ""}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`);
        } catch (error) { throw commandFailure ?? error; }
        if (commandFailure) throw commandFailure;
        return result.stdout ?? "";
    }

    function requireFile(path) {
        const info = lstatSync(path);
        if (!info.isFile() || info.size === 0) throw new Error(`Expected a non-empty regular file: ${basename(path)}`);
    }

    try {
        report.phase = "build-properties";
        requireFile(buildPropertiesPath);
        const properties = JSON.parse(readFileSync(buildPropertiesPath, "utf8")).Properties;
        const expected = {
            TargetFramework: "net10.0-ios", RuntimeIdentifier: "ios-arm64", Configuration: "Release",
            EnableCodeSigning: "false", BuildIpa: "false", ArchiveOnBuild: "false",
            UseInterpreter: "false", MtouchInterpreter: "", PublishAot: "false", MtouchUseLlvm: "true"
        };
        for (const [name, value] of Object.entries(expected)) {
            const actual = properties?.[name];
            const matches = typeof actual === "string" &&
                (["true", "false"].includes(value) ? actual.toLowerCase() === value : actual === value);
            if (!matches) throw new Error(`Unexpected device build property: ${name}`);
        }
        if (typeof properties.NuGetLockFilePath !== "string" || basename(properties.NuGetLockFilePath) !== "packages.ios-device.lock.json") {
            throw new Error("Expected the separate iOS device dependency lock.");
        }
        report.properties = properties;
        writeFileSync(join(outputDirectory, "build-properties.json"), `${JSON.stringify({ Properties: properties }, null, 2)}\n`);

        report.phase = "manifest";
        if (existsSync(join(bundlePath, "embedded.mobileprovision"))) throw new Error("A provisioning profile must not be published by this unsigned build check.");
        const infoPath = join(bundlePath, "Info.plist");
        const privacyPath = join(bundlePath, "PrivacyInfo.xcprivacy");
        requireFile(infoPath);
        requireFile(privacyPath);
        invoke("plutil", ["-lint", infoPath, privacyPath], "plist-validation.log");
        const manifest = JSON.parse(invoke("plutil", ["-convert", "json", "-o", "-", infoPath], "manifest.log"));
        if (manifest?.CFBundleIdentifier !== "com.nutriflow.app" ||
            !Array.isArray(manifest.CFBundleSupportedPlatforms) ||
            manifest.CFBundleSupportedPlatforms.length !== 1 || manifest.CFBundleSupportedPlatforms[0] !== "iPhoneOS") {
            throw new Error("Expected the NutriFlow physical iOS device bundle.");
        }
        const executable = manifest.CFBundleExecutable;
        if (typeof executable !== "string" || executable.length === 0 || executable === "." || executable === ".." || /[\\/:\0]/.test(executable)) {
            throw new Error("The bundle executable must be a single safe filename.");
        }
        const executablePath = join(bundlePath, executable);
        requireFile(executablePath);
        report.bundleIdentifier = manifest.CFBundleIdentifier;
        report.executable = executable;

        report.phase = "device-platform";
        const architecture = invoke("lipo", ["-archs", executablePath], "architecture.log").trim();
        if (architecture !== "arm64") throw new Error("Expected an ARM64 device executable.");
        const platformOutput = invoke("xcrun", ["vtool", "-show-build", executablePath], "platform.log");
        const platforms = [...platformOutput.matchAll(/^\s*platform\s+(\S+)\s*$/gm)].map(match => match[1]);
        if (platforms.length !== 1 || platforms[0] !== "IOS") throw new Error("The native executable must target IOS, not a simulator or macOS.");

        report.phase = "aot-output";
        const aotData = "NutriFlow.Mobile.aotdata.arm64";
        requireFile(join(bundlePath, aotData));
        report.aotData = aotData;
        checkDeadline();
        report.status = "passed";
        report.phase = "complete";
    } catch (error) {
        failure = error;
        report.status = "failed";
        report.failure = error.message;
    } finally {
        report.finishedAt = new Date().toISOString();
        try { writeFileSync(join(outputDirectory, "result.json"), `${JSON.stringify(report, null, 2)}\n`); }
        catch (error) { failure ??= error; }
    }
    if (failure) throw failure;
    return report;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        if (process.argv.length !== 5) throw new Error("Usage: node scripts/verify-ios-device-bundle.mjs <built.app> <build-properties.json> <new-output-directory>");
        if (process.platform !== "darwin") throw new Error("Device bundle verification requires macOS and Apple build tools.");
        console.log(JSON.stringify(verifyIosDeviceBundle({
            bundlePath: process.argv[2], buildPropertiesPath: process.argv[3], outputDirectory: process.argv[4]
        }), null, 2));
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
