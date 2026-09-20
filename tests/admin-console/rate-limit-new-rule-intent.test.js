/**
 * Tests for the intent-based new-rule form.
 *
 * The form asks who is limited (an API key, a tenant, everyone) and on which model (one, all), and
 * derives the stored scope and target. What is pinned here is the contract of that derivation: one
 * mapping in both directions, a form state in which hidden or stale text has no way into a rule, a
 * preview that is a reading of the very rule Create stores, and a list of the other limits on the
 * same requests that marks a "tightest" only where the gateway's semantics actually give one.
 *
 * Key resolution, alias handling and the redundancy warning are pinned by
 * rate-limit-new-rule-pickers.test.js, which this form has to keep passing; they are not repeated.
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

const KEY_A = '6f1c0a52-0000-4000-8000-00000000000a';
const KEYS = [
  { id: KEY_A, label: 'Checkout service', assignee: 'Payments', keyPrefix: 'sk-a1b2' },
  { id: 'aaaa0000-0000-4000-8000-000000000002', label: 'Batch importer', keyPrefix: 'sk-0002' },
];
const MODELS = [{ id: 'gpt-4', aliases: ['flagship'] }, { id: 'gpt-4-mini', aliases: [] }];

const rule = (scope, target, rpm, burst = 0, extra = {}) =>
  ({ scope, target, rpm, burst, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });
const window = (rpm, burst = 0, extra = {}) =>
  ({ name: 'w', kind: 'weekly', days: ['mon'], start: '00:00', end: '06:00', rpm, burst, maxConcurrentStreams: 0, suspend: false, ...extra });

/** An open form over a draft with these rules. */
function form({ rules = [], plans = {}, adaptive = false } = {}) {
  const app = createApp();
  const config = () => JSON.parse(JSON.stringify({
    enabled: true, adaptiveEnabled: adaptive, default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans, rules,
  }));
  app.rateLimits = config();
  app.rlDraft = config();
  app.keys = app.normalizeApiKeyList(KEYS);
  app.models = MODELS;
  app.rlKeysState = 'ready';
  app.fetchKeys = async () => {};
  app.openRateLimitNewRule();
  return app;
}

function fill(app, { who, where, subject, model, rpm, burst }) {
  if (who) app.setRateLimitNewRuleWho(who);
  if (where) app.setRateLimitNewRuleWhere(where);
  if (subject !== undefined) app.rlNewRule.subject = subject;
  if (model !== undefined) app.rlNewRule.model = model;
  if (rpm !== undefined) app.rlNewRule.rpm = rpm;
  if (burst !== undefined) app.rlNewRule.burst = burst;
  return app;
}

const stored = app => { const r = app.rlDraft.rules[app.rlDraft.rules.length - 1]; return { scope: r.scope, target: r.target }; };

test('the everyday form asks two questions with exactly these answers', () => {
  const view = form().rlNewRuleView;
  assert.deepEqual(view.whoCards.map(c => c.key), ['key', 'tenant', 'everyone']);
  assert.deepEqual(view.whereCards.map(c => c.key), ['one', 'all']);
  assert.equal(view.notProtective, true);
  assert.equal(view.isProtective, false);
});

test('the form opens on the common case: an API key on one model', () => {
  const app = form();
  assert.deepEqual([app.rlNewRule.who, app.rlNewRule.where], ['key', 'one']);
  assert.equal(app.rlNewRuleScope(), 'api_key_model');
  assert.deepEqual([app.rlNewRule.rpm, app.rlNewRule.burst], [600, 60], 'the seeds are unchanged');
});

test('each choice is stored under exactly one scope and target', async t => {
  const cases = [
    ['key', 'one', { subject: 'Checkout service', model: 'flagship' }, 'api_key_model', KEY_A + '|gpt-4'],
    ['key', 'all', { subject: 'Checkout service' }, 'api_key', KEY_A],
    ['tenant', 'one', { subject: 'acme', model: 'flagship' }, 'tenant_model', 'acme|gpt-4'],
    ['tenant', 'all', { subject: 'acme' }, 'tenant', 'acme'],
    ['everyone', 'one', { model: 'flagship' }, 'model', 'gpt-4'],
    ['everyone', 'all', {}, 'global', '*'],
  ];
  for (const [who, where, fields, scope, target] of cases) {
    await t.test(`${who} + ${where || 'n/a'} → ${scope}`, () => {
      const app = fill(form(), { who, where, ...fields, rpm: 50 });
      app.createRateLimitRule();
      assert.deepEqual(stored(app), { scope, target });
      assert.equal(app.rlNewRuleOpen, false);
    });
  }

  await t.test('the mapping reads the same backwards, for every scope the gateway stores', () => {
    const app = createApp();
    for (const s of app.rlScopeCatalog()) {
      const intent = app.rlIntentFor(s.id);
      assert.ok(intent, s.id + ' has an intent');
      assert.equal(app.rlScopeFor(intent.who, intent.where), s.id);
    }
    assert.equal(app.rlIntentFor('API_KEY_MODEL').who, 'key', 'scopes are matched as the gateway matches them');
    assert.equal(app.rlIntentFor('nonsense'), null);
  });
});

/**
 * Anonymous callers and failed sign-ins answer neither question — they have no subject and are on
 * no model — so they are entered from their own button. They are still the same form, the same
 * build and the same Create: only the way in differs.
 */
test('the protective limits have their own way in, and the same way out', async t => {
  const protective = options => {
    const app = form(options);
    app.closeRateLimitNewRule();
    app.openRateLimitProtectiveRule();
    return app;
  };

  await t.test('no subject, no model question, and the seeds they always had', () => {
    const app = protective();
    const view = app.rlNewRuleView;
    assert.equal(view.isProtective, true);
    assert.deepEqual(view.protectiveCards.map(c => c.key), ['anonymous', 'auth_failure']);
    assert.deepEqual([view.showSubject, view.showWhere, view.showModel], [false, false, false]);
    assert.deepEqual([app.rlNewRule.rpm, app.rlNewRule.burst, app.rlNewRule.maxConcurrentStreams], [30, 10, 2]);
    assert.equal(app.rlNewRuleDirty, false);
  });

  await t.test('anonymous callers are stored as anonymous *', () => {
    const app = protective();
    app.createRateLimitRule();
    assert.deepEqual(stored(app), { scope: 'anonymous', target: '*' });
  });

  await t.test('failed sign-ins are stored as auth_failure *, and the form opens there when anonymous is taken', () => {
    const app = protective({ rules: [rule('anonymous', '*', 30, 10)] });
    assert.equal(app.rlNewRule.who, 'auth_failure');
    assert.deepEqual([app.rlNewRule.rpm, app.rlNewRule.burst], [20, 10]);
    app.createRateLimitRule();
    assert.deepEqual(stored(app), { scope: 'auth_failure', target: '*' });
  });

  await t.test('each can have only one rule', () => {
    const app = protective({ rules: [rule('anonymous', '*', 30, 10), rule('auth_failure', '*', 20, 10)] });
    app.createRateLimitRule();
    assert.match(app.rlNewRuleView.error, /already exists/);
    assert.equal(app.rlDraft.rules.length, 2);
  });

  await t.test('existing protective rules still list by name and map back to their choice', () => {
    const app = form({ rules: [rule('anonymous', '*', 30, 10), rule('auth_failure', '*', 20, 10)] });
    assert.deepEqual(app.rlRuleRows.map(r => r.target), ['Anonymous callers', 'Failed sign-ins']);
    assert.deepEqual(app.rlIntentFor('auth_failure'), { who: 'auth_failure', where: '' });
    assert.equal(app.rlRuleSentence(app.rlDraft.rules[1]), 'Limit each client address to 20 failed sign-ins/minute. Up to 30 at once after a quiet spell (20 + 10 burst).');
  });
});

test('changing the intent clears what no longer applies, and keeps what still does', async t => {
  await t.test('key → tenant → everyone', () => {
    const app = form();
    app.rlNewRuleView.subjectSuggestions[0].pick();
    app.rlNewRuleView.modelSuggestions[0].pick();

    app.setRateLimitNewRuleWho('tenant');
    assert.equal(app.rlNewRule.subject, '');
    assert.equal(app.rlNewRule.model, 'gpt-4', 'key on a model → tenant on a model keeps the model');

    app.rlNewRule.subject = 'acme';
    app.setRateLimitNewRuleWho('everyone');
    assert.equal(app.rlNewRule.subject, '');
    assert.equal(app.rlNewRuleTarget(), 'gpt-4');
  });

  await t.test('one model → all models drops the model, and coming back does not restore it', () => {
    const app = fill(form(), { subject: 'Checkout service', model: 'gpt-4' });
    app.setRateLimitNewRuleWhere('all');
    assert.equal(app.rlNewRule.model, '');
    assert.equal(app.rlNewRuleTarget(), KEY_A);
    app.setRateLimitNewRuleWhere('one');
    assert.equal(app.rlNewRule.model, '');
  });

  await t.test('text forced into a hidden field still has no way into the rule', () => {
    const app = fill(form(), { who: 'everyone', where: 'all', rpm: 50 });
    Object.assign(app.rlNewRule, { subject: 'Checkout service', model: 'gpt-4', picked: { subject: { value: KEY_A, text: 'Checkout service' } } });
    assert.equal(app.rlNewRuleView.showSubject, false);
    app.createRateLimitRule();
    assert.deepEqual(stored(app), { scope: 'global', target: '*' });
  });

  await t.test('numbers the operator typed survive a change of intent; seeds do not', () => {
    const app = form();
    app.setRateLimitNewRuleTier('rpm', 25);
    app.setRateLimitNewRuleWho('auth_failure');
    assert.equal(app.rlNewRule.rpm, 25);
  });
});

test('“Limit this key…” opens the same form with the key chosen', async t => {
  await t.test('the key is picked, by id, and everything else is still to be chosen', () => {
    const app = form();
    app.closeRateLimitNewRule();
    app.startRateLimitNewRule({ key: app.keys[0] });
    assert.equal(app.rlNewRuleOpen, true);
    assert.deepEqual([app.rlNewRule.who, app.rlNewRule.where], ['key', 'one']);
    assert.equal(app.rlNewRule.subject, 'Checkout service (sk-a1b2…)');
    assert.equal(app.rlNewRuleBuild().key.id, KEY_A);
    assert.equal(app.rlNewRule.model, '');
    assert.equal(app.rlNewRuleDirty, false, 'a prefilled form is not unsaved work until it is changed');

    fill(app, { model: 'flagship', rpm: 30 });
    assert.equal(app.rlNewRuleDirty, true);
    app.createRateLimitRule();
    assert.deepEqual(stored(app), { scope: 'api_key_model', target: KEY_A + '|gpt-4' });
  });

  await t.test('from the Keys page it lands on Rate limits first, loading them if need be', async () => {
    const app = form();
    app.closeRateLimitNewRule();
    const draft = app.rlDraft;
    app.rlDraft = null;
    const went = [];
    app.setTab = name => went.push(name);
    app.setSettingsSubTab = sub => went.push(sub);
    app.loadRateLimits = async () => { app.rlDraft = draft; };
    await app.limitRateForKey(app.keys[0]);
    assert.deepEqual(went, ['settings', 'limits']);
    assert.equal(app.rlNewRuleBuild().key.id, KEY_A);
  });

  await t.test('a read-only configuration is said, not opened', async () => {
    const app = form();
    app.closeRateLimitNewRule();
    app.rlReadOnlyReason = 'Rate limits are read from appsettings on this gateway.';
    app.setTab = () => {};
    app.setSettingsSubTab = () => {};
    const said = [];
    app.toast = message => said.push(message);
    await app.limitRateForKey(app.keys[0]);
    assert.equal(app.rlNewRuleOpen, false);
    assert.match(said[0], /appsettings/);
  });
});

test('the preview is a reading of the rule Create stores', async t => {
  await t.test('names the key, the canonical model, and the burst', () => {
    const app = fill(form(), { subject: 'Checkout service', model: 'flagship', rpm: 120, burst: 20 });
    const preview = app.rlNewRuleView.preview;
    assert.equal(preview, 'Limit API key Checkout service to 120 requests/minute on gpt-4. Up to 140 at once after a quiet spell (120 + 20 burst).');
    app.createRateLimitRule();
    const created = app.rlDraft.rules[app.rlDraft.rules.length - 1];
    assert.equal(app.rlRuleSentence(created), preview, 'the sentence is a function of the stored rule alone');
  });

  await t.test('says what each intent means', () => {
    assert.equal(fill(form(), { who: 'tenant', where: 'all', subject: 'acme', rpm: 300, burst: 0 }).rlNewRuleView.preview,
      'Limit tenant acme to 300 requests/minute across all models. This replaces the tenant’s plan rate.');
    assert.equal(fill(form(), { who: 'everyone', model: 'gpt-4', rpm: 60, burst: 0 }).rlNewRuleView.preview,
      'Limit everyone together to 60 requests/minute on gpt-4.');
  });

  await t.test('does not describe a rate the rule does not set', () => {
    const app = fill(form(), { subject: 'Checkout service', model: 'gpt-4', rpm: 0, burst: 0 });
    app.rlNewRule.maxConcurrentStreams = 2;
    assert.match(app.rlNewRuleView.preview, /^No request-rate limit for API key Checkout service on gpt-4 from this rule\. At most 2 streams/);
  });
});

test('errors appear once Create is pressed, and go as soon as they are fixed', () => {
  const app = fill(form(), { rpm: 50 });
  assert.equal(app.rlNewRuleView.error, '');
  app.createRateLimitRule();
  assert.match(app.rlNewRuleView.error, /Choose the API key/);
  app.rlNewRule.subject = 'Checkout service';
  assert.match(app.rlNewRuleView.error, /Choose a model/);
  app.rlNewRule.model = 'gpt-4';
  assert.equal(app.rlNewRuleView.error, '');
  assert.equal(app.rlDraft.rules.length, 0, 'fixing the form does not create the rule');
});

/**
 * Every row counts a superset of the new rule's traffic, so all of them must admit a request. The
 * only ordering the gateway has is partial: a limit decides the outcome alone ("dominant") when it
 * counts every request the others count and is never looser than any of them, in rate and in
 * capacity. The lowest numbers do not make a limit dominant — a wider one is shared with other
 * traffic and can refuse first — so where nothing qualifies the list marks nothing.
 */
test('the other limits on the same requests', async t => {
  const rowsOf = app => app.rlNewRuleView.limitRows;
  const dominant = app => rowsOf(app).filter(r => r.hasChip).map(r => r.key);
  const note = app => app.rlNewRuleView.limitsNote;
  const keyOnModel = (options, rpm, burst = 0) =>
    fill(form(options), { subject: 'Checkout service', model: 'gpt-4', rpm, burst });

  await t.test('overlapping scopes are all listed, in the gateway’s order, and nothing narrower or unrelated is', () => {
    const rules = [
      rule('global', '*', 5000), rule('api_key', KEY_A, 200), rule('model', 'gpt-4', 1200),
      rule('model', 'gpt-4-mini', 5), rule('api_key', KEYS[1].id, 5), rule('api_key_model', KEYS[1].id + '|gpt-4', 5),
    ];
    const app = keyOnModel({ rules }, 30);
    assert.deepEqual(rowsOf(app).map(r => r.key), ['mine', 'global', 'tenant', 'api_key', 'model']);
    assert.equal(rowsOf(app)[3].label, 'Checkout service on all models');
  });

  await t.test('the lowest numbers are not dominance: wider limits are shared and can refuse first', () => {
    const app = keyOnModel({ rules: [rule('global', '*', 5000), rule('model', 'gpt-4', 1200)] }, 30);
    assert.deepEqual(dominant(app), [], 'the new rule is the narrowest; it can never vouch for a wider one');
    assert.match(note(app), /lowest rate and capacity.*can still refuse a request first/);
  });

  await t.test('a limit that counts all the others and is never looser is dominant, and that is the warning', () => {
    // A key, all models, at the seeds: whatever tenant the key is in, its 60 + 10 tier contains
    // this traffic and is within 600 + 60.
    const app = fill(form(), { where: 'all', subject: 'Checkout service' });
    assert.deepEqual(dominant(app), ['tenant']);
    assert.match(note(app), /none of the others can be the one that refuses/);
    assert.match(app.rlNewRuleView.looserWarning, /loosest tenant tier configured \(60 rpm\)/);

    const gateway = keyOnModel({ rules: [rule('global', '*', 40, 0), rule('model', 'gpt-4', 100, 0)] }, 50);
    assert.deepEqual(dominant(gateway), ['global'], 'the gateway ceiling counts everything, the tenant tier and the model included');
  });

  await t.test('a tenant tier and a model rule do not contain each other, so neither is dominant', () => {
    // Both are within the new rule, so it is redundant — but which of the two refuses first
    // depends on who else is calling the model.
    const app = keyOnModel({ rules: [rule('model', 'gpt-4', 40, 0)] }, 600, 60);
    assert.deepEqual(dominant(app), []);
    assert.match(note(app), /no one of them decides the outcome alone/);
    assert.notEqual(app.rlNewRuleView.looserWarning, '');
  });

  await t.test('a tenant’s rule on the model may count this key too, so nothing is dominant over it unheard', () => {
    // Without acme|gpt-4 the tenant tier (60 + 10) contains everything listed and is dominant.
    const before = keyOnModel({}, 600, 60);
    assert.deepEqual(dominant(before), ['tenant']);

    // acme on gpt-4 at 5 rpm refuses long before the tier does — if this key is acme's.
    const app = keyOnModel({ rules: [rule('tenant_model', 'acme|gpt-4', 5, 0), rule('tenant_model', 'acme|gpt-4-mini', 1, 0)] }, 600, 60);
    const row = rowsOf(app).find(r => r.key === 'tenant_model:acme|gpt-4');
    assert.match(row.note, /only if this key belongs to that tenant/);
    assert.equal(rowsOf(app).some(r => /gpt-4-mini/.test(r.key)), false, 'another model’s rule is not listed');
    assert.deepEqual(dominant(app), []);

    // And a limit that may not apply is never the reason to call the new rule redundant.
    const narrow = keyOnModel({ rules: [rule('tenant_model', 'acme|gpt-4', 5, 0)], plans: { big: { rpm: 9000, burst: 0, maxConcurrentStreams: 0 } } }, 50);
    assert.equal(narrow.rlNewRuleView.looserWarning, '');
  });

  await t.test('a lower rate with a bigger burst is not tighter', () => {
    // 30 + 30: slower than the model's 40, but it can hold 60 tokens to the model's 40.
    const app = keyOnModel({ rules: [rule('model', 'gpt-4', 40, 0)] }, 30, 30);
    assert.deepEqual(dominant(app), []);
    assert.match(note(app), /no one of them decides the outcome alone/);
    assert.equal(app.rlNewRuleView.looserWarning, '', 'and the new rule is certainly not redundant');
  });

  await t.test('a scheduled limit is shown as varying, is never dominant, and never a reason to warn', () => {
    const rules = [rule('global', '*', 10, 0, { schedule: [window(1200)] })];
    const app = keyOnModel({ rules }, 50);
    assert.match(rowsOf(app).find(r => r.key === 'global').note, /scheduled/);
    assert.deepEqual(dominant(app), []);
    assert.equal(app.rlNewRuleView.looserWarning, '');
  });

  await t.test('the tenant tier is a range over plans and tenant rules, because the key’s tenant is unknown', () => {
    const options = { plans: { pro: { rpm: 600, burst: 60, maxConcurrentStreams: 0 } }, rules: [rule('tenant', 'acme', 2000, 0)] };
    const app = keyOnModel(options, 100);
    const row = rowsOf(app).find(r => r.key === 'tenant');
    assert.match(row.numbers, /^60–2,000 rpm/);
    assert.match(row.note, /depends on the tenant/);
    // 100 rpm is above the default tier's 60 and below the others: it depends on the tenant.
    assert.deepEqual(dominant(app), []);
    assert.match(note(app), /no one of them decides/);
    assert.match(note(keyOnModel(options, 40, 0)), /lowest rate and capacity/);
  });

  await t.test('for a tenant on a model the tenant’s own rule widens the range but is never taken as the answer', () => {
    const app = fill(form({ rules: [rule('tenant', 'acme', 50, 0)] }), { who: 'tenant', subject: 'acme', model: 'gpt-4', rpm: 55, burst: 0 });
    assert.equal(rowsOf(app).find(r => r.key === 'tenant').label, 'Tenant tier of acme');
    assert.equal(app.rlNewRuleView.looserWarning, '', 'acme may be limited under its id by another rule');
    assert.deepEqual(dominant(app), []);
  });

  await t.test('a tenant rule replaces the plan, so the tenant tier is not listed against it', () => {
    const app = fill(form({ rules: [rule('global', '*', 100)] }), { who: 'tenant', where: 'all', subject: 'acme', rpm: 5000 });
    assert.deepEqual(rowsOf(app).map(r => r.key), ['mine', 'global']);
    assert.deepEqual(dominant(app), ['global']);
    assert.equal(app.rlNewRuleView.looserWarning, '', 'and looser is a tenant rule’s job, so it is not warned about');
  });

  await t.test('with adaptive shedding on, a limit that does not shrink cannot vouch for one that does', () => {
    const rules = [rule('global', '*', 40, 0)];
    const off = keyOnModel({ rules }, 50);
    assert.deepEqual(dominant(off), ['global']);
    assert.match(off.rlNewRuleView.looserWarning, /whole-gateway limit \(40 rpm\)/);
    // On: under load the rule on gpt-4 runs below 50, possibly below 40, and the ceiling does not move.
    const on = keyOnModel({ rules, adaptive: true }, 50);
    assert.deepEqual(dominant(on), []);
    assert.equal(on.rlNewRuleView.looserWarning, '');
  });

  await t.test('a limit with no rate is listed and takes no part in the comparison', () => {
    const rules = [rule('api_key', KEY_A, 0, 0, { maxConcurrentStreams: 4 })];
    const app = keyOnModel({ rules }, 30);
    assert.match(rowsOf(app).find(r => r.key === 'api_key').numbers, /no rate limit · 4 streams/);
    assert.match(note(app), /lowest rate and capacity/);
  });

  await t.test('switched-off rules, the whole gateway itself, and the protective budgets list nothing', () => {
    assert.deepEqual(rowsOf(fill(form({ rules: [rule('model', 'gpt-4', 40, 0, { enabled: false })] }), { who: 'everyone', model: 'gpt-4', rpm: 10 })), []);
    assert.deepEqual(rowsOf(fill(form(), { who: 'everyone', where: 'all', rpm: 10 })), []);
    const budget = form({ rules: [rule('global', '*', 100)] });
    budget.closeRateLimitNewRule();
    budget.openRateLimitProtectiveRule();
    assert.deepEqual(rowsOf(budget), [], 'the protective budgets are metered by their own middleware');
  });
});
