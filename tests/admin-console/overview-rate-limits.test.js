/**
 * The Overview's rate-limit section (/admin/api/overview/rate-limits):
 *
 *   - the glance group and the card read the section as served — refusals, subjects, limits,
 *     schedule — and say which clock each figure is on;
 *   - zero traffic is a quiet card, a failed load is an error, and a 204 (no tracker) hides it;
 *   - a percentage of a limit appears only where the server sent peakUtilization; nothing is
 *     synthesised from per-subject figures;
 *   - a limit row opens Settings → Rate limits on the rule with that stable identity;
 *   - on a wallboard only the headline travels.
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
const CSS = fs.readFileSync(path.join(ADMIN, 'admin.css'), 'utf8');

function createApp() {
  const timers = [];
  const context = {
    document: { addEventListener() {}, hidden: false, getElementById: () => ({ scrollIntoView() {}, focus() {} }) },
    window: { addEventListener() {}, AdminIcons: null },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    location: { hash: '' },
    history: { replaceState() {} },
    Alpine: { data() {}, directive() {}, store: () => ({}) },
    setInterval: () => 0, clearInterval() {}, setTimeout: fn => { timers.push(fn); return 0; }, clearTimeout() {},
    console, Intl,
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
  const app = context.adminApp();
  app._timers = timers;
  return app;
}

const clone = v => JSON.parse(JSON.stringify(v));
const inHours = h => new Date(Date.now() + h * 3600000).toISOString();

const QUIET = {
  builtAtUtc: '2026-09-22T10:00:00Z',
  enforced: true, adaptiveEnabled: false, ruleCount: 3, disabledRuleCount: 0, configReloadInProgress: false,
  schedule: { available: true, scheduledRuleCount: 0, windowsActiveNow: 0, nextChangeAtUtc: null, nextChangeRuleId: null, nextChangeWindow: null },
  retention: { historyMinutes: 180, trackingSinceUtc: '2026-09-22T08:00:00Z', processLocal: true },
  lastHour: { minutes: 60, decisions: 0, admitted: 0, refused: 0, refusedByRate: 0, refusedByStreams: 0, refusalShare: 0 },
  lastFiveMinutes: { minutes: 5, decisions: 0, admitted: 0, refused: 0, refusedByRate: 0, refusedByStreams: 0, refusalShare: 0 },
  refusedTenantCount: 0, refusedKeyCount: 0, refusedSubjectsTruncated: false,
  topRefusedTenants: [], topRefusedKeys: [],
  refusingLimitCount: 0, limits: [],
  protective: [
    { scope: 'auth_failure', checked: 0, refused: 0, enforcedRpm: 0, lastDecisionUtc: null },
    { scope: 'anonymous', checked: 0, refused: 0, enforcedRpm: 0, lastDecisionUtc: null },
  ],
  adaptive: { enabled: false, modelsReduced: 0, lowestFactor: null, lowestFactorModelId: null, backedOffPartitions: 0, lastEvaluatedUtc: null, shedding: false },
  tracker: { isSaturated: false, droppedDecisions: 0, firstDroppedUtc: null, maxKeysPerDimension: 500, atCapacity: [] },
  store: { requestPartitions: 120, streamPartitions: 4, maxPartitions: 50000, ratio: 0.0024 },
};

const limit = (o) => ({
  limitId: 'model:gpt-4', ruleId: 'model:gpt-4', scope: 'model', target: 'gpt-4', anonymousBucket: false, singleBucket: true,
  evaluations: 400, refused: 30, refusedByRate: 30, refusedByStreams: 0, configuredRpm: 100, effectiveRpm: 100,
  peakUtilization: 1.12, lastDecisionUtc: '2026-09-22T10:00:00Z', ...o,
});

function refusing() {
  const r = clone(QUIET);
  r.lastHour = { minutes: 60, decisions: 1000, admitted: 880, refused: 120, refusedByRate: 118, refusedByStreams: 2, refusalShare: 0.12 };
  r.lastFiveMinutes = { minutes: 5, decisions: 80, admitted: 70, refused: 10, refusedByRate: 10, refusedByStreams: 0, refusalShare: 0.125 };
  r.refusedTenantCount = 2; r.refusedKeyCount = 1;
  r.topRefusedTenants = [
    { key: 'a1b2', label: 'acme', tenantSlug: 'acme', anonymous: false, decisions: 600, refused: 90 },
    { key: 'anon:203.0.113.0/24', label: 'anonymous', tenantSlug: null, anonymous: true, decisions: 20, refused: 10 },
  ];
  r.topRefusedKeys = [{ key: 'k-1', label: 'ci-runner', tenantSlug: 'acme', anonymous: false, decisions: 500, refused: 80 }];
  r.refusingLimitCount = 2;
  r.limits = [
    limit(),
    limit({ limitId: 'plan:pro', ruleId: null, scope: 'plan', target: 'pro', singleBucket: false, refused: 12, refusedByRate: 12, peakUtilization: null }),
    limit({ limitId: 'tenant_model:acme|llama', ruleId: 'tenant_model:acme|llama', scope: 'tenant_model', target: 'acme|llama', refused: 0, refusedByRate: 0, peakUtilization: 0.85 }),
  ];
  r.schedule = { available: true, scheduledRuleCount: 2, windowsActiveNow: 1, nextChangeAtUtc: inHours(2), nextChangeRuleId: 'model:gpt-4', nextChangeWindow: null };
  return r;
}

const glance = app => app.glanceGroups.find(g => g.key === 'rateLimits');

// --- glance group -------------------------------------------------------------------------------

test('glance group carries the five figures in order, from the section as served', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();

  const g = glance(app);
  assert.ok(g, 'the group is present');
  assert.deepEqual(g.stats.map(s => s.label), ['Refused · 1h', 'Refused subjects', 'Limits refusing', 'Windows active', 'Next change']);
  const v = Object.fromEntries(g.stats.map(s => [s.key, s.value]));
  assert.equal(v.refused, '120 · 12%');
  assert.equal(v.subjects, '2 tenants · 1 key');
  assert.equal(v.limits, '2');
  assert.equal(v.windows, '1');
  assert.match(v.next, /^in (1 h 5\d|2 h 0) m$/);
  assert.equal(g.stats.find(s => s.key === 'refused').cls, 'is-warn');
  assert.equal(g.hint, '');
  assert.equal(g.hasOpen, true);
});

test('glance: truncated subject lists are lower bounds, marked with +', () => {
  const app = createApp();
  const r = refusing();
  r.refusedSubjectsTruncated = true;
  app.overviewRateLimits = r;

  assert.equal(glance(app).stats.find(s => s.key === 'subjects').value, '2+ tenants · 1+ key');
});

test('glance: zero traffic is a quiet group, not a missing one', () => {
  const app = createApp();
  app.overviewRateLimits = clone(QUIET);

  const g = glance(app);
  assert.ok(g);
  assert.equal(g.stats.find(s => s.key === 'refused').value, '0');
  assert.equal(g.stats.find(s => s.key === 'refused').cls, '');
  assert.equal(g.stats.find(s => s.key === 'next').value, 'none');
  assert.equal(g.hint, 'No rate-limit decisions in the last hour.');
});

test('glance: traffic with no refusals says so', () => {
  const app = createApp();
  const r = clone(QUIET);
  r.lastHour.decisions = 500; r.lastHour.admitted = 500;
  app.overviewRateLimits = r;

  assert.equal(glance(app).hint, 'Nothing refused in the last hour.');
});

test('glance: an unreadable schedule is unknown, not zero', () => {
  const app = createApp();
  const r = clone(QUIET);
  r.schedule = { available: false, scheduledRuleCount: 0, windowsActiveNow: 0, nextChangeAtUtc: null, nextChangeRuleId: null, nextChangeWindow: null };
  app.overviewRateLimits = r;

  const g = glance(app);
  assert.equal(g.stats.find(s => s.key === 'windows').value, '—');
  assert.equal(g.stats.find(s => s.key === 'next').value, '—');
});

test('no section (204) and not yet loaded: no group, no card', () => {
  const app = createApp();
  assert.equal(glance(app), undefined);
  assert.equal(app.hasRateLimits, false);
  assert.equal(app.showRateLimitsCard, false);
  assert.equal(app.rateLimitsOverviewView.has, false);
});

// --- card -----------------------------------------------------------------------------------------

test('card: title and head stats name enforcement and the one-hour clock', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();
  const v = app.rateLimitsOverviewView;

  assert.equal(v.title, 'Enforced · refusing 12% of decisions in the last hour');
  assert.deepEqual(v.headStats.map(m => m.label), ['Refused · 1h', 'Windows active', 'Next change']);
  assert.equal(v.headStats[0].cls, 'mini-stat warn');
  assert.equal(v.fiveMinText, '10 refused of 80 · last 5 min');
  assert.equal(v.quiet, false);
});

test('card: quiet states distinguish no activity from no refusals', () => {
  const app = createApp();
  app.overviewRateLimits = clone(QUIET);
  let v = app.rateLimitsOverviewView;
  assert.equal(v.quiet, true);
  assert.equal(v.title, 'Enforced · no decisions in the last hour');
  assert.equal(v.quietText, 'No rate-limit decisions in the last hour.');

  const r = clone(QUIET);
  r.lastHour.decisions = 42;
  app.overviewRateLimits = r;
  v = app.rateLimitsOverviewView;
  assert.equal(v.title, 'Enforced · nothing refused in the last hour');
  assert.equal(v.quietText, 'Nothing refused in the last hour.');
});

test('card: an error with no data shows the card with only the error; with data it keeps the data', () => {
  const app = createApp();
  app.overviewSectionErrors.rateLimits = 'Could not refresh. The gateway did not answer. Showing the last successful result.';

  assert.equal(app.showRateLimitsCard, true);
  assert.equal(app.hasRateLimits, false, 'the data block stays hidden');
  assert.equal(app.hasRateLimitsError, true);

  app.overviewRateLimits = clone(QUIET);
  assert.equal(app.hasRateLimits, true);
  assert.equal(app.hasRateLimitsError, true, 'stale data is shown with the refresh error');
});

test('card: utilisation meters appear only where the server sent peakUtilization', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();
  const rows = app.rateLimitsOverviewView.limitRows;

  const rule = rows.find(r => r.limitId === 'model:gpt-4');
  assert.equal(rule.hasMeter, true);
  assert.equal(rule.meterStyle, 'width:100%', 'capped at the track');
  assert.equal(rule.meterCls, 'load-fill is-over');
  assert.equal(rule.peakText, '112% peak');
  assert.equal(rule.countText, '30 refused');

  const plan = rows.find(r => r.limitId === 'plan:pro');
  assert.equal(plan.hasMeter, false, 'a tier sums many buckets: no percentage');
  assert.equal(plan.peakText, '');
  assert.equal(plan.meterStyle, '');
  assert.match(plan.title, /no percentage of the limit is shown/);
  assert.equal(plan.label, 'Plan pro');

  const near = rows.find(r => r.limitId === 'tenant_model:acme|llama');
  assert.equal(near.meterCls, 'load-fill is-hot');
  assert.equal(near.countText, 'near limit');
  assert.match(near.label, /acme · llama$/);
});

test('card: never synthesises a percentage from per-subject figures', () => {
  const app = createApp();
  const r = refusing();
  r.limits = [limit({ peakUtilization: null, singleBucket: true })];
  // Fields a client might be tempted to divide: none of them may produce a meter.
  r.topRefusedTenants[0].utilization = 3.2;
  app.overviewRateLimits = r;
  const v = app.rateLimitsOverviewView;

  assert.equal(v.limitRows[0].hasMeter, false);
  assert.equal(v.limitRows[0].peakText, '');
  assert.ok(v.tenantRows.every(t => !('utilization' in t)));
});

test('card: subject rows use resolved labels and keys open the Keys page by label', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();
  const v = app.rateLimitsOverviewView;

  assert.deepEqual(v.tenantRows.map(t => t.label), ['acme', 'anonymous']);
  assert.equal(v.keyRows[0].label, 'ci-runner');
  assert.equal(v.keyRows[0].sub, 'acme');
  let opened = null;
  app.openLink = link => { opened = link; };
  v.keyRows[0].open();
  assert.deepEqual(JSON.parse(JSON.stringify(opened)), { tab: 'keys', params: { q: 'ci-runner' } });
});

test('card: protective, adaptive, store and retention lines', () => {
  const app = createApp();
  const r = refusing();
  r.protective[0].refused = 7;
  r.adaptive = { enabled: true, modelsReduced: 2, lowestFactor: 0.5, lowestFactorModelId: 'gpt-4o', backedOffPartitions: 1, lastEvaluatedUtc: null, shedding: true };
  r.store = { requestPartitions: 45000, streamPartitions: 10, maxPartitions: 50000, ratio: 0.9 };
  app.overviewRateLimits = r;
  const v = app.rateLimitsOverviewView;

  assert.equal(v.protectiveText, 'Protective budgets · last hour: failed credentials 7 refused, anonymous callers 0 refused.');
  assert.equal(v.adaptiveText, 'Adaptive load shedding is reducing 2 models (lowest ×0.5 on gpt-4o).');
  assert.equal(v.adaptiveCls, 'hint wb-hide rl-ov-warn');
  assert.equal(v.storeText, 'Partition table 45,000 of 50,000 (90%).');
  assert.match(v.retentionText, /about 3 h kept\. Not durable: counts restart with the gateway and are not shared between replicas\.$/);

  r.adaptive = { enabled: false, modelsReduced: 0, lowestFactor: null, lowestFactorModelId: null, backedOffPartitions: 0, lastEvaluatedUtc: null, shedding: false };
  app.overviewRateLimits = clone(r);
  assert.equal(app.rateLimitsOverviewView.adaptiveText, 'Adaptive load shedding is off.');
  assert.equal(app.rateLimitsOverviewView.adaptiveCls, 'hint wb-hide');
});

test('card: saturation is a notice with the gateway\'s own numbers; a full-but-lossless tracker is a note', () => {
  const app = createApp();
  const r = clone(QUIET);
  r.tracker = { isSaturated: true, droppedDecisions: 12, firstDroppedUtc: '2026-09-22T09:00:00Z', maxKeysPerDimension: 500, atCapacity: ['tenants'] };
  app.overviewRateLimits = r;
  let v = app.rateLimitsOverviewView;
  assert.equal(v.saturated, true);
  assert.match(v.saturatedText, /^Activity is incomplete: 12 decisions were not counted .* at most 500 keys per dimension \(full: tenants\)\. .*a missing row is unknown, not zero\.$/);
  assert.equal(glance(app).hint, 'Counts are incomplete — some decisions were not counted.');

  r.tracker = { isSaturated: false, droppedDecisions: 0, firstDroppedUtc: null, maxKeysPerDimension: 500, atCapacity: ['apiKeys'] };
  app.overviewRateLimits = clone(r);
  v = app.rateLimitsOverviewView;
  assert.equal(v.saturated, false);
  assert.equal(v.trackerFull, true);
});

test('card: not enforced is said in words, and only as a state, never from missing refusals', () => {
  const app = createApp();
  const r = clone(QUIET);
  r.enforced = false;
  app.overviewRateLimits = r;
  const v = app.rateLimitsOverviewView;

  assert.equal(v.notEnforced, true);
  assert.equal(v.title, 'Not enforced — 3 rules configured');
  assert.equal(v.quiet, false);
  assert.equal(glance(app).hint, 'Rate limits are switched off.');

  app.overviewRateLimits = clone(QUIET);
  assert.equal(app.rateLimitsOverviewView.notEnforced, false, 'zero refusals is not "not enforced"');
});

test('card: next change shows relative time with the absolute time and rule in the title', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();
  const v = app.rateLimitsOverviewView;

  assert.match(v.nextText, /^in /);
  assert.match(v.nextTitle, /^Next scheduled change .* · model:gpt-4$/);
  assert.match(v.scheduleTitle, /^2 rules with a schedule; 1 with a window in force now$/);
});

// --- navigation -----------------------------------------------------------------------------------

test('a limit row opens Settings → Rate limits on the rule with that identity', () => {
  const app = createApp();
  app.overviewRateLimits = refusing();
  const links = [];
  const realOpenLink = app.openLink;
  app.openLink = link => links.push(JSON.parse(JSON.stringify(link)));
  app.rateLimitsOverviewView.limitRows[0].open();
  app.rateLimitsOverviewView.limitRows[1].open();

  assert.deepEqual(links[0], { tab: 'settings', params: { sub: 'limits', rule: 'model:gpt-4', section: '' } });
  assert.deepEqual(links[1], { tab: 'settings', params: { sub: 'limits', rule: '', section: 'rl-baselines' } },
    'a plan tier is edited as a baseline, not a rule');
  app.openLink = realOpenLink;
});

test('the pending rule is opened by identity once the rules are loaded', () => {
  const app = createApp();
  app.rlDraft = { enabled: true, rules: [{ scope: 'model', target: 'GPT-4', rpm: 100, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] }] };
  const opened = [];
  app.openRateLimitRule = id => opened.push(id);
  app.rlPendingRule = 'model:gpt-4';

  app.openPendingRateLimitRule();

  assert.deepEqual(opened, ['model:gpt-4'], 'identity is case-insensitive scope:target, never display text');
  assert.equal(app.rlPendingRule, '');
  app.openPendingRateLimitRule();
  assert.equal(opened.length, 1, 'runs once per link');
});

test('openLink records the rule for Settings → Rate limits and a missing rule falls back to the filtered list', () => {
  const app = createApp();
  app.applyTab = () => {};
  app.setSettingsSubTab = () => {};
  app.openLink({ tab: 'settings', params: { sub: 'limits', rule: 'API_KEY:K-9', section: '' } });
  assert.equal(app.rlPendingRule, 'api_key:k-9');

  const filtered = [];
  app.rlDraft = { rules: [] };
  app.showRateLimitRulesFor = t => filtered.push(t);
  app.openPendingRateLimitRule();
  assert.deepEqual(filtered, ['k-9']);

  app.openLink({ tab: 'settings', params: { sub: 'runtime', rule: 'model:x' } });
  assert.equal(app.rlPendingRule, '', 'only the limits page takes a rule');
});

// --- loading and wallboard -----------------------------------------------------------------------

test('rate limits load with the slow sections, on a wallboard too, at the same cadence', () => {
  const app = createApp();
  const src = fs.readFileSync(SOURCE, 'utf8');
  assert.match(src, /loadOverviewRateLimits\(\) \{\s*return this\._loadOverviewSection\('rateLimits', '\/admin\/api\/overview\/rate-limits'/);

  app.wallboard = false;
  assert.equal(app.overviewSlowLoaders().length, 6);
  app.wallboard = true;
  assert.equal(app.overviewSlowLoaders().length, 2, 'policy and rate limits only');
  assert.equal(app.overviewSectionErrors.rateLimits, '');
});

test('markup: the card sits beside Policy → Pressure and binds the view', () => {
  const policy = HTML.indexOf('<div class="card policy-card"');
  const card = HTML.indexOf('<div class="card rate-limits-card" x-show="showRateLimitsCard">');
  const finops = HTML.indexOf('<div class="card finops-card"');
  assert.ok(policy > 0 && card > policy && card < finops, 'after the policy card, before FinOps');
  const block = HTML.slice(card, HTML.indexOf('<!-- FinOps + reliability', card));

  assert.match(block, /x-text="rateLimitsOverviewView\.title"/);
  assert.match(block, /x-for="l in rateLimitsOverviewView\.limitRows"/);
  assert.match(block, /<button type="button" class="rl-ov-limit" @click="l\.open"/);
  assert.match(block, /<span class="load-track" x-show="l\.hasMeter">/);
  assert.match(block, /x-show="rateLimitsOverviewView\.quiet" x-text="rateLimitsOverviewView\.quietText"/);
  assert.match(block, /<p class="notice warn" x-show="hasRateLimitsError" x-text="rateLimitsError"><\/p>/);
  assert.match(block, /x-text="rateLimitsOverviewView\.retentionText"/);
  assert.doesNotMatch(block, /(?:x-text|x-show|:class|:style|:title)="[^"]*[?!][^"]*"/, 'no ternaries or negation in bindings (CSP build)');
});

test('wallboard: headline only — every list and detail line is .wb-hide, and the card itself travels', () => {
  const card = HTML.indexOf('<div class="card rate-limits-card"');
  const block = HTML.slice(card, HTML.indexOf('<!-- FinOps + reliability', card));

  const limits = block.indexOf('<div class="policy-section wb-hide" x-show="rateLimitsOverviewView.hasLimitRows">');
  const columns = block.indexOf('<div class="policy-columns wb-hide">');
  assert.ok(limits > 0 && limits < block.indexOf('rateLimitsOverviewView.limitRows'), 'the rule list is hidden on a wallboard');
  assert.ok(columns > 0 && columns < block.indexOf('rateLimitsOverviewView.tenantRows')
    && columns < block.indexOf('rateLimitsOverviewView.keyRows'), 'tenant and key lists are hidden on a wallboard');
  for (const line of ['protectiveText', 'storeText', 'retentionText', 'fiveMinText', 'trackerFullText']) {
    assert.match(block, new RegExp('<p class="hint[^"]*wb-hide"[^>]*rateLimitsOverviewView\\.' + line), line + ' is desk-only');
  }
  assert.match(block, /x-show="hasRateLimits">\s*<template x-for="m in rateLimitsOverviewView\.headStats"/, 'head stats are not hidden');

  const wallboardHidden = CSS.slice(CSS.indexOf('/* What does not travel to a wall. */'), CSS.indexOf('{ display: none !important; }', CSS.indexOf('/* What does not travel to a wall. */')));
  assert.doesNotMatch(wallboardHidden, /rate-limits-card/, 'the card keeps its headline on a board');
  assert.match(HTML, /<div class="glance-grid wb-hide"/, 'the glance tile is desk-only with the rest of the grid');
});

test('asset versions were bumped for this change', () => {
  assert.match(HTML, /admin-app\.js\?v=(\d+)/);
  assert.ok(Number(/admin-app\.js\?v=(\d+)/.exec(HTML)[1]) >= 56);
  assert.ok(Number(/admin\.css\?v=(\d+)/.exec(HTML)[1]) >= 36);
});
