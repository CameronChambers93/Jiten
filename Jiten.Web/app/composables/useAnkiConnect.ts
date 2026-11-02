/** What stays per device: AnkiConnect's address and its optional API key. Everything else about Anki is on the server. */
export interface AnkiDevice {
  url: string;
  key: string;
}

const DEVICE_KEY = 'jiten-anki-device';
export const DEFAULT_ANKI_CONNECT_URL = 'http://127.0.0.1:8765';

const device = ref<AnkiDevice>({ url: DEFAULT_ANKI_CONNECT_URL, key: '' });
let deviceLoaded = false;

function loadDevice() {
  if (deviceLoaded || !import.meta.client) return;
  deviceLoaded = true;
  try {
    const raw = localStorage.getItem(DEVICE_KEY);
    if (raw) device.value = { ...device.value, ...(JSON.parse(raw) as Partial<AnkiDevice>) };
  } catch {}
}

function storeDevice() {
  try {
    localStorage.setItem(DEVICE_KEY, JSON.stringify(device.value));
  } catch {}
}

export function useAnkiDevice() {
  loadDevice();
  return {
    device: readonly(device),
    setKey(key: string) {
      device.value = { ...device.value, key };
      storeDevice();
    },
    setUrl(url: string) {
      device.value = { ...device.value, url: url.trim() || DEFAULT_ANKI_CONNECT_URL };
      storeDevice();
    },
  };
}

/** Calls AnkiConnect on this device. A key passed in overrides the stored one (the settings dialog tries a new key). */
export async function ankiConnect<T = unknown>(action: string, params: Record<string, unknown> = {}, key?: string): Promise<T> {
  loadDevice();
  const body: Record<string, unknown> = { action, version: 6, params };
  const k = key ?? device.value.key;
  if (k) body.key = k;
  const res = await fetch(device.value.url || DEFAULT_ANKI_CONNECT_URL, { method: 'POST', body: JSON.stringify(body) });
  const json = await res.json();
  if (json.error) throw new Error(json.error);
  return json.result as T;
}

/** AnkiConnect's `multi`: several actions in one request, each answered or failed on its own. */
export async function ankiConnectMulti<T = unknown>(actions: { action: string; params: Record<string, unknown> }[]): Promise<T[]> {
  if (!actions.length) return [];
  const results = await ankiConnect<{ result: T; error: string | null }[] | T[]>('multi', { actions: actions.map((a) => ({ ...a, version: 6 })) });
  return results.map((r) => {
    if (r && typeof r === 'object' && 'error' in (r as object)) {
      const wrapped = r as { result: T; error: string | null };
      if (wrapped.error) throw new Error(wrapped.error);
      return wrapped.result;
    }
    return r as T;
  });
}
