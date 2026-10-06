import { For, Show, createEffect, createSignal, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { Button, Dialog, DisclosureRow, Field, Select } from '../../components/primitives';
import { IconRefresh } from '../../components/icons';
import { formatNum, formatTime } from '../../domain/format';
import { activateErrorsPage, clearErrors, disposeErrorsPage, exportErrors, useErrorsStore } from '../../stores/errors';

const RANGES = [
  { value: '1h', label: 'Last hour' },
  { value: '24h', label: 'Last 24h' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: 'all', label: 'All time' },
];

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
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="page" id="panel-errors">
      <header class="page-header">
        <div>
          <p class="eyebrow">Reliability</p>
          <h1>Errors</h1>
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

      <div class="filter-row">
        <Field label="Search">
          <input type="search" value={store.search()} onInput={(e) => store.setSearch(e.currentTarget.value)} placeholder="Search errors" />
        </Field>
        <Field label="Model">
          <input type="search" value={store.modelFilter()} onInput={(e) => store.setModelFilter(e.currentTarget.value)} placeholder="Model id" />
        </Field>
        <Field label="Status">
          <input type="search" value={store.statusFilter()} onInput={(e) => store.setStatusFilter(e.currentTarget.value)} placeholder="HTTP status" />
        </Field>
        <Field label="Code">
          <input type="search" value={store.codeFilter()} onInput={(e) => store.setCodeFilter(e.currentTarget.value)} placeholder="Error code" />
        </Field>
        <Field label="Range">
          <Select options={RANGES} value={store.range()} onChange={(v) => { store.setRange(v); void store.load({ force: true }); }} />
        </Field>
      </div>

      <Show when={store.phase() === 'loading'}>
        <p class="loading-hint">Loading error groups…</p>
      </Show>

      <div class="error-list">
        <For each={store.order()}>
          {(id) => {
            const row = () => store.byId[id];
            const expanded = () => store.expandedId() === id;
            return (
              <DisclosureRow
                expanded={expanded()}
                onToggle={() => onExpand(id, !expanded())}
                summary={
                  <span class="error-summary">
                    <strong>{String(row()?.code ?? 'error')}</strong>
                    <span class="error-count">{formatNum(row()?.count)}×</span>
                    <span class="error-time">{formatTime(row()?.lastSeenUtc as string | undefined)}</span>
                    <span>{String(row()?.message ?? '')}</span>
                  </span>
                }
                detail={
                  <div class="occurrence-list">
                    <For each={store.occurrences[id] ?? []}>
                      {(occ) => (
                        <pre class="occurrence-row">{JSON.stringify(occ, null, 2)}</pre>
                      )}
                    </For>
                    <Show when={expanded() && !(store.occurrences[id]?.length)}>
                      <p class="loading-hint">Loading occurrences…</p>
                    </Show>
                  </div>
                }
              />
            );
          }}
        </For>
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
