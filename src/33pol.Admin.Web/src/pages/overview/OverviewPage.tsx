import { For, Show, createEffect, createMemo, createSignal, onCleanup, onMount } from 'solid-js';
import { useNavigate, useSearchParams } from '@solidjs/router';
import { Button, Field, Select } from '../../components/primitives';
import { IconAlertTriangle, IconMaximize, IconMinimize, IconPause, IconPin, IconPlay, IconRefresh } from '../../components/icons';
import { DurationCell } from '../../components/DurationCell';
import { liveBadgeView } from '../../app/liveBadge';
import { useWallboard } from '../../app/useWallboard';
import { attentionRows, hasCriticalAttention } from '../../domain/attention';
import { formatNum, formatTime } from '../../domain/format';
import { useConnectionSnapshot, syncConnection } from '../../stores/connection';
import {
  activateOverviewSections,
  disposeOverviewSections,
  loadOverviewSections,
  setOverviewWallboardMode,
  useOverviewSections,
} from '../../stores/overviewSections';
import { pushToast } from '../../stores/auth';
import { resumeFromPause, useRequestsStore } from '../../stores/requests';
import { useSummary } from '../../stores/summary';

const WINDOWS = [
  { value: '1m', label: '1 min' },
  { value: '5m', label: '5 min' },
  { value: '1h', label: '1 hour' },
  { value: '24h', label: '24 h' },
];

const DISMISSED_KEY = '33pol-admin-attention-dismissed';

function loadDismissed(): string[] {
  try {
    const raw = sessionStorage.getItem(DISMISSED_KEY);
    const list = raw ? JSON.parse(raw) : [];
    return Array.isArray(list) ? list.filter((x) => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

export default function OverviewPage() {
  const { summary, updatedAt } = useSummary();
  const req = useRequestsStore();
  const conn = useConnectionSnapshot();
  const sections = useOverviewSections();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const initialWindow = () => {
    const fromHash = params.window;
    if (typeof fromHash === 'string' && WINDOWS.some((w) => w.value === fromHash)) return fromHash;
    return localStorage.getItem('33pol-admin-overview-window') || '5m';
  };
  const [window, setWindow] = createSignal(initialWindow());
  const [dismissed, setDismissed] = createSignal<string[]>(loadDismissed());
  const [attentionCollapsed, setAttentionCollapsed] = createSignal(false);

  const wallboard = createMemo(() => params.wall === '1');
  const s = () => summary();
  const rows = () => req.visibleOrder();
  const attention = createMemo(() =>
    attentionRows(s()?.attention, dismissed(), wallboard()),
  );
  const criticalAttention = createMemo(() => hasCriticalAttention(attention()));
  const live = createMemo(() => liveBadgeView(conn()));

  const wb = useWallboard({
    active: () => wallboard(),
    summaryUpdatedAt: updatedAt,
    connectionFailed: () => conn().status === 'fail',
    hasCriticalAttention: criticalAttention,
    onExit: () => setParams({ wall: undefined, window: window() }),
  });

  onMount(() => {
    syncConnection();
    activateOverviewSections(wallboard());
  });

  onCleanup(() => {
    disposeOverviewSections();
  });

  createEffect(() => {
    const on = wallboard();
    setOverviewWallboardMode(on);
    if (on) {
      req.setExpandedId(null);
      if (req.paused()) {
        resumeFromPause();
        pushToast('Live tail resumed — the wallboard has no pause control.');
      }
      void wb.enterFullscreen();
    }
  });

  createEffect(() => {
    const w = params.window;
    if (typeof w === 'string' && WINDOWS.some((x) => x.value === w) && w !== window()) {
      setWindow(w);
      localStorage.setItem('33pol-admin-overview-window', w);
    }
  });

  const dismissAttention = (key: string) => {
    const next = [...dismissed(), key];
    setDismissed(next);
    try {
      sessionStorage.setItem(DISMISSED_KEY, JSON.stringify(next));
    } catch {
      /* storage unavailable */
    }
  };

  const togglePause = () => {
    if (req.paused()) resumeFromPause();
    else req.setPaused(true);
  };

  const setOverviewWindow = (v: string) => {
    setWindow(v);
    localStorage.setItem('33pol-admin-overview-window', v);
    setParams({ window: v, wall: wallboard() ? '1' : undefined });
  };

  const toggleWallboard = () => {
    if (wallboard()) setParams({ wall: undefined, window: window() });
    else setParams({ wall: '1', window: window() });
  };

  const cardTitle = (body: Record<string, unknown> | null, fallback: string) =>
    String(body?.title ?? body?.headline ?? fallback);

  return (
    <section id="panel-dashboard" class="page overview-page" classList={{ 'is-wallboard': wallboard() }}>
      <Show when={wallboard()}>
        <div class="wallboard-bar wb-hide">
          <span class="wb-brand">33pol</span>
          <span class="wb-scope">{cardTitle(sections.controlPlane(), 'Live gateway overview')}</span>
          <span class="wb-gap" />
          <span class="wb-window">{WINDOWS.find((w) => w.value === window())?.label ?? window()}</span>
          <span class={live().className} title={live().title} role="status">
            <span class="pulse-dot" classList={{ live: live().dotClass === 'live' }} />
            <span>{live().text}</span>
          </span>
          <span class="wb-clock">{wb.clockText()}</span>
          <button type="button" class="icon-btn wb-chrome" onClick={() => wb.enterFullscreen()} title="Fullscreen" aria-label="Fullscreen">
            <span class="icon"><IconMaximize /></span>
          </button>
          <button type="button" class="icon-btn wb-chrome" onClick={() => setParams(wb.exitWallboard())} title="Exit wallboard" aria-label="Exit wallboard">
            <span class="icon"><IconMinimize /></span>
          </button>
        </div>
        <Show when={wb.stale()}>
          <div class="wallboard-stale-band" role="status" aria-live="polite">
            <span class="icon"><IconAlertTriangle /></span>
            <strong>{wb.staleTitle()}</strong>
            <span>{wb.staleText()}</span>
          </div>
        </Show>
      </Show>

      <header class="page-header">
        <div>
          <p class="eyebrow">Telemetry</p>
          <h1>Overview</h1>
          <p class="page-sub wb-hide">Gateway-wide across all tenants — for per-tenant spend see <strong>Usage &amp; cost</strong>.</p>
        </div>
        <div class="page-actions wb-hide">
          <Select options={WINDOWS} value={window()} onChange={setOverviewWindow} />
          <span class={live().className} title={live().title} role="status">
            <span class="pulse-dot" classList={{ live: live().dotClass === 'live' }} />
            <span>{live().text}</span>
          </span>
          <Button variant="ghost" size="sm" onClick={() => { syncConnection(); void loadOverviewSections(); }}>
            <span class="icon"><IconRefresh /></span> Refresh
          </Button>
          <Button variant="ghost" size="sm" onClick={togglePause}>
            <span class="icon">{req.paused() ? <IconPlay /> : <IconPause />}</span>
            {req.paused() ? 'Resume' : 'Pause'}
          </Button>
          <Button variant="ghost" size="sm" onClick={toggleWallboard}>
            <span class="icon">{wallboard() ? <IconMinimize /> : <IconMaximize />}</span>
            {wallboard() ? 'Exit wallboard' : 'Wallboard'}
          </Button>
        </div>
      </header>

      <Show when={attention().length > 0}>
        <section class="attention-banner wb-hide" aria-label="Attention">
          <header class="attention-header">
            <h2>Needs attention</h2>
            <button type="button" class="link-btn" onClick={() => setAttentionCollapsed((v) => !v)}>
              {attentionCollapsed() ? 'Show' : 'Hide'}
            </button>
          </header>
          <Show when={!attentionCollapsed() || wallboard()}>
            <ul class="attention-list">
              <For each={attention()}>
                {(row) => (
                  <li class={row.cls}>
                    <strong>{String(row.title ?? 'Notice')}</strong>
                    <span>{String(row.message ?? row.detail ?? '')}</span>
                    <Show when={!wallboard()}>
                      <button type="button" class="link-btn" onClick={() => dismissAttention(row.key)}>Dismiss</button>
                    </Show>
                  </li>
                )}
              </For>
            </ul>
          </Show>
        </section>
      </Show>

      <div class="vitals-grid">
        <div class="stat-card vital-card">
          <span class="stat-label">Requests</span>
          <span class="stat-value">{formatNum(s()?.totalRequests ?? '—')}</span>
        </div>
        <div class="stat-card vital-card">
          <span class="stat-label">Errors</span>
          <span class="stat-value">{formatNum(s()?.totalErrors ?? '—')}</span>
        </div>
        <div class="stat-card vital-card">
          <span class="stat-label">Active</span>
          <span class="stat-value">{formatNum(s()?.activeRequests ?? '—')}</span>
        </div>
        <div class="stat-card vital-card">
          <span class="stat-label">Updated</span>
          <span class="stat-value">{updatedAt() ? formatTime(new Date(updatedAt()).toISOString()) : '—'}</span>
        </div>
      </div>

      <div class="overview-cards wb-hide">
        <div class="card glance-card">
          <h3>FinOps</h3>
          <p>{String(sections.finops()?.summary ?? sections.finops()?.message ?? '—')}</p>
        </div>
        <div class="card glance-card">
          <h3>Policy</h3>
          <p>{String(sections.policy()?.summary ?? sections.policy()?.message ?? '—')}</p>
        </div>
        <div class="card glance-card">
          <h3>Control plane</h3>
          <p>{String(sections.controlPlane()?.summary ?? sections.controlPlane()?.message ?? '—')}</p>
        </div>
        <div class="card glance-card">
          <h3>Activity</h3>
          <p>{String(sections.activity()?.summary ?? sections.activity()?.message ?? '—')}</p>
        </div>
        <div class="card glance-card">
          <h3>Tenants</h3>
          <p>{String(sections.tenants()?.summary ?? sections.tenants()?.message ?? '—')}</p>
        </div>
        <div class="card rate-limits-card">
          <h3>Rate limits</h3>
          <p>{String(sections.rateLimits()?.title ?? sections.rateLimits()?.message ?? '—')}</p>
          <Button variant="ghost" size="sm" onClick={() => navigate('/settings')}>Open settings</Button>
        </div>
      </div>

      <div class="card">
        <header class="card-header">
          <h2>Live requests</h2>
          <div class="filter-row wb-hide">
            <Field label="Model">
              <input
                type="search"
                placeholder="Filter model"
                value={req.modelFilter()}
                onInput={(e) => req.setModelFilter(e.currentTarget.value)}
              />
            </Field>
            <Field label="Tenant">
              <input
                type="search"
                placeholder="Filter tenant"
                value={req.tenantFilter()}
                onInput={(e) => req.setTenantFilter(e.currentTarget.value)}
              />
            </Field>
            <Field label="Status">
              <input
                type="search"
                placeholder="Status code"
                value={req.statusFilter()}
                onInput={(e) => req.setStatusFilter(e.currentTarget.value)}
              />
            </Field>
            <label class="checkbox-label">
              <input type="checkbox" checked={req.slowOnly()} onChange={(e) => req.setSlowOnly(e.currentTarget.checked)} />
              Slow only (&gt;5s)
            </label>
          </div>
        </header>
        <div class="table-wrap">
          <table class="data-table t-requests">
            <colgroup>
              <col /><col /><col /><col /><col /><col /><col /><col /><col /><col /><col />
            </colgroup>
            <thead>
              <tr>
                <th>Request</th>
                <th>Model</th>
                <th>Tenant</th>
                <th>Status</th>
                <th>Duration</th>
                <th>Started</th>
                <th class="wb-hide">Route</th>
                <th class="wb-hide">Backend</th>
                <th class="wb-hide">Tokens</th>
                <th class="wb-hide">Cost</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <For each={rows()}>
                {(id) => {
                  const row = () => req.byId[id];
                  const expanded = () => req.expandedId() === id;
                  const isNew = () => req.isNewRow(id);
                  return (
                    <>
                      <tr
                        class="request-row"
                        classList={{ 'row-enter': isNew(), expanded: expanded(), 'is-pinned': req.pinnedIds().includes(id) }}
                        onClick={() => req.setExpandedId(expanded() ? null : id)}
                      >
                        <td><code>{String(row()?.requestId ?? id).slice(0, 12)}</code></td>
                        <td>{String(row()?.modelId ?? row()?.model ?? '—')}</td>
                        <td>{String(row()?.tenantId ?? row()?.tenant ?? '—')}</td>
                        <td>{String(row()?.status ?? row()?.statusCode ?? '—')}</td>
                        <td>
                          <DurationCell
                            startedAt={row()?.startedAt as string | undefined}
                            durationMs={row()?.durationMs as number | undefined}
                            inFlight={row()?.inFlight === true || row()?.status === 'in_flight'}
                          />
                        </td>
                        <td>{formatTime(row()?.startedAt as string | undefined)}</td>
                        <td class="wb-hide">{String(row()?.routeId ?? '—')}</td>
                        <td class="wb-hide">{String(row()?.backendId ?? '—')}</td>
                        <td class="wb-hide">{formatNum(row()?.totalTokens as number | undefined)}</td>
                        <td class="wb-hide">{String(row()?.costUsd ?? '—')}</td>
                        <td class="wb-hide">
                          <button type="button" class="icon-btn" aria-label="Pin" onClick={(e) => { e.stopPropagation(); req.togglePin(id); }}>
                            <span class="icon"><IconPin /></span>
                          </button>
                        </td>
                      </tr>
                      <Show when={expanded()}>
                        <tr class="request-detail-row">
                          <td colspan="11">
                            <pre class="request-detail">{JSON.stringify(row(), null, 2)}</pre>
                          </td>
                        </tr>
                      </Show>
                    </>
                  );
                }}
              </For>
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}
