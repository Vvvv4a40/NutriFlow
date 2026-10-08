import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFileSync, cpSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { verifyIosDeviceBundle } from "./verify-ios-device-bundle.mjs";

export function packageIosDeviceIpa({ bundlePath, buildPropertiesPath, outputDirectory, execute = spawnSync, now = Date.now }) {
    bundlePath = resolve(bundlePath);
    if (!lstatSync(bundlePath).isDirectory() || basename(bundlePath) !== "NutriFlow.Mobile.app") {
        throw new Error("Expected the regular NutriFlow.Mobile.app directory.");
    }
    bundlePath = realpathSync(bundlePath);
    outputDirectory = resolve(outputDirectory);
    outputDirectory = join(realpathSync(dirname(outputDirectory)), basename(outputDirectory));
    const outputRelativePath = relative(bundlePath, outputDirectory);
    if (outputRelativePath === "" || (!isAbsolute(outputRelativePath) && outputRelativePath !== ".." && !outputRelativePath.startsWith(`..${sep}`))) {
        throw new Error("IPA output must be outside the original app bundle.");
    }
    mkdirSync(outputDirectory);
    const deadline = now() + 120_000;
    const report = {
        status: "running", startedAt: new Date().toISOString(), runtimeIdentifier: "ios-arm64",
        deployment: { signing: "not-verified", installation: "not-tested", launch: "not-tested" }
    };
    const ipaPath = join(outputDirectory, "NutriFlow-unsigned.ipa");
    let failure;

    function checkDeadline() {
        const remaining = deadline - now();
        if (remaining <= 0) throw new Error("The IPA packaging deadline was exceeded.");
        return remaining;
    }

    function invoke(command, args, file) {
        const timeout = Math.min(30_000, checkDeadline());
        appendFileSync(join(outputDirectory, "commands.log"), `${JSON.stringify([command, ...args])}\n`);
        const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(UNZIP|UNZIPOPT|ZIPINFO|ZIPINFOOPT)$/i.test(name)));
        const result = execute(command, args, {
            encoding: "utf8", timeout, maxBuffer: 1024 * 1024, killSignal: "SIGKILL", shell: false,
            stdio: ["ignore", "pipe", "pipe"], env
        });
        let commandFailure = result.error || !Number.isInteger(result.status) || result.status !== 0
            ? new Error(`${command} failed: ${result.error?.code ?? result.status}; ${result.stderr?.trim() ?? ""}`)
            : null;
        try { checkDeadline(); } catch (error) { commandFailure ??= error; }
        try {
            writeFileSync(join(outputDirectory, file), `${result.stdout ?? ""}\n${result.stderr ?? ""}\nstatus=${result.status}; error=${result.error?.message ?? ""}\n`);
        } catch (error) { throw commandFailure ?? error; }
        if (commandFailure) throw commandFailure;
        return result.stdout ?? "";
    }

    function validateName(name) {
        if (name.length === 0 || name === "." || name === ".." || /[\\/:\x00-\x1f\x7f]/.test(name) || name.trim() !== name) {
            throw new Error("An unsafe IPA path component was found.");
        }
        if (/^(?:\.env(?:\..*)?|appsettings\.local\.json|usersecrets\.json|data|backups|label-photos|__macosx|\.ds_store|\._.*)$/i.test(name) ||
            /\.(?:p12|pfx|pem|key|keystore|jks|mobileprovision|provisionprofile|db(?:-shm|-wal|-journal|\.bak)?|sqlite(?:3)?(?:-shm|-wal|-journal|\.bak)?|binlog)$/i.test(name)) {
            throw new Error("Private data or platform metadata must not enter the unsigned IPA.");
        }
    }

    function inventory(root) {
        const entries = [];
        const foldedPaths = new Set();
        let totalBytes = 0;
        function visit(path, entryPath) {
            checkDeadline();
            const info = lstatSync(path);
            if (!info.isDirectory() && (!info.isFile() || info.nlink !== 1)) {
                throw new Error("IPA contents must contain only regular directories and unlinked files.");
            }
            if ((info.mode & 0o7000) !== 0) throw new Error("Special permission bits are not supported in the unsigned IPA.");
            const folded = entryPath.toLowerCase();
            if (foldedPaths.has(folded)) throw new Error("IPA paths must not collide by letter case.");
            foldedPaths.add(folded);
            const entry = { path: entryPath, kind: info.isDirectory() ? "directory" : "file", mode: info.mode & 0o777 };
            if (info.isFile()) {
                totalBytes += info.size;
                if (totalBytes > 512 * 1024 * 1024 || info.size > 128 * 1024 * 1024) throw new Error("The unsigned IPA file-size limit was exceeded.");
                entry.size = info.size;
                entry.sha256 = createHash("sha256").update(readFileSync(path)).digest("hex");
            }
            entries.push(entry);
            if (entries.length > 5000) throw new Error("The unsigned IPA entry limit was exceeded.");
            if (info.isDirectory()) {
                for (const name of readdirSync(path).sort()) {
                    validateName(name);
                    visit(join(path, name), entryPath ? `${entryPath}/${name}` : name);
                }
            }
        }
        visit(root, "");
        checkDeadline();
        return entries;
    }

    function requireSame(expected, actual, message) {
        if (JSON.stringify(expected) !== JSON.stringify(actual)) throw new Error(message);
    }

    try {
        report.phase = "source-inventory";
        const original = inventory(bundlePath);
        report.fileCount = original.filter(entry => entry.kind === "file").length;
        writeFileSync(join(outputDirectory, "bundle-inventory.json"), `${JSON.stringify(original, null, 2)}\n`);

        report.phase = "device-verification";
        const verification = verifyIosDeviceBundle({
            bundlePath, buildPropertiesPath, outputDirectory: join(outputDirectory, "device-verification"), execute, now
        });
        report.bundleIdentifier = verification.bundleIdentifier;
        if (process.platform !== "win32" && !(original.find(entry => entry.path === verification.executable)?.mode & 0o111)) {
            throw new Error("The app executable must retain executable permissions.");
        }
        checkDeadline();

        report.phase = "stage-payload";
        const workDirectory = join(outputDirectory, "work");
        const payloadPath = join(workDirectory, "Payload");
        mkdirSync(payloadPath, { recursive: true });
        const stagedBundle = join(payloadPath, "NutriFlow.Mobile.app");
        cpSync(bundlePath, stagedBundle, { recursive: true, force: false, errorOnExist: true, preserveTimestamps: true });
        requireSame(original, inventory(stagedBundle), "Staging changed the app files or permissions.");

        report.phase = "archive";
        invoke("ditto", ["-c", "-k", "--norsrc", "--noextattr", "--noacl", "--keepParent", payloadPath, ipaPath], "archive.log");
        const ipaInfo = lstatSync(ipaPath);
        if (!ipaInfo.isFile() || ipaInfo.nlink !== 1 || ipaInfo.size === 0 || ipaInfo.size > 512 * 1024 * 1024) {
            throw new Error("Expected a non-empty regular unsigned IPA within the size limit.");
        }
        const ipaHash = createHash("sha256").update(readFileSync(ipaPath)).digest("hex");

        report.phase = "archive-verification";
        invoke("unzip", ["-tq", ipaPath], "integrity.log");
        const listing = invoke("unzip", ["-Z", "-1", ipaPath], "entries.log");
        const names = listing.split(/\r?\n/);
        if (names.at(-1) === "") names.pop();
        const expected = new Map([["Payload/", { kind: "directory", mode: lstatSync(payloadPath).mode & 0o777 }]]);
        for (const entry of original) {
            const path = `Payload/NutriFlow.Mobile.app${entry.path ? `/${entry.path}` : ""}${entry.kind === "directory" ? "/" : ""}`;
            expected.set(path, entry);
        }
        const seen = new Set();
        for (const name of names) {
            const components = (name.endsWith("/") ? name.slice(0, -1) : name).split("/");
            components.forEach(validateName);
            if (!expected.has(name) || seen.has(name)) throw new Error("The IPA contains an unexpected or duplicate entry.");
            seen.add(name);
        }
        if (names.length === 0 || [...expected].some(([name, entry]) => entry.kind === "file" && !seen.has(name))) {
            throw new Error("The IPA is missing app files.");
        }
        const details = invoke("unzip", ["-Z", "-l", ipaPath], "entry-modes.log")
            .split(/\r?\n/).filter(line => /^[bcdlps-][rwxstST?-]{9}\s/.test(line));
        if (details.length !== names.length) throw new Error("The IPA entry type inventory is incomplete.");
        for (let index = 0; index < names.length; index++) {
            const entry = expected.get(names[index]);
            const line = details[index];
            if (line[0] !== (entry.kind === "directory" ? "d" : "-") || !line.endsWith(` ${names[index]}`)) {
                throw new Error("The IPA entry types must contain only the expected files and directories.");
            }
            const fields = line.slice(10).trimStart().split(/\s+/, 5);
            if (!/^\d+$/.test(fields[2] ?? "") || Number(fields[2]) !== (entry.size ?? 0) || !/^[tb][-xX]$/.test(fields[3] ?? "")) {
                throw new Error("Unexpected or encrypted IPA entry metadata.");
            }
            let mode = 0;
            for (let bit = 0; bit < 9; bit++) {
                const character = line[bit + 1];
                if (character === "rwx"[bit % 3]) mode |= 1 << (8 - bit);
                else if (character !== "-") throw new Error("Unsupported IPA permission bits.");
            }
            if (mode !== entry.mode) throw new Error("The IPA changed file permissions.");
        }
        report.entryCount = names.length;

        report.phase = "round-trip";
        const extractedPath = join(workDirectory, "extracted");
        mkdirSync(extractedPath);
        invoke("unzip", ["-q", ipaPath, "-d", extractedPath], "extraction.log");
        if (!lstatSync(extractedPath).isDirectory() || !lstatSync(join(extractedPath, "Payload")).isDirectory() ||
            readdirSync(extractedPath).join() !== "Payload" || readdirSync(join(extractedPath, "Payload")).join() !== "NutriFlow.Mobile.app") {
            throw new Error("The IPA must extract into exactly one Payload app.");
        }
        requireSame(original, inventory(join(extractedPath, "Payload", "NutriFlow.Mobile.app")), "The IPA round-trip changed app files or permissions.");
        requireSame(original, inventory(stagedBundle), "Packaging changed the staged app bundle.");
        requireSame(original, inventory(bundlePath), "Packaging changed the original app bundle.");
        const finalIpaInfo = lstatSync(ipaPath);
        if (!finalIpaInfo.isFile() || finalIpaInfo.nlink !== 1 || finalIpaInfo.size !== ipaInfo.size) throw new Error("The unsigned IPA changed during verification.");
        const finalIpaHash = createHash("sha256").update(readFileSync(ipaPath)).digest("hex");
        if (finalIpaHash !== ipaHash) throw new Error("The unsigned IPA changed during verification.");
        report.ipa = { name: basename(ipaPath), size: finalIpaInfo.size, sha256: finalIpaHash };
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
        if (process.argv.length !== 5) throw new Error("Usage: node scripts/package-ios-device-ipa.mjs <built.app> <build-properties.json> <new-output-directory>");
        if (process.platform !== "darwin") throw new Error("IPA packaging requires macOS and the verified iOS device build.");
        console.log(JSON.stringify(packageIosDeviceIpa({ bundlePath: process.argv[2], buildPropertiesPath: process.argv[3], outputDirectory: process.argv[4] }), null, 2));
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}
