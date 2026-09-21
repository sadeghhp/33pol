/**
 * Second pass on Settings → Rate limits: window sentences, the bounded calendar, preview
 * shortcuts, baselines as tables, narrow Activity rows, focus after self-removing controls, and
 * text that used to live only in a tooltip.
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

function createApp(documentOverride) {
  const context = {
    document: documentOverride || { addEventListener() {}, hidden: false, getElementById: () => null },
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
const weekly = (extra = {}) => ({ name: 'office', kind: 'weekly', days: ['mon', 'tue', 'wed', 'thu', 'fri'], start: '09:00', end: '17:00', timeZone: 'Europe/London', rpm: 120, burst: 0, maxConcurrentStreams: 0, ...extra });

function page(rules) {
  const app = createApp();
  app.loadRateLimitSchedule = async () => {};
  app.queueRateLimitScheduleRefresh = () => {};
  app.rlZone = 'UTC';
  app.applyRateLimitsData({ version: 1, enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans: { standard: { rpm: 1200, burst: 120, maxConcurrentStreams: 0 } }, rules: clone(rules) });
  return app;
}

test('a window reads as one sentence: when, in which zone, and what it does', async t => {
  const app = page([]);

  await t.test('weekly', () => {
    assert.equal(app.rlWindowSentence(weekly()), 'Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm');
    assert.equal(app.rlWindowSentence(weekly({ burst: 20, maxConcurrentStreams: 4 })), 'Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm · 20 burst · 4 streams');
  });

  await t.test('an end at or before the start runs into the next day; 00:00–24:00 is all day', () => {
    assert.equal(app.rlWindowSentence(weekly({ days: ['sat'], start: '22:00', end: '06:00', timeZone: 'UTC' })), 'Sat · 22:00–06:00 (next day) · UTC → 120 rpm');
    assert.equal(app.rlWindowSentence(weekly({ days: ['sat', 'sun'], start: '00:00', end: '24:00', timeZone: undefined })), 'Sat, Sun · all day · UTC → 120 rpm');
  });

  await t.test('days collapse only where a reader would: runs of three or more', () => {
    assert.equal(app.rlDaysCompact(['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun']), 'Every day');
    assert.equal(app.rlDaysCompact(['tue', 'wed', 'thu', 'sun']), 'Tue–Thu, Sun');
    assert.equal(app.rlDaysCompact(['FRI', 'mon']), 'Mon, Fri');
    assert.equal(app.rlDaysCompact([]), 'No days');
  });

  await t.test('a pause says paused, never a tier', () => {
    assert.equal(app.rlWindowSentence(weekly({ suspend: true })), 'Mon–Fri · 09:00–17:00 · Europe/London → paused');
  });

  // Dates follow the browser's locale, like every other date on the page ("21 Sep" or "Sep 21").
  const day = (iso, zone = 'UTC') => new Intl.DateTimeFormat(undefined, { timeZone: zone, hourCycle: 'h23', day: 'numeric', month: 'short' }).format(new Date(iso));
  const date = (iso) => new Intl.DateTimeFormat(undefined, { timeZone: 'UTC', hourCycle: 'h23', day: 'numeric', month: 'short', year: 'numeric' }).format(new Date(iso));

  await t.test('one-time windows are instants shown in the display zone, which is named', () => {
    const once = { name: 'launch', kind: 'once', from: '2026-09-21T14:00:00Z', until: '2026-09-21T18:00:00Z', rpm: 0, burst: 0, maxConcurrentStreams: 0, suspend: true };
    const d21 = day(once.from), d22 = day('2026-09-22T02:00:00Z');
    assert.equal(app.rlWindowSentence(once), 'Once · ' + d21 + ' 14:00–18:00 · UTC → paused');
    assert.equal(app.rlWindowSentence({ ...once, until: '2026-09-22T02:00:00Z' }), 'Once · ' + d21 + ' 14:00 → ' + d22 + ' 02:00 · UTC → paused');
    assert.equal(app.rlWindowSentence({ ...once, until: null, suspend: false, rpm: 60 }), 'Once · from ' + d21 + ' 14:00, open-ended · UTC → 60 rpm');
    app.rlZone = 'Asia/Tokyo';
    assert.equal(app.rlWindowSentence(once), 'Once · ' + d21 + ' 23:00 → ' + d22 + ' 03:00 · Asia/Tokyo → paused', 'same instants, another zone');
    app.rlZone = 'UTC';
  });

  await t.test('validity bounds and an explicit priority change when it applies, so they are said', () => {
    const s = app.rlWindowSentence(weekly({ validFrom: '2026-10-01T00:00:00Z', validUntil: '2026-12-31T00:00:00Z', priority: 5 }));
    assert.equal(s, 'Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm · valid from ' + date('2026-10-01T00:00:00Z') + ' until ' + date('2026-12-31T00:00:00Z') + ' · priority 5');
    assert.doesNotMatch(app.rlWindowSentence(weekly({ priority: '' })), /priority/);
  });

  await t.test('it restates stored fields and decides nothing: state still comes from the report', () => {
    const withWindow = page([rule('model', 'gpt-4', 600, { schedule: [weekly()] })]);
    withWindow.openRateLimitRule('model:gpt-4');
    const [w] = withWindow.rlRuleDrawerView.windows;
    assert.equal(w.sentence, 'Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm');
    assert.equal(w.stateText, 'Not saved yet', 'no report, so no claim about running or next');
    assert.match(HTML, /class="w rl-win-sentence" x-text="w\.sentence"/);
  });
});

test('the calendar is bounded, and says what it folded away', async t => {
  const many = Array.from({ length: 30 }, (_, i) => rule('model', 'm' + String(i).padStart(2, '0'), 100, { schedule: [weekly({ name: 'w' })] }));
  const report = (extra = {}) => ({
    rules: [
      { scope: 'model', target: 'm25', activeWindow: 'w', effective: { rpm: 120 }, nextChangeAt: '2099-01-03T00:00:00Z', windows: [] },
      { scope: 'model', target: 'm10', activeWindow: null, effective: { rpm: 100 }, nextChangeAt: '2099-01-01T00:00:00Z', windows: [] },
    ], occurrences: [], transitions: [], ...extra,
  });

  await t.test('twenty rows: what runs now first, then the soonest to change', () => {
    const app = page(many);
    app.rlSchedule = report();
    const view = app.rlTimelineView;
    assert.deepEqual([view.rows.length, view.totalRows, view.showRowToggle], [20, 30, true]);
    assert.deepEqual(view.rows.slice(0, 3).map(r => r.label), ['m25', 'm10', 'm00']);
    assert.match(view.rowNote, /^Showing 20 of 30 scheduled rules/);
    assert.match(view.rowNote, /10 more are folded away, not missing/);
  });

  await t.test('one press shows them all, another folds them again', () => {
    const app = page(many);
    app.toggleRateLimitTimelineRows();
    assert.deepEqual([app.rlTimelineView.rows.length, app.rlTimelineView.rowNote, app.rlTimelineView.rowToggleText], [30, '', 'Show the first 20 only']);
    app.toggleRateLimitTimelineRows();
    assert.equal(app.rlTimelineView.rows.length, 20);
  });

  await t.test('a short calendar has no cap, no note and its given order', () => {
    const app = page(many.slice(0, 5));
    const view = app.rlTimelineView;
    assert.deepEqual([view.rows.length, view.showRowToggle, view.rowNote], [5, false, '']);
    assert.deepEqual(view.rows.map(r => r.label), ['m00', 'm01', 'm02', 'm03', 'm04']);
  });

  await t.test('folding rows and the server trimming occurrences are separate statements', () => {
    const app = page(many);
    app.rlSchedule = report({ occurrences: [], occurrencesTotal: 5000, occurrencesTruncated: true });
    const view = app.rlTimelineView;
    assert.match(view.rowNote, /folded away/);
    assert.match(view.truncatedNote, /of 5,000 window occurrences/);
    assert.match(HTML, /x-show="rlTimelineView\.rowNote"[^]*x-show="rlTimelineView\.truncatedNote"/);
  });

  await t.test('the legend describes each window as a sentence', () => {
    const app = page(many.slice(0, 1));
    app.rlSchedule = report({ occurrences: [{ scope: 'model', target: 'm00', window: 'w', start: '2099-01-01T09:00:00Z', end: '2099-01-01T17:00:00Z', tier: { rpm: 120 } }] });
    assert.deepEqual(app.rlTimelineView.legend.map(l => l.text), ['w — Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm']);
  });
});

test('preview shortcuts choose an instant; the server still evaluates it', async t => {
  const sched = { rules: [{ scope: 'model', target: 'a', nextChangeAt: '2099-03-01T07:00:00Z' }, { scope: 'model', target: 'b', nextChangeAt: '2099-02-01T07:00:00Z' }, { scope: 'model', target: 'c', nextChangeAt: '2001-01-01T00:00:00Z' }], occurrences: [], transitions: [] };

  await t.test('Next change: one minute past the earliest future transition, in the display zone', async () => {
    const app = page([rule('model', 'a', 1)]);
    app.rlSchedule = sched;
    const urls = [];
    app.apiJson = async (url) => { urls.push(url); return { at: '2099-02-01T07:01:00Z', rules: [] }; };
    await app.previewRateLimitNextChange();
    assert.equal(app.rlPreviewAt, '2099-02-01T07:01');
    assert.equal(urls.length, 1);
    assert.match(urls[0], /\/schedule\?atLocal=2099-02-01T07%3A01&timeZone=UTC&take=1$/);
    assert.equal(app.rlPreviewView.has, true);

    app.rlZone = 'Asia/Tokyo';
    await app.previewRateLimitNextChange();
    assert.equal(app.rlPreviewAt, '2099-02-01T16:01', 'the same instant, typed the way this zone reads it');
    assert.match(urls[1], /timeZone=Asia%2FTokyo/);
  });

  await t.test('with a staged draft it goes to the preview route, like the typed control', async () => {
    const app = page([rule('model', 'a', 1)]);
    app.rlSchedule = sched;
    app.rlDraft.rules[0].rpm = 2;
    const calls = [];
    app.apiJson = async (url, options) => { calls.push([url, options?.method]); return { at: '2099-02-01T07:01:00Z', rules: [] }; };
    await app.previewRateLimitNextChange();
    assert.deepEqual(calls[0], ['/admin/api/rate-limits/schedule/preview', 'POST']);
  });

  await t.test('nothing scheduled: the button is disabled and its label says why', async () => {
    const app = page([]);
    app.rlSchedule = { rules: [], occurrences: [], transitions: [] };
    let asked = 0;
    app.apiJson = async () => { asked++; return {}; };
    assert.deepEqual([app.rlPreviewShortcutView.noNext, app.rlPreviewShortcutView.nextLabel], [true, 'Next change · none in range']);
    await app.previewRateLimitNextChange();
    assert.equal(asked, 0);
  });

  await t.test('Now fills the current wall-clock minute in the display zone and asks', async () => {
    const app = page([]);
    let asked = 0;
    app.apiJson = async () => { asked++; return { at: new Date().toISOString(), rules: [] }; };
    await app.previewRateLimitNow();
    assert.match(app.rlPreviewAt, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    assert.equal(app.rlPreviewAt.slice(0, 13), new Date().toISOString().slice(0, 13));
    assert.equal(asked, 1);
  });

  await t.test('both are real buttons beside the input, and the result is announced', () => {
    assert.match(HTML, /<button type="button" class="action ghost sm" @click="previewRateLimitNow"[^>]*>Now<\/button>/);
    assert.match(HTML, /@click="previewRateLimitNextChange" :disabled="rlPreviewShortcutView\.noNext"/);
    assert.match(HTML, /class="rl-preview-res" x-show="rlPreviewView\.has" role="status"/);
  });
});

test('baselines are tables: tiers and protective limits, each with its own columns', async t => {
  const tiers = HTML.slice(HTML.indexOf('<table class="t-rl-tiers">'), HTML.indexOf('</table>', HTML.indexOf('<table class="t-rl-tiers">')));
  const protect = HTML.slice(HTML.indexOf('<table class="t-rl-protect">'), HTML.indexOf('</table>', HTML.indexOf('<table class="t-rl-protect">')));

  await t.test('real tables: caption, column headers, a row header per line, and a button to open', () => {
    for (const table of [tiers, protect]) {
      assert.match(table, /<caption class="sr-only">/);
      assert.match(table, /<th scope="col" class="num">rpm<\/th>\s*<th scope="col" class="num">burst<\/th>\s*<th scope="col" class="num">streams<\/th>/);
      assert.match(table, /<th scope="row" class="rl-base-name"/);
      assert.doesNotMatch(table, /<tr[^>]*tabindex/);
    }
    assert.match(tiers, /<button type="button" class="icon-btn" @click="t\.edit" :aria-label="t\.openLabel"/);
    assert.match(protect, /@click="p\.open" :aria-label="p\.openLabel">Open<\/button>/);
    assert.match(protect, /x-show="p\.canConfigure" @click="p\.configure"/);
  });

  await t.test('the two are separate tables, not one: a tier has no switch and a budget has no plan', () => {
    assert.doesNotMatch(tiers, /role="switch"/);
    assert.match(protect, /role="switch" :checked="p\.enabled"/);
    assert.match(tiers, />Applies to</);
  });

  await t.test('protective rows keep production beside the draft, and their explanations', () => {
    assert.match(protect, /Enforcing now:<\/span> <span x-text="p\.enfText">/);
    assert.match(protect, /x-text="p\.activityNote"/);
    assert.match(protect, /x-show="p\.unset" x-text="p\.unsetText"/);
    assert.match(protect, /x-show="p\.hasDraftTag" x-text="p\.draftTag"/);
  });

  await t.test('the view-models carry what the cells need, including words for ∞ and "no streams"', () => {
    const app = page([rule('anonymous', '*', 30, { maxConcurrentStreams: 0 })]);
    const [anonymous, failed] = app.rlProtectiveCards;
    assert.deepEqual([anonymous.streams, anonymous.streamsUnlimited, anonymous.noStreams], ['∞', true, false]);
    assert.deepEqual([failed.noStreams, failed.noStreamsText], [true, 'not applicable: this budget limits rate only']);
    assert.equal(app.rlTierCards.find(c => c.name === 'standard').streamsUnlimited, true);
    assert.equal(app.rlTierCards.length, 2);
  });

  await t.test('the card styles went with the cards', () => {
    assert.doesNotMatch(CSS, /\.rl-protect-card|\.rl-tier-n\b/);
    assert.doesNotMatch(HTML, /class="rl-tier"|class="rl-protect-card/);
  });
});

test('narrow Activity rows fold their secondary columns into a second line', async t => {
  const usage = {
    windowMinutes: 60, generatedUtc: '2026-09-21T10:00:00Z', totals: { requests: 10, admitted: 9, rejected: 1, rateRejected: 1, concurrencyRejected: 0 },
    byTenant: [{ key: 't1', tenantId: 't1', requests: 100, rejected: 4, requestsPerMinute: 206.7, configuredRpm: 120, effectiveRpm: 120 }, { key: 't2', tenantId: 't2', requests: 5, rejected: 0, requestsPerMinute: 0.1, configuredRpm: 0, effectiveRpm: 0 }],
    byApiKey: [], byModel: [], byTenantModel: [], violations: [],
    adaptive: { enabled: true, models: [{ modelId: 'llama', factor: 0.7, saturation: 0.93, reason: 'queue depth' }] }, store: {},
  };

  await t.test('the line carries exactly what the hidden columns did', () => {
    const app = page([]);
    app.rateLimitUsage = usage;
    const [hot, quiet] = app.rlUsageSubjectView.rows;
    assert.equal(hot.secondLine, '206.7 avg req/min · load 172% of last limit 120 rpm');
    assert.equal(quiet.secondLine, '0.1 avg req/min · no limit seen');
    assert.equal(app.rateLimitAdaptiveRows[0].secondLine, 'saturation 93% · queue depth');
  });

  await t.test('one copy per reader: the line is hidden on wide screens, the columns on narrow ones', () => {
    assert.match(CSS, /\.rl-narrow-line \{ display: none; \}/);
    const narrow = CSS.slice(CSS.lastIndexOf('.rl-narrow-line { display: none; }'));
    assert.match(narrow, /@media \(max-width: 48rem\) \{[^]*\.t-rl-usage \.rl-wide-col, \.t-rl-adaptive \.rl-wide-col \{ display: none; \}[^]*\.rl-narrow-line \{ display: block;/);
    assert.match(narrow, /\.t-rl-usage, \.t-rl-adaptive, \.t-rl-refusals \{ min-width: 0; \}/, 'no sideways scroll');
    assert.match(HTML, /<span class="rl-narrow-line" x-text="row\.secondLine"><\/span>/);
    // Decisions, Refused and the row actions stay columns at every width.
    assert.doesNotMatch(HTML, /class="num rl-wide-col"[^>]*>\s*<button[^>]*>(Decisions|Refused)</);
  });

  await t.test('at phone width a row is two lines, and the folded columns stay folded', () => {
    const phone = CSS.slice(CSS.indexOf('@media (max-width: 30rem)'));
    assert.match(phone, /\.t-rl-usage td:not\(\.rl-wide-col\) \{ display: block;/, 'a blanket display:block would un-hide the folded columns');
    assert.match(phone, /\.t-rl-usage td:first-child \{ grid-column: 1 \/ -1; \}/);
    assert.match(HTML, /<span class="rl-xs-label" aria-hidden="true">Decisions <\/span>/);
    assert.ok(CSS.indexOf('@media (max-width: 30rem)') > CSS.lastIndexOf('.t-rl-usage .rl-wide-col, .t-rl-adaptive .rl-wide-col { display: none; }'));
  });

  await t.test('the rule cards\' own narrow fix is still the last word on the traffic cell', () => {
    const last = CSS.lastIndexOf('.rl-rules-table td.rl-col-traffic');
    const block = CSS.lastIndexOf('.rl-rules-table td { display: block;');
    assert.ok(block > 0 && last > block, 'the hide rule must follow the display:block rule');
  });
});

test('focus has somewhere to go when the control that held it removes itself', async t => {
  function dom(buttons) {
    const focused = [];
    const el = (name, extra = {}) => ({ name, offsetParent: {}, disabled: false, focus() { focused.push(name); }, ...extra });
    const state = { undo: buttons.undo.map(n => el(n)), keep: (buttons.keep || []).map(n => el(n)), save: buttons.save === false ? el('save', { offsetParent: null }) : el('save'), filter: el('filter') };
    const document = {
      addEventListener() {}, hidden: false,
      getElementById: id => (id === 'rl-save-button' ? state.save : id === 'rl-filter-input' ? state.filter : null),
      querySelectorAll: sel => (sel === '#rl-review .rl-undo' ? state.undo : sel === '#rl-review .rl-keep' ? state.keep : sel.includes('.rl-undo,') ? [...state.undo, ...state.keep] : []),
    };
    return { document, focused };
  }

  // A page that still has something unsaved, so the bar is staying.
  const dirtyApp = (document) => {
    const app = createApp(document);
    app.loadRateLimitSchedule = async () => {}; app.queueRateLimitScheduleRefresh = () => {};
    app.applyRateLimitsData({ version: 1, enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 0, maxConcurrentStreams: 0 }, plans: {}, rules: [rule('model', 'a', 1)] });
    app.rlDraft.rules[0].rpm = 2;
    return app;
  };

  await t.test('the next item takes it; from the last item, the one before', () => {
    const a = dom({ undo: ['u0', 'u1'] });
    dirtyApp(a.document).rlFocusInReview('.rl-undo', 1);
    assert.deepEqual(a.focused, ['u1']);
    const b = dom({ undo: ['u0'] });
    dirtyApp(b.document).rlFocusInReview('.rl-undo', 3);
    assert.deepEqual(b.focused, ['u0']);
  });

  await t.test('no items of that kind left: the other list, then Save', () => {
    const a = dom({ undo: [], keep: ['k0'] });
    dirtyApp(a.document).rlFocusInReview('.rl-undo', 0);
    assert.deepEqual(a.focused, ['k0']);
    const b = dom({ undo: [] });
    dirtyApp(b.document).rlFocusInReview('.rl-undo', 0);
    assert.deepEqual(b.focused, ['save']);
  });

  await t.test('nothing left unsaved: the bar is going, so the rules filter — never <body>, never a fading Save', () => {
    const a = dom({ undo: ['stale-during-fade'] });
    const app = dirtyApp(a.document);
    app.rlDraft.rules[0].rpm = 1;
    app.rlFocusInReview('.rl-undo', 0);
    assert.deepEqual(a.focused, ['filter']);
  });

  await t.test('Undo and Keep theirs both ask for it, after the DOM has been updated', () => {
    const a = dom({ undo: ['u0'] });
    const app = createApp(a.document);
    app.loadRateLimitSchedule = async () => {}; app.queueRateLimitScheduleRefresh = () => {}; app.toast = () => {};
    app.applyRateLimitsData({ version: 1, enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 0, maxConcurrentStreams: 0 }, plans: {}, rules: [rule('model', 'a', 1), rule('model', 'b', 1)] });
    const ticks = [];
    app.$nextTick = fn => ticks.push(fn);
    app.rlDraft.rules[0].rpm = 2; app.rlDraft.rules[1].rpm = 2;
    app.rlDirtyView.items[0].undo();
    assert.deepEqual([a.focused.length, ticks.length], [0, 1], 'deferred until Alpine has re-rendered the list');
    ticks[0]();
    assert.deepEqual(a.focused, ['u0']);
    assert.match(HTML, /class="rl-link rl-undo" @click="i\.undo"/);
    assert.match(HTML, /class="rl-link rl-keep" @click="i\.keep"/);
    assert.match(HTML, /id="rl-save-button"/);
  });

  await t.test('Show 100 more: the first new row\'s Open button — rows themselves stay unfocusable', () => {
    const focused = [];
    const rows = Array.from({ length: 200 }, (_, i) => ({ querySelector: sel => (sel === '.rl-col-chev button' ? { focus() { focused.push(i); } } : null) }));
    const document = { addEventListener() {}, hidden: false, getElementById: () => null, querySelectorAll: sel => (sel === '.rl-rules-table tbody tr' ? rows : []) };
    const app = createApp(document);
    app.showMoreRateLimitRules();
    assert.deepEqual([app.rlRuleLimit, focused], [200, [100]]);
    assert.doesNotMatch(HTML, /<tr class="rl-row"[^>]*tabindex/);
  });
});

test('what used to be said only in a tooltip', async t => {
  await t.test('why a refusal count is unknown reaches a screen reader, not just a mouse', () => {
    const app = page([rule('tenant', 'acme', 10)]);
    app.rateLimitUsage = { totals: { requests: 1 }, violations: [], byTenant: [] };
    const row = app.rlRuleRows[0];
    assert.equal(row.refused, '—');
    assert.match(row.refusedWhy, /^unknown: Refusals are counted by tenant id/);
    assert.match(HTML, /<span class="sr-only" x-show="r\.refusedUnknown" x-text="r\.refusedWhy"><\/span>/);
    assert.match(HTML, /means unknown, not zero; open the rule to see why/);
  });

  await t.test('what the activity column means, the bucket note and a failed schedule are visible text', () => {
    // The column is now the limit's own counters, and the words that say so are on the page, not
    // in a tooltip — including that "unknown" is not zero and that subject traffic is another figure.
    assert.match(HTML, /class="rl-traffic-note"><b>Limit activity<\/b> is what this limit itself decided/);
    assert.match(HTML, /&ldquo;unknown&rdquo; means the gateway&rsquo;s counters were full, not that nothing happened/);
    assert.match(HTML, /Traffic by subject is a different figure/);
    assert.match(HTML, /near the ceiling the oldest are evicted and start again full\.<\/p>/);
    assert.match(HTML, /x-show="rlStatusView\.scheduleError" role="status"><span class="tag level-error">schedule unavailable<\/span>/);
  });

  await t.test('status filters explain themselves to assistive tech; the adaptive switch is described', () => {
    const chip = page([]).rlFlagChips.find(c => c.label === 'Refused');
    assert.match(chip.srTitle, /since the gateway last started/);
    assert.match(HTML, /<span class="sr-only" x-text="c\.srTitle"><\/span>/);
    assert.match(HTML, /aria-label="Adapt model limits to load" aria-describedby="rl-adaptive-desc"/);
    assert.match(HTML, /id="rl-adaptive-desc">When the gateway is saturated/);
  });
});

test('render caching never changes an answer', async t => {
  await t.test('without a reactive engine every view computes, so methods and tests see the truth', () => {
    const app = page([rule('model', 'a', 1)]);
    assert.equal(app.rlDirtyRender, false);
    app.rlDraft.rules[0].rpm = 2;
    assert.deepEqual([app.rlDirtyRender, app.rateLimitsDirty, app.rlDirtyView.count], [true, true, 1]);
    assert.equal(app.rlRuleRows[0].draftTag, 'unsaved');
    app.rlDraft.rules[0].rpm = 1;
    assert.deepEqual([app.rlDirtyRender, app.rlRuleRows[0].draftTag], [false, '']);
  });

  await t.test('with one, views are computed by one effect each and a closed drawer keeps its last view', () => {
    const effects = [];
    const context = {
      document: { addEventListener() {}, hidden: false, getElementById: () => null },
      window: { addEventListener() {}, AdminIcons: null },
      localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
      Alpine: { data() {}, directive() {}, store: () => ({}), effect(fn) { effects.push(fn); fn(); return fn; } },
      setInterval: () => 0, clearInterval() {}, setTimeout: () => 0, clearTimeout() {}, console, Intl,
    };
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
    const app = context.adminApp();
    app.loadRateLimitSchedule = async () => {}; app.queueRateLimitScheduleRefresh = () => {};
    app.applyRateLimitsData({ version: 1, enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 0, maxConcurrentStreams: 0 }, plans: {}, rules: [rule('model', 'a', 1), rule('model', 'b', 5)] });
    app.rlStartLiveViews();
    const rerun = () => effects.forEach(fn => fn());
    rerun();

    assert.equal(app.rlDirtyRender, false);
    app.rlDraft.rules[0].rpm = 2;
    assert.equal(app.rateLimitsDirty, true, 'the exact getter is never cached');
    assert.equal(app.rlDirtyRender, false, 'rendering waits for the effect…');
    rerun();
    assert.deepEqual([app.rlDirtyRender, app.rlDirtyView.count], [true, 1], '…and then agrees');

    // Row views are reused while nothing they were built from changed, and rebuilt when it did.
    const before = app.rlRuleRows;
    assert.equal(app.rlRuleRows[1], before[1]);
    app.rlDraft.rules[1].rpm = 6;
    rerun();
    const after = app.rlRuleRows;
    assert.notEqual(after[1], before[1]);
    assert.equal(after[0], before[0]);
    assert.deepEqual([after[1].rpm, after[1].draftTag], ['6', 'unsaved']);
    app.rateLimitUsage = { totals: { requests: 1 }, violations: [{ scope: 'model', key: 'a', control: 'rate', hits: 9 }] };
    rerun();
    assert.equal(app.rlRuleRows[0].refused, '9', 'new activity invalidates every reused row');

    app.openRateLimitRule('model:a');
    const open = app.rlRuleDrawerView;
    app.closeRateLimitRule();
    app.rlRule.rpm = 999;
    assert.equal(app.rlRuleDrawerView, open, 'shut: the last view, not a recomputation');
    app.openRateLimitRule('model:b');
    assert.equal(app.rlRuleDrawerView.title, 'b');
  });
});
