export function mark(name) {
  performance.mark(name);
  return performance.now();
}

export async function info() {
  const estimate = await navigator.storage?.estimate?.().catch(() => null);
  const fileHandle = globalThis.FileSystemFileHandle?.prototype;
  return {
    userAgent: navigator.userAgent,
    languages: (navigator.languages ?? [navigator.language]).join(', '),
    standalone: matchMedia('(display-mode: standalone)').matches || navigator.standalone === true,
    secureContext: globalThis.isSecureContext === true,
    crossOriginIsolated: globalThis.crossOriginIsolated === true,
    opfs: typeof navigator.storage?.getDirectory === 'function',
    syncAccessHandle: typeof fileHandle?.createSyncAccessHandle === 'function',
    createWritable: typeof fileHandle?.createWritable === 'function',
    webLocks: typeof navigator.locks?.request === 'function',
    jspi: typeof globalThis.WebAssembly?.Suspending === 'function',
    persisted: await navigator.storage?.persisted?.().catch(() => null) ?? null,
    quotaBytes: estimate?.quota ?? null,
    usageBytes: estimate?.usage ?? null,
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    utcOffsetMinutes: -new Date().getTimezoneOffset(),
    localTime: new Date().toString(),
    maxTouchPoints: navigator.maxTouchPoints ?? 0,
    serviceWorkerControlled: Boolean(navigator.serviceWorker?.controller),
    hardwareConcurrency: navigator.hardwareConcurrency ?? 0
  };
}

export function timing() {
  const navigation = performance.getEntriesByType('navigation')[0];
  const resources = performance.getEntriesByType('resource');
  let transfer = 0;
  let encoded = 0;
  let decoded = 0;
  for (const resource of resources) {
    transfer += resource.transferSize ?? 0;
    encoded += resource.encodedBodySize ?? 0;
    decoded += resource.decodedBodySize ?? 0;
  }
  return {
    now: performance.now(),
    navigationType: navigation?.type ?? 'unknown',
    documentTransferBytes: navigation?.transferSize ?? 0,
    domContentLoaded: navigation?.domContentLoadedEventEnd ?? 0,
    resourceCount: resources.length,
    resourceTransferBytes: transfer,
    resourceEncodedBytes: encoded,
    resourceDecodedBytes: decoded,
    marks: performance.getEntriesByType('mark').map(entry => `${entry.name}=${Math.round(entry.startTime)}`).join(' ')
  };
}

export async function requestPersistence() {
  return await navigator.storage?.persist?.().catch(() => false) ?? false;
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
