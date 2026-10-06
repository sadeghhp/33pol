import { Show } from 'solid-js';
import { Button, Drawer, Field } from '../../components/primitives';
import {
  useRateLimitDrawersStore,
  useRateLimitsStore,
} from '../../stores/rateLimits';

export function RateLimitTierDrawer() {
  const store = useRateLimitsStore();
  const drawers = useRateLimitDrawersStore();

  return (
    <Drawer
      open={drawers.tierDrawerOpen()}
      title={
        drawers.tierDrawer()?.kind === 'default'
          ? 'Default tier'
          : drawers.tierDrawer()?.isNew
            ? 'New plan tier'
            : `Plan “${drawers.tierDrawer()?.slug}”`
      }
      onClose={() => drawers.closeTierDrawer()}
    >
      <Show when={drawers.tierDrawerError()}>
        <p class="notice error" role="alert">{drawers.tierDrawerError()}</p>
      </Show>
      <Show when={drawers.tierDrawer()}>
        {(t) => (
          <>
            <Show when={t().kind === 'plan'}>
              <Field label="Plan slug">
                <input
                  type="text"
                  value={t().slug}
                  disabled={!store.editable()}
                  onInput={(e) => drawers.updateTierDrawerSlug(e.currentTarget.value)}
                  placeholder="standard"
                />
              </Field>
            </Show>
            <div class="filter-row">
              <Field label="RPM">
                <input
                  type="number"
                  min="1"
                  value={t().rpm}
                  disabled={!store.editable()}
                  onInput={(e) => drawers.updateTierDrawerField('rpm', Number(e.currentTarget.value) || 0)}
                />
              </Field>
              <Field label="Burst">
                <input
                  type="number"
                  min="0"
                  value={t().burst}
                  disabled={!store.editable()}
                  onInput={(e) => drawers.updateTierDrawerField('burst', Number(e.currentTarget.value) || 0)}
                />
              </Field>
              <Field label="Streams" hint="0 means unlimited concurrent streams.">
                <input
                  type="number"
                  min="0"
                  value={t().maxConcurrentStreams}
                  disabled={!store.editable()}
                  onInput={(e) =>
                    drawers.updateTierDrawerField('maxConcurrentStreams', Number(e.currentTarget.value) || 0)}
                />
              </Field>
            </div>
            <div class="drawer-actions">
              <Show when={t().kind === 'plan' && t().originalSlug && store.editable()}>
                <Button variant="ghost" onClick={() => drawers.removePlanFromDrawer()}>Remove plan</Button>
              </Show>
              <span class="spacer" />
              <Button variant="ghost" onClick={() => drawers.closeTierDrawer()}>Cancel</Button>
              <Button variant="primary" disabled={!store.editable()} onClick={() => drawers.applyTierDrawer()}>
                Apply to draft
              </Button>
            </div>
          </>
        )}
      </Show>
    </Drawer>
  );
}
