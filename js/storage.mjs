// reads go through the async OPFS api on the window; writes go through a worker with a sync access
// handle, because safari before 26 has no FileSystemFileHandle.createWritable
let worker = null;
let nextId = 1;
const pending = new Map();

async function locate(path, create) {
  const parts = path.split('/').filter(Boolean);
  const name = parts.pop();
  let directory = await navigator.storage.getDirectory();
  for (const part of parts) {
    directory = await directory.getDirectoryHandle(part, { create });
  }
  return { directory, name };
}

function isMissing(error) {
  return error?.name === 'NotFoundError' || error?.name === 'TypeError';
}

export async function listFiles(root) {
  try {
    const parts = root.split('/').filter(Boolean);
    let directory = await navigator.storage.getDirectory();
    for (const part of parts) {
      directory = await directory.getDirectoryHandle(part, { create: false });
    }
    const names = [];
    for await (const [name, handle] of directory.entries()) {
      if (handle.kind === 'file') {
        names.push(name);
      }
    }
    return names.sort();
  } catch (error) {
    if (isMissing(error)) {
      return [];
    }
    throw error;
  }
}

export async function readFile(path) {
  try {
    const { directory, name } = await locate(path, false);
    const file = await (await directory.getFileHandle(name, { create: false })).getFile();
    return new Uint8Array(await file.arrayBuffer());
  } catch (error) {
    if (isMissing(error)) {
      return null;
    }
    throw error;
  }
}

export function writeFile(path, bytes) {
  return send({ path, bytes });
}

export function workerHasSyncAccessHandle() {
  return send({ probe: true });
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
  const created = new Worker(new URL('./storage-worker.mjs', import.meta.url), { type: 'module' });
  created.addEventListener('message', ({ data }) => {
    const request = pending.get(data.id);
    pending.delete(data.id);
    if (data.ok) {
      request?.resolve(data.result);
    } else {
      request?.reject(new Error(data.error));
    }
  });
  created.addEventListener('error', event => {
    for (const request of pending.values()) {
      request.reject(new Error(`storage worker failed: ${event.message ?? 'unknown error'}`));
    }
    pending.clear();
  });
  return created;
}
