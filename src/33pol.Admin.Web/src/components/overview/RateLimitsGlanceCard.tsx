import { For, Show } from 'solid-js';
import { Button } from '../primitives';
import type { RateLimitsGlanceView } from '../../domain/overviewCards';

export function RateLimitsGlanceCard(props: {
  data: RateLimitsGlanceView | null;
  error?: string;
  onOpen: () => void;
}) {
  return (
    <Show when={!props.data?.hidden}>
      <div class="card rate-limits-card">
        <h3>Rate limits</h3>
        <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
        <Show when={props.data} fallback={<p>—</p>}>
          {(d) => (
            <>
              <p class="hint">{d().summary}</p>
              <Show when={d().refusedLastHour > 0}>
                <p class="hint">{(d().refusalShare * 100).toFixed(1)}% refusal share</p>
              </Show>
              <div class="rl-ov-limits">
                <For each={d().limits}>
                  {(limit) => (
                    <div class="rl-ov-limit">
                      <span class="rl-ov-limit-label">{limit.label}</span>
                      <div class="load-track" aria-hidden="true">
                        <div
                          class="load-fill"
                          style={{ width: `${Math.min(100, (limit.utilization ?? 0) * 100)}%` }}
                        />
                      </div>
                      <span class="tag">{limit.refused} refused</span>
                    </div>
                  )}
                </For>
              </div>
              <Button variant="ghost" size="sm" onClick={() => props.onOpen()}>Open settings</Button>
            </>
          )}
        </Show>
      </div>
    </Show>
  );
}
