import { createSignal } from 'solid-js';
import { createStore } from 'solid-js/store';
import { apiClient, handleApiError } from './auth';

type SectionBody = Record<string, unknown> | null;

export type OverviewSectionKey =
  | 'finops'
  | 'policy'
  | 'controlPlane'
  | 'activity'
  | 'tenants'
  | 'rateLimits';

const [finops, setFinops] = createSignal<SectionBody>(null);
const [policy, setPolicy] = createSignal<SectionBody>(null);
const [controlPlane, setControlPlane] = createSignal<SectionBody>(null);
const [activity, setActivity] = createSignal<SectionBody>(null);
const [tenants, setTenants] = createSignal<SectionBody>(null);
const [rateLimits, setRateLimits] = createSignal<SectionBody>(null);
const [loading, setLoading] = createSignal(false);
const [sectionErrors, setSectionErrors] = createStore<Record<OverviewSectionKey, string>>({
  finops: '',
  policy: '',
  controlPlane: '',
  activity: '',
  tenants: '',
  rateLimits: '',
});

let pollTimer: ReturnType<typeof setInterval> | null = null;
let active = false;
let wallboardMode = false;

const assigners: Record<OverviewSectionKey, (v: SectionBody) => void> = {
  finops: setFinops,
  policy: setPolicy,
  controlPlane: setControlPlane,
  activity: setActivity,
  tenants: setTenants,
  rateLimits: setRateLimits,
};

function sectionErrorMessage(e: unknown): string {
  const err = e as { message?: string; title?: string };
  return err.message || err.title || 'Could not load this section.';
}

async function loadSection(key: OverviewSectionKey, path: string): Promise<void> {
  try {
    const body = await apiClient.apiJson<Record<string, unknown>>(`${path}?refresh=true`);
    assigners[key](body);
    setSectionErrors(key, '');
  } catch (e) {
    assigners[key](null);
    setSectionErrors(key, sectionErrorMessage(e));
    handleApiError(e, 'overview');
  }
}

export async function loadOverviewSections(quiet = false, wallboard = wallboardMode): Promise<void> {
  if (!quiet) setLoading(true);
  try {
    if (wallboard) {
      await Promise.all([
        loadSection('policy', '/admin/api/overview/policy'),
        loadSection('rateLimits', '/admin/api/overview/rate-limits'),
      ]);
      return;
    }
    await Promise.all([
      loadSection('finops', '/admin/api/overview/finops'),
      loadSection('policy', '/admin/api/overview/policy'),
      loadSection('controlPlane', '/admin/api/overview/control-plane'),
      loadSection('activity', '/admin/api/overview/activity?limit=20'),
      loadSection('tenants', '/admin/api/overview/tenants'),
      loadSection('rateLimits', '/admin/api/overview/rate-limits'),
    ]);
  } finally {
    if (!quiet) setLoading(false);
  }
}

export function setOverviewWallboardMode(on: boolean): void {
  wallboardMode = on;
  if (active) void loadOverviewSections(true, on);
}

export function activateOverviewSections(wallboard = false): void {
  active = true;
  wallboardMode = wallboard;
  void loadOverviewSections(false, wallboard);
  if (pollTimer) return;
  pollTimer = setInterval(() => {
    if (active) void loadOverviewSections(true, wallboardMode);
  }, 30_000);
}

export function disposeOverviewSections(): void {
  active = false;
  wallboardMode = false;
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

export function useOverviewSections() {
  return { finops, policy, controlPlane, activity, tenants, rateLimits, loading, sectionErrors };
}
