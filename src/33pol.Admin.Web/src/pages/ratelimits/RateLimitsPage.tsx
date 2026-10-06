import { For, Show, createEffect, createSignal, onCleanup, onMount } from 'solid-js';
import { Button, Dialog, Field, Select } from '../../components/primitives';
import { IconFileText, IconPlus, IconRefresh, IconX } from '../../components/icons';
import {
  RATE_LIMIT_SCOPES,
  activateRateLimitsPage,
  disposeRateLimitsPage,
  useRateLimitsStore,
} from '../../stores/rateLimits';

interface HelpSection {
  id: string;
  title: string;
  intro?: string;
  items?: Array<{ term?: string; text?: string; example?: string }>;
}

interface RateLimitHelpLang {
  ui?: { guide?: string; guideSub?: string; example?: string };
  sections?: HelpSection[];
}

type RateLimitHelpRoot = Record<string, RateLimitHelpLang>;

async function fetchRateLimitHelp(): Promise<RateLimitHelpLang | null> {
  try {
    const res = await fetch('/admin/admin-rate-limit-help.js');
    if (!res.ok) return null;
    const text = await res.text();
    const sandbox: { RateLimitHelp?: RateLimitHelpRoot } = {};
    // Help script assigns window.RateLimitHelp; run against a stub window.
    const run = new Function('window', text);
    run(sandbox);
    return sandbox.RateLimitHelp?.en ?? null;
  } catch {
    return null;
  }
}

export default function RateLimitsPage(props: { visible: boolean }) {
  const store = useRateLimitsStore();
  const [helpOpen, setHelpOpen] = createSignal(false);
  const [helpContent, setHelpContent] = createSignal<RateLimitHelpLang | null>(null);
  const [helpLoading, setHelpLoading] = createSignal(false);
  const [newScope, setNewScope] = createSignal('model');
  const [newTarget, setNewTarget] = createSignal('');
  const [newRpm, setNewRpm] = createSignal('60');
  const [newBurst, setNewBurst] = createSignal('0');

  onMount(() => {
    if (props.visible) activateRateLimitsPage();
  });

  onCleanup(() => disposeRateLimitsPage());

  createEffect(() => {
    if (props.visible) {
      activateRateLimitsPage();
      void store.load();
    }
  });

  createEffect(() => {
    if (!helpOpen()) return;
    if (helpContent() || helpLoading()) return;
    setHelpLoading(true);
    void fetchRateLimitHelp()
      .then((content) => setHelpContent(content))
      .finally(() => setHelpLoading(false));
  });

  const readNumber = (value: string, fallback = 0) => {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  };

  const addRule = () => {
    const target = newTarget().trim();
    if (!target) return;
    store.addRule({
      scope: newScope(),
      target,
      rpm: readNumber(newRpm(), 60),
      burst: readNumber(newBurst(), 0),
    });
    setNewTarget('');
    setNewRpm('60');
    setNewBurst('0');
  };

  const scopeLabel = (scope: string) =>
    RATE_LIMIT_SCOPES.find((s) => s.value === scope)?.label ?? scope;

  return (
    <div class="rate-limits-panel rl-page">
      <Show when={store.loadError()}>
        <p class="notice error" role="alert">
          {store.loadError()}{' '}
          <button type="button" class="rl-link" onClick={() => void store.load({ force: true })}>
            Retry
          </button>
        </p>
      </Show>

      <Show when={store.loading() && !store.draft()}>
        <p class="loading-hint">Loading rate limits…</p>
      </Show>

      <Show when={store.draft()}>
        {(cfg) => (
          <>
            <Show when={store.readOnlyReason()}>
              <p class="notice warn" role="status">
                <span class="tag muted">read-only</span> {store.readOnlyReason()} Rules can still be inspected.
              </p>
            </Show>

            <div class="card rl-status">
              <label class="rl-switch" title="Enforce rate limits">
                <input
                  type="checkbox"
                  role="switch"
                  checked={cfg().enabled}
                  disabled={store.locked()}
                  aria-label="Enforce rate limits"
                  onChange={(e) => store.setEnabled(e.currentTarget.checked)}
                />
                <span class="rl-switch-track" />
              </label>
              <div class="rl-status-text">
                <div class="rl-status-title">{cfg().enabled ? 'Rate limits enforced' : 'Rate limits disabled'}</div>
                <div class="rl-status-sub">
                  <label class="rl-switch sm" title="Adapt model limits to load">
                    <input
                      type="checkbox"
                      role="switch"
                      checked={cfg().adaptiveEnabled}
                      disabled={store.locked()}
                      aria-label="Adapt model limits to load"
                      onChange={(e) => store.setAdaptiveEnabled(e.currentTarget.checked)}
                    />
                    <span class="rl-switch-track" />
                    <span>Adaptive load shedding</span>
                  </label>
                </div>
              </div>
              <div class="rl-status-actions">
                <Button variant="ghost" size="sm" onClick={() => setHelpOpen(true)}>
                  <span class="icon"><IconFileText /></span> Rate limits, explained
                </Button>
                <Button variant="ghost" size="sm" onClick={() => void store.load({ force: true })} disabled={store.loading()}>
                  <span class="icon"><IconRefresh /></span> Reload
                </Button>
              </div>
            </div>

            <div class="card" id="rl-default">
              <span class="eyebrow">Default tier</span>
              <h3>Default allowance</h3>
              <p class="hint">Every tenant gets this tier unless a plan or rule overrides it.</p>
              <div class="filter-row">
                <Field label="RPM">
                  <input
                    type="number"
                    min="1"
                    value={cfg().default.rpm}
                    disabled={store.locked()}
                    onInput={(e) => store.setDefaultTier('rpm', readNumber(e.currentTarget.value, cfg().default.rpm))}
                  />
                </Field>
                <Field label="Burst">
                  <input
                    type="number"
                    min="0"
                    value={cfg().default.burst}
                    disabled={store.locked()}
                    onInput={(e) => store.setDefaultTier('burst', readNumber(e.currentTarget.value, cfg().default.burst))}
                  />
                </Field>
                <Field label="Streams" hint="0 means unlimited concurrent streams.">
                  <input
                    type="number"
                    min="0"
                    value={cfg().default.maxConcurrentStreams}
                    disabled={store.locked()}
                    onInput={(e) =>
                      store.setDefaultTier(
                        'maxConcurrentStreams',
                        readNumber(e.currentTarget.value, cfg().default.maxConcurrentStreams),
                      )}
                  />
                </Field>
              </div>
            </div>

            <div class="rl-section" id="rl-rules">
              <div class="rl-section-head">
                <div>
                  <h3>Rules</h3>
                  <p class="sub">Scoped limits on models, tenants, or keys. Every rule that applies must admit a request.</p>
                </div>
              </div>

              <Show when={!store.locked()}>
                <div class="card rl-new-rule">
                  <h4 class="rl-h4">Add rule</h4>
                  <div class="filter-row">
                    <Field label="Scope">
                      <Select
                        options={RATE_LIMIT_SCOPES.map((s) => ({ value: s.value, label: s.label }))}
                        value={newScope()}
                        onChange={setNewScope}
                      />
                    </Field>
                    <Field label="Target">
                      <input
                        type="text"
                        placeholder="Model id, tenant, key, or subject|model"
                        value={newTarget()}
                        onInput={(e) => setNewTarget(e.currentTarget.value)}
                      />
                    </Field>
                    <Field label="RPM">
                      <input type="number" min="0" value={newRpm()} onInput={(e) => setNewRpm(e.currentTarget.value)} />
                    </Field>
                    <Field label="Burst">
                      <input type="number" min="0" value={newBurst()} onInput={(e) => setNewBurst(e.currentTarget.value)} />
                    </Field>
                    <div class="field">
                      <label>&nbsp;</label>
                      <Button onClick={addRule} disabled={!newTarget().trim()}>
                        <span class="icon"><IconPlus /></span> Add rule
                      </Button>
                    </div>
                  </div>
                </div>
              </Show>

              <div class="table-wrap rl-rules card">
                <table class="data-table rl-rules-table">
                  <thead>
                    <tr>
                      <th>On</th>
                      <th>Scope</th>
                      <th>Target</th>
                      <th>RPM</th>
                      <th>Burst</th>
                      <th>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    <For each={cfg().rules}>
                      {(rule, index) => (
                        <tr>
                          <td>
                            <label class="rl-switch sm">
                              <input
                                type="checkbox"
                                role="switch"
                                checked={rule.enabled}
                                disabled={store.locked()}
                                aria-label={`Enable rule ${rule.target}`}
                                onChange={(e) => store.setRuleField(index(), 'enabled', e.currentTarget.checked)}
                              />
                              <span class="rl-switch-track" />
                            </label>
                          </td>
                          <td>{scopeLabel(rule.scope)}</td>
                          <td>
                            <input
                              type="text"
                              value={rule.target}
                              disabled={store.locked()}
                              onInput={(e) => store.setRuleField(index(), 'target', e.currentTarget.value)}
                            />
                          </td>
                          <td>
                            <input
                              type="number"
                              min="0"
                              value={rule.rpm}
                              disabled={store.locked()}
                              onInput={(e) => store.setRuleField(index(), 'rpm', readNumber(e.currentTarget.value, rule.rpm))}
                            />
                          </td>
                          <td>
                            <input
                              type="number"
                              min="0"
                              value={rule.burst}
                              disabled={store.locked()}
                              onInput={(e) => store.setRuleField(index(), 'burst', readNumber(e.currentTarget.value, rule.burst))}
                            />
                          </td>
                          <td class="row-actions">
                            <Show when={!store.locked()}>
                              <Button variant="ghost" size="sm" onClick={() => store.deleteRule(index())}>
                                <span class="icon"><IconX /></span> Delete
                              </Button>
                            </Show>
                          </td>
                        </tr>
                      )}
                    </For>
                  </tbody>
                </table>
                <Show when={cfg().rules.length === 0}>
                  <p class="hint">No rules yet. Tenants use their tier until you add a scoped rule.</p>
                </Show>
              </div>
            </div>

            <Show when={store.dirty()}>
              <div class="rl-savebar" role="region" aria-label="Unsaved rate-limit changes">
                <div class="rl-savebar-row">
                  <span class="rl-dot accent" aria-hidden="true" />
                  <div class="rl-savebar-text" role="status">
                    <span>Unsaved changes</span>
                    <small>Save to apply without a restart, or discard to revert.</small>
                  </div>
                  <span class="spacer" />
                  <Button variant="ghost" onClick={() => store.discard()} disabled={store.saving() || store.locked()}>
                    Discard
                  </Button>
                  <Button
                    class="rl-primary"
                    onClick={() => void store.save()}
                    disabled={store.locked() || store.saving() || !store.dirty()}
                  >
                    {store.saving() ? 'Saving…' : 'Save'}
                  </Button>
                </div>
              </div>
            </Show>
          </>
        )}
      </Show>

      <Dialog
        open={helpOpen()}
        title={helpContent()?.ui?.guide ?? 'Rate limits, explained'}
        onClose={() => setHelpOpen(false)}
      >
        <Show when={helpLoading()}>
          <p class="loading-hint">Loading guide…</p>
        </Show>
        <Show when={!helpLoading() && !helpContent()}>
          <p class="hint">
            Could not load the in-console guide.{' '}
            <a href="/admin/admin-rate-limit-help.js" target="_blank" rel="noopener noreferrer">
              Open help script
            </a>
          </p>
        </Show>
        <Show when={helpContent()}>
          {(help) => (
            <div class="rl-help-body">
              <p class="hint">{help().ui?.guideSub}</p>
              <nav class="rl-help-nav" aria-label="Guide contents">
                <For each={help().sections ?? []}>
                  {(section) => (
                    <a class="rl-link" href={`#rl-help-${section.id}`}>
                      {section.title}
                    </a>
                  )}
                </For>
              </nav>
              <For each={help().sections ?? []}>
                {(section) => (
                  <section id={`rl-help-${section.id}`} class="rl-help-section">
                    <h3>{section.title}</h3>
                    <Show when={section.intro}>
                      <p>{section.intro}</p>
                    </Show>
                    <For each={section.items ?? []}>
                      {(item) => (
                        <div class="rl-help-item">
                          <Show when={item.term}>
                            <h4>{item.term}</h4>
                          </Show>
                          <Show when={item.text}>
                            <p>{item.text}</p>
                          </Show>
                          <Show when={item.example}>
                            <p class="hint">
                              <strong>{help().ui?.example ?? 'Example:'}</strong> {item.example}
                            </p>
                          </Show>
                        </div>
                      )}
                    </For>
                  </section>
                )}
              </For>
            </div>
          )}
        </Show>
      </Dialog>
    </div>
  );
}
