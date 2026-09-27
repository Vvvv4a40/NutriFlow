import test from "node:test";
import assert from "node:assert/strict";
import { createSpeechCapture, getRecordingMimeType } from "../../NutriFlow.Api/wwwroot/js/speech-capture.mjs";

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((accept, decline) => { resolve = accept; reject = decline; });
    return { promise, resolve, reject };
}

function createFixture(options = {}) {
    const states = [];
    const errors = [];
    const transcripts = [];
    const requests = [];
    const timers = new Map();
    const track = { stops: 0, stop() { this.stops++; } };
    const stream = { getTracks: () => [track] };
    let recorder;

    class Recorder {
        static isTypeSupported(type) { return (options.supportedTypes ?? ["audio/webm;codecs=opus"]).includes(type); }
        constructor(inputStream, settings) {
            assert.equal(inputStream, stream);
            this.mimeType = settings.mimeType;
            this.state = "inactive";
            recorder = this;
        }
        start(timeslice) {
            assert.equal(timeslice, 1000);
            if (options.startError) throw options.startError;
            this.state = "recording";
        }
        emit(data) { this.ondataavailable?.({ data }); }
        stop() {
            this.state = "inactive";
            return this.onstop?.();
        }
    }

    const capture = createSpeechCapture({
        Recorder,
        getUserMedia: options.getUserMedia ?? (async constraints => {
            assert.deepEqual(constraints, { audio: true });
            return stream;
        }),
        transcribe: async (audio, signal) => {
            requests.push({ audio, signal });
            return options.transcribe ? options.transcribe(audio, signal) : "  Добавил 600 г говядины.  ";
        },
        onStateChange: state => states.push(state),
        onError: message => errors.push(message),
        onTranscript: text => transcripts.push(text),
        setTimeout: (callback, duration) => {
            assert.equal(duration, 60_000);
            const id = timers.size + 1;
            timers.set(id, callback);
            return id;
        },
        clearTimeout: id => timers.delete(id)
    });

    return { capture, states, errors, transcripts, requests, timers, stream, track, get recorder() { return recorder; } };
}

async function flushPromises() {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
}

test("selects the first supported recording format, including MP4 for Safari", () => {
    assert.equal(getRecordingMimeType({ isTypeSupported: () => true }), "audio/webm;codecs=opus");
    assert.equal(getRecordingMimeType({ isTypeSupported: type => type === "audio/mp4" }), "audio/mp4");
    assert.equal(getRecordingMimeType({ isTypeSupported: type => type === "audio/ogg" }), "audio/ogg");
    assert.equal(getRecordingMimeType({ isTypeSupported: () => false }), null);
    assert.equal(getRecordingMimeType(undefined), null);
});

test("records chunks, transcribes once, trims text and releases microphone and timer", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    assert.equal(fixture.capture.phase, "recording");
    fixture.recorder.emit(new Blob(["first"]));
    fixture.recorder.emit(new Blob(["second"]));
    fixture.capture.stop();
    await flushPromises();

    assert.deepEqual(fixture.states, ["requesting", "recording", "transcribing", "idle"]);
    assert.deepEqual(fixture.transcripts, ["Добавил 600 г говядины."]);
    assert.equal(fixture.requests.length, 1);
    assert.equal(await fixture.requests[0].audio.text(), "firstsecond");
    assert.equal(fixture.requests[0].audio.type, "audio/webm;codecs=opus");
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.timers.size, 0);
    assert.deepEqual(fixture.errors, []);
});

test("repeated starts and stops do not create additional recordings or requests", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["speech"]));
    fixture.capture.stop();
    fixture.capture.stop();
    await flushPromises();
    assert.equal(fixture.requests.length, 1);
    assert.equal(fixture.track.stops, 1);
});

for (const [name, expected] of [
    ["NotAllowedError", /Доступ к микрофону запрещён/],
    ["NotFoundError", /Микрофон не найден/],
    ["NotReadableError", /Не удалось включить микрофон/],
    ["UnknownError", /Не удалось начать запись/]
]) {
    test(`explains microphone ${name} without starting transcription`, async () => {
        const fixture = createFixture({ getUserMedia: async () => { throw { name }; } });
        await fixture.capture.start();
        assert.equal(fixture.capture.phase, "idle");
        assert.match(fixture.errors[0], expected);
        assert.equal(fixture.requests.length, 0);
    });
}

test("unsupported recording format does not request microphone access", async () => {
    let accesses = 0;
    const fixture = createFixture({ supportedTypes: [], getUserMedia: async () => { accesses++; } });
    await fixture.capture.start();
    assert.equal(accesses, 0);
    assert.match(fixture.errors[0], /не поддерживает/);
    assert.equal(fixture.capture.phase, "idle");
});

test("cancelling permission request stops a late stream without any late callbacks", async () => {
    const permission = deferred();
    const fixture = createFixture({ getUserMedia: () => permission.promise });
    const start = fixture.capture.start();
    fixture.capture.cancel();
    const stateCount = fixture.states.length;
    permission.resolve(fixture.stream);
    await start;
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.recorder, undefined);
    assert.equal(fixture.states.length, stateCount);
    assert.deepEqual(fixture.errors, []);
    assert.deepEqual(fixture.transcripts, []);
});

test("cancelling permission request ignores late rejection", async () => {
    const permission = deferred();
    const fixture = createFixture({ getUserMedia: () => permission.promise });
    const start = fixture.capture.start();
    fixture.capture.cancel();
    permission.reject({ name: "NotAllowedError" });
    await start;
    assert.deepEqual(fixture.errors, []);
});

test("cancelling recording discards audio and stops resources", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["speech"]));
    fixture.capture.cancel();
    await flushPromises();
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.timers.size, 0);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.capture.phase, "idle");
});

for (const settle of ["resolve", "reject"]) {
    test(`cancelling transcription aborts request and ignores late ${settle}`, async () => {
        const result = deferred();
        const fixture = createFixture({ transcribe: () => result.promise });
        await fixture.capture.start();
        fixture.recorder.emit(new Blob(["speech"]));
        fixture.capture.stop();
        assert.equal(fixture.capture.phase, "transcribing");
        fixture.capture.cancel();
        assert.equal(fixture.requests[0].signal.aborted, true);
        if (settle === "resolve") result.resolve("late text");
        else result.reject(new Error("late error"));
        await flushPromises();
        assert.deepEqual(fixture.transcripts, []);
        assert.deepEqual(fixture.errors, []);
    });
}

test("oversized chunks discard recording before an external request", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob([new Uint8Array(8 * 1024 * 1024)]));
    assert.equal(fixture.capture.phase, "recording");
    fixture.recorder.emit(new Blob(["x"]));
    assert.equal(fixture.capture.phase, "idle");
    assert.match(fixture.errors[0], /8 МБ/);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.recorder.state, "inactive");
});

test("empty recording does not transcribe", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob([]));
    fixture.capture.stop();
    assert.match(fixture.errors[0], /пустой/);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.track.stops, 1);
});

test("maximum duration automatically stops and transcribes", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["speech"]));
    const timeout = [...fixture.timers.values()][0];
    timeout();
    await flushPromises();
    assert.equal(fixture.requests.length, 1);
    assert.equal(fixture.capture.phase, "idle");
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.timers.size, 0);
});

test("an expired timer from cancelled recording cannot stop the next recording", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    const oldTimeout = [...fixture.timers.values()][0];
    fixture.capture.cancel();
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["new speech"]));
    oldTimeout();
    assert.equal(fixture.capture.phase, "recording");
    assert.equal(fixture.requests.length, 0);
    fixture.capture.cancel();
});

test("recorder error cancels recording and releases resources", async () => {
    const fixture = createFixture();
    await fixture.capture.start();
    fixture.recorder.onerror({ error: new Error("device failed") });
    assert.match(fixture.errors[0], /Запись прервалась/);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.track.stops, 1);
    assert.equal(fixture.timers.size, 0);
});

test("recorder startup failure also releases the granted microphone", async () => {
    const fixture = createFixture({ startError: new Error("start failed") });
    await fixture.capture.start();
    assert.equal(fixture.capture.phase, "idle");
    assert.equal(fixture.track.stops, 1);
    assert.match(fixture.errors[0], /Не удалось начать запись/);
});

for (const [value, expected] of [["   ", /Речь не распознана/], ["x".repeat(4001), /слишком длинная/], [null, /Речь не распознана/]]) {
    test(`rejects invalid transcript: ${value === null ? "null" : value.length}`, async () => {
        const fixture = createFixture({ transcribe: async () => value });
        await fixture.capture.start();
        fixture.recorder.emit(new Blob(["speech"]));
        fixture.capture.stop();
        await flushPromises();
        assert.match(fixture.errors[0], expected);
        assert.deepEqual(fixture.transcripts, []);
        assert.equal(fixture.capture.phase, "idle");
    });
}

test("transcription failure is shown and next recording can be started", async () => {
    const fixture = createFixture({ transcribe: async () => { throw new Error("Сервис временно недоступен."); } });
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["speech"]));
    fixture.capture.stop();
    await flushPromises();
    assert.equal(fixture.errors[0], "Сервис временно недоступен.");
    await fixture.capture.start();
    assert.equal(fixture.capture.phase, "recording");
    fixture.capture.cancel();
});

test("dispose stops resources, blocks reuse and suppresses late transcription", async () => {
    const result = deferred();
    const fixture = createFixture({ transcribe: () => result.promise });
    await fixture.capture.start();
    fixture.recorder.emit(new Blob(["speech"]));
    fixture.capture.stop();
    const stateCount = fixture.states.length;
    fixture.capture.dispose();
    result.resolve("late text");
    await flushPromises();
    await fixture.capture.start();
    assert.equal(fixture.capture.phase, "idle");
    assert.equal(fixture.states.length, stateCount);
    assert.deepEqual(fixture.transcripts, []);
    assert.equal(fixture.requests[0].signal.aborted, true);
});
