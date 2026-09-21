/**
 * Regression tests for the rate-limit load / draft / save / conflict state machine.
 *
 * The draft is the operator's work and lives only in memory. Five defects let it be lost or made
 * unsaveable without a word: a second Save while the first was out; a save or a refetch that
 * replaced the draft with whatever came back, edits made meanwhile included; a 409 whose only way
 * out was a reload that discarded the draft; a read-only lock inferred from the digits "403"/"503"
 * appearing in a validation message (rule identities quote key ids); and a dirty check that
 * disagreed with the save bar about whether anything had changed.
 *
 * Every request here is a promise the test resolves by hand, so orderings are chosen, not raced.
 *
 *     node --test tests/admin-console/
 */
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const SOURCE = path.join(__dirname, '../../src/33pol.App/wwwroot/admin/admin-app.js');

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

const clone = value => JSON.parse(JSON.stringify(value));

const rule = (target, rpm = 600) =>
  ({ scope: 'model', target, rpm, burst: 60, maxConcurrentStreams: 0, enabled: true, schedule: [] });

const SAVED = {
  version: 7,
  enabled: true,
  adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
  plans: { free: { rpm: 30, burst: 0, maxConcurrentStreams: 0 } },
  rules: [rule('gpt-4')],
};

/** What admin-store throws: the rendered message plus the HTTP status it was rendered from. */
function httpError(status, message, title) {
  const e = new Error(message);
  e.status = status;
  e.title = title || '';
  e.global = false;
  return e;
}

/**
 * A component on a saved configuration with an identical draft, whose every request is parked
 * until the test answers it. `calls` is the log; `pending(method)` finds the oldest unanswered one.
 */
function harness(saved = SAVED) {
  const app = createApp();
  const calls = [];
  app.apiJson = (url, options = {}) => new Promise((resolve, reject) => {
    calls.push({
      url, method: options.method || 'GET', headers: options.headers || {},
      body: options.body ? JSON.parse(options.body) : null, resolve, reject, done: false,
    });
  });
  // runApi minus the loading indicator: errors still propagate to the caller, as they do for real.
  app.runApi = async (scope, label, fn) => fn();
  const toasts = [];
  app.toast = message => { toasts.push(message); };
  app.loadRateLimitSchedule = async () => {};
  app.queueRateLimitScheduleRefresh = () => {};
  app.applyRateLimitsData(clone(saved));

  const pending = method => calls.find(c => !c.done && c.method === method);
  const answer = (method, value) => { const c = pending(method); c.done = true; c.resolve(value); };
  const refuse = (method, error) => { const c = pending(method); c.done = true; c.reject(error); };
  const count = method => calls.filter(c => c.method === method).length;
  return { app, calls, pending, answer, refuse, count, toasts };
}

/** Lets every already-resolved promise run its continuations. */
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

const saveOk = (version) => ({ message: 'Rate limits updated.', version });

test('only one save is ever in flight', async t => {
  await t.test('a double click sends one PUT', async () => {
    const { app, answer, count, toasts } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const first = app.saveRateLimits();
    const second = app.saveRateLimits();
    await settle();

    assert.equal(count('PUT'), 1, 'the second click found a save already out');
    assert.equal(app.rateLimitsSaveDisabled, true, 'and the button says so');

    answer('PUT', saveOk(8));
    await Promise.all([first, second]);
    assert.equal(app.rlSaving, false);
    assert.equal(app.rateLimitsSaveDisabled, false);
    assert.deepEqual(toasts, ['Rate limits updated.'], 'no "someone else changed" toast after one\'s own save');
  });

  await t.test('a refused save releases the guard', async () => {
    const { app, refuse, count } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', httpError(400, 'default.rpm must be between 1 and 1000000.'));
    await assert.rejects(save);

    assert.equal(app.rlSaving, false);
    void app.saveRateLimits().catch(() => {});
    await settle();
    assert.equal(count('PUT'), 2);
  });
});

test('a successful save adopts what was sent, at the version the server answered with', async t => {
  await t.test('the next save is based on the returned version, with no GET needed to learn it', async () => {
    const { app, calls, answer, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    assert.equal(calls[0].headers['If-Match'], 'W/"7"');
    answer('PUT', saveOk(8));
    await save;

    assert.equal(app.rateLimits.version, 8);
    assert.equal(app.rateLimits.rules[0].rpm, 300, 'what was sent is the new baseline');
    assert.equal(app.rateLimitsDirty, false);

    // The follow-up refresh fails. The save was still a save, and the version is still known.
    refuse('GET', httpError(500, 'boom'));
    await settle();
    assert.equal(app.rateLimitsDirty, false);
    assert.equal(app.rlDirtyView.show, false, 'the save bar does not come back for saved work');
    assert.deepEqual(clone(app.rlIfMatchHeaders()), { 'If-Match': 'W/"8"' });
  });

  await t.test('an edit made while the save was out survives it, and reads as unsaved', async () => {
    const { app, calls, answer, count } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    // The kill switch, flipped while the PUT is in flight.
    app.rlDraft.rules[0].enabled = false;
    answer('PUT', saveOk(8));
    await save;

    assert.equal(calls[0].body.rules[0].enabled, true, 'the request said what the draft said when it left');
    assert.equal(app.rlDraft.rules[0].enabled, false, 'the newer edit is still there');
    assert.equal(app.rateLimits.rules[0].rpm, 300);
    assert.equal(app.rateLimitsDirty, true, 'and it is not reported as saved');
    assert.equal(app.rlDirtyView.detail, 'switched off rule Model gpt-4');
    assert.equal(count('GET'), 0, 'no refresh is attempted over unsaved work');
  });

  await t.test('the refresh after a save cannot take an edit made before it lands', async () => {
    const { app, answer } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    answer('PUT', saveOk(8));
    await save;

    app.rlDraft.rules[0].enabled = false;
    answer('GET', { ...clone(SAVED), version: 8, rules: [rule('gpt-4', 300)] });
    await settle();

    assert.equal(app.rlDraft.rules[0].enabled, false);
    assert.equal(app.rateLimitsDirty, true);
  });
});

test('a version conflict never costs the draft', async t => {
  const theirs = { ...clone(SAVED), version: 9, rules: [rule('gpt-4'), rule('claude-3', 120)] };

  await t.test('the baseline and version are refreshed, the draft is kept, and saving again goes through', async () => {
    const { app, calls, answer, refuse, count } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', httpError(409, 'Rate limits were changed by someone else since this page was loaded.'));
    await settle();
    answer('GET', clone(theirs));
    await assert.rejects(save);

    assert.equal(app.rlDraft.rules.length, 1, 'the draft is the operator\'s, untouched');
    assert.equal(app.rlDraft.rules[0].rpm, 300);
    assert.equal(app.rateLimits.version, 9);
    assert.equal(app.rateLimits.rules.length, 2, 'the baseline is what is actually saved now');
    assert.equal(app.rlReviewOpen, true);
    assert.deepEqual(
      app.rlDirtyView.items.map(i => i.text).sort(),
      ['Model gpt-4', 'deleted rule Model claude-3'],
      'the review shows what saving again would do to the other operator\'s change');
    assert.match(app.rateLimitFieldError, /Your edits are kept/);
    assert.equal(app.rlReadOnlyReason, '');

    const retry = app.saveRateLimits();
    await settle();
    assert.equal(count('PUT'), 2);
    assert.equal(calls.filter(c => c.method === 'PUT')[1].headers['If-Match'], 'W/"9"');
    answer('PUT', saveOk(10));
    await retry;
    assert.equal(app.rateLimitsDirty, false);
    assert.equal(app.rateLimits.version, 10);
  });

  await t.test('when the current configuration cannot be loaded the old version stays, so the next save is refused again rather than sent blind', async () => {
    const { app, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', httpError(409, 'conflict'));
    await settle();
    refuse('GET', httpError(500, 'boom'));
    await assert.rejects(save);

    assert.equal(app.rlDraft.rules[0].rpm, 300);
    assert.equal(app.rateLimits.version, 7);
    assert.match(app.rateLimitFieldError, /Your edits are kept/);
  });
});

test('a fetched configuration never lands on top of newer local state', async t => {
  await t.test('an edit made while a tab-entry fetch is out survives the response', async () => {
    const { app, answer } = harness();

    const load = app.loadRateLimits();
    app.rlDraft.rules[0].enabled = false;
    answer('GET', { ...clone(SAVED), version: 8 });
    await load;

    assert.equal(app.rlDraft.rules[0].enabled, false);
    assert.equal(app.rateLimitsDirty, true);
    assert.equal(app.rateLimits.version, 7,
      'the dropped response leaves the version alone too: the edit is still checked against what it was based on');
  });

  await t.test('a clean draft is refreshed', async () => {
    const { app, answer } = harness();

    const load = app.loadRateLimits();
    answer('GET', { ...clone(SAVED), version: 8, rules: [rule('gpt-4', 900)] });
    await load;

    assert.equal(app.rlDraft.rules[0].rpm, 900);
    assert.equal(app.rateLimits.version, 8);
    assert.equal(app.rateLimitsDirty, false);
  });

  await t.test('an older response arriving after a newer one is dropped', async () => {
    const { app, calls } = harness();

    const older = app.loadRateLimits(true);
    const newer = app.loadRateLimits(true);
    calls[1].done = true;
    calls[1].resolve({ ...clone(SAVED), version: 9, rules: [rule('gpt-4', 900)] });
    await newer;
    calls[0].done = true;
    calls[0].resolve({ ...clone(SAVED), version: 8, rules: [rule('gpt-4', 100)] });
    await older;

    assert.equal(app.rateLimits.version, 9);
    assert.equal(app.rlDraft.rules[0].rpm, 900);
  });

  await t.test('a fetch that was out when a save completed does not roll the baseline back', async () => {
    const { app, calls, answer } = harness();

    const load = app.loadRateLimits(true);
    app.rlDraft.rules[0].rpm = 300;
    const save = app.saveRateLimits();
    await settle();
    answer('PUT', saveOk(8));
    await save;
    calls[0].done = true;
    calls[0].resolve(clone(SAVED));
    await load;

    assert.equal(app.rateLimits.version, 8);
    assert.equal(app.rateLimits.rules[0].rpm, 300);
  });

  await t.test('a dirty draft suppresses the tab-entry fetch altogether', async () => {
    const { app, count } = harness();
    app.rlDraft.rules[0].rpm = 300;

    await app.loadRateLimits();

    assert.equal(count('GET'), 0);
  });

  await t.test('"Limit this key…" waits for the load that entering Settings started, and joins it', async () => {
    const { app, answer, count } = harness();
    // What entering the Settings tab does for this section.
    app.setTab = () => { void app.loadRateLimits(); };
    app.setSettingsSubTab = () => {};
    let opened = null;
    app.startRateLimitNewRule = options => {
      opened = options;
      app.rlDraft.rules.push({ ...rule('*'), scope: 'api_key', target: options.key.id });
    };

    const flow = app.limitRateForKey({ id: 'k1' });
    await settle();
    assert.equal(opened, null, 'the form does not open over a load that is still out');
    assert.equal(count('GET'), 1, 'one request, shared');

    answer('GET', { ...clone(SAVED), version: 8 });
    await flow;

    assert.deepEqual(opened, { key: { id: 'k1' } });
    assert.equal(app.rlDraft.rules.length, 2, 'the rule created in the form is still there');
    assert.equal(app.rateLimitsDirty, true);
  });
});

test('one definition of unsaved: order that means nothing is not a change', async t => {
  const agree = (app, dirty) => {
    assert.equal(app.rateLimitsDirty, dirty);
    assert.equal(app.rlDirtyView.show, dirty, 'the save bar');
    assert.equal(app.rateLimitsWorkInProgress, dirty, 'the leave-page guard');
    assert.equal(app.settingsTabs.find(tab => tab.key === 'limits').badge !== '', dirty, 'the tab badge');
  };

  await t.test('renaming a plan away and back', () => {
    const { app } = harness({ ...clone(SAVED), plans: { free: SAVED.plans.free, pro: { rpm: 900, burst: 0, maxConcurrentStreams: 0 } } });
    const rename = (from, to) => {
      app.openRateLimitTier('plan', from);
      app.rlTier.slug = to;
      app.applyRateLimitTier();
      assert.equal(app.rlTierError, '');
    };

    rename('free', 'freeX');
    agree(app, true);
    rename('freeX', 'free');

    assert.deepEqual(Object.keys(app.rlDraft.plans), ['pro', 'free'], 'the key really did move');
    agree(app, false);
  });

  await t.test('deleting a rule and creating it again identically', () => {
    const { app } = harness({ ...clone(SAVED), rules: [rule('gpt-4'), rule('claude-3', 120)] });

    const [removed] = app.rlDraft.rules.splice(0, 1);
    agree(app, true);
    app.rlDraft.rules.push(removed);

    agree(app, false);
  });

  await t.test('a real difference is still one, whatever the order', () => {
    const { app } = harness({ ...clone(SAVED), rules: [rule('gpt-4'), rule('claude-3', 120)] });

    const [removed] = app.rlDraft.rules.splice(0, 1);
    app.rlDraft.rules.push({ ...removed, rpm: 601 });

    agree(app, true);
    assert.equal(app.rlDirtyView.count, 1);
  });
});

test('what a refused save means is read from its HTTP status, never from its text', async t => {
  const refusedWith = async error => {
    const { app, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;
    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', error);
    await assert.rejects(save);
    return app;
  };

  await t.test('a validation message that quotes a key id containing 403 stays a validation error', async () => {
    const message = "rule 'api_key:9f1c4037-aaaa-4bbb-8ccc-0123456789ab' has an rpm of 0, which leaves the rate unlimited by this rule";
    const app = await refusedWith(httpError(400, message, 'Request refused'));

    assert.equal(app.rlReadOnlyReason, '');
    assert.equal(app.rateLimitsEditable, true);
    assert.equal(app.rateLimitFieldError, message);
  });

  await t.test('and one containing 503', async () => {
    const app = await refusedWith(httpError(400, "rule 'model:gpt-5030' enforces nothing", 'Request refused'));

    assert.equal(app.rlReadOnlyReason, '');
    assert.equal(app.rateLimitsSaveDisabled, false);
  });

  await t.test('a real 403 makes the page read-only, whatever its wording', async () => {
    const app = await refusedWith(httpError(403, 'This admin key is not permitted to perform that action.', 'Not permitted'));

    assert.equal(app.rlReadOnlyReason, 'This key may view rate limits but not change them.');
    assert.equal(app.rateLimitsEditable, false);
    assert.equal(app.rlDraft.rules[0].rpm, 300, 'the draft is kept');
  });

  await t.test('a real 503 makes the page read-only', async () => {
    const app = await refusedWith(httpError(503, 'Rate-limit updates require a configured database.', 'Unavailable'));

    assert.match(app.rlReadOnlyReason, /no database/);
    assert.equal(app.rateLimitsSaveDisabled, true);
  });

  await t.test('an explicit Reload clears a read-only conclusion', async () => {
    const { app, refuse, answer } = harness();
    app.rlDraft.rules[0].rpm = 300;
    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', httpError(403, 'no', 'Not permitted'));
    await assert.rejects(save);
    app.discardRateLimitChanges();

    app.reloadRateLimits();
    await settle();
    answer('GET', clone(SAVED));
    await settle();

    assert.equal(app.rlReadOnlyReason, '');
    assert.equal(app.rateLimitsEditable, true);
  });

  await t.test('a load failure is classified by status too', async () => {
    const { app, refuse } = harness();
    const load = app.loadRateLimits(true);
    refuse('GET', httpError(500, "rule 'api_key:4010-4030' could not be read"));
    await load;

    assert.equal(app.rateLimitsLoadError, "rule 'api_key:4010-4030' could not be read");
  });
});

test('the Save button consumes a refusal the page has shown, and nothing else', async t => {
  await t.test('a refused save resolves the click, with the error on the page and the draft kept', async () => {
    const { app, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const click = app.onSaveRateLimitsClick();
    await settle();
    refuse('PUT', httpError(400, "rule 'model:gpt-4' enforces nothing", 'Request refused'));
    await click;

    assert.equal(app.rateLimitFieldError, "rule 'model:gpt-4' enforces nothing");
    assert.equal(app.rlDraft.rules[0].rpm, 300);
    assert.equal(app.rlSaving, false);
  });

  await t.test('a conflict resolves it too, after the recovery has run', async () => {
    const { app, refuse, answer } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const click = app.onSaveRateLimitsClick();
    await settle();
    refuse('PUT', httpError(409, 'conflict', 'Conflict'));
    await settle();
    answer('GET', { ...clone(SAVED), version: 9 });
    await click;

    assert.equal(app.rateLimits.version, 9);
    assert.match(app.rateLimitFieldError, /Your edits are kept/);
  });

  await t.test('saveRateLimits itself still rejects, for callers that await it', async () => {
    const { app, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const save = app.saveRateLimits();
    await settle();
    refuse('PUT', httpError(400, 'no', 'Request refused'));

    await assert.rejects(save, { status: 400 });
  });

  await t.test('a programming error is not swallowed', async () => {
    const { app, refuse } = harness();
    app.rlDraft.rules[0].rpm = 300;

    const click = app.onSaveRateLimitsClick();
    await settle();
    refuse('PUT', new TypeError('x is not a function'));

    await assert.rejects(click, TypeError);
    assert.equal(app.rlSaving, false);
  });
});
