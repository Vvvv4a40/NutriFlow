import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFileSync, lstatSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export function verifyAndroidApk({ apkPath, buildPropertiesPath, buildToolsDirectory, signerCertificatePath, outputDirectory, execute = spawnSync, now = Date.now }) {
    apkPath = resolve(apkPath);
    const apkInfo = lstatSync(apkPath);
    if (!apkInfo.isFile() || apkInfo.size === 0 || apkInfo.size > 256 * 1024 * 1024) {
        throw new Error("Expected a non-empty regular APK no larger than 256 MiB.");
    }
    apkPath = realpathSync(apkPath);
    buildToolsDirectory = realpathSync(resolve(buildToolsDirectory));
    outputDirectory = resolve(outputDirectory);
    outputDirectory = join(realpathSync(dirname(outputDirectory)), basename(outputDirectory));
    mkdirSync(outputDirectory);

    const report = {
        status: "running", startedAt: new Date().toISOString(),
        deployment: { signing: "personal-persistent", buildConfiguration: "Debug", stableUpdateSigning: true, installation: "not-tested", launch: "not-tested" }
    };
    const deadline = now() + 120_000;
    let failure;

    function checkDeadline() {
        const remaining = deadline - now();
        if (remaining <= 0) throw new Error("The Android APK verification deadline was exceeded.");
        return remaining;
    }

    function invoke(command, args, logName, sanitize = value => value) {
        const result = execute(command, args, {
            encoding: "utf8", timeout: Math.min(30_000, checkDeadline()), maxBuffer: 2 * 1024 * 1024,
            killSignal: "SIGKILL", shell: false
        });
        appendFileSync(join(outputDirectory, "commands.log"), `${JSON.stringify([command, ...args])}\n`);
        writeFileSync(join(outputDirectory, logName), `${sanitize(result.stdout ?? "")}\n${sanitize(result.stderr ?? "")}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`);
        if (result.error || result.status !== 0) throw new Error(`${basename(command)} failed: ${result.error?.code ?? result.status}`);
        checkDeadline();
        return result.stdout ?? "";
    }

    function digest() {
        return createHash("sha256").update(readFileSync(apkPath)).digest("hex");
    }

    try {
        report.phase = "build-properties";
        const properties = JSON.parse(readFileSync(buildPropertiesPath, "utf8")).Properties;
        const expected = {
            TargetFramework: "net10.0-android", Configuration: "Debug", EmbedAssembliesIntoApk: "true",
            PublishTrimmed: "false", AndroidLinkMode: "None", AndroidPackageFormats: "apk",
            AndroidKeyStore: "true", AndroidSigningKeyAlias: "nutriflow-personal", AndroidNETSdkVersion: "36.1.2", MauiVersion: "10.0.0"
        };
        for (const [name, value] of Object.entries(expected)) {
            const actual = properties?.[name];
            if (typeof actual !== "string" || actual.toLowerCase() !== value.toLowerCase()) {
                throw new Error(`Unexpected Android build property: ${name}`);
            }
        }
        if (typeof properties.NuGetLockFilePath !== "string" || basename(properties.NuGetLockFilePath) !== "packages.android.lock.json") {
            throw new Error("Expected the Android dependency lock.");
        }
        report.properties = Object.fromEntries([...Object.keys(expected), "NuGetLockFilePath"].map(name => [name, properties[name]]));
        const initialSha256 = digest();

        report.phase = "signature";
        const certificateInfo = lstatSync(signerCertificatePath);
        if (!certificateInfo.isFile() || certificateInfo.size === 0 || certificateInfo.size > 64 * 1024) {
            throw new Error("Expected the exported public signing certificate.");
        }
        const expectedCertificate = createHash("sha256").update(readFileSync(signerCertificatePath)).digest("hex");
        const signerOutput = invoke(join(buildToolsDirectory, "apksigner"), ["verify", "--verbose", "--print-certs", apkPath], "signature.log", value =>
            value.split(/\r?\n/).filter(line => !/^Signer #\d+ (?:certificate|public key)/i.test(line)).join("\n"));
        const certificates = [...signerOutput.matchAll(/^Signer #(\d+) certificate SHA-256 digest:\s*([\da-f]{64})\s*$/gim)];
        const certificate = certificates.length === 1 && certificates[0][1] === "1" ? certificates[0][2] : undefined;
        if (!certificate || certificate.toLowerCase() !== expectedCertificate) {
            throw new Error("The APK must be signed only by the permanent personal certificate.");
        }
        report.signerCertificateSha256 = certificate.toLowerCase();

        report.phase = "manifest";
        const aapt = join(buildToolsDirectory, "aapt");
        const badging = invoke(aapt, ["dump", "badging", apkPath], "badging.log");
        const packageMatch = badging.match(/^package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'/m);
        if (packageMatch?.[1] !== "com.nutriflow.app" || Number(packageMatch[2]) < 1) {
            throw new Error("Expected the versioned NutriFlow Android package.");
        }
        if (!/^sdkVersion:'21'\s*$/m.test(badging) || !/^launchable-activity: name='[^']+'/m.test(badging)) {
            throw new Error("Expected Android API 21 minimum and a launcher activity.");
        }
        const permissions = [...badging.matchAll(/^uses-permission(?:-sdk-\d+)?: name='([^']+)'/gm)].map(match => match[1]);
        if (!permissions.includes("android.permission.INTERNET")) throw new Error("The APK must declare Internet access for BYOK requests.");
        const manifest = invoke(aapt, ["dump", "xmltree", apkPath, "AndroidManifest.xml"], "manifest.log");
        if (!/^\s*A: android:allowBackup\(0x[\da-f]+\)=\(type 0x12\)0x0\s*$/im.test(manifest)) {
            throw new Error("Android automatic backup must be explicitly disabled.");
        }
        report.applicationId = packageMatch[1];
        report.versionCode = Number(packageMatch[2]);
        report.versionName = packageMatch[3];
        report.permissions = permissions;

        report.phase = "package-contents";
        const entries = invoke(aapt, ["list", apkPath], "entries.log").split(/\r?\n/).filter(Boolean);
        if (entries.length === 0 || entries.length > 10_000 || new Set(entries).size !== entries.length) {
            throw new Error("The APK entry list is empty, oversized, or contains duplicates.");
        }
        for (const entry of entries) {
            if (entry.startsWith("/") || /[\\\0\r\n]/.test(entry) || entry.split("/").some(segment => segment === ".." || segment === ".")) {
                throw new Error("The APK contains an unsafe entry path.");
            }
            const name = basename(entry);
            if (/^(?:\.env(?:\..*)?|appsettings(?:\.[^.]*)?\.json|secrets\.json)$/i.test(name) ||
                /\.(?:db|sqlite3?|keystore|jks|pfx|p12|pem|key)$/i.test(name)) {
                throw new Error("The APK must not contain private storage, credentials, or signing keys.");
            }
        }
        if (!entries.includes("AndroidManifest.xml") || !entries.includes("classes.dex") ||
            !entries.some(entry => /^lib\/arm64-v8a\/[^/]+\.so$/.test(entry))) {
            throw new Error("Expected the Android manifest, Dalvik code, and ARM64 native runtime.");
        }
        if (!entries.some(entry => /^assemblies\/.+\.(?:dll|blob)$/.test(entry) || /^lib\/[^/]+\/libassemblies[^/]*\.so$/.test(entry) || /^lib\/[^/]+\/lib[^/]+\.dll\.so$/.test(entry))) {
            throw new Error("Managed assemblies must be embedded in the standalone APK.");
        }
        report.entryCount = entries.length;
        report.architectures = [...new Set(entries.flatMap(entry => entry.match(/^lib\/([^/]+)\//)?.[1] ?? []))].sort();

        report.phase = "checksum";
        checkDeadline();
        const finalInfo = lstatSync(apkPath);
        if (!finalInfo.isFile() || finalInfo.size !== apkInfo.size || digest() !== initialSha256) {
            throw new Error("The APK changed during read-only verification.");
        }
        report.fileName = basename(apkPath);
        report.bytes = finalInfo.size;
        report.sha256 = initialSha256;
        writeFileSync(join(outputDirectory, `${basename(apkPath)}.sha256`), `${initialSha256}  ${basename(apkPath)}\n`);
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
        if (process.argv.length !== 7) throw new Error("Usage: node scripts/verify-android-apk.mjs <signed.apk> <build-properties.json> <android-build-tools-directory> <public-signer.cer> <new-output-directory>");
        if (process.platform !== "linux") throw new Error("APK verification requires the Linux Android CI runner.");
        console.log(JSON.stringify(verifyAndroidApk({
            apkPath: process.argv[2], buildPropertiesPath: process.argv[3], buildToolsDirectory: process.argv[4], signerCertificatePath: process.argv[5], outputDirectory: process.argv[6]
        }), null, 2));
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
