import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, realpathSync, renameSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { test } from "node:test";
import { verifyIosDeviceBundle } from "../verify-ios-device-bundle.mjs";

const executableName = "NutriFlow.Mobile";
const platformOutput = "NutriFlow.Mobile:\nLoad command 1\n      cmd LC_BUILD_VERSION\n  cmdsize 32\n platform IOS\n    minos 15.0\n      sdk 26.0\n   ntools 0\n";
const expectedProperties = {
    TargetFramework: "net10.0-ios",
    RuntimeIdentifier: "ios-arm64",
    Configuration: "Release",
    EnableCodeSigning: "false",
    BuildIpa: "false",
    ArchiveOnBuild: "false",
    UseInterpreter: "false",
    MtouchInterpreter: "",
    PublishAot: "false",
    MtouchUseLlvm: "true",
    NuGetLockFilePath: "packages.ios-device.lock.json"
};

function fixture(context, { change = () => undefined, manifest, properties = {} } = {}) {
    const directory = realpathSync(mkdtempSync(join(tmpdir(), "nutriflow-ios-device-")));
    context.after(() => rmSync(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }));
    const bundlePath = join(directory, "NutriFlow with spaces.app");
    const outputDirectory = join(directory, "device verification with spaces");
    const buildPropertiesPath = join(directory, "build properties.json");
    const infoPath = join(bundlePath, "Info.plist");
    const privacyPath = join(bundlePath, "PrivacyInfo.xcprivacy");
    const mainPath = join(bundlePath, executableName);
    const aotDataPath = join(bundlePath, `${executableName}.aotdata.arm64`);
    mkdirSync(bundlePath);
    mkdirSync(join(bundlePath, "Resources"));
    writeFileSync(infoPath, "synthetic built plist");
    writeFileSync(privacyPath, "synthetic privacy manifest");
    writeFileSync(mainPath, Buffer.from("cffaedfe000000000000000000000000", "hex"));
    writeFileSync(aotDataPath, Buffer.from("synthetic Mono AOT data"));
    writeFileSync(join(bundlePath, "NutriFlow.Mobile.dll"), Buffer.from("4d5a000001020304", "hex"));
    writeFileSync(join(bundlePath, "Resources", "logo.png"), Buffer.from("89504e470d0a1a0a", "hex"));
    const selectedProperties = { ...expectedProperties, ...properties };
    writeFileSync(buildPropertiesPath, `${JSON.stringify({ Properties: selectedProperties }, null, 2)}\n`);
    const data = manifest ?? {
        CFBundleIdentifier: "com.nutriflow.app",
        CFBundleSupportedPlatforms: ["iPhoneOS"],
        CFBundleExecutable: executableName
    };
    const calls = [];

    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, calls, options);
        if (changed !== undefined) return changed;
        if (command === "plutil" && args[0] === "-convert") return { status: 0, stdout: JSON.stringify(data), stderr: "" };
        if (command === "plutil" && args[0] === "-lint") return { status: 0, stdout: `${infoPath}: OK\n${privacyPath}: OK\n`, stderr: "" };
        if (command === "lipo") return { status: 0, stdout: "arm64\n", stderr: "" };
        if (command === "xcrun") return { status: 0, stdout: platformOutput, stderr: "" };
        assert.fail(`Unexpected command: ${command} ${args.join(" ")}`);
    }

    return {
        directory, bundlePath, outputDirectory, buildPropertiesPath, infoPath, privacyPath, mainPath, aotDataPath, calls, selectedProperties,
        run: (options = {}) => verifyIosDeviceBundle({ bundlePath, buildPropertiesPath, outputDirectory, execute, ...options }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

function snapshot(directory) {
    const result = {};
    function visit(path) {
        for (const entry of readdirSync(path, { withFileTypes: true })) {
            const item = join(path, entry.name);
            if (entry.isDirectory()) visit(item);
            else result[relative(directory, item)] = readFileSync(item);
        }
    }
    visit(directory);
    return result;
}

function makeDirectoryLink(context, target, link) {
    try {
        symlinkSync(target, link, process.platform === "win32" ? "junction" : "dir");
        return true;
    } catch (error) {
        if (["EPERM", "EACCES", "ENOTSUP"].includes(error.code)) {
            context.skip(`Directory links are unavailable: ${error.code}`);
            return false;
        }
        throw error;
    }
}

test("verifies the Release device bundle read-only without claiming signing, installation or launch", context => {
    const sample = fixture(context);
    const before = snapshot(sample.bundlePath);
    const propertiesBefore = readFileSync(sample.buildPropertiesPath);
    const report = sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.phase, "complete");
    assert.equal(report.runtimeIdentifier, "ios-arm64");
    assert.equal(report.aotData, `${executableName}.aotdata.arm64`);
    assert.deepEqual(report.deployment, { signing: "not-verified", installation: "not-tested", launch: "not-tested" });
    assert.deepEqual(report.properties, sample.selectedProperties);
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.deepEqual(readFileSync(sample.buildPropertiesPath), propertiesBefore);
    assert.deepEqual(sample.calls.map(call => call.command), ["plutil", "plutil", "lipo", "xcrun"]);
    assert.deepEqual(sample.calls.find(call => call.command === "plutil" && call.args[0] === "-convert").args,
        ["-convert", "json", "-o", "-", sample.infoPath]);
    assert.deepEqual(sample.calls.find(call => call.command === "plutil" && call.args[0] === "-lint").args,
        ["-lint", sample.infoPath, sample.privacyPath]);
    assert.deepEqual(sample.calls[2].args, ["-archs", sample.mainPath]);
    assert.deepEqual(sample.calls[3].args, ["vtool", "-show-build", sample.mainPath]);
    for (const call of sample.calls) {
        assert.equal(call.options.shell, false);
        assert.equal(call.options.killSignal, "SIGKILL");
        assert.equal(call.options.maxBuffer, 1024 * 1024);
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 15_000);
        assert.ok(!call.args.some(argument => /--sign|--install|--launch/.test(argument)));
    }
    assert.deepEqual(sample.report(), report);
    assert.ok(readFileSync(join(sample.outputDirectory, "commands.log"), "utf8").length > 0);
    assert.ok(readdirSync(sample.outputDirectory).length >= 6);
});

test("accepts case-insensitive MSBuild boolean values", context => {
    const sample = fixture(context, { properties: {
        EnableCodeSigning: "FALSE", BuildIpa: "False", ArchiveOnBuild: "FALSE",
        UseInterpreter: "False", PublishAot: "FALSE", MtouchUseLlvm: "TRUE"
    } });
    assert.equal(sample.run().status, "passed");
});

test("accepts a device lock path by its filename", context => {
    const sample = fixture(context, { properties: { NuGetLockFilePath: "locks/packages.ios-device.lock.json" } });
    assert.equal(sample.run().status, "passed");
});

for (const [property, value] of [
    ["TargetFramework", "net10.0-android"],
    ["RuntimeIdentifier", "iossimulator-arm64"],
    ["Configuration", "Debug"],
    ["EnableCodeSigning", "true"],
    ["BuildIpa", "true"],
    ["ArchiveOnBuild", "true"],
    ["UseInterpreter", "true"],
    ["MtouchInterpreter", "all"],
    ["PublishAot", "true"],
    ["MtouchUseLlvm", "false"],
    ["NuGetLockFilePath", "packages.ios.lock.json"]
]) {
    test(`rejects a build property outside the device contract: ${property}`, context => {
        const sample = fixture(context, { properties: { [property]: value } });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.equal(sample.calls.length, 0);
    });
}

for (const content of ["not JSON", "null", "[]", "{}", '{"Properties":null}', '{"Properties":[]}']) {
    test(`rejects missing or malformed build property data: ${content}`, context => {
        const sample = fixture(context);
        writeFileSync(sample.buildPropertiesPath, content);
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.equal(sample.calls.length, 0);
    });
}

test("rejects a missing required build property", context => {
    const sample = fixture(context);
    const properties = { ...sample.selectedProperties };
    delete properties.UseInterpreter;
    writeFileSync(sample.buildPropertiesPath, JSON.stringify({ Properties: properties }));
    assert.throws(() => sample.run());
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.calls.length, 0);
});

for (const [name, manifest] of [
    ["another identifier", { CFBundleIdentifier: "another.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: executableName }],
    ["simulator platform", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: executableName }],
    ["mixed platforms", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS", "iPhoneSimulator"], CFBundleExecutable: executableName }],
    ["platform as string", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: "iPhoneOS", CFBundleExecutable: executableName }],
    ["missing executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"] }],
    ["parent executable path", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "../outside" }],
    ["nested executable path", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "Frameworks/native" }],
    ["Windows executable path", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "..\\outside" }],
    ["drive-relative executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "C:outside" }],
    ["empty executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "" }],
    ["current directory executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "." }],
    ["parent directory executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: ".." }],
    ["NUL executable", { CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "native\0outside" }],
    ["array manifest", []]
]) {
    test(`rejects a manifest with ${name}`, context => {
        const sample = fixture(context, { manifest });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(sample.calls.every(call => call.command === "plutil"));
    });
}

for (const output of ["malformed JSON", "null"]) {
    test(`rejects invalid manifest JSON: ${output}`, context => {
        const sample = fixture(context, { change: (command, args) => command === "plutil" && args[0] === "-convert" ?
            { status: 0, stdout: output } : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(sample.calls.every(call => call.command === "plutil"));
        assert.equal(sample.calls.filter(call => call.args[0] === "-convert").length, 1);
    });
}

for (const architecture of ["x86_64", "arm64 x86_64", "arm64 arm64", "arm64e", ""]) {
    test(`rejects an unsupported device architecture: ${architecture}`, context => {
        const sample = fixture(context, { change: command => command === "lipo" ? { status: 0, stdout: architecture } : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => call.command === "xcrun"));
    });
}

for (const output of [
    platformOutput.replace("platform IOS\n", "platform IOSSIMULATOR\n"),
    platformOutput.replace("platform IOS\n", "platform MACOS\n"),
    platformOutput.replace(" platform IOS\n", ""),
    `${platformOutput}\n platform IOS\n`,
    `${platformOutput}\n platform IOSSIMULATOR\n`
]) {
    test(`rejects a missing or ambiguous device native platform: ${JSON.stringify(output)}`, context => {
        const sample = fixture(context, { change: command => command === "xcrun" ? { status: 0, stdout: output } : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
    });
}

for (const [name, response] of [
    ["nonzero exit", { status: 17, stdout: "partial output", stderr: "original tool failure" }],
    ["timeout", { status: null, stdout: "partial output", error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" }) }],
    ["spawn error", { status: null, error: Object.assign(new Error("missing tool"), { code: "ENOENT" }) }],
    ["null exit status", { status: null, stdout: "" }],
    ["missing exit status", { stdout: "" }],
    ["string exit status", { status: "0", stdout: "" }]
]) {
    test(`fails a native tool ${name} and preserves its diagnostic result`, context => {
        const sample = fixture(context, { change: command => command === "lipo" ? response : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => call.command === "xcrun"));
        if (response.stdout) assert.match(readFileSync(join(sample.outputDirectory, "architecture.log"), "utf8"), /partial output/);
        if (response.stderr) assert.match(sample.report().failure, /original tool failure/);
    });
}

test("rejects invalid privacy manifest lint without reaching native inspection", context => {
    const sample = fixture(context, { change: (command, args) => command === "plutil" && args[0] === "-lint" ?
        { status: 1, stderr: "invalid PrivacyInfo.xcprivacy" } : undefined });
    assert.throws(() => sample.run(), /PrivacyInfo/);
    assert.equal(sample.report().status, "failed");
    assert.ok(sample.calls.every(call => call.command === "plutil"));
});

for (const [name, target] of [["manifest", "infoPath"], ["privacy manifest", "privacyPath"], ["executable", "mainPath"]]) {
    test(`rejects a nonregular bundle ${name}`, context => {
        const sample = fixture(context);
        rmSync(sample[target]);
        mkdirSync(sample[target]);
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
    });
}

for (const kind of ["missing", "empty", "directory"]) {
    test(`rejects ${kind} app assembly AOT data`, context => {
        const sample = fixture(context);
        rmSync(sample.aotDataPath);
        if (kind === "empty") writeFileSync(sample.aotDataPath, "");
        if (kind === "directory") mkdirSync(sample.aotDataPath);
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
    });
}

test("rejects an embedded provisioning profile in the build-only bundle", context => {
    const sample = fixture(context);
    writeFileSync(join(sample.bundlePath, "embedded.mobileprovision"), "unexpected provisioning profile");
    assert.throws(() => sample.run(), /provision/i);
    assert.equal(sample.report().status, "failed");
});

test("refuses an existing diagnostics directory without overwriting an old report", context => {
    const sample = fixture(context);
    mkdirSync(sample.outputDirectory);
    writeFileSync(join(sample.outputDirectory, "result.json"), "old report");
    assert.throws(() => sample.run(), /EEXIST|exist/i);
    assert.equal(readFileSync(join(sample.outputDirectory, "result.json"), "utf8"), "old report");
    assert.equal(sample.calls.length, 0);
});

test("refuses diagnostics within the bundle without changing its files", context => {
    const sample = fixture(context);
    const before = snapshot(sample.bundlePath);
    assert.throws(() => sample.run({ outputDirectory: join(sample.bundlePath, "diagnostics") }), /outside|inside|bundle/i);
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.equal(sample.calls.length, 0);
});

test("checks the real diagnostics parent so an alias cannot write into the bundle", context => {
    const sample = fixture(context);
    const link = join(sample.directory, "bundle alias");
    if (!makeDirectoryLink(context, sample.bundlePath, link)) return;
    const before = snapshot(sample.bundlePath);
    assert.throws(() => sample.run({ outputDirectory: join(link, "diagnostics") }), /outside|inside|bundle/i);
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.equal(sample.calls.length, 0);
});

test("rejects a linked bundle root", context => {
    const sample = fixture(context);
    const link = join(sample.directory, "linked app.app");
    if (!makeDirectoryLink(context, sample.bundlePath, link)) return;
    assert.throws(() => sample.run({ bundlePath: link }), /directory|link/i);
    assert.equal(sample.calls.length, 0);
});

test("rejects a regular file used as the bundle root", context => {
    const sample = fixture(context);
    renameSync(sample.bundlePath, `${sample.bundlePath}-moved`);
    writeFileSync(sample.bundlePath, "not a bundle directory");
    assert.throws(() => sample.run(), /directory/i);
    assert.equal(sample.calls.length, 0);
});

test("enforces the one-minute deadline after a native command", context => {
    let currentTime = 0;
    const sample = fixture(context, { change: () => { currentTime = 60_001; } });
    assert.throws(() => sample.run({ now: () => currentTime }), /deadline|time|budget/i);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.calls.length, 1);
});

test("checks the deadline after the final native platform command", context => {
    let currentTime = 0;
    const sample = fixture(context, { change: command => { if (command === "xcrun") currentTime = 60_001; } });
    assert.throws(() => sample.run({ now: () => currentTime }), /deadline|time|budget/i);
    assert.equal(sample.report().status, "failed");
});

test("a lost diagnostics directory preserves the original native tool failure", context => {
    const sample = fixture(context, { change: command => {
        if (command === "lipo") {
            renameSync(sample.outputDirectory, `${sample.outputDirectory}-moved`);
            return { status: 17, stderr: "original architecture-tool failure" };
        }
    } });
    assert.throws(() => sample.run(), /original architecture-tool failure/);
});

test("the tracked device lock preserves the simulator package graph with its own runtime section", () => {
    const device = JSON.parse(readFileSync(new URL("../../mobile/NutriFlow.Mobile/packages.ios-device.lock.json", import.meta.url), "utf8"));
    const simulator = JSON.parse(readFileSync(new URL("../../mobile/NutriFlow.Mobile/packages.ios.lock.json", import.meta.url), "utf8"));
    const framework = "net10.0-ios26.0";
    assert.equal(device.version, 1);
    assert.deepEqual(Object.keys(device.dependencies).sort(), [framework, `${framework}/ios-arm64`]);
    assert.deepEqual(device.dependencies[framework], simulator.dependencies[framework]);
    assert.equal(Object.keys(device.dependencies[framework]).length, 20);
    assert.deepEqual(device.dependencies[`${framework}/ios-arm64`], {});
});

test("device workflow isolates the Release RID and lock while preserving the pinned toolchain", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/ios-device.yml", import.meta.url), "utf8");
    const simulator = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    const sdk = JSON.parse(readFileSync(new URL("../../global.json", import.meta.url), "utf8"));
    assert.match(workflow, /on:\s+workflow_dispatch:/);
    assert.ok(!/^\s+(?:push|pull_request|schedule):/m.test(workflow));
    assert.match(workflow, /permissions:\s+contents: read/);
    assert.match(workflow, /runs-on: macos-15/);
    assert.equal(sdk.sdk.version, "10.0.400");
    assert.ok(workflow.includes("global-json-file: global.json"));
    for (const pin of ["DEVELOPER_DIR: /Applications/Xcode_26.0.1.app/Contents/Developer", "WORKLOAD_VERSION: '10.0.100'"]) {
        assert.ok(workflow.includes(pin));
        assert.ok(simulator.includes(pin));
    }
    assert.ok(workflow.includes("IOS_RUNTIME: ios-arm64"));
    assert.ok(workflow.includes("DEVICE_LOCK: packages.ios-device.lock.json"));
    assert.ok(workflow.includes("xcrun --sdk iphoneos --show-sdk-version"));
    assert.ok(workflow.includes("workload install maui-ios"));
    assert.ok(workflow.includes('--version "$WORKLOAD_VERSION" --source https://api.nuget.org/v3/index.json'));
    assert.ok(workflow.includes('test "$(dotnet workload --version)" = "$WORKLOAD_VERSION"'));
    assert.ok(workflow.includes('NUGET_HTTP_CACHE_PATH="$RUNNER_TEMP/nuget-http-cache-workload"'));
    assert.ok(workflow.includes('NUGET_HTTP_CACHE_PATH=%s\\n'));
    const lines = workflow.split(/\r?\n/);
    const commands = [];
    for (let index = 0; index < lines.length; index++) {
        if (!/^\s*dotnet (?:restore|build|msbuild)\b/.test(lines[index])) continue;
        let command = lines[index].trim();
        while (lines[index].trimEnd().endsWith("\\")) command += `\n${lines[++index]}`;
        commands.push(command);
    }
    assert.equal(commands.length, 4);
    for (const command of commands) assert.ok(command.includes('-p:NuGetLockFilePath="$DEVICE_LOCK"'));
    const restores = commands.filter(command => command.startsWith("dotnet restore"));
    assert.equal(restores.length, 2);
    assert.ok(restores[1].includes("--locked-mode --warnaserror"));
    assert.ok(workflow.includes('if [[ ! -f "mobile/NutriFlow.Mobile/$DEVICE_LOCK" ]]; then'));
    assert.match(workflow, /git diff --exit-code -- mobile\/NutriFlow.Mobile\/packages.android.lock.json\s+\\\r?\n\s+mobile\/NutriFlow.Mobile\/packages.ios.lock.json "mobile\/NutriFlow.Mobile\/\$DEVICE_LOCK"/);
    const build = commands.find(command => command.startsWith("dotnet build"));
    assert.ok(build.includes('-f net10.0-ios --runtime "$IOS_RUNTIME"'));
    assert.ok(build.includes("--configuration Release --no-restore --warnaserror --verbosity normal"));
    for (const option of [
        "-p:UseInterpreter=false", "-p:MtouchInterpreter=", "-p:PublishAot=false", "-p:MtouchUseLlvm=true",
        "-p:EnableCodeSigning=false", "-p:BuildIpa=false", "-p:ArchiveOnBuild=false"
    ]) {
        assert.ok(build.includes(option));
        assert.ok(commands.find(command => command.startsWith("dotnet msbuild")).includes(option));
    }
    assert.ok(!/\bsimctl\b|\bdevicectl\b|\bcodesign\b|CodesignKey|APPLE_ID|APPLE_TEAM_ID|secrets\.|\.p12|\.mobileprovision|continue-on-error|PublishAot=true|RunAOTCompilation/i.test(workflow));
});

test("device workflow verifies before packaging and retains bounded failure diagnostics", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/ios-device.yml", import.meta.url), "utf8");
    const names = [
        "Build Release device app without Apple signing", "Verify physical device bundle",
        "Archive device build and dependency lock", "Upload device build", "Upload device build diagnostics"
    ];
    const offsets = names.map(name => workflow.indexOf(`- name: ${name}`));
    assert.ok(offsets.every(offset => offset >= 0));
    assert.deepEqual([...offsets].sort((left, right) => left - right), offsets);
    assert.ok(workflow.includes("node --test scripts/tests/ios-device.test.mjs"));
    const build = workflow.slice(offsets[0], offsets[1]);
    assert.ok(build.includes('2>&1 | tee "$RUNNER_TEMP/ios-device-build.log"'));
    assert.ok(build.includes('-getProperty:TargetFramework,RuntimeIdentifier,Configuration,NuGetLockFilePath,UseInterpreter,MtouchInterpreter,PublishAot,MtouchUseLlvm,EnableCodeSigning,BuildIpa,ArchiveOnBuild'));
    const verification = workflow.slice(offsets[1], offsets[2]);
    assert.ok(verification.includes("scripts/verify-ios-device-bundle.mjs"));
    assert.ok(verification.includes('bin/Release/net10.0-ios/$IOS_RUNTIME/NutriFlow.Mobile.app'));
    assert.ok(verification.includes('"$RUNNER_TEMP/ios-device-build-properties.json" "$RUNNER_TEMP/ios-device-verification"'));
    const packaging = workflow.slice(offsets[2], offsets[3]);
    assert.ok(packaging.includes("NutriFlow-ios-arm64-without-apple-signing.tar.gz"));
    assert.ok(packaging.includes('cp "mobile/NutriFlow.Mobile/$DEVICE_LOCK" ios-device-artifacts/'));
    assert.ok(!packaging.includes(".ipa"));
    const diagnostics = workflow.slice(offsets[4]);
    assert.ok(diagnostics.includes("!cancelled()"));
    assert.ok(diagnostics.includes("steps.device_build.outcome == 'success' || steps.device_build.outcome == 'failure'"));
    for (const path of ["ios-device-build.log", "ios-device-build-properties.json", "ios-device-verification/"]) {
        assert.ok(diagnostics.includes(`\${{ runner.temp }}/${path}`));
    }
    assert.ok(diagnostics.includes("if-no-files-found: warn"));
    assert.ok(diagnostics.includes("retention-days: 7"));
    assert.ok(workflow.slice(offsets[3], offsets[4]).includes("if-no-files-found: error"));
    assert.ok(workflow.slice(offsets[3], offsets[4]).includes("retention-days: 7"));
    assert.match(workflow, /name: Verify physical device bundle\s+timeout-minutes: 2/);
});
