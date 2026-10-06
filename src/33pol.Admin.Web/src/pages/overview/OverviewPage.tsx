import { For, Show, createEffect, createMemo, createSignal, onCleanup, onMount } from 'solid-js';
import { useNavigate, useSearchParams } from '@solidjs/router';
import { ActivityCard } from '../../components/overview/ActivityCard';
import { ControlPlaneCard } from '../../components/overview/ControlPlaneCard';
import { FinOpsCard } from '../../components/overview/FinOpsCard';
import { PolicyCard } from '../../components/overview/PolicyCard';
import { RateLimitsGlanceCard } from '../../components/overview/RateLimitsGlanceCard';
import { TenantsCard } from '../../components/overview/TenantsCard';
import { Button, Field, Select } from '../../components/primitives';
import {
  parseActivity,
  parseControlPlane,
  parseFinOps,
  parsePolicy,
  parseRateLimitsGlance,
  parseTenants,
} from '../../domain/overviewCards';
import { Sparkline } from '../../components/Sparkline';
import {
  IconAlertTriangle,
  IconMaximize,
  IconMinimize,
  IconPause,
  IconPin,
  IconPlay,
  IconRefresh,
  IconZap,
} from '../../components/icons';
import { DurationCell } from '../../components/DurationCell';
import { liveBadgeView } from '../../app/liveBadge';
import { useWallboard } from '../../app/useWallboard';
import { attentionRows, hasCriticalAttention } from '../../domain/attention';
import { formatMsParts, formatMsShort, formatNum, formatTime } from '../../domain/format';
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
import {
  hasTrailingWindows,
  sparkSourceText,
  sparkValues,
  useVitalsHistory,
  windowStats,
} from '../../stores/vitalsHistory';

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

function onboardingModelId(summary: Record<string, unknown> | null): string {
  const backends = summary?.backends;
  if (Array.isArray(backends) && backends[0] && typeof backends[0] === 'object') {
    const model = (backends[0] as Record<string, unknown>).modelId;
    if (model) return String(model);
  }
  return 'your-model-id';
}

function onboardingCurl(model: string): string {
  const origin = typeof location !== 'undefined' ? location.origin : 'http://localhost:8080';
  return (
    `curl -s ${origin}/v1/chat/completions \\\n` +
    "  -H 'Authorization: Bearer <inference-key>' \\\n" +
    "  -H 'Content-Type: application/json' \\\n" +
    `  -d '{"model":"${model}","messages":[{"role":"user","content":"Hello"}}]}'`
  );
}

export default function OverviewPage() {
  const { summary, updatedAt } = useSummary();
  const req = useRequestsStore();
  const conn = useConnectionSnapshot();
  const sections = useOverviewSections();
  const vitals = useVitalsHistory();
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
  const stats = createMemo(() => windowStats(s(), window(), vitals()));
  const sparkTitle = createMemo(() => sparkSourceText(s()));
  const showOnboarding = createMemo(
    () =>
      !!s() &&
      Number(s()?.totalInferenceRequests ?? 0) === 0 &&
      rows().length === 0,
  );
  const showOverviewBody = createMemo(() => !!s() && !showOnboarding());
  const showStaleNotice = createMemo(() => conn().degraded && conn().status !== 'fail');
  const connectionFailed = createMemo(() => conn().status === 'fail');

  const wb = useWallboard({
    active: () => wallboard(),
    summaryUpdatedAt: updatedAt,
    connectionFailed,
    hasCriticalAttention: criticalAttention,
    requestFilters: () => ({
      model: req.modelFilter(),
      tenant: req.tenantFilter(),
      status: req.statusFilter(),
      slowOnly: req.slowOnly(),
    }),
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

  const copyOnboardingCurl = async () => {
    const text = onboardingCurl(onboardingModelId(s()));
    try {
      await navigator.clipboard.writeText(text);
      pushToast('curl command copied.');
    } catch {
      pushToast('Could not access clipboard.', 'error');
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

  const windowLabel = () => {
    if (!hasTrailingWindows(s())) return 'lifetime';
    return `last ${WINDOWS.find((w) => w.value === window())?.label ?? window()}`;
  };

  const latencyParts = () => {
    const w = stats();
    return formatMsParts(w.lifetime ? w.avgMs : (w.p95Ms ?? 0));
  };

  const ttftParts = () => {
    const w = stats();
    if (w.lifetime || w.ttftSamples === 0) return { value: '—', unit: '' };
    return formatMsParts(w.ttftP95Ms);
  };

  const spark = (metric: Parameters<typeof sparkValues>[1]) =>
    sparkValues(s(), metric, vitals());

  const inFlightFoot = () => {
    const streams = Number(s()?.activeStreams ?? 0);
    const inflight = Number(s()?.activeRequests ?? 0);
    if (streams > 0) {
      return `${streams} streaming · ${inflight - streams} buffered`;
    }
    return inflight > 0 ? 'active' : 'idle';
  };

  return (
    <section id="panel-dashboard" class="page overview-page" classList={{ 'is-wallboard': wallboard() }}>
      <Show when={wallboard()}>
        <div class="wallboard-bar wb-hide">
          <span class="wb-brand">33pol</span>
          <span class="wb-scope">{cardTitle(sections.controlPlane(), 'Live gateway overview')}</span>
          <span class="wb-gap" />
          <span class="wb-window">{WINDOWS.find((w) => w.value === window())?.label ?? window()}</span>
          <Show when={wb.hasWallboardFilters()}>
            <span class="wb-filter-hint">{wb.wallboardFilterText()}</span>
          </Show>
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
          <Select options={WINDOWS} value={window()} onChange={setOverviewWindow} disabled={!hasTrailingWindows(s())} />
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

      <Show when={showOnboarding()}>
        <div class="empty-state onboarding wb-hide">
          <span class="empty-state-icon icon"><IconZap /></span>
          <h3>No traffic yet</h3>
          <p>Point any OpenAI SDK at this gateway and send one request — this page lights up the moment it arrives.</p>
          <pre class="onboarding-snippet mono">{onboardingCurl(onboardingModelId(s()))}</pre>
          <div class="onboarding-actions">
            <Button onClick={copyOnboardingCurl}>Copy curl</Button>
            <Button variant="ghost" onClick={() => navigate('/routing?sub=models')}>Add a model</Button>
            <Button variant="ghost" onClick={() => navigate('/keys')}>Create an inference key</Button>
          </div>
        </div>
      </Show>

      <Show when={showStaleNotice()}>
        <p class="notice warn wb-hide" role="status">Live data may be stale — the last refresh failed. Check your connection.</p>
      </Show>
      <Show when={connectionFailed()}>
        <p class="notice error wb-hide" role="status">
          Your admin API key was rejected, so these figures have stopped updating. Use <strong>Change key</strong> in the top bar to sign in again.
        </p>
      </Show>

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
                    <strong>{String(row.title ?? row.code ?? 'Notice')}</strong>
                    <span>{String(row.message ?? row.detail ?? '')}</span>
                    <Show when={row.hasLink && row.linkPath && !wallboard()}>
                      <Button variant="ghost" size="sm" onClick={() => navigate(row.linkPath!)}>Open</Button>
                    </Show>
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

      <Show when={showOverviewBody()}>
        <div class="vitals-grid vitals-5">
          <article class="vital" title={sparkTitle()}>
            <div class="vital-head"><span class="vital-label">Requests</span></div>
            <strong class="vital-value">{formatNum(stats().requests)}</strong>
            <div class="vital-foot">
              <span>{stats().rps.toFixed(stats().rps < 10 ? 1 : 0)}/s · {windowLabel()}</span>
            </div>
            <Sparkline values={spark('throughput')} />
          </article>

          <article
            class="vital is-actionable"
            classList={{ 'accent-error': stats().errors > 0 }}
            role="button"
            tabIndex={0}
            aria-label="Open the Errors tab"
            onClick={() => navigate('/errors')}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                navigate('/errors');
              }
            }}
          >
            <div class="vital-head"><span class="vital-label">Errors</span></div>
            <strong class="vital-value">{formatNum(stats().errors)}</strong>
            <div class="vital-foot">
              <span>{(stats().errorRate * 100).toFixed(2)}% error rate · {windowLabel()}</span>
            </div>
            <Sparkline values={spark('errorRate')} />
          </article>

          <article class="vital" title={sparkTitle()}>
            <div class="vital-head">
              <span class="vital-label">{stats().lifetime ? 'Avg latency' : 'Latency p95'}</span>
            </div>
            <strong class="vital-value">
              <span>{latencyParts().value}</span>
              <span class="unit">{latencyParts().unit}</span>
            </strong>
            <div class="vital-foot">
              <Show when={stats().lifetime} fallback={
                <span>
                  p50 {formatMsShort(stats().p50Ms)} · p99 {formatMsShort(stats().p99Ms)} · {windowLabel()}
                </span>
              }>
                <span>mean upstream round-trip</span>
              </Show>
            </div>
            <Sparkline values={spark('latency')} />
          </article>

          <article class="vital" title={sparkTitle()}>
            <div class="vital-head"><span class="vital-label">TTFT p95</span></div>
            <strong class="vital-value">
              <span>{ttftParts().value}</span>
              <span class="unit">{ttftParts().unit}</span>
            </strong>
            <div class="vital-foot">
              <Show when={stats().lifetime} fallback={
                <Show when={stats().ttftSamples > 0} fallback={<span>no streams · {windowLabel()}</span>}>
                  <span>
                    p50 {formatMsShort(stats().ttftP50Ms)} · {formatNum(stats().ttftSamples)} streams · {windowLabel()}
                  </span>
                </Show>
              }>
                <span>needs a newer gateway</span>
              </Show>
            </div>
            <Sparkline values={spark('ttft')} />
          </article>

          <article class="vital accent-live">
            <div class="vital-head"><span class="vital-label">In flight</span></div>
            <strong class="vital-value">{formatNum(s()?.activeRequests ?? 0)}</strong>
            <div class="vital-foot"><span>{inFlightFoot()}</span></div>
            <Sparkline values={spark('inflight')} />
          </article>
        </div>
      </Show>

      <Show when={showOverviewBody()}>
        <div class="overview-cards wb-hide">
          <FinOpsCard
            data={parseFinOps(sections.finops() as Record<string, unknown> | null)}
            error={sections.sectionErrors.finops}
          />
          <PolicyCard
            data={parsePolicy(sections.policy() as Record<string, unknown> | null)}
            error={sections.sectionErrors.policy}
          />
          <ControlPlaneCard
            data={parseControlPlane(sections.controlPlane() as Record<string, unknown> | null)}
            error={sections.sectionErrors.controlPlane}
          />
          <ActivityCard
            data={parseActivity(sections.activity() as Record<string, unknown> | null)}
            error={sections.sectionErrors.activity}
          />
          <TenantsCard
            data={parseTenants(sections.tenants() as Record<string, unknown> | null)}
            error={sections.sectionErrors.tenants}
          />
          <RateLimitsGlanceCard
            data={parseRateLimitsGlance(sections.rateLimits() as Record<string, unknown> | null)}
            error={sections.sectionErrors.rateLimits}
            onOpen={() => navigate('/settings?sub=ratelimits')}
          />
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
      </Show>
    </section>
  );
}
