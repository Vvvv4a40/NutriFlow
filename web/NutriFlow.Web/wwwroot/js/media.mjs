const maximumPhotoBytes = 8 * 1024 * 1024;
const maximumPhotoSide = 2560;
const maximumBarcodeSide = 2000;
const jpegQuality = 0.92;
const acceptedPhotoTypes = ["image/jpeg", "image/png", "image/webp"];
const barcodeFormats = ["EAN13", "EAN8", "UPCA", "UPCE"];

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

export function canSendAsIs(file) {
    return acceptedPhotoTypes.includes(file.type) && file.size <= maximumPhotoBytes;
}

// the photo chosen in a file input; rejects when the browser cannot read it, null when nothing is chosen.
// a phone stores a portrait shot lying on its side with a rotation tag the label reader does not apply, so every photo
// goes through a canvas, which draws it upright. A file only the canvas fails on is still worth sending
export async function readPhoto(input, environment = globalThis) {
    const file = input.files?.[0];
    if (!file) return null;
    try {
        let photo;
        try {
            photo = await encodeJpeg(file, environment);
        } catch (error) {
            if (!canSendAsIs(file)) throw error;
            photo = file;
        }

        return { content: new Uint8Array(await photo.arrayBuffer()), mediaType: photo.type };
    } finally {
        // choosing the same photo again has to report a change too
        input.value = "";
    }
}

// the digits of a barcode on the chosen photo: { digits } with null when none could be read, null when nothing is chosen.
// safari has no BarcodeDetector, this reader works the same in every browser and without the network
export async function readBarcode(input, environment = globalThis, loadReader = loadBarcodeReader) {
    const file = input.files?.[0];
    if (!file) return null;
    try {
        const { context, canvas } = await drawPhoto(file, environment, maximumBarcodeSide);
        const picture = context.getImageData(0, 0, canvas.width, canvas.height);
        const { readBarcodes } = await loadReader();
        const found = await readBarcodes(picture, {
            formats: barcodeFormats,
            tryHarder: true,
            tryRotate: true,
            tryInvert: false,
            maxNumberOfSymbols: 1
        });
        return { digits: found.find(barcode => barcode.isValid)?.text ?? null };
    } finally {
        input.value = "";
    }
}

let barcodeReader = null;

function loadBarcodeReader() {
    barcodeReader ??= import("./zxing/reader/index.js").then(reader => {
        // the library looks for its binary on a public cdn by default: the csp forbids that, and it has to work offline
        reader.prepareZXingModule({
            overrides: {
                locateFile: (path, prefix) => path.endsWith(".wasm")
                    ? new URL("./zxing/zxing_reader.wasm", import.meta.url).href
                    : prefix + path
            }
        });
        return reader;
    });
    return barcodeReader;
}

async function encodeJpeg(file, environment) {
    const { canvas } = await drawPhoto(file, environment, maximumPhotoSide);
    const encoded = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", jpegQuality));
    if (!encoded || encoded.size > maximumPhotoBytes) {
        throw new Error("The photo cannot be encoded within the limit.");
    }

    return encoded;
}

async function drawPhoto(file, environment, maximumSide) {
    const url = environment.URL.createObjectURL(file);
    try {
        const image = environment.document.createElement("img");
        image.src = url;
        await image.decode();
        const scale = Math.min(1, maximumSide / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = environment.document.createElement("canvas");
        canvas.width = Math.round(image.naturalWidth * scale);
        canvas.height = Math.round(image.naturalHeight * scale);
        const context = canvas.getContext("2d", { willReadFrequently: true });
        // jpeg has no transparency, it would turn black
        context.fillStyle = "#fff";
        context.fillRect(0, 0, canvas.width, canvas.height);
        context.drawImage(image, 0, 0, canvas.width, canvas.height);
        return { canvas, context };
    } finally {
        environment.URL.revokeObjectURL(url);
    }
}
