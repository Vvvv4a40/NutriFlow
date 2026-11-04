import test from "node:test";
import assert from "node:assert/strict";
import {
    decode,
    encode,
    listEntries,
    pendingSuffix,
    readEntry,
    removeDatabase,
    removeEntry,
    writeDatabase,
    writeEntry
} from "../../web/NutriFlow.Web/wwwroot/js/storage-ops.mjs";

const text = value => new TextEncoder().encode(value);
const read = bytes => bytes === null ? null : new TextDecoder().decode(bytes);
const notFound = () => new DOMException("Not found", "NotFoundError");

// minimal origin private file system: directories, files, sync access handles and an injectable crash
class FakeDirectory {
    kind = "directory";
    entriesByName = new Map();
    crashAfterTruncate = null;

    async getDirectoryHandle(name, { create = false } = {}) {
        let entry = this.entriesByName.get(name);
        if (!entry) {
            if (!create) throw notFound();
            entry = new FakeDirectory();
            entry.crashAfterTruncate = this.crashAfterTruncate;
            this.entriesByName.set(name, entry);
        }
        if (entry.kind !== "directory") throw new DOMException("Type mismatch", "TypeMismatchError");
        return entry;
    }

    async getFileHandle(name, { create = false } = {}) {
        let entry = this.entriesByName.get(name);
        if (!entry) {
            if (!create) throw notFound();
            entry = new FakeFile(this, name);
            this.entriesByName.set(name, entry);
        }
        return entry;
    }

    async removeEntry(name) {
        if (!this.entriesByName.delete(name)) throw notFound();
    }

    async *entries() {
        yield* [...this.entriesByName.entries()];
    }
}

class FakeFile {
    kind = "file";
    bytes = new Uint8Array(0);

    constructor(directory, name) {
        this.directory = directory;
        this.name = name;
    }

    async getFile() {
        const bytes = this.bytes.slice();
        return { size: bytes.length, arrayBuffer: async () => bytes.buffer };
    }

    async createSyncAccessHandle() {
        const file = this;
        return {
            truncate(size) {
                file.bytes = file.bytes.slice(0, size);
                if (file.directory.crashAfterTruncate === file.name) throw new Error("page closed");
            },
            write(data, { at }) {
                const next = new Uint8Array(Math.max(file.bytes.length, at + data.length));
                next.set(file.bytes);
                next.set(data, at);
                file.bytes = next;
                return data.length;
            },
            flush() { },
            close() { }
        };
    }
}

async function directoryOf(root, path) {
    let directory = root;
    for (const part of path.split("/")) directory = await directory.getDirectoryHandle(part);
    return directory;
}

test("encoded files decode back, damaged or partial ones do not", async () => {
    const content = await encode(text("{\"Version\":1}"));
    assert.equal(read(await decode(content)), "{\"Version\":1}");

    const tampered = content.slice();
    tampered[tampered.length - 1] ^= 1;
    assert.equal(await decode(tampered), null);
    assert.equal(await decode(content.slice(0, content.length - 3)), null);
    assert.equal(await decode(content.slice(0, 20)), null);
    assert.equal(await decode(new Uint8Array(0), { allowLegacy: true }), null);
});

test("files written before the header existed are read only where legacy content is allowed", async () => {
    const legacy = text("{\"Profiles\":[]}");
    assert.equal(await decode(legacy), null);
    assert.equal(read(await decode(legacy, { allowLegacy: true })), "{\"Profiles\":[]}");

    const root = new FakeDirectory();
    const files = await root.getDirectoryHandle("nutriflow", { create: true }).then(d => d.getDirectoryHandle("files", { create: true }));
    (await files.getFileHandle("profiles.json", { create: true })).bytes = legacy;
    assert.equal(read(await readEntry(root, "nutriflow/files/profiles.json")), "{\"Profiles\":[]}");
});

test("a write creates folders, leaves one complete copy and no pending file", async () => {
    const root = new FakeDirectory();

    await writeEntry(root, "nutriflow/files/profiles/a1/input-draft.json", text("draft"));

    const folder = await directoryOf(root, "nutriflow/files/profiles/a1");
    assert.deepEqual([...folder.entriesByName.keys()], ["input-draft.json"]);
    assert.equal(read(await readEntry(root, "nutriflow/files/profiles/a1/input-draft.json")), "draft");
});

test("a write cut off in the main copy is completed from the pending copy on the next read", async () => {
    const root = new FakeDirectory();
    await writeEntry(root, "nutriflow/files/profiles.json", text("old"));
    const files = await directoryOf(root, "nutriflow/files");
    files.crashAfterTruncate = "profiles.json";

    await assert.rejects(writeEntry(root, "nutriflow/files/profiles.json", text("new")), /page closed/);
    files.crashAfterTruncate = null;

    const repaired = [];
    const payload = await readEntry(root, "nutriflow/files/profiles.json", async (path, bytes) => {
        repaired.push(path);
        await writeEntry(root, path, bytes);
    });
    assert.equal(read(payload), "new");
    assert.deepEqual(repaired, ["nutriflow/files/profiles.json"]);
    assert.deepEqual([...files.entriesByName.keys()], ["profiles.json"]);
    assert.equal(read(await readEntry(root, "nutriflow/files/profiles.json")), "new");
});

test("a write cut off in the pending copy keeps the previous content", async () => {
    const root = new FakeDirectory();
    await writeEntry(root, "nutriflow/files/profiles.json", text("old"));
    const files = await directoryOf(root, "nutriflow/files");
    files.crashAfterTruncate = "profiles.json" + pendingSuffix;

    await assert.rejects(writeEntry(root, "nutriflow/files/profiles.json", text("new")), /page closed/);
    files.crashAfterTruncate = null;

    let repaired = false;
    assert.equal(read(await readEntry(root, "nutriflow/files/profiles.json", () => { repaired = true; })), "old");
    assert.equal(repaired, false);
});

test("a damaged file without a pending copy is skipped and reported", async (context) => {
    const errors = context.mock.method(console, "error", () => { });
    const root = new FakeDirectory();
    await writeEntry(root, "nutriflow/files/profiles.json", text("content"));
    const file = await (await directoryOf(root, "nutriflow/files")).getFileHandle("profiles.json");
    file.bytes = file.bytes.slice(0, file.bytes.length - 1);

    assert.equal(await readEntry(root, "nutriflow/files/profiles.json"), null);
    assert.equal(errors.mock.callCount(), 1);
    assert.equal(await readEntry(root, "nutriflow/files/missing.json"), null);
    assert.equal(await readEntry(root, "nutriflow/missing/profiles.json"), null);
});

test("listing returns relative paths of files once, including those with only a pending copy", async () => {
    const root = new FakeDirectory();
    assert.deepEqual(await listEntries(root, "nutriflow/files"), []);

    await writeEntry(root, "nutriflow/files/profiles.json", text("p"));
    await writeEntry(root, "nutriflow/files/profiles/a1/label-photos/x.jpg", text("photo"));
    const files = await directoryOf(root, "nutriflow/files");
    files.crashAfterTruncate = "profiles.json";
    await assert.rejects(writeEntry(root, "nutriflow/files/profiles.json", text("q")));
    (await files.getFileHandle("draft.json" + pendingSuffix, { create: true })).bytes = await encode(text("d"));

    assert.deepEqual(await listEntries(root, "nutriflow/files"),
        ["draft.json", "profiles.json", "profiles/a1/label-photos/x.jpg"]);
});

test("removal deletes both copies and ignores missing files", async () => {
    const root = new FakeDirectory();
    await writeEntry(root, "nutriflow/files/profiles/a1/input-draft.json", text("draft"));
    const folder = await directoryOf(root, "nutriflow/files/profiles/a1");
    (await folder.getFileHandle("input-draft.json" + pendingSuffix, { create: true })).bytes = text("x");

    await removeEntry(root, "nutriflow/files/profiles/a1/input-draft.json");
    await removeEntry(root, "nutriflow/files/profiles/a1/input-draft.json");
    await removeEntry(root, "nutriflow/files/profiles/b2/input-draft.json");

    assert.equal(folder.entriesByName.size, 0);
});

test("paths that leave the folder or name a pending copy are rejected", async () => {
    const root = new FakeDirectory();
    for (const path of ["nutriflow/../profiles.json", "nutriflow//profiles.json", "/profiles.json", "profiles.json" + pendingSuffix]) {
        await assert.rejects(writeEntry(root, path, text("x")), /Invalid storage path/, path);
    }
});

test("a database for bsqlite is a plain file that replaces a stale journal, and goes away with its journals", async () => {
    const root = new FakeDirectory();
    const folder = await root.getDirectoryHandle("nutriflow", { create: true });
    (await folder.getFileHandle("backup-a.db-journal", { create: true })).bytes = text("stale");
    (await folder.getFileHandle("backup-a.db-wal", { create: true })).bytes = text("stale");
    (await folder.getFileHandle("profile-b.db", { create: true })).bytes = text("other");
    const image = text("SQLite format 3  pages");

    await writeDatabase(root, "nutriflow/backup-a.db", image);

    assert.deepEqual([...folder.entriesByName.keys()].sort(), ["backup-a.db", "profile-b.db"]);
    assert.deepEqual((await folder.getFileHandle("backup-a.db")).bytes, image);

    (await folder.getFileHandle("backup-a.db-wal", { create: true })).bytes = text("written by sqlite");
    await removeDatabase(root, "nutriflow/backup-a.db");
    await removeDatabase(root, "nutriflow/missing.db");
    await removeDatabase(root, "elsewhere/missing.db");

    assert.deepEqual([...folder.entriesByName.keys()], ["profile-b.db"]);
});
