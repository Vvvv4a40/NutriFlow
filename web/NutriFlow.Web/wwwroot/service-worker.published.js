// offline worker of the published app, based on the blazor pwa template: https://aka.ms/blazor-offline-considerations

self.importScripts("./service-worker-assets.js");
self.addEventListener("install", event => event.waitUntil(onInstall()));
self.addEventListener("activate", event => event.waitUntil(onActivate()));
self.addEventListener("fetch", event => event.respondWith(onFetch(event)));
// the page asks a waiting worker to take over once the user agrees to update
self.addEventListener("message", event => {
    if (event.data?.type === "SKIP_WAITING") self.skipWaiting();
});

const cacheNamePrefix = "nutriflow-offline-";
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
// cache of the storage check that the test stand served before the app, under the template prefix
const legacyCacheNamePrefix = "offline-cache-";
// the template list lacks .mjs: our modules and the bsqlite engine would be missing offline
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.mjs$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.svg$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
// the app always starts with ru-RU and only the opfs storage, so these files are never requested
const offlineAssetsExclude = [/^service-worker\.js$/, /icudt_(CJK|EFIGS)\./, /\/wa-sqlite-(async|jspi)\./];

// the deploy script replaces this with the sub-path of the host, keep the trailing slash
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: "no-cache" }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => (key.startsWith(cacheNamePrefix) && key !== cacheName) || key.startsWith(legacyCacheNamePrefix))
        .map(key => caches.delete(key)));
}

async function onFetch(event) {
    let cachedResponse = null;
    if (event.request.method === "GET") {
        // navigations get the cached index.html unless they ask for a listed asset
        const shouldServeIndexHtml = event.request.mode === "navigate"
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? "index.html" : event.request;
        const cache = await caches.open(cacheName);
        // bsqlite imports its modules with a ?v=<version> query that the manifest urls do not carry
        const ignoreSearch = new URL(event.request.url).pathname.includes("/_content/Bit.Bsqlite/");
        cachedResponse = await cache.match(request, { ignoreSearch });
    }

    return cachedResponse || fetch(event.request);
}
