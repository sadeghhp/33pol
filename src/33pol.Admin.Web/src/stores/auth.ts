import { createSignal } from 'solid-js';
import { createApiClient, type ApiClient, type ApiError } from '../api/client';
import type { ToastItem } from '../components/primitives';

const STORAGE_KEY = '33pol-admin-key';

export interface GlobalError {
  title: string;
  message: string;
  detail?: string;
}

let verifyFn: ((candidate?: string) => Promise<boolean>) | null = null;
let recheckInFlight = false;

const [apiKey, setApiKeySignal] = createSignal(localStorage.getItem(STORAGE_KEY) || '');
const [toasts, setToasts] = createSignal<ToastItem[]>([]);
const [globalError, setGlobalErrorState] = createSignal<GlobalError | null>(null);
const [authLoading, setAuthLoading] = createSignal(false);
let toastSeq = 0;

export function getApiKey(): string {
  return apiKey();
}

export function signedIn(): boolean {
  return !!apiKey();
}

export function useAuthSignals() {
  return { apiKey, toasts, globalError, authLoading };
}

export function keyPrefix(): string {
  const k = apiKey();
  if (k.length <= 8) return k ? '••••' : '';
  return k.slice(0, 4) + '…' + k.slice(-4);
}

export function persistApiKey(key: string): void {
  const trimmed = key.trim();
  setApiKeySignal(trimmed);
  if (trimmed) localStorage.setItem(STORAGE_KEY, trimmed);
  else localStorage.removeItem(STORAGE_KEY);
}

export function clearSession(): void {
  persistApiKey('');
  setGlobalErrorState(null);
}

export function setGlobalError(err: GlobalError | null): void {
  setGlobalErrorState(err);
  if (err) {
    requestAnimationFrame(() => {
      document.getElementById('global-alert')?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    });
  }
}

export function dismissGlobalError(): void {
  setGlobalErrorState(null);
}

export function pushToast(message: string, type: ToastItem['type'] = 'success'): void {
  if (!message) return;
  const id = ++toastSeq;
  setToasts((prev) => [...prev.slice(-2), { id, message, type }]);
  setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), 3000);
}

export function setVerifyConnection(fn: (candidate?: string) => Promise<boolean>): void {
  verifyFn = fn;
}

export function requestConnectionRecheck(): void {
  if (recheckInFlight || !apiKey() || !verifyFn) return;
  recheckInFlight = true;
  Promise.resolve(verifyFn())
    .catch(() => {})
    .finally(() => {
      recheckInFlight = false;
    });
}

export const apiClient: ApiClient = createApiClient({
  getApiKey,
  hooks: {
    onCredentialRejected: () => {
      connectionSetFail?.();
    },
    onConnectionDegraded: () => {
      connectionSetDegraded?.();
    },
    requestConnectionRecheck,
  },
});

let connectionSetFail: (() => void) | null = null;
let connectionSetDegraded: (() => void) | null = null;

export function registerConnectionHooks(hooks: {
  setFail: () => void;
  setDegraded: () => void;
}): void {
  connectionSetFail = hooks.setFail;
  connectionSetDegraded = hooks.setDegraded;
}

export async function verifyConnection(candidateKey?: string): Promise<boolean> {
  const candidate = (candidateKey ?? '').trim();
  if (!candidate && !apiKey()) return false;
  setAuthLoading(true);
  try {
    const options = candidate ? { headers: { 'X-API-Key': candidate } } : {};
    await apiClient.apiJson('/admin/api/config/status', options);
    if (candidate) persistApiKey(candidate);
    connectionSetOk?.();
    setGlobalErrorState(null);
    return true;
  } catch (e) {
    const err = e as ApiError;
    if (err.credentialRejected) {
      if (candidate) throw e;
      connectionSetFail?.();
    } else if (err.status === 401) {
      connectionSetDegraded?.();
      requestConnectionRecheck();
    } else if (candidate) {
      throw e;
    } else {
      connectionSetDegraded?.();
    }
    return false;
  } finally {
    setAuthLoading(false);
  }
}

let connectionSetOk: (() => void) | null = null;

export function registerConnectionOk(fn: () => void): void {
  connectionSetOk = fn;
}

export function handleApiError(e: unknown, scope?: string): void {
  const err = e as ApiError;
  if (err?.global) {
    setGlobalError({
      title: err.title || 'Error',
      message: err.message || 'Something went wrong.',
      detail: err.detail ?? undefined,
    });
  } else if (scope) {
    pushToast(err.message || 'Request failed', 'error');
  }
}
