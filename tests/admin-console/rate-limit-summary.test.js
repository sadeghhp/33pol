/**
 * The operational summary, the Activity table's tools and the schedule's labels on
 * Settings → Rate limits.
 *
 *   - the summary answers "is anything being refused" from the usage report and the SAVED schedule,
 *     names each figure's time basis, and offers no "near limit" number it cannot back;
 *   - traffic by subject sorts, filters, and shows load against the last limit seen — with the
 *     caveat kept, because that limit is not any one rule's;
 *   - the calendar, Coming up and Preview name rules the way the list does, and say when the
 *     server trimmed the calendar.
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
  const scrolled = [];
  const context = {
    document: { addEventListener() {}, hidden: false, getElementById: id => ({ scrollIntoView() { scrolled.push(id); }, focus() {} }) },
    window: { addEventListener() {}, AdminIcons: null },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    Alpine: { data() {}, directive() {}, store: () => ({}) },
    setInterval: () => 0, clearInterval() {}, setTimeout: () => 0, clearTimeout() {},
    console, Intl,
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
  const app = context.adminApp();
  app._scrolled = scrolled;
  return app;
}

const clone = v => JSON.parse(JSON.stringify(v));
const KEY_ID = '0b9f6c1e-1111-4222-8333-444455556666';
const rule = (scope, target, rpm, extra = {}) => ({ scope, target, rpm, burst: 10, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });
const WINDOW = { name: 'off-peak', kind: 'weekly', days: ['mon'], start: '19:00', end: '07:00', timeZone: 'UTC', rpm: 120, burst: 0, maxConcurrentStreams: 0 };
const CONFIG = {
  version: 1, enabled: true, adaptiveEnabled: true,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans: {},
  rules: [rule('api_key_model', KEY_ID + '|gpt-4', 600, { schedule: [WINDOW] }), rule('model', 'llama', 1200)],
};
const row = (key, o) => ({ key, tenantId: null, apiKeyId: null, modelId: null, requests: 0, admitted: 0, rejected: 0, requestsPerMinute: 0, configuredRpm: 0, effectiveRpm: 0, ...o });
const USAGE = {
  windowMinutes: 60, generatedUtc: '2026-09-21T10:00:08Z',
  totals: { requests: 57210, admitted: 56006, rejected: 1204, rateRejected: 1100, concurrencyRejected: 104 },
  byTenant: [row('t1', { tenantId: 't1', requests: 100, rejected: 4, requestsPerMinute: 300, configuredRpm: 600, effectiveRpm: 600 }), row('t2', { tenantId: 't2', requests: 900 })],
  byApiKey: [row(KEY_ID, { apiKeyId: KEY_ID, requests: 12400, rejected: 812, requestsPerMinute: 206.7, configuredRpm: 120, effectiveRpm: 120 }), row('other', { apiKeyId: 'other', requests: 20000, requestsPerMinute: 96, configuredRpm: 120, effectiveRpm: 120 })],
  byModel: [], byTenantModel: [],
  violations: [{ scope: 'api_key_model', key: KEY_ID + '|gpt-4', control: 'rate', hits: 800 }, { scope: 'api_key_model', key: KEY_ID + '|gpt-4', control: 'concurrency', hits: 12 }, { scope: 'model', key: 'llama', control: 'rate', hits: 3 }],
  adaptive: { enabled: true, lastEvaluatedUtc: '2026-09-21T10:00:00Z', backedOffPartitions: 0, models: [{ modelId: 'llama', factor: 0.7, saturation: 0.9, reason: 'queue depth' }] },
  store: { requestPartitions: 412, streamPartitions: 38, maxPartitions: 10000 },
};
const SCHEDULE = (extra = {}) => ({
  rules: [{ scope: 'api_key_model', target: KEY_ID + '|gpt-4', base: { rpm: 600 }, effective: { rpm: 120, burst: 0, maxConcurrentStreams: 0 }, activeWindow: 'off-peak', activeUntil: '2099-01-01T07:00:00Z', nextChangeAt: '2099-01-01T07:00:00Z', windows: [] }],
  occurrences: [], transitions: [{ at: '2099-01-01T07:00:00Z', scope: 'api_key_model', target: KEY_ID + '|gpt-4', window: null, from: { rpm: 120 }, to: { rpm: 600 } }],
  transitionsTotal: 1, transitionsTruncated: false, ...extra,
});

function page(usage = USAGE) {
  const app = createApp();
  app.keys = [{ id: KEY_ID, label: 'prod-chatbot', keyPrefix: 'pk_live_ab' }];
  app.loadRateLimitSchedule = async () => {};
  app.queueRateLimitScheduleRefresh = () => {};
  app.loadRateLimitKeys = () => {};
  app.applyRateLimitsData(clone(CONFIG));
  app.rateLimitUsage = usage ? clone(usage) : null;
  app.rlScheduleSaved = SCHEDULE();
  app.rlSchedule = SCHEDULE();
  return app;
}
const stat = (app, key) => app.rlSummaryView.stats.find(s => s.key === key);

test('the summary says whether traffic is being refused, by whom, and on which clock', async t => {
  await t.test('each figure names its time basis', () => {
    const app = page();
    assert.deepEqual(app.rlSummaryView.stats.map(s => s.label), [
      'Refused · last 60 min', 'Refused subjects · last 60 min', 'Limits that refused · since restart',
      'Windows active now', 'Next scheduled change']);
    assert.deepEqual([stat(app, 'refused').value, stat(app, 'refused').sub], ['1,204 · 2.1 %', '1,100 rate · 104 streams']);
    assert.equal(stat(app, 'subjects').value, '1 key · 1 tenant');
    assert.equal(stat(app, 'limits').value, '2', 'one limit refusing on two controls is one limit');
    assert.equal(stat(app, 'active').value, '1');
    assert.equal(app.rlSummaryView.health, 'refusing 2.1 % of requests');
    assert.match(app.rlSummaryView.adaptiveDetail, /^1 model reduced · evaluated 8 s before this reading$/);
  });

  await t.test('a quiet gateway says so in words rather than showing a blank', () => {
    const quiet = clone(USAGE);
    Object.assign(quiet.totals, { rejected: 0, rateRejected: 0, concurrencyRejected: 0 });
    quiet.byApiKey.forEach(r => { r.rejected = 0; }); quiet.byTenant.forEach(r => { r.rejected = 0; });
    const app = page(quiet);
    assert.equal(app.rlSummaryView.health, 'nothing refused in the last 60 min');
    assert.equal(stat(app, 'subjects').value, 'none');
    assert.doesNotMatch(stat(app, 'refused').cls, /warn/);
  });

  await t.test('it offers no "near limit" figure: the report cannot attribute a rate to a rule', () => {
    const labels = page().rlSummaryView.stats.map(s => s.label).join(' | ');
    assert.doesNotMatch(labels, /near|utili[sz]ation|capacity/i);
  });

  await t.test('schedule figures come from the saved report, so a staged window is not "active"', () => {
    const app = page();
    app.rlScheduleSaved = { rules: [], occurrences: [], transitions: [] };
    assert.equal(stat(app, 'active').value, '0');
    assert.equal(stat(app, 'next').value, '—');
  });

  await t.test('without activity the schedule figures remain and nothing is invented', () => {
    const app = page(null);
    assert.deepEqual(app.rlSummaryView.stats.map(s => s.key), ['active', 'next']);
    assert.equal(app.rlSummaryView.health, '');
    assert.equal(app.rlSummaryView.usageMissing, true);
  });

  await t.test('every figure is a way in: to the filter or the section that explains it', () => {
    const app = page();
    stat(app, 'limits').go();
    assert.deepEqual([app.rlFilterFlags.refused, app.rlSortKey, app.rlSortDir], [true, 'refused', -1]);
    assert.deepEqual(app.rlRuleRows.map(r => r.refused), ['812', '3']);
    stat(app, 'active').go();
    assert.deepEqual([app.rlFilterFlags.refused, app.rlFilterFlags.active], [false, true], 'one question at a time');
    stat(app, 'subjects').go();
    assert.deepEqual([app.rlUsageTab, app.rlUsageSortKey, app.rlUsageSortDir], ['key', 'refused', -1]);
    stat(app, 'next').go();
    assert.deepEqual(app._scrolled.slice(-2), ['rate-limit-usage', 'rl-schedule']);
  });

  await t.test('the markup: buttons with names, the age of the data, and links to each section', () => {
    assert.match(HTML, /<button type="button" :class="m\.cls" @click="m\.go" :title="m\.title" :aria-label="m\.ariaLabel">/);
    assert.match(HTML, /<nav class="rl-anchors" aria-label="Rate limits sections">/);
    assert.match(HTML, /class="rl-fresh" role="status"><span x-show="rlActivityFreshView\.has" x-text="rlActivityFreshView\.text">/);
    for (const id of ['rl-rules', 'rate-limit-usage', 'rl-schedule', 'rl-baselines']) assert.match(HTML, new RegExp('id="' + id + '"'));
    const order = ['id="rl-rules"', 'id="rate-limit-usage"', 'id="rl-schedule"', 'id="rl-baselines"', '<h3>Protective limits</h3>', 'class="rl-savebar"'].map(m => HTML.indexOf(m));
    assert.deepEqual(order, [...order].sort((a, b) => a - b), 'rules, activity, calendar, baselines, save bar');
  });
});

test('traffic by subject: sorted, searchable, and honest about load', async t => {
  await t.test('most refused first by default; a header flips or changes the order', () => {
    const app = page();
    app.setRateLimitUsageTab('key');
    assert.deepEqual(app.rlUsageSubjectView.rows.map(r => r.name), ['prod-chatbot', 'other']);
    assert.equal(app.rlUsageSubjectView.sort.refused.ariaSort, 'descending');
    app.setRateLimitUsageSort('decisions');
    assert.deepEqual(app.rlUsageSubjectView.rows.map(r => r.name), ['other', 'prod-chatbot']);
    app.setRateLimitUsageSort('decisions');
    assert.equal(app.rlUsageSubjectView.sort.decisions.ariaSort, 'ascending');
  });

  await t.test('the filter finds by name or id and says how many it kept', () => {
    const app = page();
    app.setRateLimitUsageTab('key');
    app.rlUsageFilter = 'chatbot';
    assert.deepEqual([app.rlUsageSubjectView.rows.length, app.rlUsageSubjectView.countText], [1, '1 of 2']);
    app.rlUsageFilter = 'zzz';
    assert.deepEqual([app.rlUsageSubjectView.noMatch, app.rlUsageSubjectView.has], [true, true]);
  });

  await t.test('load is the window average over the last limit seen, flagged hot and over, absent without a limit', () => {
    const app = page();
    app.setRateLimitUsageTab('key');
    const [hot, warm] = app.rlUsageSubjectView.rows;
    assert.deepEqual([hot.loadText, hot.loadCls, hot.loadStyle], ['172%', 'load-fill is-over', 'width:100%']);
    assert.deepEqual([warm.loadText, warm.loadCls], ['80%', 'load-fill is-hot']);
    app.setRateLimitUsageTab('tenant');
    const none = app.rlUsageSubjectView.rows.find(r => r.name === 't2');
    assert.deepEqual([none.hasLoad, none.noLoad, none.loadText], [false, true, '']);
  });

  await t.test('the caveat travels with the column, and rules get no utilisation of their own', () => {
    assert.match(HTML, />Load vs last limit<span/);
    assert.match(HTML, /not any one rule’s utilisation/);
    assert.match(HTML, /never the utilisation of one rule/);
    // A rule row carries its own limit's counters and nothing from the subject rows: the fixture
    // has subject traffic but no per-limit section, so the cell says so instead of borrowing it.
    const ruleRow = page().rlRuleRows[0];
    assert.equal(Object.keys(ruleRow).some(k => /util|load/i.test(k)), false);
    assert.equal(ruleRow.hasActBar, false);
    assert.equal(ruleRow.actText, '—');
    assert.match(ruleRow.actTitle, /does not report activity per limit/);
    assert.match(HTML, /reported by the gateway per limit/);
  });

  await t.test('from a subject to its rules, and from a key to a new rule for it', () => {
    const app = page();
    app.setRateLimitUsageTab('key');
    const first = app.rlUsageSubjectView.rows[0];
    first.showRules();
    assert.equal(app.rlFilterText, 'prod-chatbot');
    assert.deepEqual(app.rlRuleRows.map(r => r.who), ['prod-chatbot']);
    assert.equal(first.canLimit, true);
    first.limit();
    assert.equal(app.rlNewRuleOpen, true);
    app.rlNewRuleOpen = false;
    app.rlReadOnlyReason = 'read-only';
    assert.equal(app.rlUsageSubjectView.rows[0].canLimit, false);
  });

  await t.test('an empty Model tab explains itself when no per-model rule exists', () => {
    const app = page();
    app.rateLimits = { ...clone(CONFIG), rules: [rule('tenant', 'acme', 10)] };
    app.setRateLimitUsageTab('model');
    assert.match(app.rlUsageSubjectView.emptyText, /only while at least one per-model rule exists/);
    const withRule = page();
    withRule.setRateLimitUsageTab('model');
    assert.equal(withRule.rlUsageSubjectView.emptyText, 'Nothing recorded under this heading in this window.');
  });

  await t.test('adaptive shedding that is on but idle still has a line; the footer counts stream slots', () => {
    const idle = clone(USAGE); idle.adaptive.models = [];
    const app = page(idle);
    assert.deepEqual([app.rlActivityView.adaptiveOn, app.rlActivityView.adaptiveIdle], [true, true]);
    assert.equal(page().rlActivityView.adaptiveIdle, false);
    assert.equal(page().rlActivityView.streamSlotsText, '38');
  });

  await t.test('a large rule set asks the report for more rows, within the endpoint cap', () => {
    const app = page();
    assert.equal(app.rlUsageTake(), 200);
    app.rateLimits = { ...clone(CONFIG), rules: Array.from({ length: 650 }, (_, i) => rule('model', 'm' + i, 10)) };
    assert.equal(app.rlUsageTake(), 650);
    app.rateLimits = { ...clone(CONFIG), rules: Array.from({ length: 2000 }, (_, i) => rule('model', 'm' + i, 10)) };
    assert.equal(app.rlUsageTake(), 1000);
  });
});

test('the schedule names rules the way the list does', async t => {
  await t.test('calendar rows, Coming up and Preview show the key name, keep the id as a title, and open the rule', () => {
    const app = page();
    const tl = app.rlTimelineView.rows[0];
    assert.deepEqual([tl.label, tl.labelTitle], ['prod-chatbot · gpt-4', KEY_ID + '|gpt-4']);
    const coming = app.rlTransitionRows[0];
    assert.equal(coming.target, 'prod-chatbot · gpt-4');
    coming.open();
    assert.equal(app.rlRule.identity, 'api_key_model:' + KEY_ID + '|gpt-4');
    app.closeRateLimitRule();
    app.rlPreview = { at: '2099-01-01T00:00:00Z', rules: SCHEDULE().rules };
    assert.equal(app.rlPreviewView.rows[0].target, 'prod-chatbot · gpt-4');
    assert.doesNotMatch(JSON.stringify([tl.label, coming.target, app.rlPreviewView.rows[0].target]), new RegExp(KEY_ID));
  });

  await t.test('a trimmed calendar says so', () => {
    const app = page();
    assert.equal(app.rlTimelineView.truncatedNote, '');
    app.rlSchedule = SCHEDULE({ occurrences: new Array(3).fill({ scope: 'model', target: 'x', window: 'w', start: '2099-01-01T00:00:00Z', end: '2099-01-01T01:00:00Z', tier: {} }), occurrencesTotal: 2310, occurrencesTruncated: true });
    assert.match(app.rlTimelineView.truncatedNote, /first 3 of 2,310 window occurrences/);
    assert.match(HTML, /x-show="rlTimelineView\.truncatedNote" x-text="rlTimelineView\.truncatedNote"/);
  });
});

test('the rule drawer puts production beside what saving would do', () => {
  const app = page();
  app.openRateLimitRule('model:llama');
  let view = app.rlRuleDrawerView;
  assert.equal(view.forceBig, '1,200 rpm · 10 burst');
  assert.match(view.forceSub, /base · adaptive ×0\.70 · ≈ 840 of 1,200 rpm/);
  assert.equal(view.hasAfterSave, false, 'nothing to say while the working copy matches what is saved');

  app.rlRule.rpm = 450;
  view = app.rlRuleDrawerView;
  assert.equal(view.forceBig, '1,200 rpm · 10 burst', 'production is not rewritten by typing');
  assert.equal(view.afterSave, 'base 450 rpm · 10 burst');
  app.rlRule.enabled = false;
  assert.match(app.rlRuleDrawerView.afterSave, /^off — enforces nothing; 450 rpm · 10 burst kept$/);
  assert.match(HTML, /Now in production/);
  assert.match(HTML, /x-show="rlRuleDrawerView\.hasAfterSave"/);
});
