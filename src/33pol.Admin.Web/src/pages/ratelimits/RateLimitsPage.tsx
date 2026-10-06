import { For, Show, createEffect, createSignal, onCleanup, onMount } from 'solid-js';
import { Button, Dialog, Field, Select } from '../../components/primitives';
import { IconChevronRight, IconFileText, IconPlus, IconRefresh } from '../../components/icons';
import {
  fetchRateLimitHelp,
  type RateLimitHelpLang,
} from '../../domain/rateLimitHelp';
import { formatEnforcingNow, scopeInfo, tierText } from '../../domain/rateLimitEdit';
import { formatNum } from '../../domain/format';
import {
  RATE_LIMIT_SCOPES,
  activateRateLimitsPage,
  disposeRateLimitsPage,
  rateLimitRuleIdentity,
  useAddRuleIntent,
  useRateLimitActivityStore,
  useRateLimitDrawersStore,
  useRateLimitsStore,
} from '../../stores/rateLimits';
import { RateLimitActivitySection } from './RateLimitActivitySection';
import { RateLimitRuleDrawer } from './RateLimitRuleDrawer';
import { RateLimitTierDrawer } from './RateLimitTierDrawer';

export default function RateLimitsPage(props: { visible: boolean }) {
  const store = useRateLimitsStore();
  const drawers = useRateLimitDrawersStore();
  const activity = useRateLimitActivityStore();
  const intent = useAddRuleIntent();
  const [helpOpen, setHelpOpen] = createSignal(false);
  const [helpContent, setHelpContent] = createSignal<RateLimitHelpLang | null>(null);
  const [helpLang, setHelpLang] = createSignal<'en' | 'fa'>('en');
  const [helpLoading, setHelpLoading] = createSignal(false);
  const [newScope, setNewScope] = createSignal('model');
  const [newTarget, setNewTarget] = createSignal('');
  const [newRpm, setNewRpm] = createSignal('60');
  const [newBurst, setNewBurst] = createSignal('0');

  const onBeforeUnload = (e: BeforeUnloadEvent) => {
    if (!store.workInProgress()) return;
    e.preventDefault();
    e.returnValue = 'Rate-limit changes have not been saved yet.';
  };

  onMount(() => {
    window.addEventListener('beforeunload', onBeforeUnload);
    if (props.visible) activateRateLimitsPage();
  });

  onCleanup(() => {
    window.removeEventListener('beforeunload', onBeforeUnload);
    disposeRateLimitsPage();
  });

  createEffect(() => {
    if (props.visible) {
      activateRateLimitsPage();
      void store.load();
      const pending = intent.addRuleIntent();
      if (pending) {
        setNewScope(pending.scope);
        setNewTarget(pending.target);
        intent.setAddRuleIntent(null);
      }
    }
  });

  createEffect(() => {
    if (!helpOpen()) return;
    const lang = helpLang();
    setHelpLoading(true);
    void fetchRateLimitHelp(lang)
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

  const limitActivity = (identity: string) => {
    const row = activity.usageReport()?.limits.find((l) => l.limitId === identity) ?? null;
    if (!row) return { text: '—', sub: '' };
    const refused = row.refusedByRate + row.refusedByStreams;
    const sub =
      row.peakUtilization != null
        ? `peak ${Math.round(row.peakUtilization * 100)}%`
        : '';
    return {
      text: `${formatNum(row.charged)} passed · ${formatNum(refused)} refused`,
      sub,
    };
  };

  const dirtyView = () => drawers.dirtyView();

  const handleSave = () => {
    void drawers.requestSave();
  };

  const planRows = () => {
    const cfg = store.draft();
    if (!cfg) return [];
    const rows: Array<{ key: string; name: string; who: string; tier: typeof cfg.default; kind: 'default' | 'plan'; slug: string }> = [
      { key: 'default', name: 'Default', who: 'Every tenant without a plan', tier: cfg.default, kind: 'default', slug: '' },
    ];
    for (const [slug, tier] of Object.entries(cfg.plans)) {
      rows.push({ key: 'plan:' + slug, name: slug, who: 'Tenants on this plan', tier, kind: 'plan', slug });
    }
    return rows;
  };

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
                <div class={`rl-status-title${cfg().enabled ? '' : ' off'}`}>
                  {cfg().enabled ? 'Rate limits enforced' : 'Rate limits disabled'}
                </div>
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

            <div class="rl-section" id="rl-rules">
              <div class="rl-section-head">
                <div>
                  <h3>Rules</h3>
                  <p class="sub">Scoped limits on models, tenants, or keys. Open a rule to change it or schedule it.</p>
                </div>
              </div>

              <Show when={store.editable()}>
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
                      <th class="rl-col-on">On</th>
                      <th class="rl-col-who">Who</th>
                      <th class="rl-col-model">Model</th>
                      <th class="rl-col-limit">Limit</th>
                      <th class="rl-col-now" title="What production applies right now from the saved configuration.">
                        Enforcing now
                        <span class="rl-th-sub">saved configuration</span>
                      </th>
                      <th class="rl-col-traffic">Limit activity</th>
                      <th class="rl-col-chev"><span class="sr-only">Open</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    <For each={cfg().rules}>
                      {(rule) => {
                        const identity = rateLimitRuleIdentity(rule.scope, rule.target);
                        const info = scopeInfo(rule.scope);
                        const enf = () =>
                          formatEnforcingNow(store.scheduleStatusFor(rule.scope, rule.target), !!store.saved()?.enabled);
                        const act = () => limitActivity(identity);
                        const modelLabel =
                          rule.scope.includes('model') && rule.target.includes('|')
                            ? rule.target.split('|').slice(-1)[0]
                            : rule.scope === 'model'
                              ? rule.target
                              : '—';
                        return (
                          <tr
                            class={`rl-row${rule.enabled ? '' : ' off'}`}
                            onClick={() => drawers.openRuleDrawer(identity)}
                          >
                            <td class="rl-col-on" onClick={(e) => e.stopPropagation()}>
                              <label class="rl-switch sm">
                                <input
                                  type="checkbox"
                                  role="switch"
                                  checked={rule.enabled}
                                  disabled={store.locked()}
                                  aria-label={`Enable rule ${rule.target}`}
                                  onChange={(e) => store.setRuleEnabled(identity, e.currentTarget.checked)}
                                />
                                <span class="rl-switch-track" />
                              </label>
                            </td>
                            <td class="rl-col-who">
                              <span class="rl-target">{info.singleton ? info.name : rule.target.split('|')[0]}</span>
                              <span class="rl-scope">{info.short}</span>
                            </td>
                            <td class="rl-col-model">
                              <span class="rl-model">{modelLabel}</span>
                            </td>
                            <td class="rl-col-limit">
                              <span class="rl-limit-nums">
                                <span><b>{formatNum(rule.rpm)}</b><i>rpm</i></span>
                                <span><b>{formatNum(rule.burst)}</b><i>burst</i></span>
                                <span>
                                  <b>{rule.maxConcurrentStreams > 0 ? formatNum(rule.maxConcurrentStreams) : '∞'}</b>
                                  <i>streams</i>
                                </span>
                              </span>
                              <Show when={(rule.schedule || []).length > 0}>
                                <span class="hint">{(rule.schedule || []).length} window(s)</span>
                              </Show>
                            </td>
                            <td class="rl-col-now" title={enf().text}>
                              <span class="rl-enf-text">{enf().text}</span>
                              <For each={enf().tags}>
                                {(tag) => <span class="tag accent">{tag}</span>}
                              </For>
                            </td>
                            <td class="rl-col-traffic">
                              <span>{act().text}</span>
                              <Show when={act().sub}>
                                <span class="rl-act-sub">{act().sub}</span>
                              </Show>
                            </td>
                            <td class="rl-col-chev">
                              <button
                                type="button"
                                class="icon-btn"
                                aria-label="Open rule"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  drawers.openRuleDrawer(identity);
                                }}
                              >
                                <span class="icon"><IconChevronRight /></span>
                              </button>
                            </td>
                          </tr>
                        );
                      }}
                    </For>
                  </tbody>
                </table>
                <Show when={cfg().rules.length === 0}>
                  <p class="hint">No rules yet. Tenants use their tier until you add a scoped rule.</p>
                </Show>
              </div>
            </div>

            <RateLimitActivitySection />

            <div class="rl-section" id="rl-baselines">
              <div class="rl-section-head">
                <div>
                  <h3>Tenant tiers</h3>
                  <p class="sub">What a tenant gets by plan. A tenant rule overrides its plan tier.</p>
                </div>
                <Show when={store.editable()}>
                  <Button variant="ghost" size="sm" onClick={() => drawers.openTierDrawer('plan')}>
                    <span class="icon"><IconPlus /></span> Add plan
                  </Button>
                </Show>
              </div>
              <div class="table-wrap">
                <table class="data-table t-rl-tiers">
                  <thead>
                    <tr>
                      <th>Tier</th>
                      <th>Applies to</th>
                      <th class="num">rpm</th>
                      <th class="num">burst</th>
                      <th class="num">streams</th>
                      <th class="cell-actions"><span class="sr-only">Open</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    <For each={planRows()}>
                      {(row) => (
                        <tr class="rl-base-row">
                          <th scope="row" class="rl-base-name">{row.name}</th>
                          <td>{row.who}</td>
                          <td class="num">{formatNum(row.tier.rpm)}</td>
                          <td class="num">{formatNum(row.tier.burst)}</td>
                          <td class="num">
                            {row.tier.maxConcurrentStreams > 0 ? formatNum(row.tier.maxConcurrentStreams) : '∞'}
                          </td>
                          <td class="cell-actions">
                            <button
                              type="button"
                              class="icon-btn"
                              aria-label={`Open ${row.name}`}
                              onClick={() => drawers.openTierDrawer(row.kind, row.slug)}
                            >
                              <span class="icon"><IconChevronRight /></span>
                            </button>
                          </td>
                        </tr>
                      )}
                    </For>
                  </tbody>
                </table>
              </div>
            </div>

            <Show when={dirtyView().show}>
              <div class="rl-savebar" role="region" aria-label="Unsaved rate-limit changes">
                <Show when={drawers.reviewOpen()}>
                  <div class="rl-review" id="rl-review">
                    <ul class="rl-diff" aria-label="Unsaved changes">
                      <For each={dirtyView().items}>
                        {(item) => (
                          <li class="rl-diff-item">
                            <span class={item.kindCls}>{item.kind}</span>
                            <span class="rl-diff-what">
                              <b>{item.subject}</b>
                              <Show when={item.change}>
                                <span class="rl-diff-change">{item.change}</span>
                              </Show>
                            </span>
                            <Show when={store.editable()}>
                              <button type="button" class="rl-link rl-undo" onClick={() => drawers.undoDirtyChange(item.id)}>
                                Undo
                              </button>
                            </Show>
                          </li>
                        )}
                      </For>
                    </ul>
                  </div>
                </Show>
                <div class="rl-savebar-row">
                  <span class="rl-dot accent" aria-hidden="true" />
                  <div class="rl-savebar-text" role="status">
                    <span>{dirtyView().countText}</span>
                    <small>{dirtyView().detail}</small>
                  </div>
                  <span class="spacer" />
                  <Button variant="ghost" onClick={() => store.discard()} disabled={store.saving() || store.locked()}>
                    Discard
                  </Button>
                  <Button
                    variant="ghost"
                    onClick={() => drawers.setReviewOpen(!drawers.reviewOpen())}
                    disabled={store.saving()}
                    aria-expanded={drawers.reviewOpen()}
                    aria-controls="rl-review"
                  >
                    {drawers.reviewOpen() ? 'Hide review' : 'Review changes'}
                  </Button>
                  <Button
                    class="rl-primary"
                    onClick={handleSave}
                    disabled={store.locked() || store.saving() || !store.dirty()}
                  >
                    {store.saving() ? 'Saving…' : dirtyView().saveLabel}
                  </Button>
                </div>
              </div>
            </Show>
          </>
        )}
      </Show>

      <RateLimitTierDrawer />
      <RateLimitRuleDrawer />

      <Dialog
        open={helpOpen()}
        title={helpContent()?.ui?.guide ?? 'Rate limits, explained'}
        onClose={() => setHelpOpen(false)}
      >
        <div class="rl-help-toolbar">
          <Button variant={helpLang() === 'en' ? 'primary' : 'ghost'} size="sm" onClick={() => setHelpLang('en')}>
            EN
          </Button>
          <Button variant={helpLang() === 'fa' ? 'primary' : 'ghost'} size="sm" onClick={() => setHelpLang('fa')}>
            FA
          </Button>
        </div>
        <Show when={helpLoading()}>
          <p class="loading-hint">Loading guide…</p>
        </Show>
        <Show when={!helpLoading() && !helpContent()}>
          <p class="hint">
            Could not load the in-console guide.{' '}
            <a href="/admin/admin-rate-limit-help.json" target="_blank" rel="noopener noreferrer">
              Open help data
            </a>
          </p>
        </Show>
        <Show when={helpContent()}>
          {(help) => (
            <div class={`rl-help-body${helpLang() === 'fa' ? ' rtl' : ''}`}>
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
