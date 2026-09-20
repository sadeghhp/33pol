/**
 * Regression tests for picking a rule's key and model in the new-rule drawer.
 *
 * An operator reported that a limit could not be put on an API key: the list of keys did not show
 * and could not be scrolled. Four causes, each pinned here — Settings never loaded the key list, so
 * opened first it had none to offer; suggestions were cut to six with no way to the seventh; a pair
 * scope poured keys and models into one list under both fields; and a pick replaced the key's name
 * with its GUID. Two silent failures sat next to them: a model alias saved as typed, which
 * enforcement (matching canonical ids only) never finds, and a "no tighter than what applies"
 * warning whose baseline was hard-coded to zero for every key, tenant-model and model rule.
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

const KEY_A = '6F1C0A52-0000-4000-8000-00000000000A';

/** Key ids are GUIDs, as the gateway issues them; a key rule is matched on nothing else. */
const keyId = i => 'aaaa0000-0000-4000-8000-' + String(i).padStart(12, '0');

function keys(count) {
  const list = [{ id: KEY_A, label: 'Checkout service', assignee: 'Payments', keyPrefix: 'sk-a1b2' }];
  for (let i = 1; i < count; i++) {
    list.push({ id: keyId(i), label: 'Service ' + i, keyPrefix: 'sk-' + String(i).padStart(4, '0') });
  }
  return list;
}

const MODELS = [
  { id: 'gpt-4', aliases: ['flagship'] },
  { id: 'gpt-4-mini', aliases: [] },
];

function config(rules = [], plans = {}, adaptiveEnabled = false) {
  return { enabled: true, adaptiveEnabled, default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans, rules };
}

/**
 * The form is driven by intent (who, on which model), not by scope. These tests were written per
 * stored scope, which is still what they are about, so the scope is turned into its intent through
 * the console's own reverse mapping, and "the target" is whichever field that intent shows.
 */
function chooseScope(app, scope) {
  const intent = app.rlIntentFor(scope);
  Object.assign(app.rlNewRule, { who: intent.who, where: intent.where || app.rlNewRule.where });
}

function setTarget(app, text) {
  app.rlNewRule[app.rlNewRule.who === 'everyone' ? 'model' : 'subject'] = text;
}

/** A component with the new-rule form on `scope`, with keys and models already loaded. */
function appAt(scope, { keyCount = 12, rules = [], plans = {}, adaptive = false } = {}) {
  const app = createApp();
  app.keys = app.normalizeApiKeyList(keys(keyCount));
  app.models = MODELS;
  app.rlKeysState = 'ready';
  app.rateLimits = config(rules, plans, adaptive);
  app.rlDraft = config(rules, plans, adaptive);
  chooseScope(app, scope);
  return app;
}

test('Settings loads the key list for itself', async t => {
  await t.test('loading, then ready', async () => {
    const app = createApp();
    let seen = '';
    app.fetchKeys = async () => { seen = app.rlKeysState; app.keys = keys(3); };
    await app.loadRateLimitKeys();
    assert.equal(seen, 'loading');
    assert.equal(app.rlKeysState, 'ready');
  });

  await t.test('a failure is a state the drawer shows, not a rejection that fails the page', async () => {
    const app = createApp();
    app.fetchKeys = async () => { throw new Error('403'); };
    await assert.doesNotReject(() => app.loadRateLimitKeys());
    assert.equal(app.rlKeysState, 'failed');
    app.rlDraft = config();
    chooseScope(app, 'api_key');
    assert.equal(app.rlNewRuleView.keysFailed, true);
    assert.equal(app.rlNewRuleView.keysLoading, false);
  });

  await t.test('two callers at once share one request', async () => {
    const app = createApp();
    let calls = 0;
    let release;
    app.fetchKeys = () => { calls++; return new Promise(resolve => { release = () => { app.keys = keys(2); resolve(); }; }); };
    const first = app.loadRateLimitKeys();
    const second = app.loadRateLimitKeys(true);
    release();
    await Promise.all([first, second]);
    assert.equal(calls, 1);
    assert.equal(app.rlKeysState, 'ready');
  });

  await t.test('a failed refresh keeps the list that was there', async () => {
    const app = createApp();
    app.keys = keys(2);
    app.fetchKeys = async () => { throw new Error('503'); };
    await app.loadRateLimitKeys(true);
    assert.equal(app.rlKeysState, 'ready');
    assert.equal(app.keys.length, 2);
  });

  await t.test('a failure can be retried', async () => {
    const app = createApp();
    app.fetchKeys = async () => { throw new Error('503'); };
    await app.loadRateLimitKeys();
    app.fetchKeys = async () => { app.keys = keys(2); };
    await app.loadRateLimitKeys();
    assert.equal(app.rlKeysState, 'ready');
  });

  await t.test('an empty result is ready-and-empty, not a failure', async () => {
    const app = createApp();
    app.fetchKeys = async () => { app.keys = []; };
    await app.loadRateLimitKeys();
    app.rlDraft = config();
    chooseScope(app, 'api_key');
    assert.equal(app.rlNewRuleView.keysEmpty, true);
    assert.equal(app.rlNewRuleView.keysFailed, false);
  });

  await t.test('a slow early key response cannot replace a later one', async () => {
    const app = createApp();
    const pending = [];
    app.apiJson = () => new Promise(resolve => pending.push(resolve));
    const early = app.fetchKeys();
    const late = app.fetchKeys();
    pending[1](keys(3));
    await late;
    pending[0](keys(1));
    await early;
    assert.equal(app.keys.length, 3);
  });

  await t.test('keys another tab already loaded are reused', async () => {
    const app = createApp();
    app.keys = keys(2);
    app.fetchKeys = async () => { throw new Error('must not refetch'); };
    await app.loadRateLimitKeys();
    assert.equal(app.rlKeysState, 'ready');
  });
});

test('every matching key is offered, not the first six', () => {
  const app = appAt('api_key', { keyCount: 40 });
  assert.equal(app.rlNewRuleView.subjectSuggestions.length, 40);
  assert.equal(app.rlNewRuleView.subjectMore, '');
});

test('a very long list is bounded and says what it left out', () => {
  const app = appAt('api_key', { keyCount: 450 });
  const view = app.rlNewRuleView;
  assert.equal(view.subjectSuggestions.length, 200);
  assert.match(view.subjectMore, /200 of 450/);
});

test('a pair scope keeps keys and models in separate lists', () => {
  const app = appAt('api_key_model');
  const view = app.rlNewRuleView;
  assert.equal(view.subjectSuggestions.length, 12);
  assert.ok(view.subjectSuggestions.every(s => !MODELS.some(m => m.id === s.text)), 'no model among the keys');
  assert.deepEqual(view.modelSuggestions.map(s => s.text), ['gpt-4', 'gpt-4-mini']);
});

test('a picked key stays readable and is stored by id', async t => {
  await t.test('the field shows the name, the rule carries the id', () => {
    const app = appAt('api_key_model');
    app.rlNewRuleView.subjectSuggestions[0].pick();
    app.rlNewRuleView.modelSuggestions[0].pick();
    assert.equal(app.rlNewRule.subject, 'Checkout service (sk-a1b2…)');
    assert.equal(app.rlNewRuleTarget(), KEY_A + '|gpt-4');
    assert.match(app.rlNewRuleView.keyNote, new RegExp(KEY_A));
    assert.equal(app.rlNewRuleView.hasSubjectSuggestions, false, 'the list closes on a pick');

    app.rlNewRule.rpm = 10;
    app.createRateLimitRule();
    assert.deepEqual(
      { scope: app.rlDraft.rules[0].scope, target: app.rlDraft.rules[0].target },
      { scope: 'api_key_model', target: KEY_A + '|gpt-4' });
  });

  await t.test('typing over a pick lets go of its id', () => {
    const app = appAt('api_key');
    app.rlNewRuleView.subjectSuggestions[0].pick();
    setTarget(app, keyId(3));
    assert.equal(app.rlNewRuleTarget(), keyId(3));
  });

  await t.test('a pasted id in another casing resolves to the key', () => {
    const app = appAt('api_key');
    setTarget(app, KEY_A.toLowerCase());
    assert.equal(app.rlNewRuleTarget(), KEY_A);
    assert.equal(app.rlNewRuleKeyError(), '');
  });
});

/**
 * A key rule is looked up by the authenticated key's id and by nothing else, and key ids are issued
 * by the gateway — so text that is not the id of a key can never match a request. Storing it would
 * produce a rule that looks configured and limits nothing. Names are a way to find the id, never a
 * substitute for it.
 */
test('a key target must resolve to a key id', async t => {
  // There is no Next any more: the one gate is Create. `step` keeps these cases reading as they
  // did — 3 is "got through", 2 is "held at the target".
  const next = (app, field, text) => {
    const before = app.rlDraft.rules.length;
    setTarget(app, text);
    app.rlNewRule.model = 'gpt-4';
    app.rlNewRule.rpm = 10;
    app.createRateLimitRule();
    return { step: app.rlDraft.rules.length > before ? 3 : 2, error: app.rlNewRuleView.error };
  };

  await t.test('a unique key name resolves to its id', () => {
    const app = appAt('api_key');
    assert.equal(next(app, 'target', 'checkout service').step, 3);
    assert.equal(app.rlNewRuleTarget(), KEY_A);
  });

  await t.test('an unknown name is refused, at Next and at Create', () => {
    const app = appAt('api_key_model');
    const result = next(app, 'subject', 'no such key');
    assert.equal(result.step, 2);
    assert.match(result.error, /No API key has this name or id/);

    assert.equal(app.rlDraft.rules.length, 0, 'nothing is stored against unresolved text');
  });

  await t.test('a well-formed id that no key has is refused while the list is loaded', () => {
    const app = appAt('api_key');
    assert.match(next(app, 'target', 'bbbb0000-0000-4000-8000-000000000001').error, /No API key has this name or id/);
  });

  await t.test('a name two keys share is refused rather than guessed', () => {
    const app = appAt('api_key');
    app.keys = app.normalizeApiKeyList([...keys(3), { id: keyId(900), label: 'Checkout service', keyPrefix: 'sk-zzzz' }]);
    const result = next(app, 'target', 'Checkout service');
    assert.equal(result.step, 2);
    assert.match(result.error, /2 keys are named/);
    // The pick text carries the prefix, so either namesake can still be chosen from the list.
    setTarget(app, 'Checkout service (sk-zzzz…)');
    assert.equal(app.rlNewRuleTarget(), keyId(900));
  });

  await t.test('a revoked key is not found by name, but its id is accepted and flagged', () => {
    const app = appAt('api_key');
    app.keys = app.normalizeApiKeyList([{ id: keyId(7), label: 'Old importer', keyPrefix: 'sk-old1', revokedAt: '2026-01-01T00:00:00Z' }]);
    assert.match(next(app, 'target', 'Old importer').error, /No API key has this name or id/);
    setTarget(app, keyId(7));
    assert.equal(app.rlNewRuleKeyError(), '');
    assert.match(app.rlNewRuleView.keyNote, /revoked/);
  });

  await t.test('an empty field asks for the key', () => {
    const app = appAt('api_key');
    assert.match(next(app, 'target', '   ').error, /Choose the API key/);
  });

  await t.test('with the key list unavailable only something shaped like an id is accepted', () => {
    const app = appAt('api_key');
    app.keys = [];
    app.rlKeysState = 'failed';
    assert.match(next(app, 'target', 'Checkout service').error, /Paste the key’s id/);
    assert.equal(next(app, 'target', 'BBBB0000-0000-4000-8000-000000000001').step, 3);
  });

  await t.test('tenant targets stay free text', () => {
    const app = appAt('tenant');
    assert.equal(next(app, 'target', 'acme').step, 3);
  });
});

test('text for one kind of target is not carried into another', () => {
  const app = appAt('api_key_model');
  app.rlNewRuleView.subjectSuggestions[0].pick();
  app.rlNewRuleView.modelSuggestions[0].pick();
  app.setRateLimitNewRuleWho('tenant');
  assert.equal(app.rlNewRule.subject, '', 'a key name must not become a tenant');
  assert.deepEqual(Object.keys(app.rlNewRule.picked), ['model']);
  assert.equal(app.rlNewRule.model, 'gpt-4', 'the model means the same thing for a key and a tenant');
});

test('typing reaches keys beyond the rendered 200', () => {
  const app = appAt('api_key', { keyCount: 450 });
  setTarget(app, 'Service 431');
  assert.deepEqual(app.rlNewRuleView.subjectSuggestions.map(s => s.text), ['Service 431']);
});

test('existing key rules are listed and found by key name', () => {
  const rules = [
    { scope: 'api_key_model', target: KEY_A + '|gpt-4', rpm: 10, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] },
    { scope: 'api_key', target: 'GONE-KEY', rpm: 10, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] },
  ];
  const app = appAt('model', { rules });
  const rows = app.rlRuleRows;
  assert.equal(rows[0].target, 'Checkout service · gpt-4');
  assert.equal(rows[0].targetTitle, KEY_A + '|gpt-4');
  assert.equal(rows[1].target, 'GONE-KEY', 'an id the key list does not know is shown as it is');

  app.rlFilterText = 'checkout';
  assert.deepEqual(app.rlRuleRows.map(r => r.target), ['Checkout service · gpt-4']);
  app.rlFilterText = KEY_A.slice(0, 8).toLowerCase();
  assert.equal(app.rlRuleRows.length, 1, 'the id still matches');
});

test('a model alias is saved as the canonical id', async t => {
  await t.test('single model scope', () => {
    const app = appAt('model');
    setTarget(app, 'Flagship');
    assert.equal(app.rlNewRuleTarget(), 'gpt-4');
    assert.match(app.rlNewRuleView.unknownNote, /alias of gpt-4/);
  });

  await t.test('the model half of a pair', () => {
    const app = appAt('tenant_model');
    app.rlNewRule.subject = 'acme';
    app.rlNewRule.model = 'flagship';
    assert.equal(app.rlNewRuleTarget(), 'acme|gpt-4');
  });

  await t.test('an alias of a model that already has a rule is a duplicate', () => {
    const rules = [{ scope: 'model', target: 'gpt-4', rpm: 600, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] }];
    const app = appAt('model', { rules });
    setTarget(app, 'flagship');
    app.rlNewRule.rpm = 10;
    app.createRateLimitRule();
    assert.match(app.rlNewRuleView.error, /already exists/);
    assert.equal(app.rlDraft.rules.length, 1);
  });

  await t.test('an unregistered id is kept as typed and flagged', () => {
    const app = appAt('model');
    setTarget(app, 'next-model');
    assert.equal(app.rlNewRuleTarget(), 'next-model');
    assert.match(app.rlNewRuleView.unknownNote, /Not a registered model/);
  });
});

test('the no-tighter warning has a real baseline', async t => {
  const keyRule = (app, rpm) => {
    setTarget(app, KEY_A);
    app.rlNewRule.rpm = rpm;
    return app.rlNewRuleView.looserWarning;
  };

  await t.test('a key rule at the seeded 600 rpm under a 60 rpm default tier warns', () => {
    assert.match(keyRule(appAt('api_key'), 600), /loosest tenant tier configured \(60 rpm\)/);
  });

  await t.test('and one below it does not', () => {
    assert.equal(keyRule(appAt('api_key'), 30), '');
  });

  await t.test('a looser plan raises the bar, because the key could belong to it', () => {
    const plans = { pro: { rpm: 2000, burst: 0, maxConcurrentStreams: 0 } };
    assert.equal(keyRule(appAt('api_key', { plans }), 600), '');
    assert.match(keyRule(appAt('api_key', { plans }), 2000), /2,000 rpm/);
  });

  await t.test('a plan at rpm 0 is floored to 1 rpm by the gateway, so it raises nothing', () => {
    const plans = { open: { rpm: 0, burst: 0, maxConcurrentStreams: 0 } };
    assert.match(keyRule(appAt('api_key', { plans }), 600), /\(60 rpm\)/);
  });

  await t.test('a key-on-model rule is measured against the model rule it shares', () => {
    const rules = [{ scope: 'model', target: 'gpt-4', rpm: 40, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] }];
    const app = appAt('api_key_model', { rules });
    app.rlNewRule.subject = KEY_A;
    app.rlNewRule.model = 'gpt-4';
    app.rlNewRule.rpm = 50;
    assert.match(app.rlNewRuleView.looserWarning, /limit on gpt-4 for every caller \(40 rpm\)/);
  });

  // ---- counterexamples found in review: rules that really do bind, which a rate-only comparison
  // against an assumed baseline would have called redundant ----

  const rule = (scope, target, rpm, burst, extra = {}) =>
    ({ scope, target, rpm, burst, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });
  const window = (rpm, burst, extra = {}) =>
    ({ name: 'w', kind: 'weekly', days: ['mon'], start: '00:00', end: '06:00', rpm, burst, maxConcurrentStreams: 0, suspend: false, ...extra });
  const propose = (app, fields, rpm, burst) => {
    const { target, ...rest } = fields;
    if (target !== undefined) setTarget(app, target);
    Object.assign(app.rlNewRule, rest, { rpm, burst });
    return app.rlNewRuleView.looserWarning;
  };

  await t.test('the same rate with a smaller burst binds on the burst', () => {
    // Default tier is 60 + 10. A key at 60 + 0 refuses the 61st request of a burst the tenant
    // bucket (capacity 70) would still admit.
    assert.equal(propose(appAt('api_key'), { target: KEY_A }, 60, 0), '');
    assert.match(propose(appAt('api_key'), { target: KEY_A }, 60, 10), /60 rpm/);
    // Rate can buy back the capacity: 70 + 0 holds as many tokens as 60 + 10 and refills faster.
    assert.match(propose(appAt('api_key'), { target: KEY_A }, 70, 0), /60 rpm/);
  });

  await t.test('the largest burst of any tier counts, not only the burst of the loosest rate', () => {
    const plans = { bursty: { rpm: 30, burst: 500, maxConcurrentStreams: 0 } };
    assert.equal(propose(appAt('api_key', { plans }), { target: KEY_A }, 100, 0), '');
    assert.match(propose(appAt('api_key', { plans }), { target: KEY_A }, 100, 430), /60 rpm/);
  });

  await t.test('a scheduled baseline is no baseline: its window may loosen or suspend it', () => {
    const rules = [rule('global', '*', 50, 0, { schedule: [window(5000, 0)] })];
    const app = appAt('model', { rules });
    assert.equal(propose(app, { target: 'gpt-4' }, 100, 0), '');
  });

  await t.test('a tenant rule window that raises the tenant rate raises the bar', () => {
    const rules = [rule('tenant', 'acme', 100, 0, { schedule: [window(5000, 0)] })];
    assert.equal(propose(appAt('api_key', { rules }), { target: KEY_A }, 600, 0), '');
    assert.match(propose(appAt('api_key', { rules }), { target: KEY_A }, 5000, 0), /5,000 rpm/);
  });

  await t.test('a window counts even on a tenant rule whose own rate is 0', () => {
    // rpm 0 keeps the plan rate outside the window; inside it the tenant runs at 5,000.
    const rules = [rule('tenant', 'acme', 0, 0, { maxConcurrentStreams: 4, schedule: [window(5000, 0)] })];
    assert.equal(propose(appAt('api_key', { rules }), { target: KEY_A }, 600, 60), '');
  });

  await t.test('a tenant rule under one spelling is not taken as that tenant’s whole story', () => {
    // 'acme' may also be limited by id in another rule, and the gateway tries the id first.
    const rules = [rule('tenant', 'acme', 50, 0), rule('tenant', '0b1e5c1e-0000-4000-8000-000000000001', 900, 0)];
    const app = appAt('tenant_model', { rules });
    assert.equal(propose(app, { subject: 'acme', model: 'gpt-4' }, 100, 0), '');
  });

  await t.test('with adaptive shedding on, an unscaled limit cannot vouch for a scaled one', () => {
    // Under load a key-on-model rule of 100 runs at, say, 50, while the key's own 100 is untouched.
    const rules = [rule('api_key', KEY_A, 100, 0), rule('global', '*', 100, 0)];
    const on = appAt('api_key_model', { rules, adaptive: true });
    assert.equal(propose(on, { subject: KEY_A, model: 'gpt-4' }, 100, 0), '');
    const off = appAt('api_key_model', { rules });
    assert.match(propose(off, { subject: KEY_A, model: 'gpt-4' }, 100, 0), /100 rpm/);
    // A key rule names no model, is never scaled, and keeps its baselines either way.
    assert.match(propose(appAt('api_key', { adaptive: true }), { target: KEY_A }, 600, 60), /60 rpm/);
  });

  await t.test('with adaptive shedding on, the same model’s rule still vouches, component by component', () => {
    const rules = [rule('model', 'gpt-4', 40, 10)];
    const fields = { subject: KEY_A, model: 'gpt-4' };
    assert.match(propose(appAt('api_key_model', { rules, adaptive: true }), fields, 50, 10), /40 rpm/);
    // 50 + 0 covers 40 + 10 unscaled, but halves to 25 + 0 against 20 + 5 only by luck of rounding.
    assert.equal(propose(appAt('api_key_model', { rules, adaptive: true }), fields, 50, 0), '');
    assert.match(propose(appAt('api_key_model', { rules }), fields, 50, 0), /40 rpm/);
  });

  await t.test('a model rule says nothing about a different model', () => {
    const rules = [rule('model', 'gpt-4', 40, 0)];
    const app = appAt('api_key_model', { rules, plans: { big: { rpm: 9000, burst: 0, maxConcurrentStreams: 0 } } });
    assert.equal(propose(app, { subject: KEY_A, model: 'gpt-4-mini' }, 50, 0), '');
  });

  await t.test('a switched-off rule is not a baseline', () => {
    const rules = [{ scope: 'global', target: '*', rpm: 10, burst: 0, maxConcurrentStreams: 0, enabled: false, schedule: [] }];
    assert.equal(keyRule(appAt('api_key', { rules }), 30), '');
  });

  await t.test('a tenant rule replaces the plan rate, so looser is never a warning', () => {
    const app = appAt('tenant');
    setTarget(app, 'acme');
    app.rlNewRule.rpm = 5000;
    assert.equal(app.rlNewRuleView.looserWarning, '');
  });
});
