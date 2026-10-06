import { For, Show } from 'solid-js';
import { Button, Drawer, Field, Select } from '../../components/primitives';
import { IconPlus, IconX } from '../../components/icons';
import { scopeInfo } from '../../domain/rateLimitEdit';
import { formatNum } from '../../domain/format';
import {
  RATE_LIMIT_SCOPES,
  useRateLimitDrawersStore,
  useRateLimitsStore,
} from '../../stores/rateLimits';

const DAY_LABELS = [
  ['mon', 'Mon'],
  ['tue', 'Tue'],
  ['wed', 'Wed'],
  ['thu', 'Thu'],
  ['fri', 'Fri'],
  ['sat', 'Sat'],
  ['sun', 'Sun'],
] as const;

export function RateLimitRuleDrawer() {
  const store = useRateLimitsStore();
  const drawers = useRateLimitDrawersStore();
  const info = () => scopeInfo(drawers.ruleDrawer()?.scope ?? '');

  const previewValid = () => drawers.windowPreview()?.valid ?? drawers.windowPreview()?.Valid;

  return (
    <Drawer
      open={drawers.ruleDrawerOpen()}
      title={info().singleton ? info().name : `${info().short} “${drawers.ruleDrawer()?.target ?? ''}”`}
      onClose={() => drawers.closeRuleDrawer()}
    >
      <Show when={!drawers.windowOpen() && drawers.ruleDrawerError()}>
        <p class="notice error" role="alert">{drawers.ruleDrawerError()}</p>
      </Show>
      <Show when={drawers.ruleDrawer()} keyed>
        {(rule) => (
          <Show when={!drawers.windowOpen()}>
          <>
            <label class="rl-switch sm">
              <input
                type="checkbox"
                role="switch"
                checked={rule.enabled}
                disabled={!store.editable()}
                onChange={(e) => drawers.updateRuleDrawerField('enabled', e.currentTarget.checked)}
              />
              <span class="rl-switch-track" />
              <span>Rule enabled</span>
            </label>
            <div class="filter-row">
              <Field label="Scope">
                <Select
                  options={RATE_LIMIT_SCOPES.map((s) => ({ value: s.value, label: s.label }))}
                  value={rule.scope}
                  disabled
                  onChange={() => {}}
                />
              </Field>
              <Field label="Target">
                <input type="text" value={rule.target} disabled />
              </Field>
            </div>
            <div class="filter-row rl-bignums">
              <Field label="RPM">
                <input
                  type="number"
                  min="0"
                  value={rule.rpm}
                  disabled={!store.editable()}
                  onInput={(e) => drawers.updateRuleDrawerField('rpm', Number(e.currentTarget.value) || 0)}
                />
              </Field>
              <Field label="Burst">
                <input
                  type="number"
                  min="0"
                  value={rule.burst}
                  disabled={!store.editable()}
                  onInput={(e) => drawers.updateRuleDrawerField('burst', Number(e.currentTarget.value) || 0)}
                />
              </Field>
              <Show when={!info().rateOnly}>
                <Field label="Streams">
                  <input
                    type="number"
                    min="0"
                    value={rule.maxConcurrentStreams}
                    disabled={!store.editable()}
                    onInput={(e) =>
                      drawers.updateRuleDrawerField('maxConcurrentStreams', Number(e.currentTarget.value) || 0)}
                  />
                </Field>
              </Show>
            </div>

            <div class="rl-section">
              <div class="rl-section-head">
                <h4 class="rl-h4">Schedule windows</h4>
                <Show when={store.editable()}>
                  <Button variant="ghost" size="sm" onClick={() => drawers.openWindowEditor(-1)}>
                    <span class="icon"><IconPlus /></span> Add window
                  </Button>
                </Show>
              </div>
              <Show when={rule.schedule.length === 0}>
                <p class="hint">No windows — the base tier applies at all times.</p>
              </Show>
              <For each={rule.schedule}>
                {(w, i) => (
                  <div class="card rl-window-row">
                    <button type="button" class="rl-link" onClick={() => drawers.openWindowEditor(i())}>
                      {w.name || '(unnamed)'}
                    </button>
                    <span class="hint">{w.kind} · {formatNum(w.rpm)} rpm</span>
                    <Show when={store.editable()}>
                      <Button variant="ghost" size="sm" onClick={() => drawers.removeWindow(i())}>
                        <span class="icon"><IconX /></span>
                      </Button>
                    </Show>
                  </div>
                )}
              </For>
            </div>

            <div class="drawer-actions">
              <Show when={store.editable()}>
                <Button variant="ghost" onClick={() => drawers.deleteRuleFromDrawer()}>Delete rule</Button>
              </Show>
              <span class="spacer" />
              <Button variant="ghost" onClick={() => drawers.closeRuleDrawer()}>Cancel</Button>
              <Button variant="primary" disabled={!store.editable()} onClick={() => drawers.applyRuleDrawer()}>
                Apply to draft
              </Button>
            </div>
          </>
          </Show>
        )}
      </Show>

      <Show when={drawers.windowForm()} keyed>
        {(form) => (
          <Show when={drawers.windowOpen()}>
          <>
            <h3 id="rl-window-title">{drawers.windowEditIndex() >= 0 ? 'Edit window' : 'New window'}</h3>
            <Show when={drawers.windowError()}>
              <p class="notice error" role="alert">{drawers.windowError()}</p>
            </Show>
            <Field label="Name">
              <input
                type="text"
                value={form.name}
                disabled={!store.editable()}
                onInput={(e) => drawers.updateWindowForm({ name: e.currentTarget.value })}
              />
            </Field>
            <Field label="Kind">
              <Select
                options={[
                  { value: 'weekly', label: 'Weekly recurring' },
                  { value: 'once', label: 'One-off' },
                ]}
                value={form.kind}
                disabled={!store.editable()}
                onChange={(v) => drawers.updateWindowForm({ kind: v })}
              />
            </Field>
            <label class="checkbox-row">
              <input
                type="checkbox"
                checked={form.suspend}
                disabled={!store.editable()}
                onChange={(e) => drawers.updateWindowForm({ suspend: e.currentTarget.checked })}
              />
              Pause the rule (suspend limits)
            </label>
            <Show when={!form.suspend}>
              <div class="filter-row">
                <Field label="RPM">
                  <input
                    type="number"
                    min="0"
                    value={form.rpm}
                    disabled={!store.editable()}
                    onInput={(e) => drawers.updateWindowForm({ rpm: Number(e.currentTarget.value) || 0 })}
                  />
                </Field>
                <Field label="Burst">
                  <input
                    type="number"
                    min="0"
                    value={form.burst}
                    disabled={!store.editable()}
                    onInput={(e) => drawers.updateWindowForm({ burst: Number(e.currentTarget.value) || 0 })}
                  />
                </Field>
                <Field label="Streams">
                  <input
                    type="number"
                    min="0"
                    value={form.maxConcurrentStreams}
                    disabled={!store.editable()}
                    onInput={(e) =>
                      drawers.updateWindowForm({ maxConcurrentStreams: Number(e.currentTarget.value) || 0 })}
                  />
                </Field>
              </div>
            </Show>
            <Show when={form.kind === 'weekly'}>
              <div class="rl-chips" role="group" aria-label="Days">
                <For each={DAY_LABELS}>
                  {([day, label]) => (
                    <button
                      type="button"
                      class={`preset${(form.days || []).includes(day) ? ' active' : ''}`}
                      disabled={!store.editable()}
                      onClick={() => drawers.toggleWindowDay(day)}
                    >
                      {label}
                    </button>
                  )}
                </For>
              </div>
              <div class="filter-row">
                <Field label="Start">
                  <input
                    type="time"
                    value={form.start ?? ''}
                    disabled={!store.editable()}
                    onInput={(e) => drawers.updateWindowForm({ start: e.currentTarget.value })}
                  />
                </Field>
                <Field label="End">
                  <input
                    type="time"
                    value={form.end ?? ''}
                    disabled={!store.editable()}
                    onInput={(e) => drawers.updateWindowForm({ end: e.currentTarget.value })}
                  />
                </Field>
              </div>
            </Show>
            <Show when={form.kind === 'once'}>
              <Field label="From (UTC ISO)">
                <input
                  type="datetime-local"
                  disabled={!store.editable()}
                  onInput={(e) => {
                    const v = e.currentTarget.value;
                    drawers.updateWindowForm({ from: v ? new Date(v).toISOString() : null });
                  }}
                />
              </Field>
            </Show>
            <Show when={drawers.windowPreview()}>
              <p class="hint" role="status">
                {previewValid() === false
                  ? (drawers.windowPreview()?.error ?? drawers.windowPreview()?.Error ?? 'Invalid window')
                  : 'Window looks valid'}
                {(drawers.windowPreview()?.activeNow ?? drawers.windowPreview()?.ActiveNow) ? ' · active now' : ''}
              </p>
            </Show>
            <div class="drawer-actions">
              <Button variant="ghost" onClick={() => drawers.closeWindowEditor()}>Back to rule</Button>
              <Button variant="primary" disabled={!store.editable()} onClick={() => drawers.applyWindowEditor()}>
                {drawers.windowEditIndex() >= 0 ? 'Update window' : 'Add window'}
              </Button>
            </div>
          </>
          </Show>
        )}
      </Show>
    </Drawer>
  );
}
