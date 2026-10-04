import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const source = (await readFile(new URL("../../NutriFlow.Api/wwwroot/js/app.js", import.meta.url), "utf8"))
    .replace(/^import .*?;\r?\n/, "");

class Element {
    constructor() {
        this.value = "";
        this.disabled = false;
        this.hidden = false;
        this.textContent = "";
        this.innerHTML = "";
        this.elements = {};
        this.lastChild = { textContent: "" };
        this.children = new Map();
        this.listeners = new Map();
        this.attributes = new Map();
        this.classes = new Set();
        this.classList = {
            add: (...names) => names.forEach(name => this.classes.add(name)),
            remove: (...names) => names.forEach(name => this.classes.delete(name)),
            toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name)
        };
    }

    addEventListener(name, listener) { this.listeners.set(name, listener); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
    focus() { this.focused = true; }
    querySelector(selector) {
        if (!this.children.has(selector)) this.children.set(selector, new Element());
        return this.children.get(selector);
    }
    async emit(name) { await this.listeners.get(name)?.({ preventDefault() {} }); }
}

async function flushPromises() {
    for (let index = 0; index < 12; index++) await Promise.resolve();
}

async function createPage(options = {}) {
    const elements = new Map();
    const requests = [];
    const storage = new Map();
    const windowListeners = new Map();
    const documentListeners = new Map();
    let callbacks;
    let starts = 0;
    let cancels = 0;
    const element = selector => {
        if (!elements.has(selector)) elements.set(selector, new Element());
        return elements.get(selector);
    };
    const document = {
        hidden: false,
        querySelector: element,
        querySelectorAll: () => [],
        addEventListener: (name, listener) => documentListeners.set(name, listener)
    };
    const window = {
        isSecureContext: options.secureContext ?? true,
        MediaRecorder: class {},
        localStorage: {
            getItem: key => storage.get(key) ?? null,
            setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        },
        addEventListener: (name, listener) => windowListeners.set(name, listener),
        setTimeout: () => 1,
        clearTimeout: () => {},
        confirm: options.confirm ?? (() => { throw new Error("Unexpected confirmation dialog."); })
    };
    const capabilities = options.capabilities ?? {
        aiProvider: "Groq",
        supportsFreeText: true,
        supportsLabelPhotos: true,
        supportsSpeechTranscription: true
    };
    const capture = {
        start: async () => {
            starts++;
            callbacks.onStateChange("requesting");
            callbacks.onStateChange("recording");
        },
        cancel: () => {
            cancels++;
            callbacks.onStateChange("idle");
        },
        stop: async () => {
            callbacks.onStateChange("transcribing");
            try {
                const text = await callbacks.transcribe(
                    new Blob(["test audio"], { type: options.mediaType ?? "audio/webm;codecs=opus" }),
                    new AbortController().signal);
                callbacks.onStateChange("idle");
                callbacks.onTranscript(text);
            } catch (error) {
                callbacks.onStateChange("idle");
                callbacks.onError(error.message);
            }
        }
    };

    runInNewContext(source, {
        window, document, FormData, Blob, AbortController, console,
        navigator: { mediaDevices: { getUserMedia: async () => {} } },
        getRecordingMimeType: () => options.supported === false ? null : "audio/webm;codecs=opus",
        createSpeechCapture: value => { callbacks = value; return capture; },
        fetch: async (url, settings) => {
            requests.push({ url, settings });
            const customResponse = await options.fetch?.(url, settings);
            if (customResponse !== undefined) return customResponse;
            const audioRequest = url === "/api/audio/transcribe";
            const status = audioRequest ? options.audioStatus ?? 200 : 200;
            const body = url === "/api/capabilities" ? capabilities :
                audioRequest ? { text: "Добавил 600 г говядины.", detail: "provider detail" } : {};
            return { ok: status < 400, status, text: async () => JSON.stringify(body) };
        }
    });
    await flushPromises();

    return {
        element, requests, storage, callbacks, capture, document, documentListeners, windowListeners,
        get starts() { return starts; }, get cancels() { return cancels; }
    };
}

test("recognized text stays editable and is not added until the user submits", async () => {
    const page = await createPage();
    await page.element("#voice-record-button").emit("click");
    assert.equal(page.element("#message-input").disabled, true);
    assert.equal(page.element("#add-message-button").disabled, true);
    assert.equal(page.element("#meal-date").disabled, true);
    assert.equal(page.element("#open-catalog-button").disabled, true);
    assert.equal(page.element("#voice-cancel-button").hidden, false);
    await page.element("#message-form").emit("submit");
    assert.equal(page.storage.size, 0);

    await page.element("#voice-record-button").emit("click");
    await flushPromises();
    assert.equal(page.element("#message-input").value, "Добавил 600 г говядины.");
    assert.equal(page.element("#message-input").disabled, false);
    assert.equal(page.element("#voice-record-button").disabled, true);
    assert.match(page.element("#voice-status").textContent, /Проверьте названия и массы/);
    assert.equal(page.storage.size, 0);
    assert.equal(page.requests.filter(request => request.settings.method === "POST").length, 1);
    const request = page.requests.find(item => item.url === "/api/audio/transcribe");
    assert.ok(request.settings.body instanceof FormData);
    assert.equal(request.settings.body.get("audio").name, "voice-recording.webm");
    assert.equal(request.settings.body.get("audio").type, "audio/webm;codecs=opus");
    assert.equal(request.settings.headers["Content-Type"], undefined);

    page.element("#message-input").value = "Добавил 650 г говядины.";
    await page.element("#message-input").emit("input");
    await page.element("#message-form").emit("submit");
    assert.equal(page.element("#message-input").value, "");
    assert.match(page.element("#message-list").innerHTML, /650 г/);
    assert.doesNotMatch(page.element("#message-list").innerHTML, /600 г/);
    assert.equal(page.element("#voice-record-button").disabled, false);
});

for (const [name, options, expected] of [
    ["fake provider", { capabilities: { aiProvider: "Fake" } }, /подключённом Groq/],
    ["insecure connection", { secureContext: false }, /HTTPS/],
    ["unsupported recording", { supported: false }, /не поддерживает/]
]) {
    test(`voice input is disabled for ${name}`, async () => {
        const page = await createPage(options);
        assert.equal(page.element("#voice-record-button").disabled, true);
        assert.match(page.element("#voice-status").textContent, expected);
        await page.element("#voice-record-button").emit("click");
        assert.equal(page.starts, 0);
    });
}

test("existing typed text cannot be overwritten by a new recording", async () => {
    const page = await createPage();
    page.element("#message-input").value = "Ручное сообщение";
    await page.element("#message-input").emit("input");
    assert.equal(page.element("#voice-record-button").disabled, true);
    await page.element("#voice-record-button").emit("click");
    assert.equal(page.starts, 0);
    assert.equal(page.element("#message-input").value, "Ручное сообщение");
});

test("cancel and hiding the page restore controls without creating a message", async () => {
    const page = await createPage();
    await page.element("#voice-record-button").emit("click");
    await page.element("#voice-cancel-button").emit("click");
    assert.equal(page.cancels, 1);
    assert.equal(page.element("#message-input").disabled, false);
    assert.equal(page.element("#voice-cancel-button").hidden, true);
    await page.element("#voice-record-button").emit("click");
    page.document.hidden = true;
    page.documentListeners.get("visibilitychange")();
    assert.equal(page.cancels, 2);
    page.windowListeners.get("pagehide")();
    assert.equal(page.cancels, 3);
    assert.equal(page.storage.size, 0);
    assert.equal(page.requests.filter(request => request.settings.method === "POST").length, 0);
});

for (const [type, extension] of [["audio/mp4", "m4a"], ["audio/ogg;codecs=opus", "ogg"]]) {
    test(`uploads ${type} with a matching filename`, async () => {
        const page = await createPage({ mediaType: type });
        await page.element("#voice-record-button").emit("click");
        await page.element("#voice-record-button").emit("click");
        await flushPromises();
        const request = page.requests.find(item => item.url === "/api/audio/transcribe");
        assert.equal(request.settings.body.get("audio").name, `voice-recording.${extension}`);
    });
}

test("provider errors are shown in Russian and allow another recording", async () => {
    const page = await createPage({ audioStatus: 429 });
    await page.element("#voice-record-button").emit("click");
    await page.element("#voice-record-button").emit("click");
    await flushPromises();
    assert.match(page.element("#voice-status").textContent, /Подождите минуту/);
    assert.equal(page.element("#voice-status").classes.has("is-error"), true);
    assert.equal(page.element("#voice-record-button").disabled, false);
    assert.equal(page.element("#message-input").value, "");
    assert.equal(page.storage.size, 0);
});

function deferred() {
    let resolve;
    const promise = new Promise(accept => { resolve = accept; });
    return { promise, resolve };
}

function jsonResponse(body, status = 200) {
    return { ok: status < 400, status, text: async () => JSON.stringify(body) };
}

function sessionResponse(date, messages) {
    return {
        id: "session-1",
        mealDate: date,
        messages,
        status: "NeedsProducts",
        canConfirm: false,
        dishes: [],
        issues: [],
        clarificationQuestions: []
    };
}

function prepareManualProductForm(page) {
    page.element("#manual-product-form").elements = {
        name: { value: "Product" },
        barcode: { value: "" },
        isEstimated: { checked: false },
        calories: { valueAsNumber: 100 },
        proteinGrams: { valueAsNumber: 10 },
        fatGrams: { valueAsNumber: 5 },
        carbohydratesGrams: { valueAsNumber: 15 }
    };
}

test("meal date stays locked while session creation is in flight", async () => {
    const creation = deferred();
    const page = await createPage({
        fetch: (url, settings) => url === "/api/meal-sessions" && settings.method === "POST"
            ? creation.promise : undefined
    });
    const originalDate = page.element("#meal-date").value;
    page.element("#message-input").value = "Original message";
    await page.element("#message-form").emit("submit");
    const building = page.element("#build-draft-button").emit("click");

    assert.equal(page.element("#meal-date").disabled, true);
    page.element("#meal-date").value = "2026-01-01";
    await page.element("#meal-date").emit("change");
    assert.equal(page.element("#meal-date").value, originalDate);

    creation.resolve(jsonResponse(sessionResponse(originalDate, ["Original message"])));
    await building;
    assert.equal(page.element("#meal-date").value, originalDate);
    assert.equal(page.element("#meal-date").disabled, true);
});

for (const action of ["reset", "append"]) {
    test(`late catalog refresh cannot overwrite a session after ${action}`, async () => {
        const refresh = deferred();
        const page = await createPage({
            confirm: () => true,
            fetch: (url, settings) => {
                if (url === "/api/meal-sessions" && settings.method === "POST") {
                    const body = JSON.parse(settings.body);
                    return jsonResponse(sessionResponse(body.mealDate, body.messages));
                }
                if (url === "/api/products/manual") {
                    return jsonResponse({ name: "Product" });
                }
                if (url === "/api/meal-sessions/session-1") {
                    return refresh.promise;
                }
                if (url === "/api/meal-sessions/session-1/messages") {
                    return jsonResponse(sessionResponse("2026-01-01", ["Original message", "New message"]));
                }
            }
        });
        page.element("#message-input").value = "Original message";
        await page.element("#message-form").emit("submit");
        await page.element("#build-draft-button").emit("click");
        prepareManualProductForm(page);
        const saving = page.element("#manual-product-form").emit("submit");
        await flushPromises();
        assert.equal(page.requests.filter(request => request.url === "/api/meal-sessions/session-1").length, 1);

        if (action === "reset") {
            await page.element("#new-session-button").emit("click");
        } else {
            page.element("#message-input").value = "New message";
            await page.element("#message-form").emit("submit");
        }

        refresh.resolve(jsonResponse(sessionResponse("2026-01-01", ["Stale message"])));
        await saving;
        assert.doesNotMatch(page.element("#message-list").innerHTML, /Stale message/);
        if (action === "reset") {
            assert.equal(page.element("#build-draft-button").hidden, false);
            assert.doesNotMatch(page.element("#message-list").innerHTML, /Original message/);
        } else {
            assert.match(page.element("#message-list").innerHTML, /New message/);
            assert.equal(page.element("#build-draft-button").hidden, true);
        }
    });
}
