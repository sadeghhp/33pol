import { For, Show } from 'solid-js';
import type { PolicyCardView } from '../../domain/overviewCards';

export function PolicyCard(props: { data: PolicyCardView | null; error?: string }) {
  return (
    <div class="card glance-card">
      <h3>Policy</h3>
      <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
      <Show when={props.data} fallback={<p>—</p>}>
        {(d) => (
          <>
            <p class="hint">{d().summary}</p>
            <For each={d().grantDenials.slice(0, 3)}>
              {(row) => (
                <div class="mini-stat">
                  <span>{row.key}</span>
                  <span>{row.count} denials</span>
                </div>
              )}
            </For>
            <Show when={d().unknownModels.length > 0}>
              <p class="hint">{d().unknownModels.length} unknown model(s) requested</p>
            </Show>
          </>
        )}
      </Show>
    </div>
  );
}
