import { For, Show, onCleanup, onMount } from 'solid-js';
import { Button, Field } from '../../components/primitives';
import { formatCost, formatNum, formatTime } from '../../domain/format';
import { activateUsagePage, disposeUsagePage, useUsageStore } from '../../stores/usage';

export default function UsagePage() {
  const store = useUsageStore();

  onMount(() => activateUsagePage());
  onCleanup(() => disposeUsagePage());

  return (
    <section class="page" id="panel-usage">
      <header class="page-header">
        <div>
          <p class="eyebrow">FinOps</p>
          <h1>Usage</h1>
        </div>
      </header>

      <div class="filter-row">
        <Field label="From">
          <input type="datetime-local" value={store.from()} onInput={(e) => store.setFrom(e.currentTarget.value)} />
        </Field>
        <Field label="To">
          <input type="datetime-local" value={store.to()} onInput={(e) => store.setTo(e.currentTarget.value)} />
        </Field>
        <Button onClick={() => store.loadReport()}>Load report</Button>
      </div>

      <Show when={store.phase() === 'loading'}>
        <p class="loading-hint">Loading usage…</p>
      </Show>

      <div class="card">
        <h2>Rollup (max 100)</h2>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Model</th><th>Requests</th><th>Cost</th></tr></thead>
            <tbody>
              <For each={store.rollup()}>
                {(row) => (
                  <tr>
                    <td>{String(row.modelId ?? row.model ?? '—')}</td>
                    <td>{formatNum(row.requestCount ?? row.count)}</td>
                    <td>{formatCost(row.cost ?? row.totalCost, row.currency as string | undefined)}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
      </div>

      <div class="card">
        <h2>Events (max 200 per load)</h2>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Time</th><th>Model</th><th>Cost</th></tr></thead>
            <tbody>
              <For each={store.events()}>
                {(row) => (
                  <tr>
                    <td>{formatTime(row.timestampUtc ?? row.time)}</td>
                    <td>{String(row.modelId ?? '—')}</td>
                    <td>{formatCost(row.cost, row.currency as string | undefined)}</td>
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
