/**
 * The pre-save review on Settings → Rate limits.
 *
 * A save replaces the whole rule set in production, so the review has to say what kind of change
 * each item is and show both sides of it. This pins:
 *
 *   - every item carries a kind, a subject an operator recognises, and before → after values;
 *   - changes that take a limit away are marked, sort first, and are named on the Save button;
 *   - the first press on a destructive save opens the review instead of sending it — no dialog;
 *   - Undo puts back exactly one item and leaves the rest staged;
 *   - after a version conflict the other party's change is listed too, apart from the operator's;
 *   - the review is rendered inside the sticky bar, and its toggle announces its state.
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

const clone = v => JSON.parse(JSON.stringify(v));
const KEY_ID = '0b9f6c1e-1111-4222-8333-444455556666';
const WINDOW = { name: 'off-peak', kind: 'weekly', days: ['mon'], start: '19:00', end: '07:00', timeZone: 'UTC', rpm: 1200, burst: 0, maxConcurrentStreams: 0 };
const rule = (scope, target, rpm, extra = {}) => ({ scope, target, rpm, burst: 60, maxConcurrentStreams: 10, enabled: true, schedule: [], ...extra });

const SAVED = {
  version: 7, enabled: true, adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
  plans: { standard: { rpm: 1200, burst: 120, maxConcurrentStreams: 20 } },
  rules: [
    rule('model', 'gpt-4', 600),
    rule('api_key_model', KEY_ID + '|gpt-4', 600, { schedule: [WINDOW, { ...WINDOW, name: 'weekend', days: ['sat'] }] }),
    rule('tenant', 'acme', 600),
  ],
};

function reviewApp() {
  const app = createApp();
  app.keys = [{ id: KEY_ID, label: 'old-batch', keyPrefix: 'pk_live_ab' }];
  app.toast = () => {};
  app.loadRateLimitSchedule = async () => {};
  app.queueRateLimitScheduleRefresh = () => {};
  app.applyRateLimitsData(clone(SAVED));
  return app;
}

const find = (app, id) => app.rlDirtyView.items.find(i => i.id === id);

test('each item says what kind of change it is, to what, and both values', async t => {
  await t.test('a changed tier shows before → after, only for the numbers that moved', () => {
    const app = reviewApp();
    Object.assign(app.rlDraft.rules[2], { rpm: 300, burst: 30 });
    const item = find(app, 'rule:tenant:acme');
    assert.deepEqual([item.kind, item.subject, item.change, item.destructive],
      ['changed', 'Tenant “acme”', '600 → 300 rpm · burst 60 → 30', false]);
  });

  await t.test('a deletion names the key, what is lost, and is destructive', () => {
    const app = reviewApp();
    app.rlDraft.rules.splice(1, 1);
    const item = find(app, 'rule:api_key_model:' + KEY_ID + '|gpt-4');
    assert.equal(item.kind, 'deleted');
    assert.equal(item.subject, 'Key & model “old-batch · gpt-4”', 'the name, never a bare id');
    assert.equal(item.change, 'was 600 rpm · 60 burst · 10 streams · 2 windows go with it');
    assert.equal(item.destructive, true);
    assert.match(item.kindCls, /level-error/);
  });

  await t.test('switching off is its own kind and says the tier is kept', () => {
    const app = reviewApp();
    app.toggleRateLimitRuleEnabled('model:gpt-4');
    const item = find(app, 'rule:model:gpt-4');
    assert.deepEqual([item.kind, item.change], ['switched off', 'tier and windows kept']);
    assert.match(item.kindCls, /warn/);
  });

  await t.test('windows report their count, or that they were edited', () => {
    const app = reviewApp();
    app.rlDraft.rules[0].schedule = [clone(WINDOW)];
    assert.equal(find(app, 'rule:model:gpt-4').change, '0 → 1 window');
    app.rlDraft.rules[1].schedule[0].rpm = 900;
    assert.equal(find(app, 'rule:api_key_model:' + KEY_ID + '|gpt-4').change, '2 windows edited');
  });

  await t.test('new rules, plans, the default tier and both switches are all itemised', () => {
    const app = reviewApp();
    app.rlDraft.rules.push(rule('model', 'claude', 50, { maxConcurrentStreams: 0 }));
    app.rlDraft.plans.premium = { rpm: 6000, burst: 600, maxConcurrentStreams: 100 };
    delete app.rlDraft.plans.standard;
    app.rlDraft.default.rpm = 90;
    app.rlDraft.enabled = false;
    app.rlDraft.adaptiveEnabled = true;
    const kinds = Object.fromEntries(app.rlDirtyView.items.map(i => [i.id, i.kind + ' | ' + i.change]));
    assert.deepEqual(kinds, {
      'enabled': 'enforcement off | every rule and window stops applying',
      'plan:standard': 'removed | was 1,200 rpm · 120 burst · 20 streams · its tenants fall back to the default tier',
      'adaptive': 'changed | off → on',
      'default': 'changed | 60 → 90 rpm',
      'plan:premium': 'new | 6,000 rpm · 600 burst · 100 streams',
      'rule:model:claude': 'new | 50 rpm · 60 burst',
    });
  });

  await t.test('what takes a limit away comes first', () => {
    const app = reviewApp();
    app.rlDraft.rules[0].rpm = 1;
    app.rlDraft.rules.splice(2, 1);
    app.rlDraft.enabled = false;
    assert.deepEqual(app.rlDirtyView.items.map(i => i.destructive), [true, true, false]);
  });
});

test('the Save button says what it will do, and a destructive save is looked at once', async t => {
  await t.test('count, and deletions by name', () => {
    const app = reviewApp();
    assert.equal(app.rlDirtyView.saveLabel, 'Save');
    app.rlDraft.rules[0].rpm = 1;
    assert.equal(app.rlDirtyView.saveLabel, 'Save 1 change');
    app.rlDraft.rules.splice(2, 1);
    assert.equal(app.rlDirtyView.saveLabel, 'Save 2 changes (1 deletion)');
    app.rlDraft.enabled = false;
    assert.equal(app.rlDirtyView.saveLabel, 'Save 3 changes (1 deletion, stops enforcing)');
  });

  await t.test('first press opens the review, second press saves; an ordinary save goes straight through', async () => {
    const app = reviewApp();
    let saves = 0;
    app.saveRateLimits = async () => { saves++; };

    app.rlDraft.rules[0].rpm = 1;
    await app.onSaveRateLimitsClick();
    assert.equal(saves, 1, 'nothing destructive: no detour');

    app.rlDraft.rules.splice(2, 1);
    await app.onSaveRateLimitsClick();
    assert.deepEqual([saves, app.rlReviewOpen], [1, true]);
    await app.onSaveRateLimitsClick();
    assert.equal(saves, 2);
  });
});

test('Undo restores one item and leaves the rest staged', async t => {
  await t.test('a deleted rule comes back where it was, with its windows', () => {
    const app = reviewApp();
    app.rlDraft.rules.splice(1, 1);
    app.rlDraft.rules[0].rpm = 1;
    find(app, 'rule:api_key_model:' + KEY_ID + '|gpt-4').undo();
    assert.equal(app.rlDraft.rules.length, 3);
    assert.equal(app.rlDraft.rules.find(r => r.scope === 'api_key_model').schedule.length, 2);
    assert.deepEqual(app.rlDirtyView.items.map(i => i.id), ['rule:model:gpt-4'], 'the other edit is still staged');
  });

  await t.test('a new rule is removed; a changed rule returns to its saved numbers in place', () => {
    const app = reviewApp();
    app.rlDraft.rules.push(rule('model', 'claude', 50));
    app.rlDraft.rules[0].rpm = 1;
    find(app, 'rule:model:claude').undo();
    find(app, 'rule:model:gpt-4').undo();
    assert.deepEqual(app.rlDraft.rules.map(r => r.target + ':' + r.rpm), SAVED.rules.map(r => r.target + ':' + r.rpm));
    assert.equal(app.rateLimitsDirty, false);
    assert.equal(app.rlReviewOpen, false, 'nothing left to review');
  });

  await t.test('switches, the default tier and plans', () => {
    const app = reviewApp();
    app.rlDraft.enabled = false;
    app.rlDraft.adaptiveEnabled = true;
    app.rlDraft.default.rpm = 90;
    delete app.rlDraft.plans.standard;
    app.rlDraft.plans.premium = { rpm: 1, burst: 0, maxConcurrentStreams: 0 };
    for (const id of ['enabled', 'adaptive', 'default', 'plan:standard', 'plan:premium']) find(app, id).undo();
    assert.equal(app.rateLimitsDirty, false);
  });

  await t.test('undoing never writes to what is saved, and read-only cannot undo', () => {
    const app = reviewApp();
    const saved = JSON.stringify(app.rateLimits);
    app.rlDraft.rules[0].rpm = 1;
    app.rlReadOnlyReason = 'This gateway has no database, so rate limits are read-only here.';
    find(app, 'rule:model:gpt-4').undo();
    assert.equal(app.rlDraft.rules[0].rpm, 1);
    app.rlReadOnlyReason = '';
    find(app, 'rule:model:gpt-4').undo();
    assert.equal(JSON.stringify(app.rateLimits), saved);
  });
});

test('after a conflict, the other save is shown apart from the operator’s own changes', async t => {
  const theirs = { ...clone(SAVED), version: 8 };
  theirs.rules[0].rpm = 900;                                  // they retiered gpt-4
  theirs.rules.push(rule('model', 'claude', 120));            // and added claude
  theirs.plans.standard.rpm = 900;

  const conflicted = async () => {
    const app = reviewApp();
    app.rlDraft.rules[0].rpm = 300;                           // we retiered gpt-4 too
    app.apiJson = async () => clone(theirs);
    await app.rlRecoverFromConflict();
    return app;
  };

  await t.test('their changes are listed with values, and each says whether saving would undo it', async () => {
    const app = await conflicted();
    const view = app.rlDirtyView;
    assert.equal(view.hasTheirs, true);
    const byId = Object.fromEntries(view.theirs.map(i => [i.id, i]));
    assert.deepEqual([byId['rule:model:gpt-4'].kind, byId['rule:model:gpt-4'].change], ['changed', '600 → 900 rpm']);
    assert.equal(byId['rule:model:claude'].kind, 'new');
    assert.equal(byId['plan:standard'].change, '1,200 → 900 rpm');
    // The draft was started from the older configuration, so saving it would put all three back.
    assert.deepEqual(view.theirs.map(i => i.overwritten), [true, true, true]);
  });

  await t.test('"Keep theirs" brings the draft into line for that item only', async () => {
    const app = await conflicted();
    app.rlDirtyView.theirs.find(i => i.id === 'plan:standard').keep();
    assert.equal(app.rlDraft.plans.standard.rpm, 900);
    const plan = app.rlDirtyView.theirs.find(i => i.id === 'plan:standard');
    assert.deepEqual([plan.overwritten, plan.kept], [false, true]);
    assert.equal(app.rlDraft.rules[0].rpm, 300, 'the operator’s own edit is untouched');
  });

  await t.test('the operator’s list now compares with what is actually saved', async () => {
    const app = await conflicted();
    assert.equal(find(app, 'rule:model:gpt-4').change, '900 → 300 rpm');
    assert.equal(find(app, 'rule:model:claude').kind, 'deleted');
    assert.equal(app.rlReviewOpen, true);
  });

  await t.test('undoing the accidental deletion adopts their rule', async () => {
    const app = await conflicted();
    find(app, 'rule:model:claude').undo();
    assert.equal(app.rlDraft.rules.some(r => r.target === 'claude' && r.rpm === 120), true);
  });

  await t.test('discard, reload and a successful save all clear it', async () => {
    const a = await conflicted();
    a.discardRateLimitChanges();
    assert.equal(a.rlDirtyView.hasTheirs, false);

    const b = await conflicted();
    b.applyRateLimitsData(clone(theirs));
    assert.equal(b.rlDirtyView.hasTheirs, false);

    const c = await conflicted();
    c.rlAdoptSaved(c.buildRateLimitsPayload(), c.rlCanonical(c.rlDraft), 9);
    assert.equal(c.rlTheirChanges.length, 0);
  });
});

test('the review is inside the sticky bar and its toggle announces its state', () => {
  const bar = HTML.slice(HTML.indexOf('<div class="rl-savebar"'), HTML.indexOf('class="rl-savebar-row"'));
  assert.match(bar, /id="rl-review" x-show="rlReviewOpen"/, 'rendered above the buttons, inside the bar');
  assert.match(bar, /x-for="i in rlDirtyView\.items"/);
  assert.match(bar, /@click="i\.undo" :aria-label="i\.undoLabel"/);
  assert.match(bar, /x-for="i in rlDirtyView\.theirs"/);
  assert.match(bar, /x-show="rateLimitFieldError"/, 'a refused save is explained where the Save button is');
  assert.match(HTML, /:aria-expanded="rlDirtyView\.reviewExpanded" aria-controls="rl-review"/);
  assert.match(HTML, /@click="onSaveRateLimitsClick" :disabled="rateLimitsSaveDisabled" x-text="rlDirtyView\.saveLabel"/);

  const app = reviewApp();
  app.rlDraft.rules[0].rpm = 1;
  assert.deepEqual([app.rlDirtyView.reviewLabel, app.rlDirtyView.reviewExpanded], ['Review changes', 'false']);
  app.toggleRateLimitReview();
  assert.deepEqual([app.rlDirtyView.reviewLabel, app.rlDirtyView.reviewExpanded], ['Hide review', 'true']);
});
