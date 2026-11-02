import { removeEntry, writeEntry } from "./storage-ops.mjs";

// one operation at a time: a sync access handle is exclusive, so two writes of one file would collide
let queue = Promise.resolve();

self.addEventListener("message", ({ data }) => {
    queue = queue.then(() => run(data));
});

async function run({ id, operation, path, bytes }) {
    try {
        const root = await navigator.storage.getDirectory();
        if (operation === "write") {
            await writeEntry(root, path, bytes);
        } else if (operation === "remove") {
            await removeEntry(root, path);
        } else {
            throw new Error(`Unknown storage operation: ${operation}`);
        }

        self.postMessage({ id, ok: true });
    } catch (error) {
        self.postMessage({ id, ok: false, error: `${error?.name ?? "Error"}: ${error?.message ?? error}` });
    }
}
