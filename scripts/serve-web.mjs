// local static server for the prepared web deploy, close to github pages: sub-path base,
// on-the-fly gzip for text files, short caching and index.html for unknown paths
// usage: node scripts/serve-web.mjs --root=web/NutriFlow.Web/bin/deploy --base=/NutriFlow/ --port=5180
import { createServer } from "node:http";
import { createReadStream, existsSync, readFileSync, statSync } from "node:fs";
import { extname, join, normalize, resolve, sep } from "node:path";
import { gzipSync } from "node:zlib";

const args = Object.fromEntries(process.argv.slice(2).map(arg => arg.replace(/^--/, "").split("=")));
const root = resolve(args.root ?? ".");
const base = args.base ?? "/";
const port = Number(args.port ?? 5180);
const types = {
    ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript",
    ".json": "application/json", ".css": "text/css", ".wasm": "application/wasm", ".png": "image/png",
    ".svg": "image/svg+xml", ".webmanifest": "application/manifest+json", ".dat": "application/octet-stream",
    ".blat": "application/octet-stream", ".dll": "application/octet-stream", ".txt": "text/plain; charset=utf-8"
};
const compressible = new Set([".html", ".js", ".mjs", ".json", ".css", ".svg", ".webmanifest"]);

if (!existsSync(join(root, "index.html"))) {
    console.error(`No index.html in ${root}; publish and prepare the deploy first.`);
    process.exit(1);
}

createServer((request, response) => {
    const url = new URL(request.url, "http://localhost");
    if (!url.pathname.startsWith(base)) {
        response.writeHead(302, { Location: base });
        response.end();
        return;
    }

    let path = normalize(join(root, decodeURIComponent(url.pathname.slice(base.length))));
    if (path !== root && !path.startsWith(root + sep)) {
        response.writeHead(403);
        response.end();
        return;
    }
    if (!existsSync(path) || statSync(path).isDirectory()) path = join(root, "index.html");

    const extension = extname(path);
    const stat = statSync(path);
    const etag = `"${stat.size.toString(16)}-${Math.trunc(stat.mtimeMs).toString(16)}"`;
    const headers = {
        "Content-Type": types[extension] ?? "application/octet-stream",
        "Cache-Control": "max-age=600",
        "ETag": etag,
        "Vary": "Accept-Encoding"
    };
    if (request.headers["if-none-match"] === etag) {
        response.writeHead(304, headers);
        response.end();
        return;
    }

    if (compressible.has(extension) && (request.headers["accept-encoding"] ?? "").includes("gzip")) {
        const body = gzipSync(readFileSync(path));
        response.writeHead(200, { ...headers, "Content-Encoding": "gzip", "Content-Length": body.length });
        response.end(body);
        return;
    }

    response.writeHead(200, { ...headers, "Content-Length": stat.size });
    createReadStream(path).pipe(response);
}).listen(port, () => console.log(`serving ${root} at http://localhost:${port}${base}`));
