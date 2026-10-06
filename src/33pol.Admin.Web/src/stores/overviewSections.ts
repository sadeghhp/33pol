import { createSignal } from 'solid-js';
import { apiClient, handleApiError } from './auth';

type SectionBody = Record<string, unknown> | null;

const [finops, setFinops] = createSignal<SectionBody>(null);
const [policy, setPolicy] = createSignal<SectionBody>(null);
const [controlPlane, setControlPlane] = createSignal<SectionBody>(null);
const [activity, setActivity] = createSignal<SectionBody>(null);
const [tenants, setTenants] = createSignal<SectionBody>(null);
const [rateLimits, setRateLimits] = createSignal<SectionBody>(null);
const [loading, setLoading] = createSignal(false);

let pollTimer: ReturnType<typeof setInterval> | null = null;
let active = false;
let wallboardMode = false;

async function loadSection(path: string, assign: (v: SectionBody) => void): Promise<void> {
  try {
    const body = await apiClient.apiJson<Record<string, unknown>>(`${path}?refresh=true`);
    assign(body);
  } catch (e) {
    handleApiError(e, 'overview');
  }
}

export async function loadOverviewSections(quiet = false, wallboard = wallboardMode): Promise<void> {
  if (!quiet) setLoading(true);
  try {
    if (wallboard) {
      await Promise.all([
        loadSection('/admin/api/overview/policy', setPolicy),
        loadSection('/admin/api/overview/rate-limits', setRateLimits),
      ]);
      return;
    }
    await Promise.all([
      loadSection('/admin/api/overview/finops', setFinops),
      loadSection('/admin/api/overview/policy', setPolicy),
      loadSection('/admin/api/overview/control-plane', setControlPlane),
      loadSection('/admin/api/overview/activity?limit=20', setActivity),
      loadSection('/admin/api/overview/tenants', setTenants),
      loadSection('/admin/api/overview/rate-limits', setRateLimits),
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
  return { finops, policy, controlPlane, activity, tenants, rateLimits, loading };
}
