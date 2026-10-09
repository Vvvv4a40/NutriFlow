import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { test } from "node:test";
import { verifyAndroidApk } from "../verify-android-apk.mjs";

const properties = {
    TargetFramework: "net10.0-android", Configuration: "Debug", EmbedAssembliesIntoApk: "true",
    PublishTrimmed: "false", AndroidLinkMode: "None", AndroidPackageFormats: "apk", AndroidKeyStore: "true", AndroidSigningKeyAlias: "nutriflow-personal",
    AndroidNETSdkVersion: "36.1.2", MauiVersion: "10.0.0", NuGetLockFilePath: "packages.android-ci.lock.json"
};
const entries = ["AndroidManifest.xml", "classes.dex", "lib/arm64-v8a/libmonodroid.so", "lib/x86_64/libmonodroid.so", "assemblies/NutriFlow.Mobile.dll"];
const badging = "package: name='com.nutriflow.app' versionCode='1' versionName='0.1.0'\nsdkVersion:'21'\nuses-permission: name='android.permission.INTERNET'\nlaunchable-activity: name='crc.MainActivity' label='NutriFlow' icon=''\n";
const manifest = "E: manifest\n  E: application\n    A: android:allowBackup(0x01010280)=(type 0x12)0x0\n";
const publicCertificate = Buffer.from("synthetic DER public signing certificate");
const signerSha256 = createHash("sha256").update(publicCertificate).digest("hex");
const signature = `Verifies\nSigner #1 certificate DN: CN=NutriFlow Personal\nSigner #1 certificate SHA-256 digest: ${signerSha256}\n`;

test("native Linux lock keeps the same versions and dependencies as the Windows graph", () => {
    const windows = JSON.parse(readFileSync(new URL("../../mobile/NutriFlow.Mobile/packages.android.lock.json", import.meta.url), "utf8"));
    const linux = JSON.parse(readFileSync(new URL("../../mobile/NutriFlow.Mobile/packages.android-ci.lock.json", import.meta.url), "utf8"));
    const hashes = [];
    for (const [framework, packages] of Object.entries(windows.dependencies)) {
        for (const [name, entry] of Object.entries(packages)) {
            const native = linux.dependencies[framework]?.[name];
            assert.ok(native, `${framework}: ${name}`);
            const { contentHash: windowsHash, ...windowsPackage } = entry;
            const { contentHash: linuxHash, ...linuxPackage } = native;
            assert.deepEqual(linuxPackage, windowsPackage);
            if (linuxHash !== windowsHash) hashes.push(name);
        }
    }
    assert.deepEqual(Object.keys(linux.dependencies).sort(), Object.keys(windows.dependencies).sort());
    for (const framework of Object.keys(windows.dependencies)) {
        assert.deepEqual(Object.keys(linux.dependencies[framework]).sort(), Object.keys(windows.dependencies[framework]).sort());
    }
    assert.deepEqual(hashes.sort(), ["Microsoft.Maui.Controls.Build.Tasks", "Microsoft.Maui.Resizetizer"]);
});

test("permanent local signing material stays outside Git and the Docker context", () => {
    const gitIgnore = readFileSync(new URL("../../.gitignore", import.meta.url), "utf8");
    const dockerIgnore = readFileSync(new URL("../../.dockerignore", import.meta.url), "utf8");
    assert.match(gitIgnore, /^\/\.private\/android-signing\/$/m);
    assert.match(dockerIgnore, /^\.private\/$/m);
});

function fixture(context, overrides = {}) {
    const directory = mkdtempSync(join(tmpdir(), "nutriflow-android-apk-"));
    context.after(() => rmSync(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }));
    const apkPath = join(directory, "NutriFlow-debug.apk");
    const buildPropertiesPath = join(directory, "build properties.json");
    const buildToolsDirectory = join(directory, "build tools");
    const signerCertificatePath = join(directory, "signing certificate.cer");
    const outputDirectory = join(directory, "verification");
    mkdirSync(buildToolsDirectory);
    writeFileSync(apkPath, "synthetic signed APK fixture");
    writeFileSync(signerCertificatePath, publicCertificate);
    writeFileSync(buildPropertiesPath, JSON.stringify({ Properties: { ...properties, ...overrides.properties } }));
    const calls = [];
    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = overrides.change?.(command, args, calls, options, apkPath);
        if (changed !== undefined) return changed;
        let stdout;
        if (basename(command) === "apksigner") stdout = overrides.signature ?? signature;
        else if (args[0] === "list") stdout = (overrides.entries ?? entries).join("\n");
        else if (args[1] === "badging") stdout = overrides.badging ?? badging;
        else if (args[1] === "xmltree") stdout = overrides.manifest ?? manifest;
        else assert.fail(`Unexpected command: ${command} ${args.join(" ")}`);
        return { status: 0, stdout, stderr: "" };
    }
    return {
        apkPath, outputDirectory, calls,
        run: extra => verifyAndroidApk({ apkPath, buildPropertiesPath, buildToolsDirectory, signerCertificatePath, outputDirectory, execute, ...extra }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

test("verifies persistent personal signing without claiming phone installation or launch", context => {
    const sample = fixture(context);
    const original = readFileSync(sample.apkPath);
    const report = sample.run();
    assert.equal(report.status, "passed");
    assert.deepEqual(report.deployment, { signing: "personal-persistent", buildConfiguration: "Debug", stableUpdateSigning: true, installation: "not-tested", launch: "not-tested" });
    assert.equal(report.signerCertificateSha256, signerSha256);
    assert.ok(!readFileSync(join(sample.outputDirectory, "signature.log"), "utf8").includes("certificate DN:"));
    assert.equal(report.sha256, createHash("sha256").update(original).digest("hex"));
    assert.equal(readFileSync(join(sample.outputDirectory, "NutriFlow-debug.apk.sha256"), "utf8"), `${report.sha256}  NutriFlow-debug.apk\n`);
    assert.deepEqual(report.architectures, ["arm64-v8a", "x86_64"]);
    assert.deepEqual(readFileSync(sample.apkPath), original);
    assert.ok(sample.calls.every(call => call.options.shell === false && call.options.timeout <= 30_000));
});

test("accepts the assembly-store layout used by .NET Android", context => {
    const sample = fixture(context, { entries: [...entries.slice(0, -1), "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"] });
    assert.equal(sample.run().status, "passed");
});

for (const selected of [
    { name: "fast deployment", properties: { EmbedAssembliesIntoApk: "false" } },
    { name: "trimming", properties: { PublishTrimmed: "true" } },
    { name: "temporary debug signing", properties: { AndroidKeyStore: "false" } },
    { name: "another signing alias", properties: { AndroidSigningKeyAlias: "androiddebugkey" } },
    { name: "changed workload", properties: { AndroidNETSdkVersion: "36.2.0" } },
    { name: "another dependency lock", properties: { NuGetLockFilePath: "packages.ios.lock.json" } },
    { name: "another package", badging: badging.replace("com.nutriflow.app", "com.example.other") },
    { name: "no internet permission", badging: badging.replace("uses-permission: name='android.permission.INTERNET'\n", "") },
    { name: "no launcher", badging: badging.replace(/launchable-activity:.+\n/, "") },
    { name: "enabled backup", manifest: manifest.replace("(type 0x12)0x0", "(type 0x12)0xffffffff") },
    { name: "missing backup policy", manifest: "E: manifest\n  E: application\n" },
    { name: "unknown signing certificate", signature: signature.replace(signerSha256, "ab".repeat(32)) },
    { name: "missing signer digest", signature: "Verifies\nSigner #1 certificate DN: CN=NutriFlow Personal\n" },
    { name: "additional signing certificate", signature: `${signature}Signer #2 certificate SHA-256 digest: ${"cd".repeat(32)}\n` },
    { name: "no embedded assemblies", entries: entries.slice(0, -1) },
    { name: "no ARM64 runtime", entries: entries.filter(entry => !entry.includes("arm64-v8a")) },
    { name: "duplicate ZIP entries", entries: [...entries, "classes.dex"] },
    { name: "unsafe ZIP path", entries: [...entries, "../outside.txt"] },
    { name: "credential file", entries: [...entries, "assets/.env"] },
    { name: "private database", entries: [...entries, "assets/nutriflow.db"] },
    { name: "private signing key", entries: [...entries, "assets/android.keystore"] },
    { name: "protected signing password", entries: [...entries, "assets/password.dpapi"] },
    { name: "private profile catalog", entries: [...entries, "assets/profiles.json"] }
]) {
    test(`rejects ${selected.name} before publishing a checksum`, context => {
        const sample = fixture(context, selected);
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.throws(() => readFileSync(join(sample.outputDirectory, "NutriFlow-debug.apk.sha256")), { code: "ENOENT" });
    });
}

test("rejects failed signature verification and preserves the failure report", context => {
    const sample = fixture(context, { change: command => basename(command) === "apksigner" ? { status: 1, stdout: "", stderr: "DOES NOT VERIFY" } : undefined });
    assert.throws(() => sample.run(), /apksigner failed/);
    assert.equal(sample.report().phase, "signature");
});

test("rejects a tool timeout", context => {
    const sample = fixture(context, { change: () => ({ status: null, stdout: "", stderr: "", error: { code: "ETIMEDOUT", message: "timeout" } }) });
    assert.throws(() => sample.run(), /ETIMEDOUT/);
});

test("rejects mutation of the APK during verification", context => {
    const sample = fixture(context, { change: (command, args, calls, options, apkPath) => {
        if (args[0] === "list") writeFileSync(apkPath, "changed synthetic APK bytes");
    } });
    assert.throws(() => sample.run(), /changed during/);
});

test("enforces the overall verification deadline", context => {
    let currentTime = 0;
    const sample = fixture(context, { change: () => { currentTime += 121_000; } });
    assert.throws(() => sample.run({ now: () => currentTime }), /deadline/);
});

test("does not publish unexpected secret-valued build properties", context => {
    const sample = fixture(context, { properties: { AndroidSigningStorePass: "private fixture password", AndroidSigningKeyStore: "private fixture path" } });
    const report = sample.run();
    assert.equal(report.properties.AndroidSigningStorePass, undefined);
    assert.equal(report.properties.AndroidSigningKeyStore, undefined);
    assert.ok(!readFileSync(join(sample.outputDirectory, "result.json"), "utf8").includes("private fixture"));
});
