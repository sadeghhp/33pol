import { createSignal, onCleanup } from 'solid-js';
import { createConnectionMachine, type ConnectionMachine } from '../realtime/connection';
import type { ConnectionSnapshot, ConnectionSource } from '../realtime/types';
import {
  apiClient,
  getApiKey,
  registerConnectionHooks,
  registerConnectionOk,
} from './auth';
import { setConnectionSnapshot } from './connectionWriter';
import { applyRequestsFromSource } from './requests';
import { applySummaryFromSource } from './summary';

const [snapshot, setSnapshot] = createSignal<ConnectionSnapshot>({
  status: '',
  mode: '',
  source: null,
  lastFrameAt: 0,
  lastVersion: null,
  retryDelayMs: 1000,
  degraded: false,
});

let machine: ConnectionMachine | null = null;
let currentTab = 'dashboard';
let logsAutoRefresh = false;
let errorsAutoRefresh = false;

export function useConnectionSnapshot() {
  return snapshot;
}

export function setActiveTab(tab: string): void {
  currentTab = tab === 'dashboard' ? 'dashboard' : tab;
  machine?.sync();
}

export function setLogsAutoRefresh(on: boolean): void {
  logsAutoRefresh = on;
}

export function setErrorsAutoRefresh(on: boolean): void {
  errorsAutoRefresh = on;
}

export function initConnection(): ConnectionMachine {
  registerConnectionHooks({
    setFail: () => machine?.setStatus('fail', true),
    setDegraded: () => {
      const s = snapshot();
      if (s.status !== 'fail') machine?.setStatus(s.status || 'degraded', true);
    },
  });
  registerConnectionOk(() => machine?.setStatus('ok', false));

  machine = createConnectionMachine({
    getApiKey,
    getTab: () => currentTab,
    isHidden: () => document.hidden,
    fetchImpl: fetch,
    onSnapshot: (s) => {
      setConnectionSnapshot(s);
      setSnapshot(s);
    },
    onApplySummary: (summary, source) => applySummaryFromSource(summary, source),
    onApplyRequests: (requests, source) => applyRequestsFromSource(requests, source),
    onPollCadence: (cadence) => {
      if (cadence.master && currentTab === 'dashboard') {
        void pollSummaryAndRequests();
      }
      if (cadence.logs) void import('./logs').then((m) => m.refreshLogsIfActive());
      if (cadence.errors) void import('./errors').then((m) => m.refreshErrorsIfActive());
      if (cadence.health) void verifyHealth();
    },
    pollOptions: {
      logsAutoRefresh: () => logsAutoRefresh && currentTab === 'logs',
      errorsAutoRefresh: () => errorsAutoRefresh && currentTab === 'errors',
    },
  });

  return machine;
}

export function syncConnection(): void {
  machine?.sync();
}

export function stopConnection(): void {
  machine?.stop();
}

async function pollSummaryAndRequests(): Promise<void> {
  const source: ConnectionSource = 'poll';
  try {
    const [summary, requests] = await Promise.all([
      apiClient.apiJson<Record<string, unknown>>('/admin/api/summary'),
      apiClient.apiJson<{ items?: Record<string, unknown>[] }>('/admin/api/requests?limit=25'),
    ]);
    if (summary) applySummaryFromSource(summary, source);
    const items = Array.isArray(requests)
      ? requests
      : Array.isArray((requests as { items?: unknown })?.items)
        ? ((requests as { items: Record<string, unknown>[] }).items ?? [])
        : [];
    if (items.length) applyRequestsFromSource(items, source);
  } catch {
    /* classified in client */
  }
}

async function verifyHealth(): Promise<void> {
  try {
    await apiClient.apiJson('/admin/api/config/status');
    const s = snapshot();
    if (s.status === 'fail') return;
    machine?.setStatus('ok', false);
  } catch (e) {
    const err = e as { status?: number; credentialRejected?: boolean };
    if (err.credentialRejected) machine?.setStatus('fail', true);
  }
}

export function useConnectionLifecycle(): void {
  const m = initConnection();
  syncConnection();
  const onVisibility = () => m.sync();
  document.addEventListener('visibilitychange', onVisibility);
  onCleanup(() => {
    document.removeEventListener('visibilitychange', onVisibility);
    stopConnection();
  });
}
