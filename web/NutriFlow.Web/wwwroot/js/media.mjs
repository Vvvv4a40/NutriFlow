const maximumPhotoBytes = 8 * 1024 * 1024;
const maximumPhotoSide = 2560;
const jpegQuality = 0.9;
const acceptedPhotoTypes = ["image/jpeg", "image/png", "image/webp"];

export const voiceMessages = {
    unsupported: "Этот браузер не записывает звук. Введите текст.",
    microphoneDenied: "Нет доступа к микрофону. Разрешите его в настройках браузера или введите текст."
};

// the recorder is the file of the old web client, linked into this folder at build time, so it is imported on demand
export async function createVoiceCapture(watcher, maximumDurationInMilliseconds, createCapture) {
    const create = createCapture ?? (await import("./speech-capture.mjs")).createSpeechCapture;
    return create({
        maximumDurationInMilliseconds,
        messages: voiceMessages,
        transcribe: async audio =>
            watcher.invokeMethodAsync("OnRecorded", new Uint8Array(await audio.arrayBuffer()), audio.type),
        onTranscript: text => watcher.invokeMethodAsync("OnTranscribed", text),
        onStateChange: phase => watcher.invokeMethodAsync("OnPhaseChanged", phase),
        onError: message => watcher.invokeMethodAsync("OnFailed", message)
    });
}

// heic from an iphone and files over the limit of the label reader are the only ones that change on the way
export function needsEncoding(file) {
    return !acceptedPhotoTypes.includes(file.type) || file.size > maximumPhotoBytes;
}

// the photo chosen in a file input; rejects when the browser cannot read it, null when nothing is chosen
export async function readPhoto(input, environment = globalThis) {
    const file = input.files?.[0];
    if (!file) return null;
    try {
        const photo = needsEncoding(file) ? await encodeJpeg(file, environment) : file;
        return { content: new Uint8Array(await photo.arrayBuffer()), mediaType: photo.type };
    } finally {
        // choosing the same photo again has to report a change too
        input.value = "";
    }
}

async function encodeJpeg(file, environment) {
    const url = environment.URL.createObjectURL(file);
    try {
        const image = environment.document.createElement("img");
        image.src = url;
        await image.decode();
        const scale = Math.min(1, maximumPhotoSide / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = environment.document.createElement("canvas");
        canvas.width = Math.round(image.naturalWidth * scale);
        canvas.height = Math.round(image.naturalHeight * scale);
        const context = canvas.getContext("2d");
        // jpeg has no transparency, it would turn black
        context.fillStyle = "#fff";
        context.fillRect(0, 0, canvas.width, canvas.height);
        context.drawImage(image, 0, 0, canvas.width, canvas.height);
        const encoded = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", jpegQuality));
        if (!encoded || encoded.size > maximumPhotoBytes) {
            throw new Error("The photo cannot be encoded within the limit.");
        }

        return encoded;
    } finally {
        environment.URL.revokeObjectURL(url);
    }
}
