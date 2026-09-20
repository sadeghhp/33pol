/**
 * Behavioural tests for the Rate limits → Activity card and the readings it lends to the rule list
 * and the rule drawer.
 *
 * The usage report carries three time bases in one payload, and the card used to blur them: the
 * per-limit refusal counters are cumulative since the gateway started but sat under a window
 * selector and an "in this window" empty state; the rule drawer called a subject's whole traffic
 * "usage" of the rule and answered "No traffic recorded for this rule" for scopes the report has no
 * section for; the window select did nothing until Refresh was pressed; and one failed refresh
 * blanked the card. This pins the corrected semantics:
 *
 *   - windowed, cumulative and subject-level figures are labelled as what they are;
 *   - a rule's refusal count is shown only where the tracker's (scope, partition) key IS the
 *     rule's bucket, is qualified for the tenant allowance, and is "—" — never 0 — when unknown;
 *   - fallback states say what the report can prove and nothing more;
 *   - picking a window loads it; a failed refresh keeps the last report and marks it stale.
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
    setInterval: () => 0,
    clearInterval() {},
    setTimeout: () => 0,
    clearTimeout() {},
    console,
    Intl,
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
  return context.adminApp();
}

const KEY_ID = '0b9f6c1e-1111-4222-8333-444455556666';
const TENANT_ID = 'a1a1a1a1-0000-4000-8000-000000000001';

const tier = (rpm) => ({ rpm, burst: 10, maxConcurrentStreams: 0, enabled: true, schedule: [] });

const CONFIG = {
  enabled: true,
  adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
  plans: {},
  rules: [
    { scope: 'model', target: 'gpt-4', ...tier(600) },
    { scope: 'api_key', target: KEY_ID, ...tier(30) },
    { scope: 'api_key_model', target: KEY_ID + '|gpt-4', ...tier(10) },
    { scope: 'tenant', target: 'acme', ...tier(120) },
    { scope: 'tenant', target: 'unknown-slug', ...tier(120) },
    { scope: 'global', target: '*', ...tier(5000) },
    { scope: 'anonymous', target: '*', ...tier(30) },
  ],
};

const usageRow = (key, extra) => ({
  key, tenantId: null, apiKeyId: null, modelId: null,
  requests: 120, admitted: 100, rejected: 20, requestsPerMinute: 2, configuredRpm: 600, effectiveRpm: 600,
  ...extra,
});

function report(overrides = {}) {
  return {
    windowMinutes: 60,
    generatedUtc: '2026-09-20T10:00:00Z',
    totals: { requests: 300, admitted: 270, rejected: 30, rateRejected: 25, concurrencyRejected: 5, rejectionRate: 0.1 },
    byTenantModel: [usageRow(TENANT_ID + '|gpt-4', { tenantId: TENANT_ID, modelId: 'gpt-4' })],
    byTenant: [usageRow(TENANT_ID, { tenantId: TENANT_ID })],
    byModel: [usageRow('gpt-4', { modelId: 'gpt-4' })],
    byApiKey: [usageRow(KEY_ID.toUpperCase(), { apiKeyId: KEY_ID.toUpperCase() })],
    violations: [
      { scope: 'model', key: 'gpt-4', control: 'rate', hits: 7 },
      { scope: 'model', key: 'gpt-4', control: 'concurrency', hits: 2 },
      { scope: 'api_key_model', key: KEY_ID.toUpperCase() + '|gpt-4', control: 'rate', hits: 4 },
      { scope: 'tenant', key: TENANT_ID, control: 'rate', hits: 11 },
    ],
    adaptive: { enabled: false, lastEvaluatedUtc: null, backedOffPartitions: 0, models: [] },
    store: { requestPartitions: 12, streamPartitions: 0, maxPartitions: 10000 },
    ...overrides,
  };
}

const clone = value => JSON.parse(JSON.stringify(value));

function loadedApp(usage = report()) {
  const app = createApp();
  app.rateLimits = clone(CONFIG);
  app.rlDraft = clone(CONFIG);
  app.keys = [{ id: KEY_ID, label: 'billing-worker', keyPrefix: 'pk_live_ab', isRevoked: false, isArchived: false }];
  app.overviewTenants = { topConsumersMonthToDate: [{ tenantId: TENANT_ID, tenantSlug: 'acme', planSlug: 'standard' }] };
  app.rateLimitUsage = usage;
  return app;
}

test('cumulative refusals are never presented as windowed', async t => {
  await t.test('the block is headed "since restart" and explains first-refuser counting', () => {
    assert.match(HTML, /Refusals by limit &mdash; since restart/);
    assert.match(HTML, /only the first one to refuse it is counted/);
    assert.match(HTML, /the window above does not apply/);
  });

  await t.test('the misleading copy is gone', () => {
    assert.doesNotMatch(HTML, /Nothing has been refused in this window/);
    assert.doesNotMatch(HTML, /Limits being hit/);
    assert.doesNotMatch(HTML, /Limit in force/);
    assert.doesNotMatch(fs.readFileSync(SOURCE, 'utf8'), /No traffic recorded for this rule/);
  });

  await t.test('the card no longer leans on classes the stylesheet never defined', () => {
    assert.doesNotMatch(HTML, /class="stat-row"/);
    assert.doesNotMatch(HTML, /<table class="data"/);
  });

  await t.test('windowed labels say what they are', () => {
    assert.match(HTML, />Decisions</);
    assert.match(HTML, />Avg req\/min</);
    assert.match(HTML, />Last limit seen</);
    assert.match(HTML, /window average, not the current rate/);
    const view = loadedApp().rlActivityView;
    assert.match(view.windowHeading, /In the selected window · last 60 min/);
    assert.equal(view.refusalRateText, '10%');
    assert.match(view.refusedText, /25 rate · 5 streams/);
  });

  await t.test('the empty state speaks of the restart, and only once a report exists', () => {
    const app = loadedApp(report({ violations: [] }));
    assert.equal(app.rateLimitNoViolations, true);
    app.rateLimitUsage = null;
    assert.equal(app.rateLimitNoViolations, false);
  });
});

test('a rule\'s refusal count is joined only where attribution holds', async t => {
  const app = loadedApp();

  await t.test('model rule: both controls of its own bucket are summed', () => {
    const r = app.rlRefusalsFor('model', 'gpt-4');
    assert.deepEqual([r.state, r.hits, r.qualified], ['ok', 9, false]);
  });

  await t.test('key-on-model rule: matched across id casing', () => {
    assert.equal(app.rlRefusalsFor('api_key_model', KEY_ID + '|gpt-4').hits, 4);
  });

  await t.test('a rule with no row in a complete list is a true zero', () => {
    const r = app.rlRefusalsFor('api_key', KEY_ID);
    assert.deepEqual([r.state, r.hits], ['ok', 0]);
  });

  await t.test('tenant rule by slug resolves to the id and is qualified as the allowance', () => {
    const r = app.rlRefusalsFor('tenant', 'acme');
    assert.deepEqual([r.state, r.hits, r.qualified], ['ok', 11, true]);
    assert.match(app.rlRefusalsView('tenant', 'acme').title, /plan tier combined with this rule/);
  });

  await t.test('an unresolvable tenant slug is unknown, not zero', () => {
    const view = app.rlRefusalsView('tenant', 'unknown-slug');
    assert.equal(view.state, 'unresolved');
    assert.equal(view.text, '—');
  });

  await t.test('protective scopes are outside the report', () => {
    assert.equal(app.rlRefusalsView('anonymous', '*').text, '—');
    assert.equal(app.rlRefusalsFor('auth_failure', '*').state, 'untracked');
  });

  await t.test('a full page of rows cannot prove a zero', () => {
    const many = Array.from({ length: app.rlUsageTake() }, (_, i) => ({ scope: 'model', key: 'm' + i, control: 'rate', hits: 1 }));
    const truncated = loadedApp(report({ violations: many }));
    assert.equal(truncated.rlRefusalsFor('model', 'gpt-4').state, 'truncated');
    assert.equal(truncated.rlRefusalsView('model', 'gpt-4').text, '—');
  });

  await t.test('no report at all is unknown', () => {
    const none = loadedApp(null);
    assert.equal(none.rlRefusalsView('model', 'gpt-4').text, '—');
    // The shape older tests hand in to skip the fetch must not crash the join either.
    none.rateLimitUsage = {};
    assert.equal(none.rlRefusalsFor('model', 'gpt-4').state, 'unavailable');
  });

  await t.test('a violation row links to the rule whose bucket it names', () => {
    const rows = app.rateLimitViolationRows;
    const tenantRow = rows.find(r => r.sub.startsWith('tenant allowance'));
    assert.equal(tenantRow.name, 'acme');
    assert.equal(tenantRow.hasRule, true);
    const keyRow = rows.find(r => r.sub === 'API key on one model');
    assert.equal(keyRow.name, 'billing-worker · gpt-4');
    assert.equal(rows.find(r => r.control === 'streams').hits, '2');
  });
});

test('the rule drawer separates subject traffic from the limit\'s own refusals', async t => {
  const app = loadedApp();

  await t.test('subject traffic is titled and qualified as subject-level', () => {
    const u = app.rlRuleUsageView('model', 'gpt-4');
    assert.equal(u.trafficTitle, 'Traffic from gpt-4');
    assert.match(u.trafficWindow, /last 60 min · subject-level/);
    assert.match(u.trafficText, /120 decisions · 20 refused by any limit · 2\.0 avg req\/min/);
    assert.match(u.trafficNote, /whichever limit decided it/);
    assert.equal(u.refusedText, '9');
    assert.match(HTML, /Refused by this limit <span class="muted">· since restart<\/span>/);
  });

  await t.test('a key is named, not shown as a GUID', () => {
    assert.equal(app.rlRuleUsageView('api_key', KEY_ID).trafficTitle, 'Traffic from billing-worker');
  });

  await t.test('a scope with no section in the report claims nothing about traffic', () => {
    const u = app.rlRuleUsageView('api_key_model', KEY_ID + '|gpt-4');
    assert.equal(u.showTraffic, false);
    assert.equal(u.noSection, true);
    assert.equal(u.trafficText, '');
    assert.equal(u.refusedText, '4');
  });

  await t.test('the gateway rule reads the window totals', () => {
    assert.match(app.rlRuleUsageView('global', '*').trafficText, /^300 decisions · 30 refused/);
  });

  await t.test('absent from a complete list: none recorded', () => {
    const u = loadedApp(report({ byModel: [] })).rlRuleUsageView('model', 'gpt-4');
    assert.match(u.trafficText, /No decisions recorded from gpt-4 in this window/);
  });

  await t.test('absent from a full page: only "not among the busiest" can be said', () => {
    const full = Array.from({ length: app.rlUsageTake() }, (_, i) => usageRow('m' + i, { modelId: 'm' + i }));
    const u = loadedApp(report({ byModel: full })).rlRuleUsageView('model', 'gpt-4');
    assert.match(u.trafficText, /not among the 200 busiest subjects/);
  });

  await t.test('a quiet gateway says so', () => {
    const quiet = report({ byModel: [], totals: { requests: 0, admitted: 0, rejected: 0, rateRejected: 0, concurrencyRejected: 0 } });
    assert.match(loadedApp(quiet).rlRuleUsageView('model', 'gpt-4').trafficText, /No decisions were recorded in this window/);
  });

  await t.test('no report: unavailable', () => {
    const u = loadedApp(null).rlRuleUsageView('model', 'gpt-4');
    assert.equal(u.trafficText, 'Activity is unavailable.');
    assert.equal(u.refusedText, '—');
  });
});

test('traffic can be read by every dimension the payload carries', () => {
  const app = loadedApp();
  assert.deepEqual(Array.from(app.rlUsageTabRows, r => r.label), ['Tenant', 'API key', 'Model', 'Tenant × model']);

  assert.equal(app.rlUsageSubjectView.rows[0].name, 'acme');
  assert.equal(app.rlUsageSubjectView.hasNote, false);

  app.setRateLimitUsageTab('key');
  assert.equal(app.rlUsageSubjectView.rows[0].name, 'billing-worker');

  app.setRateLimitUsageTab('tenantModel');
  assert.equal(app.rlUsageSubjectView.rows[0].name, 'acme · gpt-4');
  assert.match(app.rlUsageSubjectView.note, /refused before the model was read/);

  app.setRateLimitUsageTab('model');
  assert.equal(app.rlUsageSubjectView.rows[0].rpmText, '2.0');
});

test('picking a window loads it', async t => {
  await t.test('choosing a window fetches it at once, with no Refresh click', async () => {
    const app = loadedApp();
    const urls = [];
    app.apiJson = async url => { urls.push(url); return report({ windowMinutes: 15 }); };
    app.setRateLimitUsageMinutes(15);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(urls.length, 1);
    assert.match(urls[0], /minutes=15&take=200/);
    assert.equal(app.rateLimitUsage.windowMinutes, 15);
  });

  await t.test('the segmented control goes through the same path', async () => {
    const app = loadedApp();
    let calls = 0;
    app.apiJson = async () => { calls++; return report({ windowMinutes: 180 }); };
    app.rlActivityView.windowRows.find(w => w.key === 180).select();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls, 1);
    assert.equal(app.rlActivityView.windowRows.find(w => w.key === 180).pressed, 'true');
  });

  await t.test('an older, slower answer cannot overwrite a newer one', async () => {
    const app = loadedApp();
    const pending = [];
    app.apiJson = () => new Promise(resolve => pending.push(resolve));
    app.setRateLimitUsageMinutes(15);
    app.setRateLimitUsageMinutes(180);
    pending[1](report({ windowMinutes: 180 }));
    await new Promise(resolve => setImmediate(resolve));
    pending[0](report({ windowMinutes: 15 }));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.rateLimitUsage.windowMinutes, 180);
    assert.equal(app.rateLimitUsageLoading, false);
  });
});

test('a failed refresh never blanks what is already known', async t => {
  await t.test('the last report stays, marked stale, with its as-of time and a way to retry', async () => {
    const app = loadedApp();
    app.apiJson = async () => { throw Object.assign(new Error('HTTP 500'), { status: 500 }); };
    await app.loadRateLimitUsage();
    assert.ok(app.rateLimitUsage?.totals, 'the previous report is kept');
    const view = app.rlActivityView;
    assert.equal(view.stale, true);
    assert.equal(view.has, true);
    assert.match(view.staleText, /could not be refreshed/);
    assert.notEqual(view.asOfText, '');
    assert.equal(app.rlRuleUsageView('model', 'gpt-4').stale, true);
    // The join still answers from the kept report.
    assert.equal(app.rlRefusalsFor('model', 'gpt-4').hits, 9);
    assert.match(HTML, /x-show="rlActivityView\.stale" role="alert">.*Retry<\/button>/);
  });

  await t.test('a later success clears the stale mark', async () => {
    const app = loadedApp();
    app.apiJson = async () => { throw new Error('boom'); };
    await app.loadRateLimitUsage();
    app.apiJson = async () => report();
    await app.loadRateLimitUsage();
    assert.equal(app.rlActivityView.stale, false);
    assert.equal(app.rateLimitUsageError, '');
  });

  await t.test('with no earlier report the card warns without touching the configuration', async () => {
    const app = loadedApp(null);
    app.apiJson = async () => { throw Object.assign(new Error('HTTP 500'), { status: 500 }); };
    await app.loadRateLimitUsage();
    const view = app.rlActivityView;
    assert.equal(view.failedEmpty, true);
    assert.equal(view.has, false);
    assert.match(view.failedText, /Rules and tiers above are unaffected/);
    assert.ok(app.rlDraft.rules.length > 0);
    assert.equal(app.rateLimitsLoadError || '', '');
  });

  await t.test('a deployment without the tracker is a fact, not a retryable failure', async () => {
    const app = loadedApp(null);
    app.apiJson = async () => { throw Object.assign(new Error('HTTP 503'), { status: 503 }); };
    await app.loadRateLimitUsage();
    assert.equal(app.rlActivityView.unavailable, true);
    assert.equal(app.rlActivityView.failedEmpty, false);
  });
});
