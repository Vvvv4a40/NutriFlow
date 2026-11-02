// turns the published wwwroot of NutriFlow.Web into the site uploaded to a host such as github.io/NutriFlow/:
// rewrites the base path, writes the channel, allows the inline import map in the csp by its hash
// and refreshes the hashes the service worker checks with integrity while installing
import { cpSync, existsSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const channels = ["test", "production"];
const manifestPrefix = "self.assetsManifest = ";

export function prepareWebDeploy({ source, target, base = "/", channel = "test" }) {
    if (!/^\/([^/]+\/)*$/.test(base)) throw new Error(`Base path must start and end with a slash: ${base}`);
    if (!channels.includes(channel)) throw new Error(`Channel must be one of ${channels.join(", ")}: ${channel}`);
    if (!existsSync(join(source, "index.html"))) throw new Error(`Published wwwroot not found: ${source}`);

    rmSync(target, { recursive: true, force: true });
    cpSync(source, target, { recursive: true });
    const edited = [];
    const edit = (name, change) => {
        const path = join(target, name);
        writeFileSync(path, change(readFileSync(path, "utf8")));
        edited.push(name);
    };

    edit("index.html", html => {
        html = replaceOnce(html, '<base href="/" />', `<base href="${base}" />`, "index.html base");
        const importMap = /<script type="importmap">([\s\S]*?)<\/script>/.exec(html);
        if (!importMap || importMap[1].trim() === "") throw new Error("index.html has no filled import map");
        // the html parser turns crlf into lf before the browser hashes an inline script
        const hash = sha256(importMap[1].replace(/\r\n?/g, "\n"));
        return replaceOnce(html, "script-src 'self' 'wasm-unsafe-eval'",
            `script-src 'self' 'wasm-unsafe-eval' '${hash}'`, "index.html script-src");
    });
    edit("service-worker.js", worker =>
        replaceOnce(worker, 'const base = "/";', `const base = "${base}";`, "service-worker.js base"));
    edit("appsettings.json", settings => {
        const value = JSON.parse(settings);
        value.Channel = channel;
        return JSON.stringify(value, null, 2) + "\n";
    });

    const manifestName = "service-worker-assets.js";
    const manifest = readAssetsManifest(join(target, manifestName));
    for (const asset of manifest.assets) {
        if (edited.includes(asset.url)) asset.hash = sha256(readFileSync(join(target, asset.url)));
    }
    // a new version gives the worker a new cache, so an edited channel or base is not mixed with old files
    manifest.version = createHash("sha256").update(manifest.assets.map(asset => asset.hash).join("\n"))
        .digest("base64url").slice(0, 8);
    writeFileSync(join(target, manifestName), `${manifestPrefix}${JSON.stringify(manifest, null, 2)};\n`);

    for (const name of [...edited, manifestName]) {
        for (const suffix of [".br", ".gz"]) rmSync(join(target, name + suffix), { force: true });
    }
    writeFileSync(join(target, ".nojekyll"), "");
    return { version: manifest.version, edited };
}

function readAssetsManifest(path) {
    const text = readFileSync(path, "utf8").trim();
    if (!text.startsWith(manifestPrefix) || !text.endsWith(";")) throw new Error(`Unexpected assets manifest: ${path}`);
    return JSON.parse(text.slice(manifestPrefix.length, -1));
}

function replaceOnce(text, search, replacement, description) {
    const index = text.indexOf(search);
    if (index < 0 || text.indexOf(search, index + search.length) >= 0) {
        throw new Error(`Expected exactly one match for ${description}`);
    }
    return text.slice(0, index) + replacement + text.slice(index + search.length);
}

function sha256(content) {
    return "sha256-" + createHash("sha256").update(content).digest("base64");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = Object.fromEntries(process.argv.slice(2).map(arg => {
        const [name, ...value] = arg.replace(/^--/, "").split("=");
        return [name, value.join("=")];
    }));
    const result = prepareWebDeploy({ source: args.source, target: args.target, base: args.base, channel: args.channel });
    console.log(`prepared ${args.target} for ${args.base ?? "/"} (${args.channel ?? "test"}, assets ${result.version})`);
}
