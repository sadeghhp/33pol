import { For, Show } from 'solid-js';
import type { FinOpsCardView } from '../../domain/overviewCards';

export function FinOpsCard(props: { data: FinOpsCardView | null; error?: string }) {
  return (
    <div class="card glance-card">
      <h3>FinOps</h3>
      <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
      <Show when={props.data} fallback={<p>—</p>}>
        {(d) => (
          <>
            <p class="hint">{d().summary}</p>
            <Show when={d().unpricedCount > 0}>
              <p class="notice warn">{d().unpricedCount} unpriced model(s)</p>
            </Show>
            <For each={d().topModels.slice(0, 3)}>
              {(m) => (
                <div class="mini-stat">
                  <span>{m.key}</span>
                  <span>{d().currency} {m.cost.toFixed(2)}</span>
                </div>
              )}
            </For>
            <For each={d().budgets.slice(0, 2)}>
              {(b) => (
                <div class="mini-stat">
                  <span>{b.name}</span>
                  <span>{Math.round(b.ratio * 100)}% of {b.limit.toFixed(2)}</span>
                </div>
              )}
            </For>
          </>
        )}
      </Show>
    </div>
  );
}
