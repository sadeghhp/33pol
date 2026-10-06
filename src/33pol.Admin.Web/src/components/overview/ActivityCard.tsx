import { For, Show } from 'solid-js';
import { formatTime } from '../../domain/format';
import type { ActivityCardView } from '../../domain/overviewCards';

export function ActivityCard(props: { data: ActivityCardView | null; error?: string }) {
  return (
    <div class="card glance-card">
      <h3>Activity</h3>
      <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
      <Show when={props.data} fallback={<p>—</p>}>
        {(d) => (
          <>
            <p class="hint">{d().summary}</p>
            <Show when={!d().available}>
              <p class="hint">Audit trail unavailable</p>
            </Show>
            <ul class="activity-list">
              <For each={d().entries}>
                {(entry) => (
                  <li>
                    <strong>{entry.action}</strong>
                    <span>{formatTime(entry.timestamp)}</span>
                    <Show when={entry.detail}><small>{entry.detail}</small></Show>
                  </li>
                )}
              </For>
            </ul>
          </>
        )}
      </Show>
    </div>
  );
}
