export async function requestPersistence() {
    return await navigator.storage?.persist?.().catch(() => false) ?? false;
}

export async function isPersisted() {
    return await navigator.storage?.persisted?.().catch(() => false) ?? false;
}
