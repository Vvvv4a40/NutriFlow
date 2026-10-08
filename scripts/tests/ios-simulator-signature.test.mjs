import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, renameSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, relative } from "node:path";
import { test } from "node:test";
import { verifyIosSimulatorSignature } from "../verify-ios-simulator-signature.mjs";

const bundleIdentifier = "com.nutriflow.app";
const executableName = "NutriFlow.Mobile";
const machO = Buffer.from("cffaedfe000000000000000000000000", "hex");
const platformOutput = "NutriFlow.Mobile:\nLoad command 1\n      cmd LC_BUILD_VERSION\n  cmdsize 32\n platform IOSSIMULATOR\n    minos 15.0\n      sdk 26.0\n   ntools 0\n";

function fixture(context, { change = () => undefined, manifest } = {}) {
    const directory = realpathSync(mkdtempSync(join(tmpdir(), "nutriflow-ios-signature-")));
    context.after(() => rmSync(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }));
    const bundlePath = join(directory, "NutriFlow with spaces.app");
    const outputDirectory = join(directory, "signature output with spaces");
    const mainPath = join(bundlePath, executableName);
    const frameworkPath = join(bundlePath, "Frameworks", "Nutri.Native.framework", "Nutri.Native");
    const dylibPath = join(bundlePath, "lib with spaces.dylib");
    const infoPath = join(bundlePath, "Info.plist");
    mkdirSync(join(frameworkPath, ".."), { recursive: true });
    writeFileSync(infoPath, "synthetic plist");
    writeFileSync(mainPath, machO);
    writeFileSync(frameworkPath, machO);
    writeFileSync(dylibPath, machO);
    writeFileSync(join(bundlePath, "NutriFlow.Domain.dll"), Buffer.from("4d5a000001020304", "hex"));
    writeFileSync(join(bundlePath, "logo.png"), Buffer.from("89504e470d0a1a0a", "hex"));
    const data = manifest ?? {
        CFBundleIdentifier: bundleIdentifier,
        CFBundleSupportedPlatforms: ["iPhoneSimulator"],
        CFBundleExecutable: executableName
    };
    const calls = [];

    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, calls, options);
        if (changed !== undefined) return changed;
        if (command === "plutil") return { status: 0, stdout: JSON.stringify(data), stderr: "" };
        if (command === "lipo") return { status: 0, stdout: "arm64\n", stderr: "" };
        if (command === "xcrun") return { status: 0, stdout: platformOutput, stderr: "" };
        assert.equal(command, "codesign");
        if (args[0] === "--display") {
            return { status: 0, stdout: "", stderr: `Executable=${mainPath}\nIdentifier=${bundleIdentifier}\nSignature=adhoc\n` };
        }
        return { status: 0, stdout: "", stderr: "valid on disk\nsatisfies its Designated Requirement\n" };
    }

    return {
        directory, bundlePath, outputDirectory, mainPath, frameworkPath, dylibPath, infoPath, calls,
        run: (options = {}) => verifyIosSimulatorSignature({ bundlePath, outputDirectory, execute, ...options }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

function verificationCalls(sample) {
    return sample.calls.filter(call => call.command === "codesign" && call.args[0] === "--verify");
}

function makeLink(context, target, link) {
    try {
        symlinkSync(target, link, process.platform === "win32" ? "junction" : "dir");
        return true;
    } catch (error) {
        if (["EPERM", "EACCES", "ENOTSUP"].includes(error.code)) {
            context.skip(`Symbolic links are unavailable: ${error.code}`);
            return false;
        }
        throw error;
    }
}

test("verifies ad-hoc arm64 simulator code without changing any bundle file", context => {
    const sample = fixture(context);
    const before = [sample.infoPath, sample.mainPath, sample.frameworkPath, sample.dylibPath]
        .map(path => readFileSync(path));
    const report = sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.mode, "require-valid");
    assert.equal(report.signature, "adhoc");
    assert.equal(report.bundleIdentifier, bundleIdentifier);
    assert.equal(report.executable, executableName);
    assert.deepEqual(report.bundleVerification, { status: "passed", exitStatus: 0 });
    const expected = [sample.mainPath, sample.frameworkPath, sample.dylibPath]
        .map(path => relative(sample.bundlePath, path).replaceAll("\\", "/")).sort();
    assert.deepEqual(report.nativeCode.map(item => item.path).sort(), expected);
    assert.ok(report.nativeCode.every(item => item.status === "passed" && item.exitStatus === 0));
    assert.deepEqual(sample.calls.find(call => call.command === "plutil").args, ["-convert", "json", "-o", "-", sample.infoPath]);
    assert.deepEqual(sample.calls.find(call => call.command === "lipo").args, ["-archs", sample.mainPath]);
    assert.deepEqual(sample.calls.find(call => call.command === "xcrun").args, ["vtool", "-show-build", sample.mainPath]);
    assert.deepEqual(sample.calls.find(call => call.command === "codesign" && call.args[0] === "--display").args,
        ["--display", "--verbose=4", sample.bundlePath]);
    const verifications = verificationCalls(sample);
    assert.equal(verifications.length, 4);
    assert.deepEqual(verifications.at(-1).args, ["--verify", "--deep", "--strict", "--verbose=4", sample.bundlePath]);
    for (const call of verifications.slice(0, -1)) {
        assert.deepEqual(call.args.slice(0, -1), ["--verify", "--strict", "--verbose=4"]);
    }
    assert.deepEqual(verifications.slice(0, -1).map(call => call.args.at(-1)).sort(),
        [sample.mainPath, sample.frameworkPath, sample.dylibPath].sort());
    for (const call of sample.calls) {
        assert.equal(call.options.shell, false);
        assert.equal(call.options.killSignal, "SIGKILL");
        assert.equal(call.options.maxBuffer, 1024 * 1024);
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 15_000);
        assert.ok(!call.args.includes("--sign"));
    }
    assert.deepEqual([sample.infoPath, sample.mainPath, sample.frameworkPath, sample.dylibPath]
        .map(path => readFileSync(path)), before);
    assert.equal(sample.report().status, "passed");
    assert.deepEqual(JSON.parse(readFileSync(join(sample.outputDirectory, "manifest.log"), "utf8").split("\n")[0]), {
        CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: executableName
    });
    for (const name of ["commands.log", "architecture.log", "platform.log", "signature-display.log", "native-001.log", "native-002.log", "native-003.log", "bundle-verification.log"]) {
        assert.ok(readFileSync(join(sample.outputDirectory, name), "utf8").length > 0);
    }
});

test("observes an already valid signature without treating managed DLLs and assets as native code", context => {
    const sample = fixture(context);
    const report = sample.run({ observeOnly: true });
    assert.equal(report.status, "passed");
    assert.equal(report.mode, "observe");
    assert.equal(report.nativeCode.length, 3);
    assert.ok(verificationCalls(sample).every(call => !["NutriFlow.Domain.dll", "logo.png", "Info.plist"].includes(basename(call.args.at(-1)))));
});

test("identifies native code by its header rather than a filename extension", context => {
    const sample = fixture(context);
    const hiddenNativePath = join(sample.bundlePath, "native disguised.dll");
    writeFileSync(hiddenNativePath, machO);
    const report = sample.run();
    assert.equal(report.nativeCode.length, 4);
    assert.ok(report.nativeCode.some(item => item.path === "native disguised.dll"));
    assert.equal(verificationCalls(sample).filter(call => call.args.at(-1) === hiddenNativePath).length, 1);
});

for (const observeOnly of [false, true]) {
    for (const kind of ["display", "dylib", "bundle"]) {
        test(`${observeOnly ? "records" : "rejects"} invalid ${kind} signature in ${observeOnly ? "observe" : "required"} mode`, context => {
            const sample = fixture(context, { change: (command, args) => {
                if (command === "codesign" && (
                    kind === "display" && args[0] === "--display" ||
                    kind === "dylib" && args[0] === "--verify" && args.at(-1).endsWith(".dylib") ||
                    kind === "bundle" && args.includes("--deep")
                )) return { status: 1, stdout: "", stderr: "code object is not signed at all" };
            } });
            if (observeOnly) assert.equal(sample.run({ observeOnly }).status, "invalid");
            else assert.throws(() => sample.run(), /signature|signed|verification|invalid|codesign/i);
            const report = sample.report();
            assert.equal(report.status, "invalid");
            assert.equal(report.mode, observeOnly ? "observe" : "require-valid");
            assert.equal(verificationCalls(sample).length, 4);
            if (kind === "dylib") {
                const invalid = report.nativeCode.find(item => item.path.endsWith(".dylib"));
                assert.equal(invalid.status, "invalid");
                assert.equal(invalid.exitStatus, 1);
            }
            if (kind === "bundle") assert.deepEqual(report.bundleVerification, { status: "invalid", exitStatus: 1 });
            if (kind === "display") assert.equal(report.signature, "unknown");
        });
    }
}

for (const signature of ["", "Signature=Developer ID Application", "Signature=Adhoc", "NotSignature=adhoc"]) {
    test(`observes a missing or non-ad-hoc display signature as invalid: ${signature}`, context => {
        const sample = fixture(context, { change: (command, args) => command === "codesign" && args[0] === "--display" ?
            { status: 0, stdout: "", stderr: `Identifier=${bundleIdentifier}\n${signature}\n` } : undefined });
        assert.equal(sample.run({ observeOnly: true }).status, "invalid");
        assert.equal(sample.report().signature, "unknown");
    });

}

for (const observeOnly of [false, true]) {
    for (const [name, response] of [
        ["timeout", { status: null, stdout: "partial native output", error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" }) }],
        ["spawn error", { status: null, error: Object.assign(new Error("missing tool"), { code: "ENOENT" }) }],
        ["null exit status", { status: null, stdout: "" }],
        ["missing exit status", { stdout: "" }],
        ["string exit status", { status: "0", stdout: "" }]
    ]) {
        test(`fails ${name} even in ${observeOnly ? "observe" : "required"} mode`, context => {
            const sample = fixture(context, { change: (command, args) => command === "codesign" && args[0] === "--verify" ? response : undefined });
            assert.throws(() => sample.run({ observeOnly }), /failed|status|timeout|ETIMEDOUT|ENOENT|codesign/i);
            assert.equal(sample.report().status, "failed");
            if (response.stdout) assert.match(readFileSync(join(sample.outputDirectory, "native-001.log"), "utf8"), /partial native output/);
        });
    }
}

for (const [name, manifest] of [
    ["missing identifier", { CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: executableName }],
    ["another identifier", { CFBundleIdentifier: "another.app", CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: executableName }],
    ["device platform", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: executableName }],
    ["mixed supported platforms", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator", "iPhoneOS"], CFBundleExecutable: executableName }],
    ["string supported platforms", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: "iPhoneSimulator", CFBundleExecutable: executableName }],
    ["missing executable", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"] }],
    ["parent executable path", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "../outside" }],
    ["nested executable path", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "Frameworks/native" }],
    ["Windows executable path", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "..\\outside" }],
    ["absolute executable path", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "/tmp/outside" }],
    ["empty executable", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "" }],
    ["current directory executable", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "." }],
    ["drive-relative executable", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "C:outside" }],
    ["NUL executable", { CFBundleIdentifier: bundleIdentifier, CFBundleSupportedPlatforms: ["iPhoneSimulator"], CFBundleExecutable: "native\0outside" }],
    ["array manifest", []],
    ["null manifest", null]
]) {
    test(`rejects ${name} without codesign verification`, context => {
        const sample = fixture(context, { manifest, change: command => name === "null manifest" && command === "plutil" ? { status: 0, stdout: "null" } : undefined });
        assert.throws(() => sample.run({ observeOnly: true }));
        assert.equal(sample.report().status, "failed");
        assert.equal(verificationCalls(sample).length, 0);
    });
}

test("rejects malformed plist JSON without hiding the original parse error", context => {
    const sample = fixture(context, { change: command => command === "plutil" ? { status: 0, stdout: "malformed JSON" } : undefined });
    assert.throws(() => sample.run({ observeOnly: true }), SyntaxError);
    assert.equal(sample.report().status, "failed");
    assert.match(readFileSync(join(sample.outputDirectory, "manifest.log"), "utf8"), /malformed JSON/);
});

for (const architecture of ["x86_64", "arm64 x86_64", "arm64 arm64", "", "arm64e"]) {
    test(`rejects an unsupported main architecture: ${architecture}`, context => {
        const sample = fixture(context, { change: command => command === "lipo" ? { status: 0, stdout: architecture } : undefined });
        assert.throws(() => sample.run({ observeOnly: true }), /arm64|architecture/i);
        assert.equal(sample.report().status, "failed");
        assert.equal(verificationCalls(sample).length, 0);
    });
}

for (const output of [
    platformOutput.replace("IOSSIMULATOR", "IOS"),
    platformOutput.replace(" platform IOSSIMULATOR\n", ""),
    `${platformOutput}\n platform IOSSIMULATOR\n`,
    `${platformOutput}\n platform IOS\n`
]) {
    test(`rejects missing, device or ambiguous main platform: ${JSON.stringify(output)}`, context => {
        const sample = fixture(context, { change: command => command === "xcrun" ? { status: 0, stdout: output } : undefined });
        assert.throws(() => sample.run({ observeOnly: true }), /platform|simulator/i);
        assert.equal(sample.report().status, "failed");
        assert.equal(verificationCalls(sample).length, 0);
    });
}

for (const kind of ["missing plist", "directory plist", "missing executable", "directory executable", "non-Mach-O executable"]) {
    test(`rejects a ${kind} as a bundle validation failure`, context => {
        const sample = fixture(context);
        const path = kind.includes("plist") ? sample.infoPath : sample.mainPath;
        rmSync(path);
        if (kind.startsWith("directory")) mkdirSync(path);
        if (kind.startsWith("non-Mach-O")) writeFileSync(path, "managed or text file");
        assert.throws(() => sample.run({ observeOnly: true }));
        assert.equal(verificationCalls(sample).length, 0);
    });
}

test("refuses an existing output directory instead of reusing stale validation", context => {
    const sample = fixture(context);
    mkdirSync(sample.outputDirectory);
    writeFileSync(join(sample.outputDirectory, "result.json"), "old report");
    assert.throws(() => sample.run(), /EEXIST|exist/i);
    assert.equal(readFileSync(join(sample.outputDirectory, "result.json"), "utf8"), "old report");
    assert.equal(sample.calls.length, 0);
});

test("rejects a bundle path that is a regular file before creating diagnostics", context => {
    const sample = fixture(context);
    renameSync(sample.bundlePath, `${sample.bundlePath}-moved`);
    writeFileSync(sample.bundlePath, "not a directory");
    assert.throws(() => sample.run(), /regular directory/);
    assert.equal(sample.calls.length, 0);
});

test("rejects a linked bundle root instead of treating it as a regular directory", context => {
    const sample = fixture(context);
    const link = join(sample.directory, "linked app.app");
    if (!makeLink(context, sample.bundlePath, link)) return;
    assert.throws(() => sample.run({ bundlePath: link }), /regular directory|link/i);
    assert.equal(sample.calls.length, 0);
});

test("refuses an output path inside the application bundle without writing into it", context => {
    const sample = fixture(context);
    const outputDirectory = join(sample.bundlePath, "signature-output");
    assert.throws(() => sample.run({ outputDirectory }), /outside|inside|bundle/i);
    assert.equal(sample.calls.length, 0);
    assert.throws(() => readFileSync(join(outputDirectory, "result.json")), /ENOENT/);
});

test("checks the real output parent so an alias cannot write into the bundle", context => {
    const sample = fixture(context);
    const link = join(sample.directory, "bundle alias");
    if (!makeLink(context, sample.bundlePath, link)) return;
    assert.throws(() => sample.run({ outputDirectory: join(link, "signature-output") }), /outside|inside|bundle/i);
    assert.equal(sample.calls.length, 0);
});

test("rejects directory links inside the bundle instead of following outside native code", context => {
    const sample = fixture(context);
    const target = join(sample.directory, "outside framework");
    mkdirSync(target);
    writeFileSync(join(target, "outside-native"), machO);
    if (!makeLink(context, target, join(sample.bundlePath, "Linked.framework"))) return;
    assert.throws(() => sample.run({ observeOnly: true }), /symbolic|symlink|link/i);
    assert.equal(sample.report().status, "failed");
    assert.ok(!verificationCalls(sample).some(call => call.args.at(-1).includes("outside-native")));
});

for (const observeOnly of [false, true]) {
    test(`enforces the total two-minute deadline in ${observeOnly ? "observe" : "required"} mode`, context => {
        let currentTime = 0;
        const sample = fixture(context, { change: () => { currentTime += 120_001; } });
        assert.throws(() => sample.run({ observeOnly, now: () => currentTime }), /deadline|time|budget/i);
        assert.equal(sample.report().status, "failed");
        assert.equal(sample.calls.length, 1);
    });
    test(`checks the total deadline after the final bundle verification in ${observeOnly ? "observe" : "required"} mode`, context => {
        let currentTime = 0;
        const sample = fixture(context, { change: (command, args) => {
            if (command === "codesign" && args.includes("--deep")) currentTime = 120_001;
        } });
        assert.throws(() => sample.run({ observeOnly, now: () => currentTime }), /deadline|time|budget/i);
        assert.equal(sample.report().status, "failed");
    });
}

test("a lost diagnostic directory does not replace an earlier native-tool failure", context => {
    context.mock.method(console, "error", () => {});
    const sample = fixture(context, { change: command => {
        if (command === "lipo") {
            renameSync(sample.outputDirectory, `${sample.outputDirectory}-moved`);
            return { status: 17, stderr: "original architecture-tool failure" };
        }
    } });
    assert.throws(() => sample.run({ observeOnly: true }), /original architecture-tool failure/);
});

test("workflow observes, signs with the SDK and requires validity before archiving and smoke", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    const names = [
        "Build simulator app without SDK signing", "Inspect original simulator signature",
        "Sign simulator app with the SDK", "Verify simulator signature before packaging",
        "Verify and archive simulator bundle", "Launch app in isolated iOS simulator"
    ];
    const offsets = names.map(name => workflow.indexOf(`- name: ${name}`));
    assert.ok(offsets.every(offset => offset >= 0));
    assert.deepEqual([...offsets].sort((left, right) => left - right), offsets);
    const before = workflow.slice(offsets[1], offsets[2]);
    const sign = workflow.slice(offsets[2], offsets[3]);
    const after = workflow.slice(offsets[3], workflow.indexOf("- name: Upload simulator signature diagnostics"));
    assert.ok(before.includes("verify-ios-simulator-signature.mjs"));
    assert.ok(before.includes('"$RUNNER_TEMP/ios-signature-before" --observe'));
    assert.ok(sign.includes('dotnet build "$MOBILE_PROJECT" -f net10.0-ios --runtime "$IOS_RUNTIME"'));
    assert.ok(sign.includes("--configuration Debug --no-restore --warnaserror"));
    assert.ok(sign.includes("-p:EnableCodeSigning=true -p:CodesignKey=-"));
    assert.ok(after.includes('"$RUNNER_TEMP/ios-signature-after"'));
    assert.ok(!after.includes("--observe"));
    assert.ok(!workflow.includes("continue-on-error"));
    assert.ok(!/--sign|--force|CodesignVerify=false|SkipCodesignVerification=true/.test(workflow));
    assert.ok(!/secrets\.|\.p12|\.mobileprovision|APPLE_ID|APPLE_TEAM_ID/.test(workflow));
});

test("workflow retains both signature diagnostic sets on success and failure without altering launch limits", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/ios-simulator.yml", import.meta.url), "utf8");
    const start = workflow.indexOf("- name: Upload simulator signature diagnostics");
    const diagnostics = workflow.slice(start, workflow.indexOf("- name: Verify and archive simulator bundle"));
    assert.ok(diagnostics.includes("!cancelled()"));
    assert.ok(diagnostics.includes("steps.ios_signature_before.outcome == 'success' || steps.ios_signature_before.outcome == 'failure'"));
    assert.ok(diagnostics.includes("${{ runner.temp }}/ios-signature-before/"));
    assert.ok(diagnostics.includes("${{ runner.temp }}/ios-signature-after/"));
    assert.ok(diagnostics.includes("retention-days: 7"));
    assert.ok(workflow.includes("node --test scripts/tests/ios-simulator*.test.mjs"));
    assert.match(workflow, /runs-on: macos-15\s+timeout-minutes: 36/);
    assert.match(workflow, /id: ios_smoke\s+timeout-minutes: 24/);
    assert.match(workflow, /name: Sign simulator app with the SDK\s+timeout-minutes: 2/);
    const jobBudget = 36 * 60_000;
    assert.ok(30 * 60_000 + 2 * 120_000 + 2 * 60_000 <= jobBudget);
});
