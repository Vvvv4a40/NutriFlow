import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmodSync, cpSync, linkSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, renameSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, relative } from "node:path";
import { test } from "node:test";
import { packageIosDeviceIpa } from "../package-ios-device-ipa.mjs";

const properties = {
    TargetFramework: "net10.0-ios", RuntimeIdentifier: "ios-arm64", Configuration: "Release",
    EnableCodeSigning: "false", BuildIpa: "false", ArchiveOnBuild: "false", UseInterpreter: "false",
    MtouchInterpreter: "", PublishAot: "false", MtouchUseLlvm: "true", NuGetLockFilePath: "packages.ios-device.lock.json"
};
const manifest = {
    CFBundleIdentifier: "com.nutriflow.app", CFBundleSupportedPlatforms: ["iPhoneOS"], CFBundleExecutable: "NutriFlow.Mobile"
};

function snapshot(directory) {
    const result = [];
    function visit(path) {
        const info = lstatSync(path);
        result.push({
            path: relative(directory, path).replaceAll("\\", "/"), directory: info.isDirectory(),
            mode: info.mode & 0o777, size: info.isFile() ? info.size : null,
            hash: info.isFile() ? createHash("sha256").update(readFileSync(path)).digest("hex") : null
        });
        if (info.isDirectory()) for (const entry of readdirSync(path).sort()) visit(join(path, entry));
    }
    visit(directory);
    return result;
}

function archiveEntries(payloadPath) {
    const result = [];
    function visit(path) {
        const info = lstatSync(path);
        const name = `Payload/${relative(payloadPath, path).replaceAll("\\", "/")}${info.isDirectory() ? "/" : ""}`;
        result.push({ name, directory: info.isDirectory(), mode: info.mode & 0o777, size: info.isFile() ? info.size : 0 });
        if (info.isDirectory()) for (const child of readdirSync(path).sort()) visit(join(path, child));
    }
    result.push({ name: "Payload/", directory: true, mode: lstatSync(payloadPath).mode & 0o777, size: 0 });
    visit(join(payloadPath, "NutriFlow.Mobile.app"));
    return result;
}

function longListing(entries) {
    const lines = entries.map(entry => {
        let permissions = entry.directory ? "d" : "-";
        for (let bit = 0; bit < 9; bit++) permissions += entry.mode & (1 << (8 - bit)) ? "rwx"[bit % 3] : "-";
        return `${permissions}  3.0 unx ${entry.size} bx ${entry.size} stor 26-Oct-08 12:00 ${entry.name}`;
    });
    return `Archive: unsigned.ipa\nZip file size: 64 bytes, number of entries: ${entries.length}\n${lines.join("\n")}\n${entries.length} files, 64 bytes uncompressed, 64 bytes compressed: 0.0%\n`;
}

function fixture(context, { change = () => undefined } = {}) {
    const directory = realpathSync(mkdtempSync(join(tmpdir(), "nutriflow-ios-ipa-")));
    context.after(() => rmSync(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }));
    const bundlePath = join(directory, "NutriFlow.Mobile.app");
    const outputDirectory = join(directory, "unsigned IPA with spaces");
    const buildPropertiesPath = join(directory, "build properties.json");
    const payloadPath = join(outputDirectory, "work", "Payload");
    const stagedBundlePath = join(payloadPath, "NutriFlow.Mobile.app");
    const extractedPayloadPath = join(outputDirectory, "work", "extracted", "Payload");
    const extractedBundlePath = join(extractedPayloadPath, "NutriFlow.Mobile.app");
    mkdirSync(bundlePath);
    mkdirSync(join(bundlePath, "Resources"));
    writeFileSync(join(bundlePath, "Info.plist"), "synthetic built plist");
    writeFileSync(join(bundlePath, "PrivacyInfo.xcprivacy"), "synthetic privacy manifest");
    writeFileSync(join(bundlePath, "NutriFlow.Mobile"), Buffer.from("cffaedfe000000000000000000000000", "hex"));
    writeFileSync(join(bundlePath, "NutriFlow.Mobile.aotdata.arm64"), "synthetic Mono AOT data");
    writeFileSync(join(bundlePath, "NutriFlow.Mobile.dll"), Buffer.from("4d5a000001020304", "hex"));
    writeFileSync(join(bundlePath, "Resources", "logo with spaces.png"), Buffer.from("89504e470d0a1a0a", "hex"));
    chmodSync(join(bundlePath, "NutriFlow.Mobile"), 0o755);
    writeFileSync(buildPropertiesPath, `${JSON.stringify({ Properties: properties }, null, 2)}\n`);
    const calls = [];
    const sample = { directory, bundlePath, outputDirectory, buildPropertiesPath, payloadPath, stagedBundlePath, extractedPayloadPath, extractedBundlePath, calls };

    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, options, sample);
        if (changed !== undefined) return changed;
        if (command === "plutil") return { status: 0, stdout: args[0] === "-convert" ? JSON.stringify(manifest) : "OK\n", stderr: "" };
        if (command === "lipo") return { status: 0, stdout: "arm64\n", stderr: "" };
        if (command === "xcrun") return { status: 0, stdout: "Load command 1\n platform IOS\n minos 15.0\n sdk 26.0\n", stderr: "" };
        if (command === "ditto") {
            writeFileSync(args.at(-1), Buffer.from("synthetic ZIP archive, not a real Apple app"));
            return { status: 0, stdout: "", stderr: "" };
        }
        if (command === "unzip") {
            if (args[0] === "-tq") return { status: 0, stdout: "No errors detected in compressed data.\n", stderr: "" };
            if (args[0] === "-Z" && args[1] === "-1") return { status: 0, stdout: `${archiveEntries(payloadPath).map(entry => entry.name).join("\n")}\n`, stderr: "" };
            if (args[0] === "-Z" && args[1] === "-l") return { status: 0, stdout: longListing(archiveEntries(payloadPath)), stderr: "" };
            if (args[0] === "-q") {
                const extractionDirectory = args[args.indexOf("-d") + 1];
                cpSync(payloadPath, join(extractionDirectory, "Payload"), { recursive: true });
                return { status: 0, stdout: "", stderr: "" };
            }
        }
        assert.fail(`Unexpected command: ${command} ${args.join(" ")}`);
    }

    sample.run = (options = {}) => packageIosDeviceIpa({ bundlePath, buildPropertiesPath, outputDirectory, execute, ...options });
    sample.report = () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"));
    return sample;
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

test("packages the verified device bundle read-only and checks an extracted Payload copy", context => {
    const sample = fixture(context);
    const before = snapshot(sample.bundlePath);
    const propertiesBefore = readFileSync(sample.buildPropertiesPath);
    const report = sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.phase, "complete");
    assert.deepEqual(report.deployment, { signing: "not-verified", installation: "not-tested", launch: "not-tested" });
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.deepEqual(snapshot(sample.stagedBundlePath), before);
    assert.deepEqual(snapshot(sample.extractedBundlePath), before);
    assert.deepEqual(readFileSync(sample.buildPropertiesPath), propertiesBefore);
    assert.deepEqual(sample.report(), report);
    assert.ok(readFileSync(join(sample.outputDirectory, "NutriFlow-unsigned.ipa")).length > 0);
    assert.equal(JSON.parse(readFileSync(join(sample.outputDirectory, "device-verification", "result.json"), "utf8")).status, "passed");
    assert.deepEqual(sample.calls.map(call => call.command), ["plutil", "plutil", "lipo", "xcrun", "ditto", "unzip", "unzip", "unzip", "unzip"]);
    const packaging = sample.calls.find(call => call.command === "ditto");
    assert.deepEqual(packaging.args, ["-c", "-k", "--norsrc", "--noextattr", "--noacl", "--keepParent", sample.payloadPath, join(sample.outputDirectory, "NutriFlow-unsigned.ipa")]);
    for (const call of sample.calls.filter(call => call.command === "ditto" || call.command === "unzip")) {
        assert.equal(call.options.shell, false);
        assert.equal(call.options.killSignal, "SIGKILL");
        assert.equal(call.options.maxBuffer, 1024 * 1024);
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 30_000);
        assert.ok(!call.args.some(argument => /--sign|--install|--launch/.test(argument)));
    }
});

test("accepts a ZIP listing that omits optional directory entries", context => {
    const sample = fixture(context, { change: (command, args, options, current) => {
        if (command !== "unzip" || args[0] !== "-Z") return;
        const files = archiveEntries(current.payloadPath).filter(entry => !entry.directory);
        if (args[1] === "-1") return { status: 0, stdout: `${files.map(entry => entry.name).join("\n")}\n` };
        if (args[1] === "-l") return { status: 0, stdout: longListing(files) };
    } });
    assert.equal(sample.run().status, "passed");
    assert.equal(sample.report().entryCount, snapshot(sample.bundlePath).filter(entry => !entry.directory).length);
});

for (const name of [
    ".env", ".env.production", ".ENV.production", "usersecrets.json", "USERSECRETS.JSON", "appsettings.Local.json", "embedded.mobileprovision",
    "distribution.p12", "distribution.pfx", "certificate.pem", "private.key", "android.keystore", "android.jks",
    "nutriflow.db", "nutriflow.db-wal", "nutriflow.db-shm", "nutriflow.db-journal", "nutriflow.db.bak", "nutriflow.sqlite", "nutriflow.sqlite-wal", "nutriflow.sqlite-shm", "nutriflow.sqlite-journal", "nutriflow.sqlite.bak", "nutriflow.sqlite3", "nutriflow.sqlite3-journal", "nutriflow.sqlite3.bak",
    "developer.provisionprofile", "private-build.binlog", ".DS_Store", "._NutriFlow.Mobile"
]) {
    test(`rejects a private or platform metadata file before native checks: ${name}`, context => {
        const sample = fixture(context);
        writeFileSync(join(sample.bundlePath, "Resources", name), "private synthetic fixture");
        assert.throws(() => sample.run());
        assert.equal(sample.calls.length, 0);
        assert.equal(sample.report().status, "failed");
    });
}

for (const name of ["data", "backups", "label-photos", "__MACOSX"]) {
    test(`rejects a private or metadata directory before native checks: ${name}`, context => {
        const sample = fixture(context);
        mkdirSync(join(sample.bundlePath, "Resources", name));
        writeFileSync(join(sample.bundlePath, "Resources", name, "synthetic.txt"), "not application content");
        assert.throws(() => sample.run());
        assert.equal(sample.calls.length, 0);
        assert.equal(sample.report().status, "failed");
    });
}

test("rejects a linked directory anywhere inside the original bundle", context => {
    const sample = fixture(context);
    const outside = join(sample.directory, "outside directory");
    mkdirSync(outside);
    writeFileSync(join(outside, "outside.txt"), "private outside content");
    if (!makeDirectoryLink(context, outside, join(sample.bundlePath, "Resources", "linked directory"))) return;
    assert.throws(() => sample.run());
    assert.equal(sample.calls.length, 0);
    assert.equal(sample.report().status, "failed");
});

test("rejects hard-linked application files before copying their content", context => {
    const sample = fixture(context);
    linkSync(join(sample.bundlePath, "NutriFlow.Mobile.dll"), join(sample.bundlePath, "Resources", "linked.dll"));
    assert.throws(() => sample.run());
    assert.equal(sample.calls.length, 0);
    assert.equal(sample.report().status, "failed");
});

test("refuses a linked bundle root without executing native tools", context => {
    const sample = fixture(context);
    const target = join(sample.directory, "original bundle");
    renameSync(sample.bundlePath, target);
    if (!makeDirectoryLink(context, target, sample.bundlePath)) return;
    assert.throws(() => sample.run());
    assert.equal(sample.calls.length, 0);
});

test("requires the fixed expected application bundle name", context => {
    const sample = fixture(context);
    const renamed = join(sample.directory, "Another.app");
    renameSync(sample.bundlePath, renamed);
    assert.throws(() => sample.run({ bundlePath: renamed }));
    assert.equal(sample.calls.length, 0);
});

test("refuses an existing output directory without replacing old artifacts", context => {
    const sample = fixture(context);
    mkdirSync(sample.outputDirectory);
    writeFileSync(join(sample.outputDirectory, "NutriFlow-unsigned.ipa"), "old archive");
    assert.throws(() => sample.run(), /exist|EEXIST/i);
    assert.equal(readFileSync(join(sample.outputDirectory, "NutriFlow-unsigned.ipa"), "utf8"), "old archive");
    assert.equal(sample.calls.length, 0);
});

test("refuses output inside the original app without changing its files", context => {
    const sample = fixture(context);
    const before = snapshot(sample.bundlePath);
    assert.throws(() => sample.run({ outputDirectory: join(sample.bundlePath, "packaging") }));
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.equal(sample.calls.length, 0);
});

test("checks the canonical output parent rather than trusting a bundle alias", context => {
    const sample = fixture(context);
    const alias = join(sample.directory, "bundle alias");
    if (!makeDirectoryLink(context, sample.bundlePath, alias)) return;
    const before = snapshot(sample.bundlePath);
    assert.throws(() => sample.run({ outputDirectory: join(alias, "packaging") }));
    assert.deepEqual(snapshot(sample.bundlePath), before);
    assert.equal(sample.calls.length, 0);
});

test("preserves native validation failures and never invokes an archiver", context => {
    const sample = fixture(context, { change: command => command === "lipo" ? { status: 1, stderr: "original native architecture failure" } : undefined });
    assert.throws(() => sample.run(), /original native architecture failure/);
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => call.command === "ditto" || call.command === "unzip"));
});

for (const [name, response] of [
    ["nonzero exit", { status: 17, stdout: "partial output", stderr: "original archiver failure" }],
    ["timeout", { status: null, error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" }) }],
    ["spawn failure", { status: null, error: Object.assign(new Error("missing tool"), { code: "ENOENT" }) }],
    ["missing status", { stdout: "" }],
    ["string status", { status: "0", stdout: "" }]
]) {
    test(`stops packaging after an archiver ${name}`, context => {
        const sample = fixture(context, { change: command => command === "ditto" ? response : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => call.command === "unzip"));
    });
}

test("rejects an archiver that produces no IPA despite a zero exit code", context => {
    const sample = fixture(context, { change: command => command === "ditto" ? { status: 0, stdout: "" } : undefined });
    assert.throws(() => sample.run());
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => call.command === "unzip"));
});

test("rejects an empty generated IPA", context => {
    const sample = fixture(context, { change: (command, args) => {
        if (command === "ditto") {
            writeFileSync(args.at(-1), "");
            return { status: 0, stdout: "" };
        }
    } });
    assert.throws(() => sample.run());
    assert.equal(sample.report().status, "failed");
});

test("preserves an archiver failure when the diagnostics directory disappears", context => {
    const sample = fixture(context, { change: (command, args, options, current) => {
        if (command === "ditto") {
            renameSync(current.outputDirectory, `${current.outputDirectory}-moved`);
            return { status: 17, stderr: "original archiver failure with missing diagnostics" };
        }
    } });
    assert.throws(() => sample.run(), /original archiver failure with missing diagnostics/);
});

test("rejects failed ZIP integrity without extracting it", context => {
    const sample = fixture(context, { change: (command, args) => command === "unzip" && args[0] === "-tq" ?
        { status: 2, stderr: "bad archive checksum" } : undefined });
    assert.throws(() => sample.run(), /bad archive checksum/);
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => call.command === "unzip" && call.args[0] === "-q"));
});

for (const extra of [
    "../outside.txt", "/outside.txt", "Payload/../outside.txt", "Payload/NutriFlow.Mobile.app/../outside.txt",
    "Payload\\NutriFlow.Mobile.app\\outside.txt", "C:outside.txt", "__MACOSX/._NutriFlow.Mobile.app",
    "Payload/Another.app/Info.plist", "Payload/NutriFlow.Mobile.app/unexpected.txt",
    "Payload/NutriFlow.Mobile.app/.env", "Payload/NutriFlow.Mobile.app/Resources/LOGO WITH SPACES.PNG"
]) {
    test(`rejects an unexpected or unsafe ZIP entry before extraction: ${extra}`, context => {
        const sample = fixture(context, { change: (command, args, options, current) => command === "unzip" && args[0] === "-Z" && args[1] === "-1" ?
            { status: 0, stdout: `${archiveEntries(current.payloadPath).map(entry => entry.name).join("\n")}\n${extra}\n` } : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => call.command === "unzip" && call.args[0] === "-q"));
    });
}

for (const modification of ["missing", "duplicate", "empty", "directory-file-collision"]) {
    test(`rejects a ZIP entry listing that is ${modification}`, context => {
        const sample = fixture(context, { change: (command, args, options, current) => {
            if (command !== "unzip" || args[0] !== "-Z" || args[1] !== "-1") return;
            const entries = archiveEntries(current.payloadPath).map(entry => entry.name);
            if (modification === "missing") entries.pop();
            if (modification === "duplicate") entries.push(entries.at(-1));
            if (modification === "empty") entries.length = 0;
            if (modification === "directory-file-collision") entries.push("Payload/NutriFlow.Mobile.app/Resources");
            return { status: 0, stdout: entries.length ? `${entries.join("\n")}\n` : "" };
        } });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.ok(!sample.calls.some(call => call.command === "unzip" && call.args[0] === "-q"));
    });
}

for (const [name, changeListing] of [
    ["symbolic link", listing => listing.replace(/^-[rwx-]{9}/m, "lrwxrwxrwx")],
    ["character device", listing => listing.replace(/^-[rwx-]{9}/m, "crw-r--r--")],
    ["FIFO", listing => listing.replace(/^-[rwx-]{9}/m, "prw-r--r--")],
    ["encrypted content", listing => listing.replace(" bx ", " Bx ")],
    ["encrypted text", listing => listing.replace(" bx ", " Tx ")],
    ["special permission bits", listing => listing.replace(/^-[rwx-]{9}/m, "-rwsr-xr-x")],
    ["changed permissions", listing => listing.replace(/^-[rwx-]{9}/m, "----------")],
    ["malformed metadata", () => "not archive metadata\n"]
]) {
    test(`rejects ZIP metadata with ${name} before extraction`, context => {
        const sample = fixture(context, { change: (command, args, options, current) => command === "unzip" && args[0] === "-Z" && args[1] === "-l" ?
            { status: 0, stdout: changeListing(longListing(archiveEntries(current.payloadPath))) } : undefined });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.equal(sample.report().phase, "archive-verification");
        assert.ok(!sample.calls.some(call => call.command === "unzip" && call.args[0] === "-q"));
    });
}

for (const [name, mutation] of [
    ["different extracted bytes", current => writeFileSync(join(current.extractedBundlePath, "NutriFlow.Mobile.dll"), "changed extracted bytes")],
    ["missing extracted file", current => rmSync(join(current.extractedBundlePath, "NutriFlow.Mobile.dll"))],
    ["unexpected extracted file", current => writeFileSync(join(current.extractedBundlePath, "unexpected.txt"), "unexpected bytes")],
    ["unexpected extracted payload root", current => writeFileSync(join(current.extractedPayloadPath, "unexpected.txt"), "unexpected bytes")],
    ["mutated original bytes", current => writeFileSync(join(current.bundlePath, "NutriFlow.Mobile.dll"), "changed source bytes")],
    ["mutated staged bytes", current => writeFileSync(join(current.stagedBundlePath, "NutriFlow.Mobile.dll"), "changed staged bytes")],
    ["mutated archive bytes of the same length", current => {
        const archivePath = join(current.outputDirectory, "NutriFlow-unsigned.ipa");
        const archive = readFileSync(archivePath);
        archive[archive.length - 1] ^= 1;
        writeFileSync(archivePath, archive);
    }]
]) {
    test(`does not publish success for ${name}`, context => {
        const sample = fixture(context, { change: (command, args, options, current) => {
            if (command === "unzip" && args[0] === "-q") {
                cpSync(current.payloadPath, join(args[args.indexOf("-d") + 1], "Payload"), { recursive: true });
                mutation(current);
                return { status: 0, stdout: "" };
            }
        } });
        assert.throws(() => sample.run());
        assert.equal(sample.report().status, "failed");
        assert.equal(sample.report().phase, "round-trip");
        assert.ok(sample.calls.some(call => call.command === "unzip" && call.args[0] === "-q"));
    });
}

test("fails when extraction reports success without creating the app", context => {
    const sample = fixture(context, { change: (command, args) => command === "unzip" && args[0] === "-q" ? { status: 0, stdout: "" } : undefined });
    assert.throws(() => sample.run());
    assert.equal(sample.report().status, "failed");
});

test("enforces the packaging deadline after a tool finishes", context => {
    let currentTime = 0;
    const sample = fixture(context, { change: command => { if (command === "ditto") currentTime = 120_001; } });
    assert.throws(() => sample.run({ now: () => currentTime }), /deadline|time|budget/i);
    assert.equal(sample.report().status, "failed");
    assert.ok(!sample.calls.some(call => call.command === "unzip"));
});

test("checks the deadline after the final archive extraction tool", context => {
    let currentTime = 0;
    const sample = fixture(context, { change: (command, args) => { if (command === "unzip" && args[0] === "-q") currentTime = 120_001; } });
    assert.throws(() => sample.run({ now: () => currentTime }), /deadline|time|budget/i);
    assert.equal(sample.report().status, "failed");
});

test("packaging commands sanitize option-bearing Info-ZIP environment variables", context => {
    const variableNames = ["UNZIP", "UNZIPOPT", "ZIPINFO", "ZIPINFOOPT"];
    const previousValues = new Map(variableNames.map(name => [name, process.env[name]]));
    context.after(() => {
        for (const [name, value] of previousValues) {
            if (value === undefined) delete process.env[name];
            else process.env[name] = value;
        }
    });
    for (const name of variableNames) process.env[name] = "--synthetic-unzip-option";
    const sample = fixture(context);
    sample.run();
    const commands = sample.calls.filter(call => call.command === "ditto" || call.command === "unzip");
    for (const call of commands) {
        assert.ok(call.options.env);
        assert.ok(!Object.keys(call.options.env).some(name => /^(UNZIP|UNZIPOPT|ZIPINFO|ZIPINFOOPT)$/i.test(name)));
        assert.deepEqual(call.options.stdio, ["ignore", "pipe", "pipe"]);
    }
});

test("the IPA byte digest can be independently reproduced", context => {
    const sample = fixture(context);
    const report = sample.run();
    const bytes = readFileSync(join(sample.outputDirectory, "NutriFlow-unsigned.ipa"));
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    assert.equal(report.ipa.sha256, sha256);
    assert.equal(report.ipa.size, bytes.length);
    assert.equal(report.ipa.name, "NutriFlow-unsigned.ipa");
    assert.ok(readFileSync(join(sample.outputDirectory, "commands.log"), "utf8").includes("ditto"));
    assert.equal(basename(sample.bundlePath), "NutriFlow.Mobile.app");
});
