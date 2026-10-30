async function locate(path) {
  const parts = path.split('/').filter(Boolean);
  const name = parts.pop();
  let directory = await navigator.storage.getDirectory();
  for (const part of parts) {
    directory = await directory.getDirectoryHandle(part, { create: true });
  }
  return { directory, name };
}

self.addEventListener('message', async ({ data }) => {
  const { id, path, bytes } = data;
  if (data.probe) {
    self.postMessage({ id, ok: true, result: typeof FileSystemFileHandle?.prototype?.createSyncAccessHandle === 'function' });
    return;
  }
  try {
    const { directory, name } = await locate(path);
    const handle = await directory.getFileHandle(name, { create: true });
    // early safari versions return promises from these methods, so every call is awaited
    const access = await handle.createSyncAccessHandle();
    try {
      await access.truncate(0);
      await access.write(bytes, { at: 0 });
      await access.flush();
    } finally {
      await access.close();
    }
    self.postMessage({ id, ok: true });
  } catch (error) {
    self.postMessage({ id, ok: false, error: `${error?.name ?? 'Error'}: ${error?.message ?? error}` });
  }
});
