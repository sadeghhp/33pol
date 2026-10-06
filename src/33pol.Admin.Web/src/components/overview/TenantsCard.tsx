import { For, Show } from 'solid-js';
import type { TenantsCardView } from '../../domain/overviewCards';

export function TenantsCard(props: { data: TenantsCardView | null; error?: string }) {
  return (
    <div class="card glance-card">
      <h3>Tenants</h3>
      <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
      <Show when={props.data} fallback={<p>—</p>}>
        {(d) => (
          <>
            <p class="hint">{d().summary}</p>
            <Show when={d().revokedKeyCount > 0}>
              <p class="hint">{d().revokedKeyCount} revoked key(s)</p>
            </Show>
            <For each={d().topConsumers}>
              {(t) => (
                <div class="mini-stat">
                  <span>{t.slug}</span>
                  <span>{t.requests} req · ${t.cost.toFixed(2)}</span>
                </div>
              )}
            </For>
          </>
        )}
      </Show>
    </div>
  );
}
