import { Show } from 'solid-js';

export function ControlPlaneCard(props: { data: { modelCount: number; summary: string } | null; error?: string }) {
  return (
    <div class="card glance-card">
      <h3>Control plane</h3>
      <Show when={props.error}><p class="hint error-text">{props.error}</p></Show>
      <Show when={props.data} fallback={<p>—</p>}>
        {(d) => (
          <>
            <p class="hint">{d().summary}</p>
            <div class="mini-stat">
              <span>Registry</span>
              <span>{d().modelCount} models</span>
            </div>
          </>
        )}
      </Show>
    </div>
  );
}
