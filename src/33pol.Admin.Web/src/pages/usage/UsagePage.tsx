import { For, Show, createEffect, createMemo, createSignal, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { Button, Field, Select } from '../../components/primitives';
import { formatCost, formatNum, formatSharePct, formatTime } from '../../domain/format';
import { apiClient } from '../../stores/auth';
import {
  activateUsagePage,
  applyHashUsageParams,
  disposeUsagePage,
  usagePresetRange,
  useUsageStore,
} from '../../stores/usage';

export default function UsagePage() {
  const store = useUsageStore();
  const [params] = useSearchParams();
  const [keyOptions, setKeyOptions] = createSignal<{ value: string; label: string }[]>([]);
  const [modelOptions, setModelOptions] = createSignal<{ value: string; label: string }[]>([]);

  onMount(async () => {
    applyHashUsageParams(params);
    activateUsagePage();
    try {
      const keys = await apiClient.apiJson<{ items?: Record<string, unknown>[] }>('/admin/api/keys');
      setKeyOptions(
        (keys?.items ?? []).map((k) => ({
          value: String(k.id ?? ''),
          label: String(k.label || k.keyPrefix || k.id || 'key'),
        })),
      );
    } catch {
      /* keys optional for filters */
    }
  });
  onCleanup(() => disposeUsagePage());

  const presetActive = (days: number | 'mtd') => {
    const r = usagePresetRange(days);
    return store.from() === r.from && store.to() === r.to;
  };

  const keyShareRows = createMemo(() => {
    const data = store.keyShares();
    const currency = String(data?.currency ?? store.summary()?.currency ?? 'USD');
    return ((data?.keys as Record<string, unknown>[]) ?? []).map((k) => {
      const anonymous = k.apiKeyId == null;
      const share = Math.max(0, Math.min(1, Number(k.requestShare) || 0));
      return {
        key: String(k.apiKeyId ?? 'anonymous'),
        name: anonymous
          ? 'anonymous'
          : String(k.label || k.keyPrefix || `${String(k.apiKeyId).slice(0, 8)}… (deleted)`),
        assignee: String(k.assignee ?? '—'),
        requestsText: formatNum(k.requests),
        requestShareText: formatSharePct(k.requestShare),
        barStyle: `width:${(share * 100).toFixed(1)}%`,
        tokensText: formatNum(Number(k.promptTokens ?? 0) + Number(k.completionTokens ?? 0)),
        tokenShareText: formatSharePct(k.tokenShare),
        costText: formatCost(k.totalCost, currency),
        costShareText: formatSharePct(k.costShare),
        anonymous,
      };
    });
  });

  createEffect(() => {
    const roll = store.rollup();
    const models = new Set<string>();
    for (const r of roll) {
      const m = String(r.modelId ?? r.model ?? '');
      if (m) models.add(m);
    }
    setModelOptions([...models].map((m) => ({ value: m, label: m })));
  });

  return (
    <section class="page" id="panel-usage">
      <header class="page-header">
        <div>
          <p class="eyebrow">FinOps</p>
          <h1>Usage &amp; cost</h1>
          <p class="page-sub">
            Scoped to your tenant — gateway-wide counts on <strong>Overview</strong> may differ.
          </p>
        </div>
      </header>

      <div class="chip-row" role="group" aria-label="Date presets">
        <button type="button" class={presetActive(7) ? 'preset active' : 'preset'} onClick={() => store.setPreset(7)}>Last 7 days</button>
        <button type="button" class={presetActive(30) ? 'preset active' : 'preset'} onClick={() => store.setPreset(30)}>Last 30 days</button>
        <button type="button" class={presetActive('mtd') ? 'preset active' : 'preset'} onClick={() => store.setPreset('mtd')}>Month to date</button>
      </div>

      <div class="card form-card">
        <div class="filter-row grid">
          <Field label="From">
            <input type="date" value={store.from()} onInput={(e) => store.setFrom(e.currentTarget.value)} />
          </Field>
          <Field label="To">
            <input type="date" value={store.to()} onInput={(e) => store.setTo(e.currentTarget.value)} />
          </Field>
          <Field label="Cost center">
            <input
              type="search"
              placeholder="Exact name or (none)"
              value={store.costCenter()}
              onInput={(e) => store.setCostCenter(e.currentTarget.value)}
            />
          </Field>
          <Field label="API key">
            <Select
              options={[{ value: '', label: 'All keys' }, ...keyOptions()]}
              value={store.apiKeyId()}
              onChange={(v) => store.setApiKeyId(v)}
            />
          </Field>
          <Field label="Model">
            <Select
              options={[{ value: '', label: 'All models' }, ...modelOptions()]}
              value={store.modelId()}
              onChange={(v) => store.setModelId(v)}
            />
          </Field>
        </div>
        <Show when={store.rangeError()}>
          <p class="field-error" role="alert">{store.rangeError()}</p>
        </Show>
        <label class="checkbox-label">
          <input
            type="checkbox"
            checked={store.includeAnonymous()}
            onChange={(e) => store.setIncludeAnonymous(e.currentTarget.checked)}
          />
          Include anonymous usage (public models, no key)
        </label>
        <div class="toolbar" style={{ 'margin-top': 'var(--space-3)' }}>
          <Button onClick={() => store.loadReport()} disabled={!!store.rangeError()}>Apply</Button>
          <span class="spacer" />
          <span class="export-group" role="group" aria-label="Export daily rollups">
            <span class="export-label">Rollups</span>
            <Button variant="ghost" size="sm" onClick={() => void store.exportDataset('rollups', 'json')}>JSON</Button>
            <Button variant="ghost" size="sm" onClick={() => void store.exportDataset('rollups', 'csv')}>CSV</Button>
          </span>
          <span class="export-group" role="group" aria-label="Export billing events">
            <span class="export-label">Events</span>
            <Button variant="ghost" size="sm" onClick={() => void store.exportDataset('events', 'json')}>JSON</Button>
            <Button variant="ghost" size="sm" onClick={() => void store.exportDataset('events', 'csv')}>CSV</Button>
          </span>
        </div>
      </div>

      <Show when={store.phase() === 'loading'}>
        <p class="loading-hint">Loading usage…</p>
      </Show>

      <div class="section-header">
        <div>
          <span class="eyebrow">Contribution</span>
          <h3>{store.modelId() ? `Load on ${store.modelId()} by API key` : 'Load by API key (all models)'}</h3>
        </div>
      </div>
      <Show when={keyShareRows().length > 0}>
        <div class="table-wrap">
          <table class="data-table t-usage-keys">
            <thead>
              <tr>
                <th>Key</th>
                <th>Assignee</th>
                <th>Share of requests</th>
                <th class="num">Requests</th>
                <th class="num">Tokens (share)</th>
                <th class="num">Cost (share)</th>
              </tr>
            </thead>
            <tbody>
              <For each={keyShareRows()}>
                {(row) => (
                  <tr>
                    <td><span classList={{ tag: row.anonymous, muted: row.anonymous }}>{row.name}</span></td>
                    <td>{row.assignee}</td>
                    <td>
                      <div class="share-cell">
                        <span class="load-track"><span class="load-fill" style={row.barStyle} /></span>
                        <span class="share-pct">{row.requestShareText}</span>
                      </div>
                    </td>
                    <td class="num">{row.requestsText}</td>
                    <td class="num">{row.tokensText} <span class="muted">{row.tokenShareText}</span></td>
                    <td class="num">{row.costText} <span class="muted">{row.costShareText}</span></td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
      </Show>
      <Show when={store.keyShares() && keyShareRows().length === 0 && store.phase() !== 'loading'}>
        <p class="hint">No keyed usage matches these filters.</p>
      </Show>

      <div class="card">
        <h2>Daily rollups (max {100})</h2>
        <div class="table-wrap">
          <table class="data-table t-usage-rollups">
            <thead>
              <tr>
                <th>Date</th>
                <th>Model</th>
                <th>Cost center</th>
                <th class="num">Prompt</th>
                <th class="num">Completion</th>
                <th class="num">Cost</th>
                <th class="num">Req</th>
              </tr>
            </thead>
            <tbody>
              <For each={store.rollup()}>
                {(row) => (
                  <tr>
                    <td>{String(row.usageDate ?? row.date ?? '—')}</td>
                    <td>{String(row.modelId ?? row.model ?? '—')}</td>
                    <td>{String(row.costCenter ?? '—')}</td>
                    <td class="num">{formatNum(row.promptTokens)}</td>
                    <td class="num">{formatNum(row.completionTokens)}</td>
                    <td class="num">{formatCost(row.totalCost ?? row.cost, row.currency as string | undefined)}</td>
                    <td class="num">{formatNum(row.requestCount ?? row.count)}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
      </div>

      <div class="card">
        <h2>Events</h2>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Time</th><th>Model</th><th>Cost</th></tr></thead>
            <tbody>
              <For each={store.events()}>
                {(row) => (
                  <tr>
                    <td>{formatTime(row.timestampUtc ?? row.time)}</td>
                    <td>{String(row.modelId ?? '—')}</td>
                    <td>{formatCost(row.cost ?? row.totalCost, row.currency as string | undefined)}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
        <Show when={store.eventsHasMore()}>
          <Button variant="ghost" onClick={() => store.loadMoreEvents()}>Load more events</Button>
        </Show>
      </div>
    </section>
  );
}
