/**
 * Behavioural tests for the redesigned Rules list on Settings → Rate limits.
 *
 * The list is read by the same two questions a rule is created with — who is limited, on which
 * model — and never by the scope a rule is stored under. This pins:
 *
 *   - rows expose Who / Model / Limit / Now / Refused, with the key's name first and its id kept;
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
    const visible = rows.flatMap(r => [r.who, r.whoKind, r.model, r.nowTag, r.nowSub, r.openLabel]).join(' ');
    assert.doesNotMatch(visible, /api_key|tenant_model|\bglobal\b|auth_failure/);
  });

  await t.test('Now is words: off, unsaved, windows — never a bare dot, and blank when nothing differs', () => {
    assert.equal(by('acme').find(r => r.model === 'claude').nowTag, 'off');
    assert.equal(by('Everyone').find(r => r.model === 'gpt-4').nowTag, '1 window');
    assert.equal(by('Everyone').find(r => r.model === 'All models').noNow, true);
    app.rlDraft.rules[5].rpm = 4000;
    assert.equal(app.rlRuleRows.find(r => r.who === 'Everyone' && r.model === 'All models').nowTag, 'unsaved');
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

  await t.test('status toggles: scheduled, off — and pressing again releases', () => {
    const app = listApp();
    app.toggleRateLimitFlagFilter('scheduled');
    assert.deepEqual(names(app), ['Everyone/gpt-4']);
    app.toggleRateLimitFlagFilter('off');
    assert.deepEqual(names(app), ['acme/claude']);
    app.toggleRateLimitFlagFilter('off');
    assert.equal(app.rlRuleRows.length, 6);
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
    assert.deepEqual(chips.flags.map(c => c.label + ' ' + c.count), ['Scheduled 1', 'Off 1']);
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
