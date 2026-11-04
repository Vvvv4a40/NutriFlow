export function createPhotoUrl(content, mediaType, environment = globalThis) {
    return environment.URL.createObjectURL(new environment.Blob([content], { type: mediaType }));
}

export function revokePhotoUrl(url, environment = globalThis) {
    environment.URL.revokeObjectURL(url);
}
