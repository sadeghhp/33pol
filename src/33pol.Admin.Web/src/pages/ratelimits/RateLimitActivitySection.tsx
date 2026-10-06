import { For, Show, createMemo } from 'solid-js';
import { Button } from '../../components/primitives';
import { IconRefresh } from '../../components/icons';
import { formatNum } from '../../domain/format';
import { sparkFill, sparkLine } from '../../domain/rateLimitEdit';
import { useRateLimitActivityStore } from '../../stores/rateLimits';

const WINDOW_OPTIONS = [
  { minutes: 15, label: '15 m' },
  { minutes: 60, label: '1 h' },
  { minutes: 180, label: '3 h' },
  { minutes: 1440, label: '24 h' },
];

const TAB_OPTIONS = [
  { id: 'tenantModel' as const, label: 'Tenant & model' },
  { id: 'tenant' as const, label: 'Tenant' },
  { id: 'model' as const, label: 'Model' },
  { id: 'key' as const, label: 'API key' },
];

export function RateLimitActivitySection() {
  const activity = useRateLimitActivityStore();

  const report = () => activity.usageReport();
  const series = () => activity.usageSeries();

  const subjectRows = createMemo(() => {
    const r = report();
    if (!r) return [];
    const tab = activity.usageTab();
    if (tab === 'tenant') return r.byTenant;
    if (tab === 'model') return r.byModel;
    if (tab === 'key') return r.byApiKey;
    return r.byTenantModel;
  });

  const spark = createMemo(() => {
    const s = series();
    if (!s?.points?.length) return null;
    const covered = s.points.filter((p) => p.covered);
    const refused = covered.map((p) => p.refusedByRate + p.refusedByStreams);
    const max = Math.max(...refused, 1);
    return {
      refusedLine: sparkLine(refused, max),
      refusedFill: sparkFill(refused, max),
      total: refused.reduce((a, b) => a + b, 0),
    };
  });

  const windowLabel = () => {
    const m = report()?.windowMinutes ?? activity.usageMinutes();
    if (m === 60) return 'last hour';
    if (m === 15) return 'last 15 minutes';
    if (m === 180) return 'last 3 hours';
    if (m === 1440) return 'last 24 hours';
    return `last ${m} minutes`;
  };

  return (
    <div class="rl-section rl-activity" id="rate-limit-usage">
      <div class="rl-section-head">
        <div>
          <h3>Activity <span class="tag muted">live · in memory</span></h3>
          <p class="sub">
            Whether limits are being reached. Counters live in the gateway&apos;s memory and start again when it restarts.
          </p>
        </div>
        <div class="rl-activity-controls">
          <div class="rl-seg" role="group" aria-label="Activity window">
            <For each={WINDOW_OPTIONS}>
              {(w) => (
                <button
                  type="button"
                  class={activity.usageMinutes() === w.minutes ? 'active' : ''}
                  aria-pressed={activity.usageMinutes() === w.minutes}
                  onClick={() => activity.setUsageMinutes(w.minutes)}
                >
                  {w.label}
                </button>
              )}
            </For>
          </div>
          <Button variant="ghost" size="sm" onClick={() => void activity.loadUsage()} disabled={activity.usageLoading()}>
            <span class="icon"><IconRefresh /></span> Refresh
          </Button>
        </div>
      </div>

      <div class="card">
        <Show when={activity.usageLoading() && !report()}>
          <p class="loading-hint" role="status">Loading activity…</p>
        </Show>
        <Show when={activity.usageStale() && activity.usageError()}>
          <p class="notice warn" role="status">
            {activity.usageError()}{' '}
            <button type="button" class="rl-link" onClick={() => void activity.loadUsage()}>Retry</button>
          </p>
        </Show>
        <Show when={activity.usageUnavailable()}>
          <p class="hint" role="status">Activity tracking is not enabled in this deployment.</p>
        </Show>

        <Show when={spark()}>
          {(s) => (
            <div class="rl-trend">
              <svg class="rl-trend-svg" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
                <path class="spark-fill rl-trend-refused-fill" d={s().refusedFill} />
                <polyline class="spark-line rl-trend-refused" points={s().refusedLine} />
              </svg>
              <span class="rl-trend-text">
                <span class="ms-label">Refusals</span>{' '}
                <b>{formatNum(s().total)}</b> in {windowLabel()}
              </span>
            </div>
          )}
        </Show>

        <Show when={report()}>
          {(r) => (
            <>
              <h4 class="rl-subhead">{windowLabel()}</h4>
              <div class="mini-stats rl-activity-stats">
                <span class="mini-stat">
                  <span class="ms-label">Decisions</span>
                  <span class="ms-value">{formatNum(r().totals.requests)}</span>
                </span>
                <span class="mini-stat">
                  <span class="ms-label">Admitted</span>
                  <span class="ms-value">{formatNum(r().totals.admitted)}</span>
                </span>
                <span class="mini-stat">
                  <span class="ms-label">Refused</span>
                  <span class="ms-value">{formatNum(r().totals.rejected)}</span>
                </span>
                <span class="mini-stat">
                  <span class="ms-label">Refusal rate</span>
                  <span class="ms-value">{(r().totals.rejectionRate * 100).toFixed(1)}%</span>
                </span>
              </div>

              <div class="rl-activity-head">
                <h4 class="rl-subhead">
                  Traffic by subject <span class="muted">{windowLabel()}</span>
                </h4>
                <div class="rl-seg" role="group" aria-label="Group traffic by">
                  <For each={TAB_OPTIONS}>
                    {(t) => (
                      <button
                        type="button"
                        class={activity.usageTab() === t.id ? 'active' : ''}
                        aria-pressed={activity.usageTab() === t.id}
                        onClick={() => activity.setUsageTab(t.id)}
                      >
                        {t.label}
                      </button>
                    )}
                  </For>
                </div>
              </div>

              <Show when={subjectRows().length === 0}>
                <p class="hint">No traffic in this window.</p>
              </Show>
              <Show when={subjectRows().length > 0}>
                <div class="table-wrap">
                  <table class="data-table t-rl-usage">
                    <thead>
                      <tr>
                        <th>Subject</th>
                        <th class="num">Decisions</th>
                        <th class="num">Refused</th>
                        <th class="num">Avg req/min</th>
                        <th class="num">Last limit seen</th>
                      </tr>
                    </thead>
                    <tbody>
                      <For each={subjectRows().slice(0, 50)}>
                        {(row) => (
                          <tr>
                            <td><span class="rl-subject">{row.key}</span></td>
                            <td class="num">{formatNum(row.requests)}</td>
                            <td class="num">{formatNum(row.rejected)}</td>
                            <td class="num">{row.requestsPerMinute.toFixed(2)}</td>
                            <td class="num">{row.effectiveRpm > 0 ? formatNum(row.effectiveRpm) : '—'}</td>
                          </tr>
                        )}
                      </For>
                    </tbody>
                  </table>
                </div>
              </Show>

              <div class="rl-cumulative">
                <h4 class="rl-subhead">
                  Refusals by limit — since restart <span class="tag muted">cumulative</span>
                </h4>
                <p class="hint">Which limit refused requests since the gateway last started.</p>
                <Show when={r().violations.length === 0}>
                  <p class="hint">No limit has refused a request since the gateway last started.</p>
                </Show>
                <Show when={r().violations.length > 0}>
                  <div class="table-wrap">
                    <table class="data-table t-rl-refusals">
                      <thead>
                        <tr>
                          <th>Limit</th>
                          <th>Control</th>
                          <th class="num">Refused</th>
                        </tr>
                      </thead>
                      <tbody>
                        <For each={r().violations.slice(0, 50)}>
                          {(v) => (
                            <tr>
                              <td>{v.scope}:{v.key}</td>
                              <td>{v.control}</td>
                              <td class="num">{formatNum(v.hits)}</td>
                            </tr>
                          )}
                        </For>
                      </tbody>
                    </table>
                  </div>
                </Show>
              </div>

              <Show when={r().trackerSaturated}>
                <p class="notice warn rl-tracker-note" role="status">
                  <span class="tag warn">incomplete</span> Some activity was not counted because tracker buckets were full.
                </p>
              </Show>
            </>
          )}
        </Show>
      </div>
    </div>
  );
}
