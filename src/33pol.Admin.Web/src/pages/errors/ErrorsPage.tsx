import { For, Show, createEffect, createSignal, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { Button, Dialog, Field, Select } from '../../components/primitives';
import { IconRefresh } from '../../components/icons';
import { formatNum, formatTime } from '../../domain/format';
import {
  activateErrorsPage,
  clearErrors,
  disposeErrorsPage,
  exportErrors,
  facetOptions,
  useErrorsStore,
} from '../../stores/errors';
import { setErrorsAutoRefresh } from '../../stores/connection';

const RANGES = [
  { value: '1h', label: 'Last hour' },
  { value: '24h', label: 'Last 24h' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: 'all', label: 'All time' },
];

const LEVELS = [
  { value: 'all', label: 'All severities' },
  { value: 'warning', label: 'Warning and above' },
  { value: 'error', label: 'Errors only' },
  { value: 'critical', label: 'Critical only' },
];

function endpointText(row: Record<string, unknown> | undefined): string {
  if (!row) return '—';
  const endpoint = [row.endpointMethod, row.endpointPath].filter(Boolean).join(' ');
  return endpoint || '—';
}

export default function ErrorsPage() {
  const store = useErrorsStore();
  const [params, setParams] = useSearchParams();
  const [confirmClear, setConfirmClear] = createSignal(false);
  const [busy, setBusy] = createSignal(false);

  onMount(() => {
    activateErrorsPage();
    if (params.model) store.setModelFilter(String(params.model));
    if (params.status) store.setStatusFilter(String(params.status));
    if (params.code) store.setCodeFilter(String(params.code));
    if (params.range && RANGES.some((r) => r.value === params.range)) store.setRange(String(params.range));
    void store.load({ force: true });
  });
  onCleanup(() => disposeErrorsPage());

  createEffect(() => {
    setParams({
      model: store.modelFilter() || undefined,
      status: store.statusFilter() || undefined,
      code: store.codeFilter() || undefined,
      range: store.range() !== '24h' ? store.range() : undefined,
    });
  });

  createEffect(() => {
    setErrorsAutoRefresh(store.autoRefresh());
  });

  onCleanup(() => setErrorsAutoRefresh(false));

  const onExpand = (id: string, open: boolean) => {
    store.setExpandedId(open ? id : null);
    if (open) void store.loadOccurrences(id);
  };

  const runClear = async () => {
    setBusy(true);
    try {
      await clearErrors();
      setConfirmClear(false);
      await store.load({ force: true });
      void store.loadFacets();
    } finally {
      setBusy(false);
    }
  };

  const modelOptions = () => facetOptions(store.facets()?.models);
  const statusOptions = () => facetOptions(store.facets()?.statusCodes);
  const codeOptions = () => facetOptions(store.facets()?.errorCodes);

  return (
    <section class="page" id="panel-errors">
      <header class="page-header">
        <div>
          <p class="eyebrow">Reliability</p>
          <h1>Errors</h1>
          <Show when={store.groupsTotal() > 0}>
            <p class="page-sub">{formatNum(store.groupsTotal())} error groups in this window</p>
          </Show>
          <Show when={store.facetsError()}>
            <p class="page-sub">The model and status filter lists could not be loaded; free-text search still works.</p>
          </Show>
        </div>
        <div class="page-actions">
          <Button variant="ghost" size="sm" onClick={() => void exportErrors('json')}>Export JSON</Button>
          <Button variant="ghost" size="sm" onClick={() => void exportErrors('csv')}>Export CSV</Button>
          <Button variant="ghost" size="sm" onClick={() => setConfirmClear(true)}>Clear all</Button>
          <Button variant="ghost" size="sm" onClick={() => store.load({ force: true })}>
            <span class="icon"><IconRefresh /></span> Refresh
          </Button>
        </div>
      </header>

      <div class="chip-row">
        <For each={RANGES}>
          {(r) => (
            <button
              type="button"
              class={store.range() === r.value ? 'preset active' : 'preset'}
              onClick={() => {
                store.setRange(r.value);
                void store.loadFacets();
                void store.load({ force: true });
              }}
            >
              {r.label}
            </button>
          )}
        </For>
      </div>

      <div class="filter-row toolbar">
        <Field label="Search">
          <input
            type="search"
            value={store.search()}
            onInput={(e) => {
              store.setSearch(e.currentTarget.value);
              store.applyFilters();
            }}
            placeholder="message, exception, model, request id…"
          />
        </Field>
        <Field label="Model">
          <Select
            options={[{ value: '', label: 'All models' }, ...modelOptions()]}
            value={store.modelFilter()}
            onChange={(v) => {
              store.setModelFilter(v);
              store.applyFilters();
            }}
          />
        </Field>
        <Field label="Status">
          <Select
            options={[{ value: '', label: 'All statuses' }, ...statusOptions()]}
            value={store.statusFilter()}
            onChange={(v) => {
              store.setStatusFilter(v);
              store.applyFilters();
            }}
          />
        </Field>
        <Field label="Code">
          <Select
            options={[{ value: '', label: 'All codes' }, ...codeOptions()]}
            value={store.codeFilter()}
            onChange={(v) => {
              store.setCodeFilter(v);
              store.applyFilters();
            }}
          />
        </Field>
        <Field label="Severity">
          <Select
            options={LEVELS}
            value={store.levelFilter()}
            onChange={(v) => {
              store.setLevelFilter(v);
              store.applyFilters();
            }}
          />
        </Field>
        <label class="checkbox-label">
          <input
            type="checkbox"
            checked={store.autoRefresh()}
            onChange={(e) => store.setAutoRefresh(e.currentTarget.checked)}
          />
          Auto-refresh
        </label>
      </div>

      <Show when={store.phase() === 'loading'}>
        <p class="loading-hint">Loading error groups…</p>
      </Show>

      <div class="table-wrap">
        <table class="data-table t-errors">
          <thead>
            <tr>
              <th>Last seen</th>
              <th>Severity</th>
              <th>Count</th>
              <th>Message</th>
              <th>Model</th>
              <th>Status</th>
              <th>Code</th>
              <th>Endpoint</th>
            </tr>
          </thead>
          <For each={store.order()}>
            {(id) => {
              const row = () => store.byId[id];
              const expanded = () => store.expandedId() === id;
              return (
                <>
                  <tbody>
                    <tr
                      class="request-row"
                      classList={{ expanded: expanded() }}
                      role="button"
                      tabIndex={0}
                      aria-expanded={expanded()}
                      onClick={() => onExpand(id, !expanded())}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          onExpand(id, !expanded());
                        }
                      }}
                    >
                      <td>{formatTime(row()?.lastSeenUtc as string | undefined)}</td>
                      <td><span class={`tag ${row()?.level ? `is-${row()?.level}` : ''}`}>{String(row()?.level ?? '—')}</span></td>
                      <td>{formatNum(row()?.count)}</td>
                      <td class="col-truncate" title={String(row()?.message ?? '')}>{String(row()?.message ?? '')}</td>
                      <td class="col-truncate">{String(row()?.modelId ?? '—')}</td>
                      <td>{row()?.statusCode ? String(row()?.statusCode) : '—'}</td>
                      <td class="col-truncate">{String(row()?.errorCode ?? '—')}</td>
                      <td class="col-truncate" title={endpointText(row())}>{endpointText(row())}</td>
                    </tr>
                    <Show when={expanded()}>
                      <tr class="request-detail-row">
                        <td colspan="8">
                          <p class="error-detail-message">{String(row()?.message ?? '')}</p>
                          <div class="occurrence-list">
                            <For each={store.occurrences[id] ?? []}>
                              {(occ) => (
                                <pre class="occurrence-row">{JSON.stringify(occ, null, 2)}</pre>
                              )}
                            </For>
                            <Show when={!(store.occurrences[id]?.length)}>
                              <p class="loading-hint">Loading occurrences…</p>
                            </Show>
                          </div>
                        </td>
                      </tr>
                    </Show>
                  </tbody>
                </>
              );
            }}
          </For>
        </table>
      </div>

      <Dialog open={confirmClear()} title="Clear all recorded errors?" onClose={() => setConfirmClear(false)}>
        <p>
          Deletes every stored error record and resets the gateway error counters. This cannot be undone —
          durable logs written by configured log providers are unaffected.
        </p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setConfirmClear(false)}>Cancel</Button>
          <Button variant="danger" onClick={runClear} disabled={busy()}>Clear errors</Button>
        </div>
      </Dialog>
    </section>
  );
}
