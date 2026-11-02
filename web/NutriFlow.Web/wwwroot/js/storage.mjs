// opfs api for PersistentDirectory: reads use the async file api on the page, writes and removals go
// through a worker with sync access handles, because safari before 26 has no createWritable
import { listEntries, readEntry } from "./storage-ops.mjs";

let worker = null;
let nextId = 1;
const pending = new Map();

export async function listFiles(base) {
    return listEntries(await navigator.storage.getDirectory(), base);
}

export async function readFile(path) {
    return readEntry(await navigator.storage.getDirectory(), path, writeFile);
}

export function writeFile(path, bytes) {
    return send({ operation: "write", path, bytes });
}

export function removeFile(path) {
    return send({ operation: "remove", path });
}

function send(message) {
    worker ??= createWorker();
    const id = nextId++;
    return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        worker.postMessage({ id, ...message });
    });
}

function createWorker() {
    const created = new Worker(new URL("./storage-worker.mjs", import.meta.url), { type: "module" });
    created.addEventListener("message", ({ data }) => {
        const request = pending.get(data.id);
        pending.delete(data.id);
        if (data.ok) {
            request?.resolve();
        } else {
            // the error name (QuotaExceededError and others) stays at the start of the message for .NET
            request?.reject(new Error(data.error));
        }
    });
    created.addEventListener("error", event => {
        for (const request of pending.values()) {
            request.reject(new Error(`StorageWorkerError: ${event.message ?? "the storage worker stopped"}`));
        }
        pending.clear();
        worker = null;
    });
    return created;
}
