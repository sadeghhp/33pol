/**
 * Behavioural tests for the rate-limit drawers after the redesign: the two pickers as comboboxes,
 * field-level validation, read-only inspection, the delete confirmation's wording, and the window
 * sub-panel's Back / Close split.
 *
 * None of this may change what a rule is stored as — `rlNewRuleBuild()` still decides that, and the
 * intent and picker suites pin it. These tests pin the layer on top:
 *
 *   - ArrowDown / ArrowUp / Home / End move an active option without moving focus, Enter picks it,
 *     and Escape closes the list WITHOUT reaching the drawer's own Escape handling;
 *   - a validation message is attached to the control it is about, and only rule-wide problems
 *     use the shared alert;
 *   - with read-only access rules and tiers open for inspection and cannot be applied;
 *   - a confirmation names a key by its name, never by a bare id.
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

const KEY_A = '6f1c0a52-0000-4000-8000-00000000000a';
const KEYS = [
  { id: KEY_A, label: 'Checkout service', assignee: 'Payments', keyPrefix: 'sk-a1b2' },
  { id: 'aaaa0000-0000-4000-8000-000000000002', label: 'Batch importer', keyPrefix: 'sk-0002' },
  { id: 'aaaa0000-0000-4000-8000-000000000003', label: 'Chat frontend', keyPrefix: 'sk-0003' },
];
const MODELS = [{ id: 'gpt-4', aliases: ['flagship'] }, { id: 'gpt-4-mini', aliases: [] }];
const rule = (scope, target, rpm, extra = {}) => ({ scope, target, rpm, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [], ...extra });

function app({ rules = [], open = true } = {}) {
  const a = createApp();
  const config = () => JSON.parse(JSON.stringify({
    enabled: true, adaptiveEnabled: false, default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 }, plans: { pro: { rpm: 600, burst: 60, maxConcurrentStreams: 0 } }, rules,
  }));
  a.rateLimits = config();
  a.rlDraft = config();
  a.keys = a.normalizeApiKeyList(KEYS);
  a.models = MODELS;
  a.rlKeysState = 'ready';
  a.fetchKeys = async () => {};
  a.rateLimitUsage = {};
  if (open) a.openRateLimitNewRule();
  return a;
}

/** A keydown as the handler sees it, recording what it asked the browser to do. */
function key(name) {
  return { key: name, prevented: false, stopped: false, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } };
}

test('the key picker is a combobox', async t => {
  await t.test('it announces an expanded list with no active option to begin with', () => {
    const a = app();
    assert.equal(a.rlNewRuleView.subjectCombo.expanded, 'true');
    assert.equal(a.rlNewRuleView.subjectCombo.active, '');
    assert.deepEqual([...new Set(a.rlNewRuleView.subjectSuggestions.map(s => s.selected))], ['false']);
  });

  await t.test('ArrowDown / ArrowUp / Home / End move the active option and wrap', () => {
    const a = app();
    const press = name => { const e = key(name); a.onRateLimitSubjectKeydown(e); return e; };
    assert.equal(press('ArrowDown').prevented, true);
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-0');
    assert.equal(a.rlNewRuleView.subjectSuggestions[0].selected, 'true');
    press('ArrowDown');
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-1');
    press('End');
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-2');
    press('ArrowDown');
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-0', 'wraps to the top');
    press('ArrowUp');
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-2', 'and back to the bottom');
    press('Home');
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-0');
  });

  await t.test('Home and End stay with the caret until an option is active', () => {
    const a = app();
    const e = key('Home');
    a.onRateLimitSubjectKeydown(e);
    assert.equal(e.prevented, false);
    assert.equal(a.rlNewRuleView.subjectCombo.active, '');
  });

  await t.test('Enter picks the active option exactly as a click would', () => {
    const a = app();
    a.onRateLimitSubjectKeydown(key('ArrowDown'));
    const enter = key('Enter');
    a.onRateLimitSubjectKeydown(enter);
    assert.equal(enter.prevented, true);
    assert.equal(a.rlNewRule.picked.subject.value, KEY_A);
    assert.equal(a.rlNewRule.subject, 'Checkout service (sk-a1b2…)');
    assert.equal(a.rlNewRuleView.hasSubjectSuggestions, false, 'a picked field closes its list');
    assert.equal(a.rlNewRuleBuild().rule.target.startsWith(KEY_A), true);
  });

  await t.test('Enter with nothing active is left alone', () => {
    const a = app();
    const enter = key('Enter');
    a.onRateLimitSubjectKeydown(enter);
    assert.equal(enter.prevented, false);
    assert.equal(a.rlNewRule.picked.subject, undefined);
  });

  await t.test('Escape closes the list and does not reach the drawer', () => {
    const a = app();
    const esc = key('Escape');
    a.onRateLimitSubjectKeydown(esc);
    assert.deepEqual([esc.prevented, esc.stopped], [true, true]);
    assert.equal(a.rlNewRuleView.hasSubjectSuggestions, false);
    assert.equal(a.rlNewRuleView.subjectCombo.expanded, 'false');
    assert.equal(a.rlNewRuleOpen, true, 'the drawer is still open');
  });

  await t.test('a second Escape, with no list left, is the drawer\'s', () => {
    const a = app();
    a.onRateLimitSubjectKeydown(key('Escape'));
    const esc = key('Escape');
    a.onRateLimitSubjectKeydown(esc);
    assert.equal(esc.stopped, false);
  });

  await t.test('typing, or an arrow, reopens a closed list', () => {
    const a = app();
    a.onRateLimitSubjectKeydown(key('Escape'));
    a.onRateLimitSubjectInput();
    assert.equal(a.rlNewRuleView.hasSubjectSuggestions, true);
    a.onRateLimitSubjectKeydown(key('Escape'));
    a.onRateLimitSubjectKeydown(key('ArrowDown'));
    assert.equal(a.rlNewRuleView.hasSubjectSuggestions, true);
    assert.equal(a.rlNewRuleView.subjectCombo.active, 'rl-opt-subject-0', 'an arrow reopens onto the first option');
  });

  await t.test('typing forgets the active option, which no longer names the same row', () => {
    const a = app();
    a.onRateLimitSubjectKeydown(key('ArrowDown'));
    a.rlNewRule.subject = 'chat';
    a.onRateLimitSubjectInput();
    assert.equal(a.rlNewRuleView.subjectCombo.active, '');
    assert.deepEqual(a.rlNewRuleView.subjectSuggestions.map(s => s.text), ['Chat frontend']);
  });

  await t.test('the model picker has its own list and its own active option', () => {
    const a = app();
    a.onRateLimitModelKeydown(key('ArrowDown'));
    assert.equal(a.rlNewRuleView.modelCombo.active, 'rl-opt-model-0');
    assert.equal(a.rlNewRuleView.subjectCombo.active, '');
    a.onRateLimitModelKeydown(key('Enter'));
    assert.equal(a.rlNewRule.model, 'gpt-4');
  });

  await t.test('the markup carries the combobox contract', () => {
    for (const field of ['subject', 'model']) {
      assert.match(HTML, new RegExp('id="rl-new-' + field + '"[^>]*role="combobox" aria-autocomplete="list" aria-controls="rl-new-' + field + '-list"'));
      assert.match(HTML, new RegExp('id="rl-new-' + field + '-list" role="listbox"'));
    }
    assert.match(HTML, /role="option" tabindex="-1" :id="s\.id" :class="s\.cls" :aria-selected="s\.selected"/);
    assert.match(HTML, /:aria-activedescendant="rlNewRuleView\.subjectCombo\.active"/);
  });
});

test('validation lands on the control it is about', async t => {
  await t.test('nothing is shown before Create is pressed', () => {
    const v = app().rlNewRuleView;
    assert.deepEqual([v.subjectError, v.modelError, v.tierError, v.generalError], ['', '', '', '']);
    assert.equal(v.subjectInvalid, 'false');
  });

  await t.test('a missing key is the subject field\'s error', () => {
    const a = app();
    a.createRateLimitRule();
    const v = a.rlNewRuleView;
    assert.match(v.subjectError, /Choose the API key/);
    assert.equal(v.subjectInvalid, 'true');
    assert.deepEqual([v.modelError, v.tierError, v.generalError], ['', '', '']);
    assert.equal(v.error, v.subjectError, 'the combined error other code reads is unchanged');
  });

  await t.test('a missing model is the model field\'s', () => {
    const a = app();
    a.pickRateLimitSuggestion('subject', KEY_A, 'Checkout service (sk-a1b2…)');
    a.createRateLimitRule();
    assert.match(a.rlNewRuleView.modelError, /Choose a model/);
    assert.equal(a.rlNewRuleView.modelInvalid, 'true');
    assert.equal(a.rlNewRuleView.subjectError, '');
  });

  await t.test('a rule that limits nothing is the numbers\'', () => {
    const a = app();
    a.pickRateLimitSuggestion('subject', KEY_A, 'Checkout service (sk-a1b2…)');
    a.rlNewRule.model = 'gpt-4';
    a.rlNewRule.rpm = 0; a.rlNewRule.burst = 0; a.rlNewRule.maxConcurrentStreams = 0;
    a.createRateLimitRule();
    assert.match(a.rlNewRuleView.tierError, /must limit something/);
    assert.equal(a.rlNewRuleView.tierInvalid, 'true');
  });

  await t.test('a duplicate is about the rule as a whole, so it uses the shared alert', () => {
    const a = app({ rules: [rule('api_key_model', KEY_A + '|gpt-4', 10)] });
    a.pickRateLimitSuggestion('subject', KEY_A, 'Checkout service (sk-a1b2…)');
    a.rlNewRule.model = 'gpt-4';
    a.rlNewRule.rpm = 5;
    a.createRateLimitRule();
    const v = a.rlNewRuleView;
    assert.match(v.generalError, /already exists/);
    assert.deepEqual([v.subjectError, v.modelError, v.tierError], ['', '', '']);
  });

  await t.test('each message is wired to its control', () => {
    assert.match(HTML, /:aria-invalid="rlNewRuleView\.subjectInvalid" aria-describedby="rl-new-subject-error rl-new-subject-more"/);
    assert.match(HTML, /<p class="field-error" id="rl-new-model-error"[^>]*role="alert">/);
    assert.match(HTML, /aria-describedby="rl-new-tier-error"/);
    assert.match(HTML, /x-show="rlNewRuleView\.generalError"[^>]*role="alert"/);
  });
});

test('the form reads top to bottom: who, which, where, which, how much, this rule, other limits', () => {
  const a = HTML.indexOf('aria-labelledby="rl-new-title"');
  const order = ['Who is limited?', 'id="rl-new-subject"', 'On which model?', 'id="rl-new-model"', 'How much?', 'This rule', 'Limits on the same requests', 'rlNewRuleView.generalError', 'Create rule']
    .map(s => HTML.indexOf(s, a));
  assert.ok(order.every(i => i > 0), 'every part is present');
  assert.deepEqual([...order].sort((x, y) => x - y), order);
  const drawer = HTML.slice(a, HTML.indexOf('<!-- Confirm dialog -->'));
  assert.equal((drawer.match(/<details class="rl-help"/g) || []).length, 2, 'at most one inline explainer per part');
});

test('read-only access inspects, and cannot apply', async t => {
  const locked = () => {
    const a = app({ rules: [rule('model', 'gpt-4', 600)], open: false });
    a.rlReadOnlyReason = 'This key may view rate limits but not change them.';
    return a;
  };

  await t.test('a rule opens, says it is read-only, and Done does nothing', () => {
    const a = locked();
    a.openRateLimitRule('model:gpt-4');
    assert.equal(a.rlRuleDrawerOpen, true);
    assert.match(a.rlRuleDrawerView.eyebrow, /read-only$/);
    assert.deepEqual([a.rlRuleDrawerView.readOnly, a.rlRuleDrawerView.editable], [true, false]);
    a.rlRule.rpm = 1;
    a.applyRateLimitRule();
    assert.equal(a.rlDraft.rules[0].rpm, 600);
  });

  await t.test('a tier opens for inspection; a new plan does not', () => {
    const a = locked();
    a.openRateLimitTier('plan', 'pro');
    assert.equal(a.rlTierDrawerOpen, true);
    assert.equal(a.rlTierView.eyebrow, 'Tier · read-only');
    assert.equal(a.rlTierView.canRemove, false);
    a.rlTier.rpm = 1;
    a.applyRateLimitTier();
    assert.equal(a.rlDraft.plans.pro.rpm, 600);

    a.rlTierDrawerOpen = false;
    a.openRateLimitNewPlan();
    assert.equal(a.rlTierDrawerOpen, false);
  });

  await t.test('the footer is Close-only, and the new-rule form stays shut', () => {
    assert.match(HTML, /@click="closeRateLimitRule" x-show="rlRuleDrawerView\.readOnly">Close</);
    assert.match(HTML, /@click="closeRateLimitTier" x-show="rateLimitsLocked">Close</);
    const a = locked();
    a.openRateLimitNewRule();
    assert.equal(a.rlNewRuleOpen, false);
  });

  await t.test('with edit rights nothing changes: tiers still open and apply', () => {
    const a = app({ open: false });
    a.openRateLimitTier('plan', 'pro');
    assert.equal(a.rlTierView.eyebrow, 'Tier');
    a.rlTier.rpm = 700;
    a.applyRateLimitTier();
    assert.equal(a.rlDraft.plans.pro.rpm, 700);
  });
});

test('the delete confirmation names a key, not its id', () => {
  const a = app({ rules: [rule('api_key_model', KEY_A + '|gpt-4', 10), rule('global', '*', 5000)], open: false });
  let asked = null;
  a.openConfirm = options => { asked = options; };

  a.openRateLimitRule('api_key_model:' + KEY_A + '|gpt-4');
  a.confirmDeleteRateLimitRule();
  assert.match(asked.message, /“Checkout service · gpt-4”/);
  assert.doesNotMatch(asked.message, new RegExp(KEY_A));
  assert.match(asked.message, /switch it off instead\./);

  a.openRateLimitRule('global:*');
  a.confirmDeleteRateLimitRule();
  assert.match(asked.message, /^Whole gateway stops being limited/);
});

test('the window panel: Back returns to the rule, Close leaves it, and both protect edits', async t => {
  const opened = () => {
    const a = app({ rules: [rule('model', 'gpt-4', 600)], open: false });
    a.queueRateLimitWindowPreview = () => {};
    a.openRateLimitRule('model:gpt-4');
    a.openRateLimitWindow(-1);
    return a;
  };

  await t.test('markup: a labelled Back beside the title, a Close that names what it closes', () => {
    assert.match(HTML, /class="rl-link rl-back" @click="dismissRateLimitWindow">.*Back to the rule<\/button>/);
    assert.match(HTML, /@click="dismissRateLimitRuleFromWindow" aria-label="Close the rule"/);
    assert.match(HTML, /<h3 id="rl-window-title" tabindex="-1"/);
  });

  await t.test('an untouched window: Back keeps the rule open, Close closes it', () => {
    const a = opened();
    a.dismissRateLimitWindow();
    assert.deepEqual([a.rlWindowOpen, a.rlRuleDrawerOpen], [false, true]);

    const b = opened();
    b.dismissRateLimitRuleFromWindow();
    assert.deepEqual([b.rlWindowOpen, b.rlRuleDrawerOpen], [false, false]);
  });

  await t.test('an edited window asks before either exit', () => {
    for (const exit of ['dismissRateLimitWindow', 'dismissRateLimitRuleFromWindow']) {
      const a = opened();
      let asked = null;
      a.openConfirm = options => { asked = options; };
      a.rlWindow.name = 'changed';
      a[exit]();
      assert.equal(asked?.title, 'Discard these changes?', exit);
      assert.equal(a.rlWindowOpen, true, exit + ' leaves the panel until confirmed');
    }
  });

  await t.test('entering remembers where to return focus', () => {
    const a = opened();
    assert.equal(a._rlWindowReturn, 'rl-add-window');
  });
});

/**
 * A confirmation raised from inside a drawer (delete this rule, discard these edits, remove this
 * plan) sits later in the document than the drawer. Choosing "the first visible surface" picked the
 * drawer underneath, so the confirmation was left inert: on screen, but dead to the pointer and to
 * Tab, with Escape — which cancels — the only key that still worked.
 */
test('a confirmation over a drawer is the active surface', async t => {
  const surface = (role, extra = {}) => ({
    offsetParent: {}, getAttribute: name => (name === 'role' ? role : null),
    classList: { contains: cls => (extra.classes || []).includes(cls) },
  });
  function appWithDom(list) {
    const context = {
      document: { addEventListener() {}, hidden: false, getElementById: () => null, querySelectorAll: () => list, activeElement: null, body: { children: [] } },
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

  const drawer = surface('dialog');
  const confirm = surface('alertdialog');

  await t.test('while the confirm is open it wins, wherever it sits in the document', () => {
    const a = appWithDom([drawer, confirm]);
    a.rlRuleDrawerOpen = true;
    a.confirmDialog = { title: 'Delete this rule permanently?' };
    assert.equal(a._visibleModalSurface(), confirm);
  });

  await t.test('once dismissed — even while it is still fading out — the drawer is active again', () => {
    const a = appWithDom([drawer, confirm]);
    a.rlRuleDrawerOpen = true;
    a.confirmDialog = null;
    assert.equal(a._visibleModalSurface(), drawer);
  });

  await t.test('the help guide still outranks the drawer it was opened from, and yields when closed', () => {
    const help = surface('dialog', { classes: ['rl-help-drawer'] });
    const a = appWithDom([help, drawer]);
    a.rlHelpOpen = true;
    assert.equal(a._visibleModalSurface(), help);
    a.rlHelpOpen = false;
    assert.equal(a._visibleModalSurface(), drawer);
  });

  await t.test('a confirm on its own is still found', () => {
    const a = appWithDom([confirm]);
    a.confirmDialog = { title: 'x' };
    assert.equal(a._visibleModalSurface(), confirm);
  });
});
