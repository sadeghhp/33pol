/**
 * Regression tests for the warning on a new protective limit (failed sign-ins, anonymous callers).
 *
 * The form said of a protective rule at or above the fallback's rpm that it "is no tighter than the
 * N rpm already in force, so it would not change what this scope allows". That is how ordinary
 * rules work — every one of them must admit a request, so a looser one never binds — and it is the
 * opposite of how these two work: a configured protective tier *replaces* the default-tier fallback
 * (RateLimitPolicyResolver.ResolveAuthFailureTier, and Compose for the anonymous tier). Entering
 * 600 over a 60 rpm fallback raised the credential-guessing budget tenfold while the form said
 * nothing would change.
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

/** The protective form on `scope`, over a default tier of 60 rpm + 10 burst, 5 streams. */
function warningFor(scope, tier) {
  const app = createApp();
  const config = () => ({
    enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 10, maxConcurrentStreams: 5 }, plans: {}, rules: [],
  });
  app.rateLimits = config();
  app.rlDraft = config();
  app.keys = [];
  app.models = [];
  app.rlKeysState = 'ready';
  app.fetchKeys = async () => {};
  app.startRateLimitNewRule({ who: scope });
  Object.assign(app.rlNewRule, tier);
  assert.equal(app.rlNewRuleBuild().error, '', 'the rule itself is valid');
  return app.rlNewRuleView.looserWarning;
}

const NEVER = /would not change|no tighter than/;

test('a higher rpm is named as a replacement that loosens the budget', () => {
  const warning = warningFor('auth_failure', { rpm: 600, burst: 10, maxConcurrentStreams: 0 });

  assert.match(warning, /replaces the default-tier fallback/);
  assert.match(warning, /would loosen what failed sign-ins are held to/);
  assert.match(warning, /the rate from 60 to 600 rpm/);
  assert.doesNotMatch(warning, NEVER);
  assert.doesNotMatch(warning, /burst/, 'a dimension that did not move is not blamed');
});

test('the same rpm with a different burst is a change, and is never called none', async t => {
  await t.test('a larger burst loosens', () => {
    const warning = warningFor('auth_failure', { rpm: 60, burst: 50, maxConcurrentStreams: 0 });
    assert.match(warning, /the burst from 10 to 50/);
    assert.doesNotMatch(warning, /the rate from/);
    assert.doesNotMatch(warning, NEVER);
  });

  await t.test('a smaller burst tightens, which is what the form is for: nothing is said', () => {
    assert.equal(warningFor('auth_failure', { rpm: 60, burst: 0, maxConcurrentStreams: 0 }), '');
  });
});

test('the same rpm with a different stream cap, on the scope that has one', async t => {
  await t.test('more streams loosens', () => {
    const warning = warningFor('anonymous', { rpm: 60, burst: 10, maxConcurrentStreams: 8 });
    assert.match(warning, /concurrent streams from 5 to 8/);
    assert.doesNotMatch(warning, NEVER);
  });

  await t.test('zero is unlimited, the loosest value there is', () => {
    assert.match(warningFor('anonymous', { rpm: 60, burst: 10, maxConcurrentStreams: 0 }), /concurrent streams from 5 to unlimited/);
  });

  await t.test('fewer streams tightens: nothing is said', () => {
    assert.equal(warningFor('anonymous', { rpm: 60, burst: 0, maxConcurrentStreams: 1 }), '');
  });

  await t.test('rpm 0 keeps the default rate and replaces only the stream cap', () => {
    assert.equal(warningFor('anonymous', { rpm: 0, burst: 0, maxConcurrentStreams: 2 }), '');
    assert.match(warningFor('anonymous', { rpm: 0, burst: 0, maxConcurrentStreams: 9 }), /^(?!.*the rate from).*concurrent streams from 5 to 9/);
  });

  await t.test('failed sign-ins open no streams, so the default tier\'s cap is not compared', () => {
    assert.equal(warningFor('auth_failure', { rpm: 60, burst: 10, maxConcurrentStreams: 0 }), '');
  });
});

test('a tier identical to the fallback changes nothing, and nothing is claimed about it', () => {
  assert.equal(warningFor('anonymous', { rpm: 60, burst: 10, maxConcurrentStreams: 5 }), '');
  assert.equal(warningFor('auth_failure', { rpm: 60, burst: 10, maxConcurrentStreams: 0 }), '');
});

test('several loosened dimensions are all named', () => {
  const warning = warningFor('anonymous', { rpm: 600, burst: 60, maxConcurrentStreams: 0 });
  assert.match(warning, /the rate from 60 to 600 rpm, and the burst from 10 to 60, and concurrent streams from 5 to unlimited/);
});
