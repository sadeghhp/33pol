import { For, Show, createSignal, onCleanup, onMount } from 'solid-js';
import { Button, Dialog, DisclosureRow, Field, Select } from '../../components/primitives';
import { IconRefresh } from '../../components/icons';
import { formatTime } from '../../domain/format';
import { activateLogsPage, clearLogs, disposeLogsPage, useLogsStore } from '../../stores/logs';

const LEVELS = [
  { value: '', label: 'All levels' },
  { value: 'Information', label: 'Information' },
  { value: 'Warning', label: 'Warning' },
  { value: 'Error', label: 'Error' },
];

export default function LogsPage() {
  const store = useLogsStore();
  const [confirmClear, setConfirmClear] = createSignal(false);
  const [busy, setBusy] = createSignal(false);

  onMount(() => {
    activateLogsPage();
    void store.load({ force: true });
  });
  onCleanup(() => disposeLogsPage());

  const runClear = async () => {
    setBusy(true);
    try {
      await clearLogs();
      setConfirmClear(false);
    } finally {
      setBusy(false);
    }
  };

  const pageStart = () => store.offset() + 1;
  const pageEnd = () => Math.min(store.offset() + store.pageSize(), store.total());

  return (
    <section class="page" id="panel-logs" data-solid-root="logs">
      <header class="page-header">
        <div>
          <p class="eyebrow">Operations</p>
          <h1>Logs</h1>
        </div>
        <div class="page-actions">
          <Button variant="ghost" size="sm" onClick={() => setConfirmClear(true)} disabled={store.order().length === 0}>
            Clear buffer
          </Button>
          <Button variant="ghost" size="sm" onClick={() => store.load({ force: true })}>
            <span class="icon"><IconRefresh /></span> Refresh
          </Button>
        </div>
      </header>

      <div class="filter-row">
        <Field label="Search">
          <input
            type="search"
            placeholder="Search logs"
            value={store.search()}
            onInput={(e) => store.setSearch(e.currentTarget.value)}
          />
        </Field>
        <Field label="Level">
          <Select options={LEVELS} value={store.level()} onChange={(v) => { store.setLevel(v); void store.load({ force: true }); }} />
        </Field>
        <label class="checkbox-label">
          <input type="checkbox" checked={store.autoRefresh()} onChange={(e) => store.setAutoRefresh(e.currentTarget.checked)} />
          Auto-refresh (10s)
        </label>
      </div>

      <Show when={store.phase() === 'loading'}>
        <p class="loading-hint">Loading logs…</p>
      </Show>

      <div class="pager-row">
        <Button variant="ghost" size="sm" disabled={store.offset() === 0} onClick={() => store.setOffset(store.offset() - store.pageSize())}>
          Previous
        </Button>
        <span class="pager-meta">
          {store.total() > 0 ? `${pageStart()}–${pageEnd()} of ${store.total()}` : 'No entries'}
        </span>
        <Button
          variant="ghost"
          size="sm"
          disabled={store.offset() + store.pageSize() >= store.total()}
          onClick={() => store.setOffset(store.offset() + store.pageSize())}
        >
          Next
        </Button>
      </div>

      <div class="log-list">
        <For each={store.order()}>
          {(id) => {
            const row = () => store.byId[id];
            const expanded = () => store.expandedId() === id;
            return (
              <DisclosureRow
                expanded={expanded()}
                onToggle={() => store.setExpandedId(expanded() ? null : id)}
                summary={
                  <span class="log-summary">
                    <span class="log-level">{String(row()?.level ?? '')}</span>
                    <span class="log-time">{formatTime(row()?.timestampUtc as string | undefined)}</span>
                    <span>{String(row()?.message ?? '')}</span>
                  </span>
                }
                detail={
                  <pre class="log-detail">{JSON.stringify(row(), null, 2)}</pre>
                }
              />
            );
          }}
        </For>
      </div>

      <Dialog open={confirmClear()} title="Clear the log buffer?" onClose={() => setConfirmClear(false)}>
        <p>Discards every entry currently held in memory. This cannot be undone.</p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setConfirmClear(false)}>Cancel</Button>
          <Button variant="danger" onClick={runClear} disabled={busy()}>Clear</Button>
        </div>
      </Dialog>
    </section>
  );
}
