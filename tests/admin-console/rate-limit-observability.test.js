/**
 * What the gateway now reports about the limits themselves, and how far the page trusts it:
 *
 *   - read-only is known on load (`writable`), not after a failed save;
 *   - a full tracker is said out loud, and a missing row reads "unknown", never zero;
 *   - a rule's activity comes from the per-limit report, joined by the rule's identity and nothing
 *     else, and utilisation is shown only where the gateway computed one;
 *   - protective limits have their own figures with their own meaning;
 *   - the trend skips minutes the gateway did not observe;
 *   - history is a view of the audit trail, and the rule drawer finds its rule in it by id.
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
    document: { addEventListener() {}, hidden: false, getElementById: () => ({ scrollIntoView() {}, focus() {} }) },
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

const clone = v => JSON.parse(JSON.stringify(v));
const rule = (scope, target, rpm, extra = {}) => ({ scope, target, rpm, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });
const CONFIG = {
  version: 7, enabled: true, adaptiveEnabled: false,
  default: { rpm: 60, burst: 0, maxConcurrentStreams: 0 }, plans: { pro: { rpm: 600, burst: 0, maxConcurrentStreams: 0 } },
  rules: [rule('tenant', 'Acme', 120), rule('model', 'gpt-4', 100), rule('model', 'llama', 50), rule('auth_failure', '*', 30)],
};
const dim = (name, o = {}) => ({ name, trackedKeys: 3, maxKeys: 500, atCapacity: false, droppedDecisions: 0, firstDroppedUtc: null, ...o });
const TRACKER = (over = {}) => ({
  trackingSinceUtc: '2026-09-21T08:00:00Z', maxKeysPerDimension: 500, isSaturated: false,
  dimensions: ['tenants', 'models', 'apiKeys', 'tenantModels', 'violations', 'limits'].map(n => dim(n, over[n])),
  ...(over.root || {}),
});
const limit = (limitId, o = {}) => {
  const [scope, ...t] = limitId.split(':');
  return { limitId, scope, target: t.join(':'), anonymousBucket: false, singleBucket: true, evaluations: 0, charged: 0, refusedByRate: 0,
    passedThenRefunded: 0, streamsStarted: 0, refusedByStreams: 0, chargedPerMinute: 0, peakChargedInOneMinute: 0, peakMinuteUtc: null,
    configuredRpm: 0, effectiveRpm: 0, peakUtilization: null, lastDecisionUtc: null, ...o };
};
const USAGE = (over = {}) => ({
  windowMinutes: 60, generatedUtc: '2026-09-21T10:00:00Z',
  totals: { requests: 5000, admitted: 4900, rejected: 100, rateRejected: 90, concurrencyRejected: 10 },
  byTenant: [], byApiKey: [], byModel: [], byTenantModel: [], violations: [],
  adaptive: { enabled: false, models: [] }, store: { requestPartitions: 1, streamPartitions: 0, maxPartitions: 10000 },
  limits: [
    limit('tenant:acme', { evaluations: 1300, charged: 1204, refusedByRate: 37, passedThenRefunded: 59, refusedByStreams: 2, peakChargedInOneMinute: 108, configuredRpm: 120, effectiveRpm: 120, peakUtilization: 0.9, lastDecisionUtc: '2026-09-21T09:59:30Z' }),
    limit('model:gpt-4', { evaluations: 400, charged: 400, peakChargedInOneMinute: 20, configuredRpm: 100, effectiveRpm: 70, peakUtilization: 0.2857 }),
    limit('model:gpt-4', { anonymousBucket: true, evaluations: 9, charged: 8, refusedByRate: 1 }),
    limit('plan:pro', { singleBucket: false, evaluations: 9000, charged: 8990, refusedByRate: 10, peakChargedInOneMinute: 700, configuredRpm: 600, effectiveRpm: 600 }),
  ],
  protective: [
    { scope: 'auth_failure', limitId: 'auth_failure:*', checked: 40, charged: 12, refused: 3, enforcedRpm: 30, refusedByStreams: 0, lastDecisionUtc: '2026-09-21T09:58:00Z' },
    { scope: 'anonymous', limitId: 'anonymous:*', checked: 0, charged: 0, refused: 0, enforcedRpm: 0, refusedByStreams: 0, lastDecisionUtc: null },
  ],
  tracker: TRACKER(),
  ...over,
});

function page(usage = USAGE(), config = CONFIG) {
  const app = createApp();
  app.loadRateLimitSchedule = async () => {};
  app.queueRateLimitScheduleRefresh = () => {};
  app.loadRateLimitKeys = () => {};
  app.applyRateLimitsData(clone(config));
  app.rateLimitUsage = usage ? clone(usage) : null;
  return app;
}
const rowFor = (app, who) => app.rlRuleRows.find(r => r.identity === who);

test('writability is known on load', async t => {
  await t.test('a gateway that says it cannot save is read-only before anyone edits', () => {
    const app = page(USAGE(), { ...CONFIG, writable: false, readOnlyReason: 'store_unavailable' });
    assert.equal(app.rateLimitsEditable, false);
    assert.match(app.rateLimitsReadOnlyText, /no database/);
    assert.ok(app.rlRuleRows.length > 0, 'inspection still works');
  });

  await t.test('an unknown reason is still read-only, with words that do not guess', () => {
    const app = page(USAGE(), { ...CONFIG, writable: false, readOnlyReason: 'something_new' });
    assert.equal(app.rateLimitsEditable, false);
    assert.match(app.rateLimitsReadOnlyText, /cannot save rate-limit changes right now/);
  });

  await t.test('writable: true is authoritative and clears a stale read-only', () => {
    const app = page();
    app.rlReadOnlyReason = 'left over from a refused save';
    app.applyRateLimitsData({ ...clone(CONFIG), writable: true });
    assert.equal(app.rateLimitsEditable, true);
  });

  await t.test('a gateway that does not say leaves the old behaviour alone, and the flag never enters the draft', () => {
    const app = page();
    app.rlReadOnlyReason = 'learnt from a refused save';
    app.applyRateLimitsData(clone(CONFIG));
    assert.equal(app.rateLimitsEditable, false);
    const withFlag = page(USAGE(), { ...CONFIG, writable: true });
    assert.equal('writable' in withFlag.buildRateLimitsPayload(), false);
    assert.equal(withFlag.rateLimitsDirty, false);
  });

  await t.test('the notice is on the page as a status', () => {
    assert.match(HTML, /x-show="rateLimitsReadOnlyText" role="status"><span class="tag muted">read-only<\/span>/);
  });
});

test('tracker completeness', async t => {
  await t.test('complete: nothing is said', () => {
    const v = page().rlTrackerView;
    assert.deepEqual([v.known, v.saturated, v.full, v.text], [true, false, false, '']);
  });

  await t.test('full but nothing lost yet is a note, not a warning', () => {
    const v = page(USAGE({ tracker: TRACKER({ tenants: { atCapacity: true, trackedKeys: 500 } }) })).rlTrackerView;
    assert.equal(v.saturated, false);
    assert.equal(v.full, true);
    assert.match(v.fullText, /as many tenants as it can hold \(500\)\. Nothing has been missed yet/);
  });

  await t.test('saturated is the gateway’s statement, never tracked == max', () => {
    const notSaid = page(USAGE({ tracker: TRACKER({ tenants: { atCapacity: true, trackedKeys: 500 }, root: { isSaturated: false } }) }));
    assert.equal(notSaid.rlTrackerView.saturated, false);
    const v = page(USAGE({ tracker: TRACKER({
      tenants: { atCapacity: true, droppedDecisions: 41, firstDroppedUtc: '2026-09-21T09:00:00Z' },
      limits: { atCapacity: true, droppedDecisions: 1 }, root: { isSaturated: true } }) })).rlTrackerView;
    assert.equal(v.saturated, true);
    assert.match(v.text, /Activity is incomplete\. The gateway tracks at most 500 tenants, limits, and 42 decisions about others/);
    assert.match(v.text, /a missing row is unknown, not zero/);
    assert.match(v.text, /totals are exact/);
  });

  await t.test('a rule with no row reads unknown while the limits section is lossy, and "no decisions" when it is complete', () => {
    const lossy = page(USAGE({ tracker: TRACKER({ limits: { droppedDecisions: 5 }, root: { isSaturated: true } }) }));
    const row = rowFor(lossy, 'model:llama');
    assert.equal(row.actText, 'unknown');
    assert.equal(row.actUnknown, true);
    assert.match(row.actSr, /^unknown: .*counters are full/);
    assert.equal(rowFor(page(), 'model:llama').actText, 'no decisions');
    assert.equal(rowFor(page(), 'model:llama').actUnknown, false);
  });

  await t.test('a refusal count that may have been dropped is unknown too', () => {
    const app = page(USAGE({ tracker: TRACKER({ violations: { droppedDecisions: 2 }, root: { isSaturated: true } }) }));
    const r = app.rlRefusalsView('model', 'llama');
    assert.equal(r.known, false);
    assert.equal(r.text, '—');
    assert.match(r.title, /refusal counters are full/);
  });

  await t.test('an older gateway with no tracker section claims nothing', () => {
    const usage = USAGE(); delete usage.tracker;
    assert.equal(page(usage).rlTrackerView.known, false);
  });

  await t.test('the warning is visible text in Activity and a tag in the summary', () => {
    assert.match(HTML, /class="notice warn rl-tracker-note" x-show="rlTrackerView\.saturated" role="status"><span class="tag warn">incomplete<\/span>/);
    assert.match(HTML, /<span class="tag warn" x-show="rlTrackerView\.saturated">activity incomplete<\/span>/);
  });
});

test('per-limit activity', async t => {
  await t.test('joined by the rule’s identity, case-insensitively, and never by subject', () => {
    const app = page();
    const row = rowFor(app, 'tenant:acme');
    assert.equal(row.actText, '1,204 passed · 39 refused');
    assert.equal(row.actSub, 'peak 108/min of 120 rpm · 90%');
    assert.equal(row.hasActBar, true);
    assert.equal(row.actBarStyle, 'width:90%');
    assert.match(row.actBarCls, /is-hot/);
    // Subject rows that look alarming change nothing about the rule.
    app.rateLimitUsage = { ...app.rateLimitUsage, byTenant: [{ key: 'acme', tenantId: 'acme', requests: 1, requestsPerMinute: 9999, effectiveRpm: 1 }] };
    assert.equal(rowFor(app, 'tenant:acme').actSub, 'peak 108/min of 120 rpm · 90%');
  });

  await t.test('utilisation is the gateway’s number; a tier gets none however busy', () => {
    const app = page();
    const tier = app.rlTierCards.find(c => c.key === 'plan:pro');
    assert.equal(tier.activity.text, '8,990 passed · 10 refused');
    assert.equal(tier.activity.sub, 'peak 700/min across all callers');
    assert.equal(tier.activity.hasBar, false);
    assert.equal(app.rlLimitActivityFor('plan:pro').near, false);
    assert.equal(app.rlTierCards.find(c => c.key === 'default:').activity.text, 'no decisions');
  });

  await t.test('adaptive scaling shows as enforced vs configured', () => {
    assert.match(page().rlLimitActivityView('model:gpt-4').rateText, /^70 rpm enforced \(100 before load shedding\)$/);
  });

  await t.test('a model rule’s anonymous bucket stays a separate statement', () => {
    const app = page();
    assert.equal(rowFor(app, 'model:gpt-4').actText, '400 passed · 0 refused');
    app.openRateLimitRule('model:gpt-4');
    assert.match(app.rlComputeRuleDrawerView().limitAnon, /separate bucket under this rule: 8 passed · 1 refused/);
  });

  await t.test('the drawer breaks the counters out with their meanings', () => {
    const app = page();
    app.openRateLimitRule('tenant:acme');
    const v = app.rlComputeRuleDrawerView();
    assert.equal(v.limitOk, true);
    assert.deepEqual(v.limit.lines.map(l => l.label + '=' + l.value),
      ['Evaluated=1,300', 'Passed=1,204', 'Refused by rate=37', 'Passed, then refunded=59', 'Streams started=0', 'Refused by streams=2']);
    assert.match(v.limit.lines[3].title, /got it back because another limit refused/);
  });

  await t.test('sorting by activity puts measured utilisation first, then volume, then nothing', () => {
    const app = page();
    app.setRateLimitSort('activity');
    app.setRateLimitSort('activity');
    assert.deepEqual(app.rlRuleRows.map(r => r.identity), ['tenant:acme', 'model:gpt-4', 'model:llama']);
  });

  await t.test('no report, or a gateway without the section: a dash with its reason, never a zero', () => {
    assert.equal(rowFor(page(null), 'tenant:acme').actText, '—');
    const usage = USAGE(); delete usage.limits;
    const row = rowFor(page(usage), 'tenant:acme');
    assert.equal(row.actText, '—');
    assert.match(row.actSr, /does not report activity per limit/);
  });
});

test('protective activity', async t => {
  await t.test('failed credentials: checked, charged and refused mean what the limiter does', () => {
    const card = page().rlProtectiveCards.find(c => c.key === 'auth_failure' || c.isAuthFailure);
    assert.equal(card.activity.known, true);
    assert.match(card.activity.text, /^40 credentialed requests checked · 12 failed credentials charged · 3 refused/);
    assert.match(card.activity.sub, /30 rpm per address block enforced/);
  });

  await t.test('anonymous at zero is a real zero, and says where its traffic is counted otherwise', () => {
    const card = page().rlProtectiveCards.find(c => !c.isAuthFailure);
    assert.match(card.activity.text, /^0 anonymous requests checked · 0 passed · 0 refused/);
    assert.match(card.activityNote, /counted under, the Default tier/);
  });

  await t.test('without the section the row points at the metrics instead of showing zeros', () => {
    const usage = USAGE(); delete usage.protective;
    const card = page(usage).rlProtectiveCards.find(c => c.isAuthFailure);
    assert.equal(card.activity.known, false);
    assert.match(card.activityNote, /gateway_rate_limit_rejections_total/);
  });

  await t.test('the row shows it only when known', () => {
    assert.match(HTML, /class="rl-protect-act" x-show="p\.activity\.known"/);
  });
});

test('trend', async t => {
  const point = (m, o = {}) => ({ startUtc: '2026-09-21T09:' + String(m).padStart(2, '0') + ':00Z', covered: true, decisions: 10, admitted: 10, refusedByRate: 0, refusedByStreams: 0, ...o });
  const series = points => ({ subject: 'gateway', limitId: null, bucketMinutes: 1, trackingSinceUtc: '2026-09-21T09:02:00Z', points });

  await t.test('minutes the gateway did not observe are left out, and the page says so', () => {
    const app = page();
    app.rlSeries = series([point(0, { covered: false, decisions: 0 }), point(1, { covered: false, decisions: 0 }), point(2), point(3, { refusedByRate: 4, refusedByStreams: 1 }), point(4)]);
    const v = app.rlSeriesView;
    assert.equal(v.show, true);
    assert.equal(v.refusedLine.split(' ').length, 3);
    assert.match(v.text, /^peak 5 at /);
    assert.match(v.partialText, /^Counting began .*earlier minutes are not shown/);
  });

  await t.test('no refusals is said in words; one point is not a trend', () => {
    const app = page();
    app.rlSeries = series([point(1), point(2)]);
    assert.equal(app.rlSeriesView.text, 'no refusals');
    app.rlSeries = series([point(1)]);
    assert.equal(app.rlSeriesView.show, false);
  });

  await t.test('loading follows the activity window, and a gateway without the route is not an error', async () => {
    const app = page();
    const urls = [];
    app.rateLimitUsageMinutes = 180;
    app.apiJson = async url => { urls.push(url); const e = new Error('nope'); e.status = 404; throw e; };
    await app.loadRateLimitSeries();
    assert.deepEqual(urls, ['/admin/api/rate-limits/usage/timeseries?minutes=180&bucketMinutes=3']);
    assert.equal(app.rlSeriesError, '');
  });

  await t.test('a rule’s own series: 404 is "nothing recorded", and a late answer for another rule is dropped', async () => {
    const app = page();
    app.apiJson = async () => { const e = new Error('none'); e.status = 404; throw e; };
    await app.loadRateLimitLimitSeries('model:llama');
    assert.equal(app.rlLimitSeriesState, 'none');
    assert.equal(app.rlLimitSeriesView('model:llama').note, '');

    let release;
    app.apiJson = () => new Promise(resolve => { release = () => resolve(series([point(1), point(2)])); });
    const slow = app.loadRateLimitLimitSeries('tenant:acme');
    app.apiJson = async url => { assert.match(url, /limitId=model%3Agpt-4/); return series([point(3), point(4)]); };
    await app.loadRateLimitLimitSeries('model:gpt-4');
    release();
    await slow;
    assert.equal(app.rlLimitSeriesFor, 'model:gpt-4');
    assert.equal(app.rlLimitSeriesView('tenant:acme').show, false);
    assert.equal(app.rlLimitSeriesView('model:gpt-4').show, true);
  });

  await t.test('the charts are hidden from assistive tech and summarised in text', () => {
    assert.match(HTML, /<svg class="rl-trend-svg" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">/);
    assert.match(HTML, /<span class="sr-only" x-text="rlSeriesView\.sr"><\/span>/);
  });
});

test('change history', async t => {
  const HISTORY = {
    available: true, hasMore: true, nextBefore: '2026-09-20T08:00:00Z', scanLimitReached: false,
    entries: [
      { timestampUtc: '2026-09-21T09:00:00Z', outcome: 'refused', actorApiKeyId: 'aaaaaaaa-0000-0000-0000-000000000000', statusCode: 409, message: 'Rate limits were changed by someone else.', basedOnVersion: 5, changes: null, changeCount: 0 },
      { timestampUtc: '2026-09-21T08:00:00Z', outcome: 'applied', actorApiKeyId: 'aaaaaaaa-0000-0000-0000-000000000000', version: 7, enabled: true, adaptiveEnabled: false, ruleCount: 4, changeCount: 2, changesTruncated: false,
        changes: [{ kind: 'changed', ruleId: 'tenant:acme', before: '300rpm+0burst/0streams', after: '120rpm+0burst/0streams' }, { kind: 'removed', ruleId: 'model:gone', before: '10rpm+0burst/0streams', after: null }] },
      { timestampUtc: '2026-09-20T08:00:00Z', outcome: 'applied', actorApiKeyId: null, version: 6, changeCount: 1, changes: [{ kind: 'added', ruleId: null, summary: 'added tenant:acme = 300rpm+0burst/0streams' }] },
    ],
  };

  await t.test('collapsed until asked for; opening it loads once', async () => {
    const app = page();
    let calls = 0;
    app.apiJson = async url => { calls++; assert.equal(url, '/admin/api/rate-limits/history?take=20'); return clone(HISTORY); };
    assert.equal(app.rlHistoryView.open, false);
    app.toggleRateLimitHistory();
    await new Promise(resolve => setImmediate(resolve));
    app.toggleRateLimitHistory();
    app.toggleRateLimitHistory();
    assert.equal(calls, 1);
    assert.equal(app.rlHistoryView.entries.length, 3);
  });

  await t.test('a refusal says why and what it was based on; a save says what changed', () => {
    const app = page();
    app.rlHistory = clone(HISTORY);
    const [refused, saved, legacy] = app.rlHistoryView.entries;
    assert.equal(refused.summary, 'Save refused — conflict: based on an older version');
    assert.equal(refused.based, 'based on v5');
    assert.equal(refused.hasChanges, false);
    assert.equal(saved.summary, 'Saved — 2 rule changes');
    assert.equal(saved.version, 'v7');
    assert.deepEqual(saved.changes.map(c => [c.kind, c.name, c.detail, c.canOpen]), [
      ['changed', 'Acme', '300rpm+0burst/0streams → 120rpm+0burst/0streams', true],
      ['removed', 'gone', 'was 10rpm+0burst/0streams', false],
    ]);
    // An entry from before ids were recorded is shown as written and claims no rule.
    assert.equal(legacy.changes[0].name, 'added tenant:acme = 300rpm+0burst/0streams');
    assert.equal(legacy.changes[0].canOpen, false);
    assert.equal(legacy.actor, 'unknown key');
  });

  await t.test('older pages append, using the cursor the gateway gave', async () => {
    const app = page();
    app.rlHistory = clone(HISTORY);
    app.apiJson = async url => {
      assert.equal(url, '/admin/api/rate-limits/history?take=20&before=' + encodeURIComponent('2026-09-20T08:00:00Z'));
      return { available: true, hasMore: false, nextBefore: null, entries: [{ timestampUtc: '2026-09-19T08:00:00Z', outcome: 'applied', changes: [], changeCount: 0 }] };
    };
    await app.loadRateLimitHistory(true);
    assert.equal(app.rlHistoryView.entries.length, 4);
    assert.equal(app.rlHistoryView.hasMore, false);
    assert.equal(app.rlHistoryView.entries[3].summary, 'Saved — no rule changed (tiers or switches only)');
  });

  await t.test('no trail is not "no changes"; a failure keeps the page and offers a retry', async () => {
    const app = page();
    app.rlHistory = { available: false, entries: [] };
    assert.equal(app.rlHistoryView.unavailable, true);
    assert.equal(app.rlHistoryView.empty, false);
    app.rlHistory = null;
    app.apiJson = async () => { throw new Error('boom'); };
    await app.loadRateLimitHistory(false);
    assert.equal(app.rlHistoryView.error, 'boom');
    assert.match(HTML, /@click="retryRateLimitHistory">Retry<\/button>/);
  });

  await t.test('the rule drawer finds its rule by id, and says how far it looked', () => {
    const app = page();
    app.rlHistory = clone(HISTORY);
    const mine = app.rlRuleHistoryView('Tenant:ACME');
    assert.deepEqual(mine.rows.map(r => r.kind + ' ' + r.detail), ['changed 300rpm+0burst/0streams → 120rpm+0burst/0streams']);
    const other = app.rlRuleHistoryView('model:llama');
    assert.equal(other.hasRows, false);
    assert.match(other.note, /No change to this rule in the 3 most recent saves loaded/);
    assert.equal(page().rlRuleHistoryView('tenant:acme').loaded, false);
  });

  await t.test('history is a disclosure with a name, placed after the limits and out of the rules table', () => {
    assert.match(HTML, /id="rl-history-toggle" @click="toggleRateLimitHistory" :aria-expanded="rlHistoryView\.expanded" aria-controls="rl-history-body"/);
    assert.ok(HTML.indexOf('id="rl-history"') > HTML.indexOf('class="t-rl-protect"'));
    assert.ok(HTML.indexOf('id="rl-history"') < HTML.indexOf('id="rl-save-button"'));
    const table = HTML.slice(HTML.indexOf('<table class="rl-rules-table">'), HTML.indexOf('</table>', HTML.indexOf('<table class="rl-rules-table">')));
    assert.equal(/history/i.test(table), false);
  });
});
