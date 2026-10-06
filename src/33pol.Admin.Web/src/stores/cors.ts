import { createSignal } from 'solid-js';
import { apiClient, handleApiError, pushToast } from './auth';

const [origins, setOrigins] = createSignal<string[] | null>(null);
const [loadError, setLoadError] = createSignal('');
const [fieldError, setFieldError] = createSignal('');
const [loading, setLoading] = createSignal(false);
let active = false;

export function useCorsStore() {
  return {
    origins,
    loadError,
    fieldError,
    loading,
    setOrigins,
    addRow: () => setOrigins((prev) => [...(prev ?? []), '']),
    removeRow: (index: number) => setOrigins((prev) => (prev ?? []).filter((_, i) => i !== index)),
    updateRow: (index: number, value: string) =>
      setOrigins((prev) => (prev ?? []).map((row, i) => (i === index ? value : row))),
  };
}

function buildPayload(rows: string[]) {
  return {
    allowedOrigins: rows.map((o) => String(o || '').trim()).filter((o) => o.length > 0),
  };
}

export async function loadCors(): Promise<void> {
  setLoading(true);
  setLoadError('');
  try {
    const data = await apiClient.apiJson<{ allowedOrigins?: string[]; AllowedOrigins?: string[] }>('/admin/api/cors');
    const list = data?.allowedOrigins ?? data?.AllowedOrigins ?? [];
    setOrigins(Array.isArray(list) ? list.map((o) => String(o ?? '')) : []);
  } catch (e) {
    const err = e as { status?: number; title?: string; message?: string };
    setOrigins(null);
    if (String(err.title ?? '').startsWith('404') || /not found/i.test(err.message ?? '')) {
      setLoadError('CORS API is not available on this gateway (rebuild/restart the server with the latest image).');
    } else if (err.status === 401 || err.title === 'Authentication failed') {
      setLoadError('Connect with an Admin API key to load CORS settings.');
    } else {
      setLoadError(err.message || 'Could not load CORS settings.');
    }
  } finally {
    setLoading(false);
  }
}

export async function saveCors(): Promise<void> {
  const rows = origins();
  if (rows == null) return;
  setFieldError('');
  setLoading(true);
  try {
    const body = await apiClient.apiJson<{ message?: string }>('/admin/api/cors', {
      method: 'PUT',
      body: JSON.stringify(buildPayload(rows)),
    });
    pushToast(body?.message || 'CORS origins saved.');
    await loadCors();
  } catch (e) {
    const err = e as { message?: string };
    setFieldError(err.message || 'Failed to save CORS origins.');
    handleApiError(e, 'settings');
    throw e;
  } finally {
    setLoading(false);
  }
}

export function activateCorsPanel(): void {
  active = true;
  if (origins() == null && !loadError()) void loadCors();
}

export function disposeCorsPanel(): void {
  active = false;
}
