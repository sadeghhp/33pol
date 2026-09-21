/**
 * Behavioural tests for the redesigned Rules list on Settings → Rate limits.
 *
 * The list is read by the same two questions a rule is created with — who is limited, on which
 * model — and never by the scope a rule is stored under. This pins:
 *
 *   - rows expose Who / Model / Limit (the draft) / Enforcing now (production) / Refused, with the key's name first and its id kept;
 *   - the protective budgets are stored as rules but listed in their own section, always both;
 *   - filters follow the creation model (who × model, plus Scheduled / Off) and keep text search;
 *   - sorting is by who, model, rpm and refusals, with unknown refusals last either way;
 *   - the Refused cell is "—" rather than 0 wherever the report cannot vouch for a number;
 *   - none of it changes what is stored or sent.
 *
 *     node --test tests/admin-console/
 */
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const ADMIN = path.join(__dirname, '../../src/33pol.App/wwwroot/admin');
const SOURCE = path.join(ADMIN, 'admin-app.js');
const HTML = fs.readFileSync(path.join(ADMIN, 'index.html'), 'utf8');

function createApp() {
  const context = {
    document: { addEventListener() {}, hidden: false, getElementById: () => null },
    window: { addEventListener() {}, AdminIcons: null },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    Alpine: { data() {}, directive() {}, store: () => ({}) },
    setInterval: () => 0, clearInterval() {}, setTimeout: () => 0, clearTimeout() {},
    console, Intl,
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
  return context.adminApp();
}

const KEY_ID = '0b9f6c1e-1111-4222-8333-444455556666';
const rule = (scope, target, rpm, extra = {}) => ({ scope, target, rpm, burst: 10, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });
const WINDOW = { name: 'off-peak', kind: 'weekly', days: ['mon'], start: '19:00', end: '07:00', timeZone: 'UTC', rpm: 1200, burst: 0, maxConcurrentStreams: 0 };

const CONFIG = {
  enabled: true, adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans: {},
  rules: [
    rule('model', 'gpt-4', 600, { schedule: [WINDOW] }),
    rule('api_key_model', KEY_ID + '|gpt-4', 10, { maxConcurrentStreams: 2 }),
    rule('api_key', KEY_ID, 30),
    rule('tenant', 'acme', 0, { maxConcurrentStreams: 3 }),
    rule('tenant_model', 'acme|claude', 50, { enabled: false }),
    rule('global', '*', 5000),
    rule('anonymous', '*', 30),
  ],
};

const clone = v => JSON.parse(JSON.stringify(v));

function listApp(usage = null) {
  const app = createApp();
  app.rateLimits = clone(CONFIG);
  app.rlDraft = clone(CONFIG);
  app.keys = [{ id: KEY_ID, label: 'billing-worker', keyPrefix: 'pk_live_ab', isRevoked: false, isArchived: false }];
  app.rateLimitUsage = usage;
  return app;
}

const USAGE = {
  windowMinutes: 60, generatedUtc: '2026-09-20T10:00:00Z',
  totals: { requests: 10, admitted: 9, rejected: 1, rateRejected: 1, concurrencyRejected: 0 },
  byTenant: [], byModel: [], byApiKey: [], byTenantModel: [],
  violations: [
    { scope: 'model', key: 'gpt-4', control: 'rate', hits: 40 },
    { scope: 'api_key', key: KEY_ID, control: 'rate', hits: 3 },
  ],
};

test('a row answers who, on which model, how much — without a stored scope name', async t => {
  const app = listApp();
  const rows = app.rlRuleRows;
  const by = who => rows.filter(r => r.who === who);

  await t.test('key rules lead with the key name and keep the id one glance away', () => {
    const [onModel] = by('billing-worker').filter(r => r.model === 'gpt-4');
    assert.equal(onModel.whoKind, 'API key · 0b9f6c1e…');
    assert.equal(onModel.targetTitle, KEY_ID + '|gpt-4');
    assert.equal(onModel.streams, '2');
    const [allModels] = by('billing-worker').filter(r => r.model === 'All models');
    assert.match(allModels.modelCls, /muted/);
    assert.equal(allModels.streams, '∞');
  });

  await t.test('everyone-rules say Everyone; the gateway rule covers all models', () => {
    assert.deepEqual(by('Everyone').map(r => r.model).sort(), ['All models', 'gpt-4']);
  });

  await t.test('a tenant rule with rpm 0 shows that the plan rate is kept, not a zero', () => {
    const [tenant] = by('acme').filter(r => r.model === 'All models');
    assert.equal(tenant.rpm, 'plan');
    assert.match(tenant.rpmTitle, /keeps its plan/);
  });

  await t.test('no visible cell carries a stored scope id', () => {
    const visible = rows.flatMap(r => [r.who, r.whoKind, r.model, r.enfText, r.enfSub, ...r.enfTags.map(g => g.text), r.draftTag, r.was, r.openLabel]).join(' ');
    assert.doesNotMatch(visible, /api_key|tenant_model|\bglobal\b|auth_failure/);
  });

  await t.test('Enforcing now is words about production — never blank, never a dash, never the draft', () => {
    const tags = r => r.enfTags.map(g => g.text);
    assert.deepEqual(tags(by('acme').find(r => r.model === 'claude')), ['off']);
    assert.deepEqual(tags(by('Everyone').find(r => r.model === 'gpt-4')), ['1 window'], 'until the schedule report says which tier is in force');
    const gateway = by('Everyone').find(r => r.model === 'All models');
    assert.deepEqual([gateway.enfText, tags(gateway)], ['5,000 rpm · 10 burst', ['base']]);
    for (const r of rows) {
      assert.ok(r.enfTags.length >= 1 && r.enfText, 'every row says something');
      assert.doesNotMatch(r.enfText, /^—$/);
    }

    app.rlDraft.rules[5].rpm = 4000;
    const edited = app.rlRuleRows.find(r => r.who === 'Everyone' && r.model === 'All models');
    assert.deepEqual([edited.rpm, edited.draftTag, edited.was], ['4,000', 'unsaved', 'was 5,000 / 10 / ∞']);
    assert.deepEqual([edited.enfText, tags(edited)], ['5,000 rpm · 10 burst', ['base']], 'production still runs the saved tier');
    const untouched = app.rlRuleRows.find(r => r.who === 'Everyone' && r.model === 'gpt-4');
    assert.deepEqual([untouched.draftTag, untouched.was], ['', ''], 'editing one rule marks only that rule');
  });
});

test('the list separates the draft from production', async t => {
  const SCHEDULE = {
    rules: [{
      scope: 'model', target: 'gpt-4', base: { rpm: 600, burst: 10, maxConcurrentStreams: 0, suspended: false },
      effective: { rpm: 1200, burst: 0, maxConcurrentStreams: 0, suspended: false },
      activeWindow: 'off-peak', activeUntil: '2026-09-21T07:00:00Z', nextChangeAt: '2026-09-21T07:00:00Z', nextWindow: null, windows: [],
    }],
    occurrences: [], transitions: [],
  };
  const model = app => app.rlRuleRows.find(r => r.who === 'Everyone' && r.model === 'gpt-4');

  await t.test('a running window is reported from the saved schedule', () => {
    const app = listApp();
    app.rlScheduleSaved = clone(SCHEDULE);
    const row = model(app);
    assert.equal(row.enfText, '1,200 rpm');
    assert.deepEqual(row.enfTags.map(g => g.text), ['window: off-peak']);
    assert.equal(row.windowActive, true);
  });

  await t.test('the draft preview never feeds the column, so a staged schedule is not shown as running', () => {
    const app = listApp();
    app.rlSchedule = clone(SCHEDULE);          // what the Schedule section draws
    app.rlScheduleSaved = { rules: [], occurrences: [], transitions: [] };
    assert.deepEqual(model(app).enfTags.map(g => g.text), ['1 window']);
  });

  await t.test('a rule switched off in the draft is still enforced until saved — and says so', () => {
    const app = listApp();
    app.toggleRateLimitRuleEnabled('model:gpt-4');
    const row = model(app);
    assert.deepEqual([row.enabled, row.draftTag, row.was], [false, 'unsaved', 'was on']);
    assert.deepEqual(row.enfTags.map(g => g.text), ['1 window']);
    assert.notEqual(row.enfTags[0].text, 'off');
  });

  await t.test('a rule that exists only in the draft enforces nothing yet', () => {
    const app = listApp();
    app.rlDraft.rules.push(rule('model', 'new-model', 5));
    const row = app.rlRuleRows.find(r => r.model === 'new-model');
    assert.deepEqual([row.draftTag, row.enfTags.map(g => g.text)], ['new', ['not saved yet']]);
  });

  await t.test('the master switch reaches every row, and only once it is saved', () => {
    const app = listApp();
    app.rlDraft.enabled = false;
    assert.equal(app.rlRuleRows.some(r => r.enfKind === 'unenforced'), false, 'staged, not in force');
    assert.equal(app.rlStatusView.title, 'Rate limits are enforced');
    assert.match(app.rlStatusView.pending, /stops enforcing when you save/);
    assert.equal(app.rateLimitsSavedDisabled, false);

    app.rateLimits.enabled = false;
    app.rateLimits = clone(app.rateLimits);
    assert.equal(app.rlRuleRows.every(r => r.enfKind === 'unenforced'), true);
    assert.equal(app.rlStatusView.title, 'Rate limits are not enforced');
    assert.equal(app.rlStatusView.pending, '');
    assert.equal(app.rateLimitsSavedDisabled, true);

    app.rlDraft.enabled = true;
    assert.match(app.rlStatusView.pending, /resumes enforcing when you save/);
    assert.equal(app.rlStatusView.title, 'Rate limits are not enforced', 'still off in production');
  });

  await t.test('adaptive shedding is named on the rules it scales, and on no others', () => {
    const app = listApp({ ...USAGE, adaptive: { enabled: true, models: [{ modelId: 'gpt-4', factor: 0.7, saturation: 0.93, reason: 'queue depth' }] } });
    app.rateLimits.adaptiveEnabled = true;
    const row = model(app);
    assert.deepEqual(row.enfTags.map(g => g.text), ['1 window', 'adaptive ×0.70']);
    assert.match(row.enfSub, /≈ 420 of 600 rpm · queue depth/);
    const pair = app.rlRuleRows.find(r => r.who === 'billing-worker' && r.model === 'gpt-4');
    assert.deepEqual(pair.enfTags.map(g => g.text), ['base', 'adaptive ×0.70']);
    const allModels = app.rlRuleRows.find(r => r.who === 'billing-worker' && r.model === 'All models');
    assert.deepEqual(allModels.enfTags.map(g => g.text), ['base']);
  });

  await t.test('the markup keeps the two statements in separate columns', () => {
    assert.match(HTML, />Enforcing now<span class="rl-th-sub">saved configuration<\/span>/);
    assert.match(HTML, /x-show="r\.hasWas" x-text="r\.was"/);
    assert.match(HTML, /x-show="rateLimitsSavedDisabled"/);
    assert.doesNotMatch(HTML, /Limit in force|Limits being hit/);
  });
});

test('protective limits are the same stored rules, listed apart', async t => {
  await t.test('they never appear in the ordinary list or its counts', () => {
    const app = listApp();
    assert.equal(app.rlRuleRows.some(r => /Anonymous|Failed/.test(r.target)), false);
    assert.equal(app.rlScopeChips.who[0].count, '6');
    assert.equal(app.rlDraft.rules.length, 7, 'and nothing was removed from the draft');
  });

  await t.test('both cards always show: configured opens its rule, unset offers Configure', () => {
    const app = listApp();
    const [anonymous, failed] = app.rlProtectiveCards;
    assert.deepEqual([anonymous.configured, anonymous.rpm, anonymous.canConfigure], [true, '30', false]);
    assert.deepEqual([failed.configured, failed.unset], [false, true]);
    assert.match(failed.unsetText, /default tier/);

    anonymous.open();
    assert.equal(app.rlRuleDrawerOpen, true);
    assert.equal(app.rlRule.scope, 'anonymous');
  });

  await t.test('Configure opens the same form on that budget, and stores the same rule as before', () => {
    const app = listApp({});
    app.loadRateLimitKeys = () => {};
    app.rlProtectiveCards[1].configure();
    assert.equal(app.rlNewRuleOpen, true);
    assert.equal(app.rlNewRule.who, 'auth_failure');
    app.createRateLimitRule();
    const stored = app.rlDraft.rules[app.rlDraft.rules.length - 1];
    assert.deepEqual([stored.scope, stored.target], ['auth_failure', '*']);
  });

  await t.test('a list with only protective rules is an empty list, not a blank table', () => {
    const app = listApp();
    app.rlDraft.rules = app.rlDraft.rules.filter(r => r.scope === 'anonymous');
    assert.equal(app.rlNoRules, true);
    assert.equal(app.rlHasRules, false);
  });

  await t.test('read-only hides Configure but keeps the configured card openable', () => {
    const app = listApp();
    app.rlReadOnlyReason = 'This key may view rate limits but not change them.';
    assert.equal(app.rlProtectiveCards[1].canConfigure, false);
    assert.equal(app.rlProtectiveCards[0].configured, true);
  });
});

test('filters follow the creation model', async t => {
  const names = app => app.rlRuleRows.map(r => r.who + '/' + r.model).sort();

  await t.test('who', () => {
    const app = listApp();
    app.setRateLimitWhoFilter('key');
    assert.deepEqual(names(app), ['billing-worker/All models', 'billing-worker/gpt-4']);
    app.setRateLimitWhoFilter('everyone');
    assert.deepEqual(names(app), ['Everyone/All models', 'Everyone/gpt-4']);
  });

  await t.test('who × model combine', () => {
    const app = listApp();
    app.setRateLimitWhoFilter('tenant');
    app.setRateLimitWhereFilter('one');
    assert.deepEqual(names(app), ['acme/claude']);
  });

  await t.test('status flags are toggles that combine, and pressing again releases', () => {
    const app = listApp();
    app.toggleRateLimitFlagFilter('scheduled');
    assert.deepEqual(names(app), ['Everyone/gpt-4']);
    app.toggleRateLimitFlagFilter('off');
    assert.deepEqual(names(app), [], 'scheduled AND off: no rule is both');
    app.toggleRateLimitFlagFilter('scheduled');
    assert.deepEqual(names(app), ['acme/claude']);
    app.toggleRateLimitFlagFilter('off');
    assert.equal(app.rlRuleRows.length, 6);
  });

  await t.test('refused, window active and unsaved filter on what the page really knows', () => {
    const app = listApp(USAGE);
    app.toggleRateLimitFlagFilter('refused');
    assert.deepEqual(names(app), ['Everyone/gpt-4', 'billing-worker/All models']);
    app.clearRateLimitFilters();

    app.rlScheduleSaved = { rules: [{ scope: 'model', target: 'gpt-4', activeWindow: 'off-peak', effective: { rpm: 1200 }, windows: [] }] };
    app.toggleRateLimitFlagFilter('active');
    assert.deepEqual(names(app), ['Everyone/gpt-4']);
    app.clearRateLimitFilters();

    app.rlDraft.rules[2].rpm = 31;
    app.toggleRateLimitFlagFilter('unsaved');
    assert.deepEqual(names(app), ['billing-worker/All models']);
    assert.equal(app.rlFiltersActive, true);
  });

  await t.test('near-limit exists, and only the gateway’s per-limit report can put a rule in it', () => {
    const app = listApp();
    assert.deepEqual(app.rlScopeChips.flags.map(c => c.label), ['Scheduled', 'Window active', 'Refused', 'Near limit', 'Off', 'Unsaved']);
    // A report with no per-limit section — however hot its subject rows look — marks nothing.
    app.rateLimitUsage = { windowMinutes: 60, totals: { requests: 10 }, violations: [],
      byModel: [{ key: 'gpt-4', requests: 9999, requestsPerMinute: 999, effectiveRpm: 10, utilization: 99 }] };
    assert.equal(app.rlScopeChips.flags.find(c => c.label === 'Near limit').count, 0);
    // The gateway names the limit by id and states its peak against the rate it enforced.
    app.rateLimitUsage = { ...app.rateLimitUsage, limits: [
      { limitId: 'model:gpt-4', singleBucket: true, evaluations: 100, charged: 95, refusedByRate: 5, peakChargedInOneMinute: 9, effectiveRpm: 10, peakUtilization: 0.9 },
      { limitId: 'plan:pro', singleBucket: false, evaluations: 900, charged: 900, peakChargedInOneMinute: 500, effectiveRpm: 10, peakUtilization: null }
    ] };
    assert.equal(app.rlScopeChips.flags.find(c => c.label === 'Near limit').count, 1);
    app.toggleRateLimitFlagFilter('near');
    assert.deepEqual(names(app), ['Everyone/gpt-4']);
  });

  await t.test('text search still finds by key name, stored id and window name', () => {
    const app = listApp();
    app.rlFilterText = 'billing';
    assert.equal(app.rlRuleRows.length, 2);
    app.rlFilterText = KEY_ID.slice(0, 8);
    assert.equal(app.rlRuleRows.length, 2);
    app.rlFilterText = 'off-peak';
    assert.deepEqual(names(app), ['Everyone/gpt-4']);
  });

  await t.test('no match offers one way back to everything', () => {
    const app = listApp();
    app.rlFilterText = 'zzz';
    app.setRateLimitWhoFilter('key');
    assert.equal(app.rlNoFilteredRules, true);
    assert.equal(app.rlFiltersActive, true);
    app.clearRateLimitFilters();
    assert.equal(app.rlRuleRows.length, 6);
    assert.equal(app.rlFiltersActive, false);
    assert.match(HTML, /@click="clearRateLimitFilters">Clear filters</);
  });

  await t.test('chips are labelled by intent, with counts', () => {
    const chips = listApp().rlScopeChips;
    assert.deepEqual(chips.who.map(c => c.label + ' ' + c.count), ['All 6', 'API keys 2', 'Tenants 2', 'Everyone 2']);
    assert.deepEqual(chips.where.map(c => c.label + ' ' + c.count), ['Any 6', 'One model 3', 'All models 3']);
    assert.deepEqual(chips.flags.map(c => c.label + ' ' + c.count), ['Scheduled 1', 'Window active 0', 'Refused 0', 'Near limit 0', 'Off 1', 'Unsaved 0']);
  });
});

test('sorting', async t => {
  await t.test('defaults to who, ascending, and a second press flips it', () => {
    const app = listApp();
    assert.deepEqual([...new Set(app.rlRuleRows.map(r => r.who))], ['acme', 'billing-worker', 'Everyone']);
    assert.equal(app.rlSortView.who.ariaSort, 'ascending');
    app.setRateLimitSort('who');
    assert.equal(app.rlSortView.who.ariaSort, 'descending');
    assert.equal(app.rlRuleRows[0].who, 'Everyone');
    assert.equal(app.rlSortView.model.ariaSort, 'none');
  });

  await t.test('rpm sorts numerically', () => {
    const app = listApp();
    app.setRateLimitSort('rpm');
    assert.deepEqual(app.rlRuleRows.map(r => r.rpm), ['plan', '10', '30', '50', '600', '5,000']);
  });

  await t.test('refused starts descending and keeps unknowns last in both directions', () => {
    const app = listApp(USAGE);
    app.setRateLimitSort('refused');
    assert.deepEqual(app.rlRuleRows.map(r => r.refused), ['40', '3', '0', '0', '—', '—']);
    app.setRateLimitSort('refused');
    assert.deepEqual(app.rlRuleRows.map(r => r.refused).slice(-2), ['—', '—']);
  });

  await t.test('headers are buttons that announce their state', () => {
    assert.match(HTML, /<th scope="col" class="rl-col-who" :aria-sort="rlSortView\.who\.ariaSort"><button type="button" class="th-sort"/);
    assert.match(HTML, /:aria-sort="rlSortView\.refused\.ariaSort"/);
  });
});

test('the Refused cell never fabricates a zero', async t => {
  await t.test('no report: every cell is an em dash', () => {
    assert.deepEqual([...new Set(listApp(null).rlRuleRows.map(r => r.refused))], ['—']);
  });

  await t.test('with a report: true counts, true zeros, and "—" for a tenant the console cannot resolve', () => {
    const rows = listApp(USAGE).rlRuleRows;
    assert.equal(rows.find(r => r.who === 'Everyone' && r.model === 'gpt-4').refused, '40');
    assert.equal(rows.find(r => r.who === 'Everyone' && r.model === 'All models').refused, '0');
    const tenant = rows.find(r => r.who === 'acme' && r.model === 'All models');
    assert.equal(tenant.refused, '—');
    assert.match(tenant.refusedTitle, /counted by tenant id/);
  });

  await t.test('the column is labelled with its time basis, in the header and under the table', () => {
    assert.match(HTML, />Refused<span x-html="rlSortView\.refused\.icon"><\/span><\/button><span class="rl-th-sub">since restart<\/span>/);
    assert.match(HTML, /means unknown, not zero/);
  });
});

test('opening a rule does not depend on a focusable table row', () => {
  assert.doesNotMatch(HTML, /<tr class="rl-row"[^>]*tabindex/);
  assert.match(HTML, /<td class="rl-col-chev"><button type="button" class="icon-btn" @click="r\.open" :aria-label="r\.openLabel"/);
  // The switch cell still swallows its click so toggling never opens the drawer.
  assert.match(HTML, /<td class="rl-col-on" @click\.stop>/);
});

test('the list redesign changes nothing that is sent', () => {
  const app = listApp(USAGE);
  const before = JSON.stringify(app.buildRateLimitsPayload(app.rlDraft));
  app.setRateLimitSort('refused');
  app.setRateLimitWhoFilter('key');
  void app.rlRuleRows; void app.rlProtectiveCards; void app.rlScopeChips;
  assert.equal(JSON.stringify(app.buildRateLimitsPayload(app.rlDraft)), before);
  assert.equal(app.rateLimitsDirty, false);
});

test('the list stays usable at the server\'s rule ceiling', async t => {
  const big = () => {
    const app = listApp(USAGE);
    const rules = [];
    for (let i = 0; i < 2000; i++) rules.push(rule('model', 'model-' + String(i).padStart(4, '0'), 10 + i));
    app.rateLimits = { ...clone(CONFIG), rules };
    app.rlDraft = clone(app.rateLimits);
    return app;
  };

  await t.test('a hundred rows at a time, with the count said and a way to see more', () => {
    const app = big();
    assert.equal(app.rlRuleRows.length, 100);
    assert.deepEqual(app.rlRuleCountView, { text: 'Showing 100 of 2,000 rules', hasMore: true, moreLabel: 'Show 100 more' });
    app.showMoreRateLimitRules();
    assert.equal(app.rlRuleRows.length, 200);
    app.setRateLimitFilterText('model-19');
    assert.equal(app.rlRuleLimit, 100, 'a new question starts from the top');
    assert.deepEqual([app.rlRuleRows.length, app.rlRuleCountView.text], [100, '100 of 2,000 rules']);
    app.setRateLimitFilterText('model-1');
    assert.equal(app.rlRuleCountView.text, 'Showing 100 of 1,000 matching rules');
  });

  await t.test('sorting applies to every rule, not only the visible hundred', () => {
    const app = big();
    app.setRateLimitSort('rpm');
    app.setRateLimitSort('rpm');
    assert.equal(app.rlRuleRows[0].rpm, '2,009');
  });

  await t.test('a row finds its saved twin by lookup, not by scanning: one payload build per row', () => {
    const app = big();
    void app.rlRuleRows;                       // warm the index
    let builds = 0;
    const original = app.rlRulePayload;
    app.rlRulePayload = function (row) { builds++; return original.call(this, row); };
    void app.rlRuleRows;
    assert.ok(builds <= 100, 'visible rows only, and no rebuild of the saved side — got ' + builds);
  });

  await t.test('the narrow-width sort control drives the same state as the header buttons', () => {
    const app = listApp(USAGE);
    app.setRateLimitSortChoice('refused:desc');
    assert.deepEqual([app.rlSortKey, app.rlSortDir, app.rlSortView.refused.ariaSort], ['refused', -1, 'descending']);
    app.setRateLimitSortChoice('bogus:asc');
    assert.equal(app.rlSortKey, 'refused');
    assert.match(HTML, /<select class="inline-select" x-model="mdl\.rlSortChoice" aria-label="Sort rules">/);
  });
});
