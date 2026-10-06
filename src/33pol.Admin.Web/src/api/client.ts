import { classifyError, type ClassifiedError } from './errors';

/** Statuses that mean "not now, try again" — retried only on GET with a retry budget. */
export const RETRYABLE_STATUS = new Set([429, 503]);
export const RETRY_BASE_MS = 250;
export const RETRY_JITTER = 0.25;
/** Never park a caller longer than the 2s poll's own cadence. */
export const RETRY_MAX_WAIT_MS = 2000;

export interface ApiError extends Error {
  status: number;
  title: string;
  detail: string | null;
  global: boolean;
  section?: string | null;
  credentialRejected: boolean;
  classified: ClassifiedError;
}

export interface ApiClientHooks {
  onCredentialRejected?: () => void;
  onConnectionDegraded?: () => void;
  requestConnectionRecheck?: () => void;
}

export interface ApiClientConfig {
  getApiKey: () => string;
  fetchImpl?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
  hooks?: ApiClientHooks;
}

export interface FetchResponse extends Response {
  _bodyText?: string;
}

function defaultSleep(ms: number): Promise<void> {
  return ms > 0 ? new Promise((resolve) => setTimeout(resolve, ms)) : Promise.resolve();
}

export function createApiError(
  status: number,
  statusText: string,
  text: string,
  context?: { editModelUrl?: string; gatewayErrorCode?: string },
): ApiError {
  const classified = classifyError(status, statusText, text, context);
  const err = new Error(classified.message) as ApiError;
  err.status = status;
  err.title = classified.title;
  err.detail = classified.detail;
  err.global = classified.global;
  err.section = classified.section;
  err.credentialRejected = classified.credentialRejected === true;
  err.classified = classified;
  return err;
}

/**
 * How long to wait before the next attempt.
 *
 * A server that states Retry-After is answered on its own terms, capped: the 2s poll calls
 * through here, and parking a tick for the 60s a rate limiter might ask for would freeze the
 * live vitals for half a minute and stack up timers behind it. Without that header the wait is
 * exponential from 250ms with +/-25% jitter.
 */
export function retryDelayMs(attempt: number, response?: Response): number {
  const header = response?.headers?.get?.('Retry-After');
  if (header) {
    const seconds = Number(header);
    const ms = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(header) - Date.now();
    if (Number.isFinite(ms) && ms >= 0) return Math.min(ms, RETRY_MAX_WAIT_MS);
  }
  const base = RETRY_BASE_MS * Math.pow(2, attempt);
  const jitter = base * RETRY_JITTER * (Math.random() * 2 - 1);
  return Math.max(0, Math.round(Math.min(base + jitter, RETRY_MAX_WAIT_MS)));
}

export function authHeaders(apiKey: string): Record<string, string> {
  return apiKey ? { 'X-API-Key': apiKey } : {};
}

export function jsonHeaders(apiKey: string): Record<string, string> {
  return { ...authHeaders(apiKey), 'Content-Type': 'application/json' };
}

export function createApiClient(config: ApiClientConfig) {
  const fetchImpl = config.fetchImpl ?? fetch;
  const sleep = config.sleep ?? defaultSleep;
  const hooks = config.hooks ?? {};

  function classifyAndThrow(
    status: number,
    statusText: string,
    text: string,
    editModelUrl?: string,
    gatewayErrorCode?: string | null,
  ): never {
    const err = createApiError(status, statusText, text, { editModelUrl, gatewayErrorCode: gatewayErrorCode ?? undefined });

    if (err.credentialRejected) {
      hooks.onCredentialRejected?.();
    } else if (status === 401) {
      hooks.onConnectionDegraded?.();
      hooks.requestConnectionRecheck?.();
    }
    throw err;
  }

  async function fetchWithRetry(
    url: string,
    options: RequestInit = {},
    editModelUrl?: string,
    retries?: number,
    readBodyAsText = true,
  ): Promise<FetchResponse> {
    const max = retries ?? 1;
    let lastErr: unknown;
    for (let i = 0; i <= max; i++) {
      try {
        const res = (await fetchImpl(url, options)) as FetchResponse;
        if (!res.ok) {
          if (i < max && RETRYABLE_STATUS.has(res.status)) {
            const wait = retryDelayMs(i, res);
            try {
              await res.text();
            } catch {
              /* already consumed or torn down */
            }
            await sleep(wait);
            continue;
          }
          const text = readBodyAsText ? await res.text() : '';
          classifyAndThrow(
            res.status,
            res.statusText,
            text,
            editModelUrl,
            res.headers?.get?.('X-33pol-Error-Code'),
          );
        }
        if (readBodyAsText) {
          res._bodyText = await res.text();
        }
        return res;
      } catch (e) {
        lastErr = e;
        if (e && typeof e === 'object' && 'title' in e && 'global' in e) throw e;
        if (i === max) {
          classifyAndThrow(0, 'Failed to fetch', String(e), editModelUrl);
        }
        await sleep(retryDelayMs(i));
      }
    }
    throw lastErr;
  }

  async function apiFetch(url: string, options: RequestInit = {}, editModelUrl?: string): Promise<FetchResponse> {
    const method = (options.method || 'GET').toUpperCase();
    const retry = method === 'GET' ? 1 : 0;
    const hasBody = options.body !== undefined && options.body !== null;
    const apiKey = config.getApiKey();
    return fetchWithRetry(
      url,
      {
        ...options,
        headers: {
          ...(hasBody ? jsonHeaders(apiKey) : authHeaders(apiKey)),
          ...(options.headers as Record<string, string> | undefined),
        },
      },
      editModelUrl,
      retry,
    );
  }

  async function apiJson<T = unknown>(
    url: string,
    options: RequestInit = {},
    editModelUrl?: string,
  ): Promise<T | null> {
    const { data } = await apiJsonWithMeta<T>(url, options, editModelUrl);
    return data;
  }

  async function apiJsonWithMeta<T = unknown>(
    url: string,
    options: RequestInit = {},
    editModelUrl?: string,
  ): Promise<{ data: T | null; response: FetchResponse; etag: string | null }> {
    const res = await apiFetch(url, options, editModelUrl);
    const text = res._bodyText ?? '';
    const etag = res.headers?.get?.('ETag') ?? res.headers?.get?.('etag') ?? null;
    if (!text) return { data: null, response: res, etag };
    try {
      return { data: JSON.parse(text) as T, response: res, etag };
    } catch {
      classifyAndThrow(
        res.status,
        res.status === 200 ? 'Unexpected response' : res.statusText,
        text,
        editModelUrl,
        res.headers?.get?.('X-33pol-Error-Code'),
      );
    }
  }

  function filenameFromDisposition(header: string | null): string {
    if (!header) return '';
    const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
    if (star) {
      try {
        return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''));
      } catch {
        /* fall through */
      }
    }
    const plain = /filename="?([^";]+)"?/i.exec(header);
    return plain ? plain[1].trim() : '';
  }

  /** Saves the response body; Content-Disposition filename wins over the fallback. */
  async function downloadBlob(
    url: string,
    fallbackFilename: string,
    editModelUrl?: string,
  ): Promise<FetchResponse> {
    const res = await fetchWithRetry(
      url,
      { headers: authHeaders(config.getApiKey()) },
      editModelUrl,
      0,
      false,
    );
    const blob = await res.blob();
    const objectUrl = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = objectUrl;
    a.download = filenameFromDisposition(res.headers.get('Content-Disposition')) || fallbackFilename;
    a.click();
    setTimeout(() => URL.revokeObjectURL(objectUrl), 0);
    return res;
  }

  return {
    fetchWithRetry,
    apiFetch,
    apiJson,
    apiJsonWithMeta,
    downloadBlob,
    filenameFromDisposition,
    classifyAndThrow,
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
