/**
 * Regression tests for the forms refusing what the server would refuse.
 *
 * The rule drawer and the new-rule form mirrored RateLimitConfigValidation only in part, so a rule
 * the server rejects — rpm 0 with a burst on anything but a tenant, a failed-sign-ins rule with a
 * stream cap or no rate, a "|" in a model id, an over-long target or plan slug — was accepted into
 * the draft and came back as a refused Save of the whole configuration, worded in the API's terms,
 * after the form that caused it had closed. And every numeric field was read with `Number(x) || 0`,
 * so emptying the RPM box of a rule that also caps streams applied "this rule does not limit the
 * rate" without a word.
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
const rule = (scope, target, rpm, burst = 0, streams = 0) =>
  ({ scope, target, rpm, burst, maxConcurrentStreams: streams, enabled: true, schedule: [] });

function appWith(rules = [], plans = {}) {
  const app = createApp();
  const config = () => JSON.parse(JSON.stringify({
    enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 10, maxConcurrentStreams: 5 }, plans, rules,
  }));
  app.rateLimits = config();
  app.rlDraft = config();
  app.keys = app.normalizeApiKeyList([{ id: KEY_A, label: 'Checkout service', keyPrefix: 'sk-a1b2' }]);
  app.models = [{ id: 'gpt-4', aliases: [] }];
  app.rlKeysState = 'ready';
  app.fetchKeys = async () => {};
  app.rateLimitUsage = {};
  app.queueRateLimitScheduleRefresh = () => {};
  return app;
}

/** The new-rule form, answered. */
function newRule(app, { who, where, subject, model, protective, ...tier }) {
  if (protective) app.startRateLimitNewRule({ who: protective }); else app.openRateLimitNewRule();
  if (who) app.setRateLimitNewRuleWho(who);
  if (where) app.setRateLimitNewRuleWhere(where);
  if (subject !== undefined) app.rlNewRule.subject = subject;
  if (model !== undefined) app.rlNewRule.model = model;
  Object.assign(app.rlNewRule, tier);
  return app.rlNewRuleBuild();
}

/** The rule drawer on an existing rule, edited and submitted. Returns the error Done produced. */
function editRule(app, identity, tier) {
  app.openRateLimitRule(identity);
  Object.assign(app.rlRule, tier);
  app.applyRateLimitRule();
  return app.rlRuleError;
}

test('rpm 0 with a burst is refused for every scope, not only for tenants', async t => {
  const everyScope = [
    ['api_key', { who: 'key', where: 'all', subject: 'Checkout service' }],
    ['api_key_model', { who: 'key', where: 'one', subject: 'Checkout service', model: 'gpt-4' }],
    ['tenant', { who: 'tenant', where: 'all', subject: 'acme' }],
    ['tenant_model', { who: 'tenant', where: 'one', subject: 'acme', model: 'gpt-4' }],
    ['model', { who: 'everyone', where: 'one', model: 'gpt-4' }],
    ['global', { who: 'everyone', where: 'all' }],
  ];

  for (const [scope, answers] of everyScope) {
    await t.test('new ' + scope + ' rule', () => {
      // The seeded burst of 60 left in place, which is how an operator arrives here.
      const built = newRule(appWith(), { ...answers, rpm: 0, burst: 60, maxConcurrentStreams: 5 });

      assert.equal(built.rule.scope, scope);
      assert.match(built.error, /set burst to 0 as well/);
      assert.equal(built.errorField, 'tier');
    });
  }

  await t.test('the form does not create it', () => {
    const app = appWith();
    newRule(app, { who: 'key', where: 'all', subject: 'Checkout service', rpm: 0, burst: 60, maxConcurrentStreams: 5 });
    app.createRateLimitRule();
    assert.equal(app.rlDraft.rules.length, 0);
  });

  await t.test('rpm 0 with burst 0 and a stream cap is still a legitimate rule', () => {
    const built = newRule(appWith(), { who: 'key', where: 'all', subject: 'Checkout service', rpm: 0, burst: 0, maxConcurrentStreams: 5 });
    assert.equal(built.error, '');
  });

  await t.test('the rule drawer refuses it on a model rule too, and leaves the rule as it was', () => {
    const app = appWith([rule('model', 'gpt-4', 600, 60)]);

    const error = editRule(app, 'model:gpt-4', { rpm: 0, burst: 50, maxConcurrentStreams: 4 });

    assert.match(error, /set burst to 0 as well/);
    assert.equal(app.rlDraft.rules[0].rpm, 600);
    assert.equal(app.rlRuleDrawerOpen, true);
  });
});

test('failed sign-ins is a rate-only limit', async t => {
  await t.test('rpm 0 is refused even though nothing else is wrong with it', () => {
    const built = newRule(appWith(), { protective: 'auth_failure', rpm: 0, burst: 0 });
    assert.match(built.error, /set rpm or streams above zero|set rpm above zero/);
  });

  await t.test('the shared check names both rate-only refusals', () => {
    const app = appWith();
    assert.match(app.rlRuleTierError('auth_failure', { rpm: 0, burst: 0, maxConcurrentStreams: 3 }), /limits the request rate only; set rpm above zero/);
    assert.match(app.rlRuleTierError('auth_failure', { rpm: 20, burst: 10, maxConcurrentStreams: 3 }), /streams has no effect there and must be 0/);
    assert.equal(app.rlRuleTierError('auth_failure', { rpm: 20, burst: 10, maxConcurrentStreams: 0 }), '');
    assert.equal(app.rlRuleTierError('anonymous', { rpm: 30, burst: 10, maxConcurrentStreams: 2 }), '', 'anonymous does cap streams');
  });

  await t.test('there is no Streams input for it, and a number left behind by another choice is not stored', () => {
    const app = appWith();
    app.startRateLimitNewRule({ who: 'anonymous' });
    app.setRateLimitNewRuleTier('maxConcurrentStreams', 3);
    assert.equal(app.rlNewRuleView.showStreams, true);

    app.setRateLimitNewRuleWho('auth_failure');

    assert.equal(app.rlNewRule.maxConcurrentStreams, 3, 'touched numbers are kept across a change of scope');
    assert.equal(app.rlNewRuleView.showStreams, false);
    assert.equal(app.rlNewRuleBuild().error, '');
    app.createRateLimitRule();
    assert.deepEqual(
      JSON.parse(JSON.stringify(app.rlDraft.rules)),
      [{ scope: 'auth_failure', target: '*', rpm: 30, burst: 10, maxConcurrentStreams: 0, enabled: true, schedule: [] }]);
  });

  await t.test('the rule drawer hides Streams for it and shows it for everything else', () => {
    const app = appWith([rule('auth_failure', '*', 20, 10), rule('model', 'gpt-4', 600, 60)]);
    app.openRateLimitRule('auth_failure:*');
    assert.equal(app.rlRuleDrawerView.showStreams, false);
    assert.equal(editRule(app, 'auth_failure:*', { rpm: 0 }), 'A rule must limit something: set rpm or streams above zero.');
    app.closeRateLimitRule();
    app.openRateLimitRule('model:gpt-4');
    assert.equal(app.rlRuleDrawerView.showStreams, true);
  });
});

test('targets the server would refuse are refused under the field that holds them', async t => {
  await t.test('a "|" in the model of a rule for everyone', () => {
    const built = newRule(appWith(), { who: 'everyone', where: 'one', model: 'a|b' });
    assert.match(built.error, /model id cannot contain/);
    assert.equal(built.errorField, 'model');
  });

  await t.test('a "|" in the model of a pair rule, which would make three halves', () => {
    const built = newRule(appWith(), { who: 'tenant', where: 'one', subject: 'acme', model: 'a|b' });
    assert.equal(built.rule.target, 'acme|a|b');
    assert.equal(built.errorField, 'model');
  });

  await t.test('a target past the server\'s 256 characters', () => {
    const app = appWith();
    assert.equal(app.rlLimits().maxTargetLength, 256);

    const long = newRule(app, { who: 'everyone', where: 'one', model: 'm'.repeat(257) });
    assert.match(long.error, /at most 256/);
    assert.equal(long.errorField, 'model');

    app.closeRateLimitNewRule();
    assert.equal(newRule(app, { who: 'everyone', where: 'one', model: 'm'.repeat(256) }).error, '');
  });

  await t.test('a pair whose halves fit but whose whole does not', () => {
    const built = newRule(appWith(), { who: 'tenant', where: 'one', subject: 't'.repeat(200), model: 'm'.repeat(100) });
    assert.match(built.error, /301 characters/);
  });
});

test('a plan slug past the server\'s 64 characters is refused in the tier drawer', () => {
  const app = appWith();
  const name = slug => {
    app.openRateLimitNewPlan();
    app.rlTier.slug = slug;
    app.applyRateLimitTier();
    return app.rlTierError;
  };

  assert.match(name('p'.repeat(65)), /at most 64 characters/);
  assert.equal(Object.keys(app.rlDraft.plans).length, 0);

  assert.equal(name('p'.repeat(64)), '');
  assert.deepEqual(Object.keys(app.rlDraft.plans), ['p'.repeat(64)]);
});

test('an empty numeric field is empty, not zero', async t => {
  await t.test('clearing RPM on a rule that also caps streams does not become "no rate limit"', () => {
    const app = appWith([rule('model', 'gpt-4', 600, 0, 4)]);

    // What x-model.number leaves behind when the box is emptied.
    const error = editRule(app, 'model:gpt-4', { rpm: '' });

    assert.match(error, /^RPM is empty/);
    assert.equal(app.rlDraft.rules[0].rpm, 600, 'the limit is still there');
    assert.equal(app.rateLimitsDirty, false);
  });

  for (const [field, label] of [['burst', 'Burst'], ['maxConcurrentStreams', 'Streams']]) {
    await t.test('an empty ' + label + ' is asked for rather than assumed', () => {
      const app = appWith([rule('model', 'gpt-4', 600, 60, 4)]);
      assert.match(editRule(app, 'model:gpt-4', { [field]: null }), new RegExp('^' + label + ' is empty'));
    });
  }

  await t.test('a typed zero is still a zero', () => {
    const app = appWith([rule('model', 'gpt-4', 600, 0, 4)]);
    assert.equal(editRule(app, 'model:gpt-4', { rpm: 0 }), '');
    assert.equal(app.rlDraft.rules[0].rpm, 0);
  });

  await t.test('the new-rule form', () => {
    const built = newRule(appWith(), { who: 'key', where: 'all', subject: 'Checkout service', rpm: '', burst: 0, maxConcurrentStreams: 5 });
    assert.match(built.error, /^RPM is empty/);
    assert.equal(built.errorField, 'tier');
  });

  await t.test('the whole-gateway form still opens on its own question', () => {
    const built = newRule(appWith(), { who: 'everyone', where: 'all' });
    assert.match(built.error, /^Name the ceiling/);
  });

  await t.test('the tier drawer', () => {
    const app = appWith();
    app.openRateLimitTier('default', '');
    app.rlTier.burst = '';
    app.applyRateLimitTier();
    assert.match(app.rlTierError, /^Burst is empty/);
    assert.equal(app.rlDraft.default.burst, 10);
  });

  await t.test('the window form', () => {
    const app = appWith([rule('model', 'gpt-4', 600, 60)]);
    app.openRateLimitRule('model:gpt-4');
    app.openRateLimitNewWindow();
    Object.assign(app.rlWindow, { name: 'night', rpm: '', burst: 0, maxConcurrentStreams: 5 });

    assert.match(app.rlWindowLocalCheck(app.rlWindowFromForm()), /^RPM is empty/);

    app.rlWindow.suspend = true;
    assert.equal(app.rlWindowLocalCheck(app.rlWindowFromForm()), '', 'a pausing window has no numbers to ask for');
  });
});
