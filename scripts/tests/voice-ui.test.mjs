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
    const storage = new Map(Object.entries(options.storage ?? {}));
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

function readySessionResponse(date, messages, purpose = "Diary") {
    return {
        ...sessionResponse(date, messages),
        purpose,
        status: "ReadyForConfirmation",
        canConfirm: true,
        previewToken: "preview-1",
        dishes: [{ name: "Моё рагу", ingredients: [], portions: [] }]
    };
}

async function buildSession(page, message = "Original message") {
    page.element("#message-input").value = message;
    await page.element("#message-form").emit("submit");
    await page.element("#build-draft-button").emit("click");
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

test("create dish mode submits its purpose and an example without eaten portions", async () => {
    const page = await createPage({
        fetch: (url, settings) => {
            if (url === "/api/meal-sessions" && settings.method === "POST") {
                const body = JSON.parse(settings.body);
                return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
            }
        }
    });
    await page.element("#create-dish-mode-button").emit("click");
    await page.element("#example-button").emit("click");
    const pendingDraft = JSON.parse(page.storage.get("nutriflow.pendingMealDraft"));
    assert.equal(pendingDraft.purpose, "CreateDish");
    assert.equal(pendingDraft.messages.length, 3);
    assert.equal(pendingDraft.messages.some(message => message.includes("Съел")), false);
    await page.element("#build-draft-button").emit("click");

    const creation = page.requests.find(request => request.url === "/api/meal-sessions");
    assert.equal(JSON.parse(creation.settings.body).purpose, "CreateDish");
    assert.equal(page.element("#confirm-button").textContent, "Сохранить блюдо");
    assert.equal(page.element("#create-dish-mode-button").attributes.get("aria-pressed"), "true");
    assert.doesNotMatch(page.element("#preview-content").innerHTML, /Съеденные порции/);
    assert.match(page.element("#confirm-hint").textContent, /по названию/);
});

for (const purpose of ["Diary", "CreateDish", undefined]) {
    test(`pending draft restores ${purpose ?? "legacy Diary"} purpose`, async () => {
        const page = await createPage({
            storage: {
                "nutriflow.pendingMealDraft": JSON.stringify({
                    date: "2026-10-05",
                    messages: ["Saved message"],
                    ...(purpose ? { purpose } : {})
                })
            },
            fetch: (url, settings) => {
                if (url === "/api/meal-sessions" && settings.method === "POST") {
                    const body = JSON.parse(settings.body);
                    return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
                }
            }
        });
        assert.match(page.element("#message-list").innerHTML, /Saved message/);
        await page.element("#build-draft-button").emit("click");
        const creation = page.requests.find(request => request.url === "/api/meal-sessions");
        assert.equal(JSON.parse(creation.settings.body).purpose, purpose ?? "Diary");
    });
}

test("active server session restores its purpose instead of a local pending mode", async () => {
    const page = await createPage({
        storage: {
            "nutriflow.activeMealSessionId": "session-1",
            "nutriflow.pendingMealDraft": JSON.stringify({
                date: "2026-10-05", messages: ["Local message"], purpose: "Diary"
            })
        },
        fetch: url => url === "/api/meal-sessions/session-1"
            ? jsonResponse(readySessionResponse("2026-10-05", ["Server recipe"], "CreateDish"))
            : undefined
    });
    assert.equal(page.element("#create-dish-mode-button").attributes.get("aria-pressed"), "true");
    assert.match(page.element("#message-list").innerHTML, /Server recipe/);
    assert.doesNotMatch(page.element("#message-list").innerHTML, /Local message/);
    assert.equal(page.storage.has("nutriflow.pendingMealDraft"), false);
});

for (const draftKind of ["typed", "queued", "server"]) {
    test(`declining mode switch preserves the ${draftKind} draft`, async () => {
        let confirmations = 0;
        const page = await createPage({
            confirm: () => { confirmations++; return false; },
            fetch: (url, settings) => {
                if (url === "/api/meal-sessions" && settings.method === "POST") {
                    const body = JSON.parse(settings.body);
                    return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
                }
            }
        });
        page.element("#message-input").value = "Do not lose this";

        if (draftKind !== "typed") {
            await page.element("#message-form").emit("submit");
        }
        if (draftKind === "server") {
            await page.element("#build-draft-button").emit("click");
        }

        const messageHistory = page.element("#message-list").innerHTML;
        const storageBefore = [...page.storage];
        await page.element("#create-dish-mode-button").emit("click");
        assert.equal(confirmations, 1);
        assert.equal(page.element("#diary-mode-button").attributes.get("aria-pressed"), "true");
        assert.equal(page.element("#message-list").innerHTML, messageHistory);
        assert.deepEqual([...page.storage], storageBefore);
        if (draftKind === "typed") {
            assert.equal(page.element("#message-input").value, "Do not lose this");
        }
    });
}

test("accepted mode switch starts a fresh session instead of changing the existing server session", async () => {
    const page = await createPage({
        confirm: () => true,
        fetch: (url, settings) => {
            if (url === "/api/meal-sessions" && settings.method === "POST") {
                const body = JSON.parse(settings.body);
                return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
            }
        }
    });
    await buildSession(page);
    page.element("#message-input").value = "Unsubmitted clarification";
    await page.element("#create-dish-mode-button").emit("click");
    assert.equal(page.element("#message-input").value, "");
    assert.doesNotMatch(page.element("#message-list").innerHTML, /Original message/);
    assert.equal(page.storage.has("nutriflow.activeMealSessionId"), false);
    await buildSession(page, "New recipe");
    const creations = page.requests.filter(request => request.url === "/api/meal-sessions");
    assert.equal(creations.length, 2);
    assert.equal(JSON.parse(creations[0].settings.body).purpose, "Diary");
    assert.equal(JSON.parse(creations[1].settings.body).purpose, "CreateDish");
    assert.equal(page.requests.some(request => request.url.includes("/messages")), false);
});

test("mode switches are disabled during voice capture and cannot discard its transcript", async () => {
    const page = await createPage();
    await page.element("#voice-record-button").emit("click");
    assert.equal(page.element("#create-dish-mode-button").disabled, true);
    assert.equal(page.element("#diary-mode-button").disabled, true);
    await page.element("#create-dish-mode-button").emit("click");
    assert.equal(page.element("#diary-mode-button").attributes.get("aria-pressed"), "true");
    await page.element("#voice-record-button").emit("click");
    await flushPromises();
    assert.equal(page.element("#message-input").value, "Добавил 600 г говядины.");
    assert.equal(page.element("#create-dish-mode-button").disabled, false);
});

test("mode stays locked while session creation is in flight", async () => {
    const creation = deferred();
    const page = await createPage({
        fetch: (url, settings) => url === "/api/meal-sessions" && settings.method === "POST"
            ? creation.promise : undefined
    });
    page.element("#message-input").value = "Original message";
    await page.element("#message-form").emit("submit");
    const building = page.element("#build-draft-button").emit("click");
    assert.equal(page.element("#create-dish-mode-button").disabled, true);
    await page.element("#create-dish-mode-button").emit("click");
    assert.equal(page.element("#diary-mode-button").attributes.get("aria-pressed"), "true");
    creation.resolve(jsonResponse(readySessionResponse("2026-10-05", ["Original message"])));
    await building;
});

test("confirming a saved dish refreshes its list but never records diary entries", async () => {
    let savedListRequests = 0;
    const page = await createPage({
        fetch: (url, settings) => {
            if (url === "/api/saved-dishes") {
                savedListRequests++;
                return jsonResponse(savedListRequests === 1 ? [] : [{ id: "dish-1", name: "Моё рагу" }]);
            }
            if (url === "/api/meal-sessions" && settings.method === "POST") {
                const body = JSON.parse(settings.body);
                return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
            }
            if (url === "/api/meal-sessions/session-1/confirm") {
                return jsonResponse({
                    session: { ...readySessionResponse("2026-10-05", ["Recipe"], "CreateDish"), status: "Confirmed" },
                    entries: [],
                    savedDish: { id: "dish-1", name: "Моё рагу" }
                });
            }
        }
    });
    const dailyRequestCount = page.requests.filter(request => request.url.startsWith("/api/daily-progress/")).length;
    await page.element("#create-dish-mode-button").emit("click");
    await buildSession(page, "Recipe");
    await page.element("#confirm-button").emit("click");
    assert.equal(savedListRequests, 2);
    assert.equal(page.requests.filter(request => request.url.startsWith("/api/daily-progress/")).length, dailyRequestCount);
    assert.match(page.element("#preview-content").innerHTML, /Блюдо сохранено/);
    assert.doesNotMatch(page.element("#preview-content").innerHTML, /Приём пищи записан|учтены в дневном балансе/);
    assert.equal(page.element("#session-status").textContent, "Сохранено");
    assert.match(page.element("#saved-dishes").innerHTML, /Моё рагу/);
    assert.match(page.element("#toast").textContent, /Блюдо сохранено/);
    await page.element("#diary-mode-button").emit("click");
    assert.equal(page.element("#message-input").disabled, false);
    assert.equal(page.element("#confirm-button").textContent, "Записать в дневник");
});

test("saved dish names are escaped and list failures do not block capturing", async () => {
    const page = await createPage({
        fetch: url => url === "/api/saved-dishes"
            ? jsonResponse([{ id: "dish-1", name: "<script>bad</script>" }])
            : undefined
    });
    assert.match(page.element("#saved-dishes").innerHTML, /&lt;script&gt;/);
    assert.doesNotMatch(page.element("#saved-dishes").innerHTML, /<script>/);

    const failedPage = await createPage({
        fetch: url => url === "/api/saved-dishes" ? jsonResponse({}, 500) : undefined
    });
    failedPage.element("#message-input").value = "Съел яблоко";
    await failedPage.element("#message-form").emit("submit");
    assert.match(failedPage.element("#message-list").innerHTML, /Съел яблоко/);
    assert.equal(failedPage.element("#build-draft-button").disabled, false);
});

test("saved dish ingredients link to their own snapshot rather than a web source", async () => {
    const dishId = "0123456789abcdef0123456789abcdef";
    const page = await createPage({
        storage: { "nutriflow.activeMealSessionId": "session-1" },
        fetch: url => url === "/api/meal-sessions/session-1" ? jsonResponse({
            ...readySessionResponse("2026-10-05", ["Съел 100 г моего рагу"]),
            dishes: [{
                name: "Моё рагу",
                ingredients: [{
                    productName: "Моё рагу",
                    resolvedProduct: {
                        name: "Моё рагу",
                        sourceKind: "SavedDish",
                        sourceName: "NutriFlow",
                        sourceReference: `saved-dish:${dishId}`,
                        dataQuality: "Exact"
                    }
                }],
                portions: []
            }]
        }) : undefined
    });
    const preview = page.element("#preview-content").innerHTML;
    assert.match(preview, /своё блюдо/);
    assert.ok(preview.includes(`href="/api/saved-dishes/${dishId}"`));
    assert.doesNotMatch(preview, /href="saved-dish:/);
});

for (const [provider, expectedPhrase] of [
    ["Fake", "Съел 100 г Демо-блюда."],
    ["Groq", "Съел 150 г Демо-блюдо"]
]) {
    test(`${provider} shows a compatible demo dish reuse phrase in both hints`, async () => {
        const page = await createPage({
            capabilities: { aiProvider: provider, supportsFreeText: provider !== "Fake" },
            fetch: (url, settings) => {
                if (url === "/api/saved-dishes") {
                    return jsonResponse([{ id: "dish-1", name: "Демо-блюдо" }]);
                }
                if (url === "/api/meal-sessions" && settings.method === "POST") {
                    const body = JSON.parse(settings.body);
                    return jsonResponse(readySessionResponse(body.mealDate, body.messages, body.purpose));
                }
                if (url === "/api/meal-sessions/session-1/confirm") {
                    return jsonResponse({
                        session: { ...readySessionResponse("2026-10-05", ["Recipe"], "CreateDish"), status: "Confirmed" },
                        entries: [],
                        savedDish: { id: "dish-1", name: "Демо-блюдо" }
                    });
                }
            }
        });
        assert.ok(page.element("#saved-dishes").innerHTML.includes(expectedPhrase));
        await page.element("#create-dish-mode-button").emit("click");
        await buildSession(page, "Recipe");
        await page.element("#confirm-button").emit("click");
        assert.ok(page.element("#preview-content").innerHTML.includes(expectedPhrase));
    });
}

test("late Fake capabilities replace a generic reuse hint with its supported phrase", async () => {
    const capabilities = deferred();
    const page = await createPage({
        storage: { "nutriflow.activeMealSessionId": "session-1" },
        fetch: url => {
            if (url === "/api/capabilities") {
                return capabilities.promise;
            }
            if (url === "/api/saved-dishes") {
                return jsonResponse([
                    { id: "dish-2", name: "Другое блюдо" },
                    { id: "dish-1", name: "Демо-блюдо" }
                ]);
            }
            if (url === "/api/meal-sessions/session-1") {
                return jsonResponse({
                    ...readySessionResponse("2026-10-05", ["Recipe"], "CreateDish"),
                    status: "Confirmed",
                    dishes: [{ name: "Демо-блюдо", ingredients: [], portions: [] }]
                });
            }
        }
    });
    assert.ok(page.element("#preview-content").innerHTML.includes("Съел 150 г Демо-блюдо"));
    capabilities.resolve(jsonResponse({ aiProvider: "Fake", supportsFreeText: false }));
    await flushPromises();
    assert.ok(page.element("#preview-content").innerHTML.includes("Съел 100 г Демо-блюда."));
    assert.ok(page.element("#saved-dishes").innerHTML.includes("Съел 100 г Демо-блюда."));
});
