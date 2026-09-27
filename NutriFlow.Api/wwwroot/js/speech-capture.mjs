const maximumDurationInMilliseconds = 60_000;
const maximumAudioSizeInBytes = 8 * 1024 * 1024;
const maximumTranscriptLength = 4000;

export function getRecordingMimeType(Recorder) {
    if (typeof Recorder?.isTypeSupported !== "function") return null;

    const mediaTypes = [
        "audio/webm;codecs=opus",
        "audio/mp4",
        "audio/ogg;codecs=opus",
        "audio/webm",
        "audio/ogg"
    ];

    return mediaTypes.find(mediaType => Recorder.isTypeSupported(mediaType)) ?? null;
}

export function createSpeechCapture({
    transcribe,
    onTranscript,
    onStateChange,
    onError,
    getUserMedia = constraints => globalThis.navigator.mediaDevices.getUserMedia(constraints),
    Recorder = globalThis.MediaRecorder,
    setTimeout: scheduleTimeout = globalThis.setTimeout,
    clearTimeout: cancelTimeout = globalThis.clearTimeout
}) {
    let phase = "idle";
    let activeOperation = null;
    let disposed = false;
    let operationNumber = 0;

    function setPhase(value) {
        if (phase === value) return;
        phase = value;
        if (!disposed) onStateChange?.(value);
    }

    function isActive(operation) {
        return !disposed && activeOperation === operation;
    }

    function releaseMicrophone(operation) {
        if (operation.timeoutId !== null) {
            cancelTimeout(operation.timeoutId);
            operation.timeoutId = null;
        }

        operation.stream?.getTracks().forEach(track => track.stop());
        operation.stream = null;
    }

    function cleanUp(operation, abortRequest) {
        releaseMicrophone(operation);
        if (abortRequest) operation.controller?.abort();

        if (operation.recorder) {
            operation.recorder.ondataavailable = null;
            operation.recorder.onerror = null;
            operation.recorder.onstop = null;
            if (operation.recorder.state !== "inactive") {
                try { operation.recorder.stop(); } catch { }
            }
        }

        operation.chunks.length = 0;
    }

    function fail(operation, message) {
        if (!isActive(operation)) return;
        activeOperation = null;
        cleanUp(operation, true);
        setPhase("idle");
        if (!disposed && operation.number === operationNumber) onError?.(message);
    }

    function microphoneErrorMessage(error) {
        switch (error?.name) {
            case "NotAllowedError":
                return "Доступ к микрофону запрещён. Разрешите его в настройках браузера и попробуйте снова.";
            case "NotFoundError":
                return "Микрофон не найден. Подключите его и попробуйте снова.";
            case "NotReadableError":
                return "Не удалось включить микрофон. Возможно, его использует другое приложение.";
            default:
                return "Не удалось начать запись. Проверьте микрофон и попробуйте снова.";
        }
    }

    async function finishRecording(operation) {
        if (!isActive(operation)) return;
        releaseMicrophone(operation);

        const audio = new Blob(operation.chunks, {
            type: operation.recorder.mimeType || operation.mediaType
        });
        operation.chunks.length = 0;
        if (audio.size === 0) {
            fail(operation, "Запись оказалась пустой. Попробуйте записать сообщение ещё раз.");
            return;
        }
        if (audio.size > maximumAudioSizeInBytes) {
            fail(operation, "Запись превышает 8 МБ. Запишите более короткое сообщение.");
            return;
        }

        operation.controller = new AbortController();
        setPhase("transcribing");
        if (!isActive(operation)) return;

        try {
            const result = await transcribe(audio, operation.controller.signal);
            if (!isActive(operation)) return;
            const transcript = typeof result === "string" ? result.trim() : "";
            if (!transcript) {
                fail(operation, "Речь не распознана. Попробуйте говорить ближе к микрофону.");
                return;
            }
            if (transcript.length > maximumTranscriptLength) {
                fail(operation, "Расшифровка слишком длинная. Запишите более короткое сообщение.");
                return;
            }

            activeOperation = null;
            cleanUp(operation, false);
            setPhase("idle");
            if (!disposed && operation.number === operationNumber) onTranscript?.(transcript);
        } catch (error) {
            fail(operation, error?.message || "Не удалось распознать речь. Попробуйте ещё раз.");
        }
    }

    async function start() {
        if (disposed || phase !== "idle") return;
        const operation = {
            number: ++operationNumber,
            stream: null,
            recorder: null,
            controller: null,
            chunks: [],
            sizeInBytes: 0,
            timeoutId: null,
            mediaType: null
        };
        activeOperation = operation;

        try {
            operation.mediaType = getRecordingMimeType(Recorder);
            if (!operation.mediaType) {
                fail(operation, "Этот браузер не поддерживает запись нужного аудиоформата.");
                return;
            }

            setPhase("requesting");
            if (!isActive(operation)) return;
            const stream = await getUserMedia({ audio: true });
            if (!isActive(operation)) {
                stream.getTracks().forEach(track => track.stop());
                return;
            }

            operation.stream = stream;
            operation.recorder = new Recorder(stream, { mimeType: operation.mediaType });
            operation.recorder.ondataavailable = event => {
                if (!isActive(operation) || !event.data?.size) return;
                operation.sizeInBytes += event.data.size;
                if (operation.sizeInBytes > maximumAudioSizeInBytes) {
                    fail(operation, "Запись превышает 8 МБ. Запишите более короткое сообщение.");
                    return;
                }
                operation.chunks.push(event.data);
            };
            operation.recorder.onerror = () => {
                fail(operation, "Запись прервалась. Проверьте микрофон и попробуйте снова.");
            };
            operation.recorder.onstop = () => finishRecording(operation);
            operation.recorder.start(1000);
            setPhase("recording");
            if (!isActive(operation)) return;
            operation.timeoutId = scheduleTimeout(() => {
                if (isActive(operation)) stop();
            }, maximumDurationInMilliseconds);
        } catch (error) {
            fail(operation, microphoneErrorMessage(error));
        }
    }

    function stop() {
        const operation = activeOperation;
        if (!operation || phase !== "recording" || operation.recorder.state === "inactive") return;
        try {
            operation.recorder.stop();
        } catch {
            fail(operation, "Не удалось завершить запись. Попробуйте ещё раз.");
        }
    }

    function cancel() {
        operationNumber++;
        const operation = activeOperation;
        activeOperation = null;
        if (operation) cleanUp(operation, true);
        setPhase("idle");
    }

    function dispose() {
        disposed = true;
        cancel();
    }

    return { start, stop, cancel, dispose, get phase() { return phase; } };
}
