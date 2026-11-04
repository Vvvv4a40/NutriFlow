import test from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const worker = readFileSync(join(root, "web/NutriFlow.Web/wwwroot/service-worker.published.js"), "utf8");

// the worker script with its install lists, under a fake self
function loadWorker() {
    const listeners = new Map();
    const self = {
        origin: "https://example.test",
        assetsManifest: { version: "v1", assets: [] },
        importScripts() { },
        skipped: 0,
        skipWaiting() { this.skipped++; },
        addEventListener(type, listener) { listeners.set(type, listener); }
    };
    const context = vm.createContext({ self, URL });
    const lists = vm.runInContext(`${worker}\n;({ offlineAssetsInclude, offlineAssetsExclude })`, context);
    return { self, listeners, ...lists };
}

function cachedOffline(name, { offlineAssetsInclude, offlineAssetsExclude }) {
    return offlineAssetsInclude.some(pattern => pattern.test(name)) && !offlineAssetsExclude.some(pattern => pattern.test(name));
}

function modulesIn(folder, prefix) {
    return readdirSync(join(root, folder)).filter(name => name.endsWith(".mjs")).map(name => prefix + name);
}

test("every module of the app and its library is cached for offline use", () => {
    const lists = loadWorker();
    const modules = [
        ...modulesIn("web/NutriFlow.Web/wwwroot/js", "js/"),
        ...modulesIn("NutriFlow.Ui/wwwroot", "_content/NutriFlow.Ui/"),
        "js/speech-capture.mjs",
        "_content/Bit.Bsqlite/wa-sqlite.mjs"
    ];

    assert.ok(modules.length > 8, modules.join());
    for (const name of modules) {
        assert.ok(cachedOffline(name, lists), name);
    }
});

test("the page, the styles, the settings, the icons and the runtime are cached, the unused engines are not", () => {
    const lists = loadWorker();
    for (const name of ["index.html", "manifest.webmanifest", "appsettings.json", "css/app.css", "_content/NutriFlow.Ui/nutriflow-ui.css",
        "icons/icon-192.png", "_framework/dotnet.native.abc.wasm", "_framework/NutriFlow.Web.abc.wasm", "_framework/icudt_no_CJK.abc.dat"]) {
        assert.ok(cachedOffline(name, lists), name);
    }

    for (const name of ["service-worker.js", "_framework/icudt_CJK.abc.dat", "_content/Bit.Bsqlite/wa-sqlite-async.mjs"]) {
        assert.ok(!cachedOffline(name, lists), name);
    }
});

test("a new worker waits until the page asks it to take over", () => {
    const { self, listeners } = loadWorker();

    listeners.get("message")({ data: { type: "other" } });
    assert.equal(self.skipped, 0);

    listeners.get("message")({ data: { type: "SKIP_WAITING" } });
    assert.equal(self.skipped, 1);
    assert.ok(!worker.includes("clients.claim"), "the first worker must not take over a running page");
});

test("every startup screen has its template in the page", () => {
    const page = readFileSync(join(root, "web/NutriFlow.Web/wwwroot/index.html"), "utf8");
    for (const screen of ["unsupported", "install", "second-instance", "moved"]) {
        assert.ok(page.includes(`id="startup-${screen}"`), screen);
    }
});
