// file operations of the opfs mirror, shared by the page (reads) and the storage worker (writes).
// every file is stored with a header - magic, payload length, sha-256 - and is written first to
// "<name>.nf-pending" and only then to "<name>", so an interrupted write always leaves one complete copy

export const pendingSuffix = ".nf-pending";
const magic = [0x4e, 0x46, 0x4d, 0x31];
const headerLength = 40;

export async function encode(payload, subtle = globalThis.crypto.subtle) {
    const digest = new Uint8Array(await subtle.digest("SHA-256", payload));
    const content = new Uint8Array(headerLength + payload.length);
    content.set(magic, 0);
    new DataView(content.buffer).setUint32(4, payload.length, true);
    content.set(digest, 8);
    content.set(payload, headerLength);
    return content;
}

// the payload of a complete file, or null; files written before the header existed are taken as they are
export async function decode(content, { allowLegacy = false } = {}, subtle = globalThis.crypto.subtle) {
    if (content.length === 0) return null;
    if (!magic.every((value, index) => content[index] === value)) return allowLegacy ? content : null;
    if (content.length < headerLength) return null;
    const length = new DataView(content.buffer, content.byteOffset, content.byteLength).getUint32(4, true);
    if (content.length !== headerLength + length) return null;
    const payload = content.slice(headerLength);
    const digest = new Uint8Array(await subtle.digest("SHA-256", payload));
    return digest.every((value, index) => value === content[8 + index]) ? payload : null;
}

export async function listEntries(root, base) {
    const paths = new Set();
    let directory;
    try {
        directory = await descend(root, splitPath(base), false);
    } catch (error) {
        if (isMissing(error)) return [];
        throw error;
    }

    await collect(directory, "", paths);
    return [...paths].sort();
}

export async function readEntry(root, path, repair) {
    let parent;
    try {
        parent = await locate(root, path, false);
    } catch (error) {
        if (isMissing(error)) return null;
        throw error;
    }

    const { directory, name } = parent;
    const pending = await readBytes(directory, name + pendingSuffix);
    if (pending) {
        const payload = await decode(pending);
        if (payload) {
            // the last write stopped before it finished the main copy
            await repair?.(path, payload);
            return payload;
        }
    }

    const stored = await readBytes(directory, name);
    if (!stored) return null;
    const payload = await decode(stored, { allowLegacy: true });
    if (!payload) console.error(`NutriFlow: stored file is damaged and was skipped: ${path}`);
    return payload;
}

export async function writeEntry(root, path, payload) {
    const { directory, name } = await locate(root, path, true);
    const content = await encode(payload);
    await writeWhole(directory, name + pendingSuffix, content);
    await writeWhole(directory, name, content);
    await removeIfPresent(directory, name + pendingSuffix);
}

export async function removeEntry(root, path) {
    let parent;
    try {
        parent = await locate(root, path, false);
    } catch (error) {
        if (isMissing(error)) return;
        throw error;
    }

    await removeIfPresent(parent.directory, parent.name);
    await removeIfPresent(parent.directory, parent.name + pendingSuffix);
}

async function writeWhole(directory, name, content) {
    const handle = await directory.getFileHandle(name, { create: true });
    // early safari versions return promises from these methods, so every call is awaited
    const access = await handle.createSyncAccessHandle();
    try {
        await access.truncate(0);
        const written = await access.write(content, { at: 0 });
        if (written !== content.length) throw new Error(`Short write: ${written} of ${content.length} bytes`);
        await access.flush();
    } finally {
        await access.close();
    }
}

async function collect(directory, prefix, paths) {
    for await (const [name, handle] of directory.entries()) {
        if (handle.kind === "directory") {
            await collect(handle, `${prefix}${name}/`, paths);
        } else {
            paths.add(prefix + (name.endsWith(pendingSuffix) ? name.slice(0, -pendingSuffix.length) : name));
        }
    }
}

async function readBytes(directory, name) {
    try {
        const file = await (await directory.getFileHandle(name)).getFile();
        return new Uint8Array(await file.arrayBuffer());
    } catch (error) {
        if (isMissing(error)) return null;
        throw error;
    }
}

async function removeIfPresent(directory, name) {
    try {
        await directory.removeEntry(name);
    } catch (error) {
        if (!isMissing(error)) throw error;
    }
}

async function locate(root, path, create) {
    const parts = splitPath(path);
    const name = parts.pop();
    return { directory: await descend(root, parts, create), name };
}

async function descend(directory, parts, create) {
    for (const part of parts) directory = await directory.getDirectoryHandle(part, { create });
    return directory;
}

function splitPath(path) {
    const parts = path.split("/");
    if (parts.length === 0 || parts.some(part => part === "" || part === "." || part === ".." || part.endsWith(pendingSuffix))) {
        throw new Error(`Invalid storage path: ${path}`);
    }

    return parts;
}

function isMissing(error) {
    // safari reports a missing directory as TypeError
    return error?.name === "NotFoundError" || error?.name === "TypeError";
}
