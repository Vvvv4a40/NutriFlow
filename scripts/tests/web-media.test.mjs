import test from "node:test";
import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { createVoiceCapture, needsEncoding, readPhoto, voiceMessages } from "../../web/NutriFlow.Web/wwwroot/js/media.mjs";
import { createPhotoUrl, revokePhotoUrl } from "../../NutriFlow.Ui/wwwroot/photo.mjs";

const megabyte = 1024 * 1024;

function file(type, size = 3, name = "photo") {
    const blob = new Blob([new Uint8Array(size)], { type });
    blob.name = name;
    return blob;
}

// the page around the canvas: records what was drawn and encodes it into a fixed result
function environment({ width = 4000, height = 3000, decodeError = null, encoded = new Blob([new Uint8Array(5)], { type: "image/jpeg" }) } = {}) {
    const calls = { urls: [], revoked: [], fills: [], drawn: [], encodings: [], canvases: [] };
    return {
        calls,
        URL: {
            createObjectURL(value) {
                calls.urls.push(value);
                return "blob:photo";
            },
            revokeObjectURL: url => calls.revoked.push(url)
        },
        document: {
            createElement(tag) {
                if (tag === "img") {
                    return {
                        naturalWidth: width,
                        naturalHeight: height,
                        decode: async () => {
                            if (decodeError) throw decodeError;
                        }
                    };
                }

                const canvas = {
                    width: 0,
                    height: 0,
                    getContext: () => ({
                        fillRect: (...args) => calls.fills.push(args),
                        drawImage: (image, ...args) => calls.drawn.push(args)
                    }),
                    toBlob(callback, type, quality) {
                        calls.encodings.push({ type, quality });
                        callback(encoded);
                    }
                };
                calls.canvases.push(canvas);
                return canvas;
            }
        }
    };
}

test("jpeg, png and webp within the limit stay as they are", () => {
    for (const type of ["image/jpeg", "image/png", "image/webp"]) {
        assert.equal(needsEncoding(file(type)), false, type);
    }
});

test("other formats and photos over 8 mb are encoded", () => {
    assert.equal(needsEncoding(file("image/heic")), true);
    assert.equal(needsEncoding(file("")), true);
    assert.equal(needsEncoding({ type: "image/jpeg", size: 8 * megabyte + 1 }), true);
    assert.equal(needsEncoding({ type: "image/jpeg", size: 8 * megabyte }), false);
});

test("a photo in a good format reaches .NET unchanged and the input can take the same file again", async () => {
    const page = environment();
    const input = { files: [file("image/png", 4)], value: "C:\\fakepath\\photo.png" };

    const photo = await readPhoto(input, page);

    assert.equal(photo.mediaType, "image/png");
    assert.equal(photo.content.length, 4);
    assert.equal(input.value, "");
    assert.deepEqual(page.calls.urls, []);
});

test("no file means nothing was chosen", async () => {
    assert.equal(await readPhoto({ files: [], value: "" }, environment()), null);
});

test("heic becomes a jpeg with the long side within 2560 px over a white background", async () => {
    const page = environment({ width: 4000, height: 3000 });

    const photo = await readPhoto({ files: [file("image/heic")], value: "" }, page);

    assert.equal(photo.mediaType, "image/jpeg");
    assert.equal(photo.content.length, 5);
    const canvas = page.calls.canvases[0];
    assert.deepEqual([canvas.width, canvas.height], [2560, 1920]);
    assert.deepEqual(page.calls.drawn, [[0, 0, 2560, 1920]]);
    assert.deepEqual(page.calls.fills, [[0, 0, 2560, 1920]]);
    assert.deepEqual(page.calls.encodings, [{ type: "image/jpeg", quality: 0.9 }]);
    assert.deepEqual(page.calls.revoked, ["blob:photo"]);
});

test("a small photo in another format is not enlarged", async () => {
    const page = environment({ width: 800, height: 600 });

    await readPhoto({ files: [file("image/gif")], value: "" }, page);

    assert.deepEqual([page.calls.canvases[0].width, page.calls.canvases[0].height], [800, 600]);
});

test("a large jpeg is encoded again to fit the limit", async () => {
    const page = environment();
    const big = new Blob([new Uint8Array(8 * megabyte + 1)], { type: "image/jpeg" });

    const photo = await readPhoto({ files: [big], value: "" }, page);

    assert.equal(photo.content.length, 5);
    assert.equal(page.calls.encodings.length, 1);
});

test("what the browser cannot decode or encode rejects, cleans up and leaves the input ready", async () => {
    for (const page of [
        environment({ decodeError: new Error("EncodingError") }),
        environment({ encoded: null }),
        environment({ encoded: { size: 8 * megabyte + 1, type: "image/jpeg" } })
    ]) {
        const input = { files: [file("image/heic")], value: "x" };
        await assert.rejects(readPhoto(input, page));
        assert.equal(input.value, "");
        assert.deepEqual(page.calls.revoked, ["blob:photo"]);
    }
});

test("the voice capture gets the limit of the form and the texts of the app, its events reach .NET", async () => {
    let options;
    const capture = {};
    const calls = [];
    const watcher = {
        invokeMethodAsync: async (method, ...args) => {
            calls.push([method, ...args]);
            return method === "OnRecorded" ? "гречка" : undefined;
        }
    };

    const created = await createVoiceCapture(watcher, 110_000, settings => {
        options = settings;
        return capture;
    });

    assert.equal(created, capture);
    assert.equal(options.maximumDurationInMilliseconds, 110_000);
    assert.equal(options.messages, voiceMessages);
    assert.match(voiceMessages.microphoneDenied, /^Нет доступа к микрофону\./);
    assert.match(voiceMessages.unsupported, /^Этот браузер не записывает звук\./);

    const text = await options.transcribe(new Blob([new Uint8Array([1, 2])], { type: "audio/webm;codecs=opus" }));
    options.onStateChange("recording");
    options.onTranscript("гречка");
    options.onError("Нет доступа");

    assert.equal(text, "гречка");
    assert.deepEqual(calls[0], ["OnRecorded", new Uint8Array([1, 2]), "audio/webm;codecs=opus"]);
    assert.deepEqual(calls.slice(1), [["OnPhaseChanged", "recording"], ["OnTranscribed", "гречка"], ["OnFailed", "Нет доступа"]]);
});

test("the recorder of the old web client is linked into the web app, not copied", async () => {
    const project = await readFile(new URL("../../web/NutriFlow.Web/NutriFlow.Web.csproj", import.meta.url), "utf8");
    assert.match(project, /Include="\.\.\/\.\.\/NutriFlow\.Api\/wwwroot\/js\/speech-capture\.mjs" Link="wwwroot\/js\/speech-capture\.mjs"\s+ContentRoot=/);
    await assert.rejects(readFile(new URL("../../web/NutriFlow.Web/wwwroot/js/speech-capture.mjs", import.meta.url)));
});

test("handled failures do not write to Console.Error: in webassembly it opens the fatal bar of the page", async () => {
    for (const folder of ["../../NutriFlow.Ui/", "../../web/NutriFlow.Web/"]) {
        const root = new URL(folder, import.meta.url);
        const files = await readdir(root, { recursive: true });
        for (const file of files.filter(name => /\.(cs|razor)$/.test(name) && !/(^|[\\/])(bin|obj)[\\/]/.test(name))) {
            const source = await readFile(new URL(file.replaceAll("\\", "/"), root), "utf8");
            assert.ok(!source.includes("Console.Error.Write"), file);
        }
    }
});

test("the preview of a photo is a blob url that can be released", () => {
    const made = [];
    const revoked = [];
    const page = {
        Blob,
        URL: {
            createObjectURL(blob) {
                made.push(blob);
                return "blob:preview";
            },
            revokeObjectURL: url => revoked.push(url)
        }
    };

    assert.equal(createPhotoUrl(new Uint8Array([1, 2]), "image/png", page), "blob:preview");
    assert.equal(made[0].type, "image/png");
    assert.equal(made[0].size, 2);

    revokePhotoUrl("blob:preview", page);
    assert.deepEqual(revoked, ["blob:preview"]);
});
