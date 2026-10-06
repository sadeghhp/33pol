import { createSignal } from 'solid-js';
import { reconcileSummary } from '../domain/reconcile';
import type { ConnectionSource } from '../realtime/types';
import { snapshot as connectionSnapshot } from './connectionWriter';
import { recordVitals } from './vitalsHistory';

const [summary, setSummary] = createSignal<Record<string, unknown> | null>(null);
const [updatedAt, setUpdatedAt] = createSignal(0);

export function useSummary() {
  return { summary, updatedAt };
}

export function applySummaryFromSource(incoming: Record<string, unknown>, source: ConnectionSource): void {
  const snap = connectionSnapshot();
  if (snap.source && snap.source !== source) return;
  const prev = summary();
  const { value, changedPaths } = reconcileSummary(prev, incoming);
  if (changedPaths.length === 0 && prev) return;
  setSummary(value);
  setUpdatedAt(Date.now());
  recordVitals(value);
}

export function clearSummary(): void {
  setSummary(null);
  setUpdatedAt(0);
}
