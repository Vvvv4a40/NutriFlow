import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { prepareWebDeploy } from "../prepare-web-deploy.mjs";

const importMap = '\r\n{ "imports": { "./_framework/dotnet.js": "./_framework/dotnet.abc123.js" } }\r\n';

function sha256(content) {
    return "sha256-" + createHash("sha256").update(content).digest("base64");
}

function createPublishedSite() {
    const root = mkdtempSync(join(tmpdir(), "nutriflow-web-deploy-"));
    const source = join(root, "wwwroot");
    mkdirSync(join(source, "_framework"), { recursive: true });
    writeFileSync(join(source, "index.html"), [
        "<html><head>",
        `<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'" />`,
        '<base href="/" />',
        `<script type="importmap">${importMap}</script>`,
        "</head></html>"
    ].join("\r\n"));
    writeFileSync(join(source, "service-worker.js"), 'const base = "/";\nconst baseUrl = new URL(base, self.origin);\n');
    writeFileSync(join(source, "appsettings.json"), '{\n  "Channel": "test"\n}\n');
    writeFileSync(join(source, "_framework", "dotnet.abc123.js"), "export {};");
    const assets = ["index.html", "service-worker.js", "appsettings.json", "_framework/dotnet.abc123.js"]
        .map(url => ({ hash: sha256(readFileSync(join(source, url))), url }));
    writeFileSync(join(source, "service-worker-assets.js"),
        `self.assetsManifest = ${JSON.stringify({ version: "ORIGINAL", assets }, null, 2)};\n`);
    for (const name of ["index.html", "service-worker-assets.js", "_framework/dotnet.abc123.js"]) {
        writeFileSync(join(source, name + ".br"), "stale");
        writeFileSync(join(source, name + ".gz"), "stale");
    }
    return { root, source, target: join(root, "deploy") };
}

function readManifest(target) {
    const text = readFileSync(join(target, "service-worker-assets.js"), "utf8").trim();
    return JSON.parse(text.slice("self.assetsManifest = ".length, -1));
}

test("prepares a sub-path deploy with base, channel, csp hash and fresh integrity hashes", () => {
    const site = createPublishedSite();
    try {
        const result = prepareWebDeploy({ source: site.source, target: site.target, base: "/NutriFlow/", channel: "production" });

        const html = readFileSync(join(site.target, "index.html"), "utf8");
        assert.match(html, /<base href="\/NutriFlow\/" \/>/);
        const expectedHash = sha256(importMap.replace(/\r\n/g, "\n"));
        assert.ok(html.includes(`script-src 'self' 'wasm-unsafe-eval' '${expectedHash}'`));
        assert.match(readFileSync(join(site.target, "service-worker.js"), "utf8"), /const base = "\/NutriFlow\/";/);
        assert.equal(JSON.parse(readFileSync(join(site.target, "appsettings.json"), "utf8")).Channel, "production");

        const manifest = readManifest(site.target);
        for (const asset of manifest.assets) {
            assert.equal(asset.hash, sha256(readFileSync(join(site.target, asset.url))), asset.url);
        }
        assert.notEqual(manifest.version, "ORIGINAL");
        assert.equal(manifest.version, result.version);

        assert.equal(existsSync(join(site.target, "index.html.br")), false);
        assert.equal(existsSync(join(site.target, "service-worker-assets.js.gz")), false);
        assert.equal(existsSync(join(site.target, "_framework", "dotnet.abc123.js.br")), true);
        assert.equal(existsSync(join(site.target, ".nojekyll")), true);
        assert.match(readFileSync(join(site.source, "index.html"), "utf8"), /<base href="\/" \/>/);
    } finally {
        rmSync(site.root, { recursive: true, force: true });
    }
});

test("root deploy keeps the base and still pins the import map", () => {
    const site = createPublishedSite();
    try {
        prepareWebDeploy({ source: site.source, target: site.target });

        const html = readFileSync(join(site.target, "index.html"), "utf8");
        assert.match(html, /<base href="\/" \/>/);
        assert.match(html, /'wasm-unsafe-eval' 'sha256-[^']+'/);
        assert.equal(JSON.parse(readFileSync(join(site.target, "appsettings.json"), "utf8")).Channel, "test");
    } finally {
        rmSync(site.root, { recursive: true, force: true });
    }
});

test("rejects invalid base, unknown channel and an unexpected published page", () => {
    const site = createPublishedSite();
    try {
        assert.throws(() => prepareWebDeploy({ source: site.source, target: site.target, base: "NutriFlow" }), /Base path/);
        assert.throws(() => prepareWebDeploy({ source: site.source, target: site.target, base: "/NutriFlow" }), /Base path/);
        assert.throws(() => prepareWebDeploy({ source: site.source, target: site.target, channel: "staging" }), /Channel/);

        writeFileSync(join(site.source, "index.html"), "<html><head><base href=\"/\" /></head></html>");
        assert.throws(() => prepareWebDeploy({ source: site.source, target: site.target }), /import map/);
    } finally {
        rmSync(site.root, { recursive: true, force: true });
    }
});
