/**
 * Regression tests for the rate-limit unsaved-work guard (W8).
 *
 * Staged rate-limit edits are the one thing on the console that lives only in memory: the draft is
 * held in component state and nothing persists it until Save. Two ways to lose it silently existed —
 * a reload or a closed tab discarded it with no prompt, and the sticky save bar that reports it sits
 * inside the Rate limits sub-tab, so stepping over to CORS hid every trace that a draft was still
 * there. This pins both guards, and pins that neither fires when there is nothing to lose.
 *
 * `admin-app.js` is a browser script that ends by registering with Alpine, so it is evaluated in a
 * vm context with the handful of globals it touches stubbed; `adminApp` is then a plain factory.
 *
 *     node --test tests/admin-console/
 */
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const SOURCE = path.join(__dirname, '../../src/33pol.App/wwwroot/admin/admin-app.js');

/** A fresh component, with only the globals admin-app.js touches while being defined. */
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

const SAVED = {
  enabled: true,
  adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
  plans: {},
  rules: [
    { scope: 'model', target: 'gpt-4', rpm: 600, burst: 60, maxConcurrentStreams: 0, enabled: true, schedule: [] },
  ],
};

const clone = value => JSON.parse(JSON.stringify(value));

/** A component holding a saved configuration and an identical, untouched draft. */
function pristineApp() {
  const app = createApp();
  app.rateLimits = clone(SAVED);
  app.rlDraft = clone(SAVED);
  return app;
}

/** The event object the browser hands a beforeunload listener. */
function unloadEvent() {
  return { defaultPrevented: false, returnValue: undefined, preventDefault() { this.defaultPrevented = true; } };
}

test('the unload guard fires only while edits are staged', async t => {
  await t.test('a pristine page leaves without a prompt', () => {
    const app = pristineApp();
    const event = unloadEvent();

    assert.equal(app.onBeforeUnload(event), undefined);
    assert.equal(event.defaultPrevented, false);
    assert.equal(event.returnValue, undefined);
  });

  await t.test('a staged edit prompts, in the shape the event contract requires', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;
    const event = unloadEvent();

    const returned = app.onBeforeUnload(event);
    assert.equal(event.defaultPrevented, true, 'preventDefault is what current engines honour');
    // The legacy paths arm the dialog only on a non-empty string, so an empty one is no fallback
    // at all — which is what this asserted before, and it asserted nothing.
    assert.ok(returned, 'the returned string must be non-empty to mean anything');
    assert.equal(event.returnValue, returned);
  });

  /**
   * The prompt makes "Stay on page" reachable, and the page has to be intact afterwards. Tearing
   * the push stream down here would leave the Overview on its 2s poll with nothing to restart it:
   * syncLive only runs on a tab change, a visibility change or the stream's own lifecycle, never on
   * a timer. Teardown belongs on pagehide, which fires only when the page really goes.
   */
  await t.test('the guard does not tear down the live stream', () => {
    for (const dirty of [false, true]) {
      const app = pristineApp();
      let stopped = 0;
      app.stopLive = () => { stopped += 1; };
      if (dirty) app.rlDraft.rules[0].rpm = 300;

      app.onBeforeUnload(unloadEvent());
      assert.equal(stopped, 0, `stopLive must not run from beforeunload when dirty=${dirty}`);
    }
  });

  await t.test('a page that never loaded rate limits does not prompt', () => {
    const app = createApp();
    const event = unloadEvent();

    assert.equal(app.onBeforeUnload(event), undefined);
    assert.equal(event.defaultPrevented, false);
  });
});

test('dirtiness is a comparison of state, not a record that something was typed', async t => {
  await t.test('editing a rule and typing the value back leaves the page pristine', () => {
    const app = pristineApp();

    app.rlDraft.rules[0].rpm = 300;
    assert.equal(app.rateLimitsDirty, true);

    app.rlDraft.rules[0].rpm = 600;
    assert.equal(app.rateLimitsDirty, false, 'reverting to the saved value is not a pending change');
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
  });

  await t.test('every staged change shape is seen', () => {
    const cases = {
      'a tier number': app => { app.rlDraft.rules[0].rpm = 300; },
      'the enforcement switch': app => { app.rlDraft.enabled = false; },
      'the adaptive switch': app => { app.rlDraft.adaptiveEnabled = true; },
      'the default tier': app => { app.rlDraft.default.rpm = 120; },
      'a new plan': app => { app.rlDraft.plans.standard = { rpm: 120, burst: 10, maxConcurrentStreams: 0 }; },
      'a new rule': app => { app.rlDraft.rules.push({ scope: 'model', target: 'new', rpm: 10, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] }); },
      'a deleted rule': app => { app.rlDraft.rules = []; },
      'a switched-off rule': app => { app.rlDraft.rules[0].enabled = false; },
      'an added window': app => {
        app.rlDraft.rules[0].schedule = [{
          name: 'off-peak', kind: 'weekly', rpm: 1200, burst: 0, maxConcurrentStreams: 0,
          suspend: false, priority: null, days: ['mon'], start: '19:00', end: '07:00', timeZone: 'UTC',
        }];
      },
    };

    for (const [what, edit] of Object.entries(cases)) {
      const app = pristineApp();
      edit(app);
      assert.equal(app.rateLimitsDirty, true, `${what} must count as unsaved work`);
      assert.ok(app.onBeforeUnload(unloadEvent()), `${what} must arm the unload guard`);
    }
  });

  /**
   * The two signals are computed differently — the guard compares serialised payloads, the badge
   * counts an identity-keyed diff — so they could in principle disagree and leave the guard armed
   * behind a badge showing nothing. Every edit the console can actually make is checked against both.
   * (A pure reordering would separate them, but the draft's rule list is only ever filtered in place
   * or appended to, so there is no way to produce one.)
   */
  await t.test('the armed guard and the visible count never disagree', () => {
    const edits = [
      app => { app.rlDraft.rules[0].rpm = 300; },
      app => { app.rlDraft.rules[0].enabled = false; },
      app => { app.rlDraft.enabled = false; },
      app => { app.rlDraft.adaptiveEnabled = true; },
      app => { app.rlDraft.default.burst = 99; },
      app => { app.rlDraft.plans.standard = { rpm: 120, burst: 10, maxConcurrentStreams: 0 }; },
      app => { app.rlDraft.rules.push({ scope: 'model', target: 'new', rpm: 10, burst: 0, maxConcurrentStreams: 0, enabled: true, schedule: [] }); },
      app => { app.rlDraft.rules = []; },
    ];

    for (const [i, edit] of edits.entries()) {
      const app = pristineApp();
      edit(app);
      assert.equal(app.rateLimitsDirty, true, `edit ${i} must arm the guard`);
      assert.ok(app.rateLimitsUnsavedCount > 0, `edit ${i} must also be counted on the badge`);
    }
  });
});

test('the sub-tab badge carries the count out of the Rate limits pane', async t => {
  const limitsTab = app => app.settingsTabs.find(tab => tab.key === 'limits');

  await t.test('nothing staged shows no badge on any tab', () => {
    const app = pristineApp();

    for (const tab of app.settingsTabs) {
      assert.equal(tab.badge, '', `${tab.key} must carry no badge`);
      assert.equal(tab.badgeLabel, '');
    }
  });

  await t.test('the count is visible from the other settings sub-tabs', () => {
    const app = pristineApp();
    app.settingsSubTab = 'cors';
    app.rlDraft.rules[0].rpm = 300;
    app.rlDraft.enabled = false;

    assert.equal(limitsTab(app).badge, '2');
    assert.equal(limitsTab(app).badgeLabel, '2 unsaved rate-limit changes');
    assert.equal(app.rateLimitsUnsavedCount, 2);
  });

  await t.test('one change is singular', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    assert.equal(limitsTab(app).badge, '1');
    assert.equal(limitsTab(app).badgeLabel, '1 unsaved rate-limit change');
  });

  await t.test('only the Rate limits tab is ever badged', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    for (const tab of app.settingsTabs.filter(t => t.key !== 'limits')) {
      assert.equal(tab.badge, '', `${tab.key} must not be badged`);
    }
  });

  await t.test('a page that never loaded rate limits is not badged', () => {
    assert.equal(limitsTab(createApp()).badge, '');
  });
});

test('the guard follows the draft through save, discard and failure', async t => {
  await t.test('a successful save clears it — the draft is replaced by what came back', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;
    assert.equal(app.rateLimitsDirty, true);

    // What applyRateLimitsData does once the reload after a save lands.
    const saved = clone(app.rlDraft);
    app.rateLimits = saved;
    app.rlDraft = clone(saved);

    assert.equal(app.rateLimitsDirty, false);
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
    assert.equal(app.settingsTabs.find(t => t.key === 'limits').badge, '');
  });

  await t.test('discarding clears it without a round trip', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    app.toast = () => {};
    app.discardRateLimitChanges();

    assert.equal(app.rateLimitsDirty, false);
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
  });

  await t.test('a failed save keeps it armed — the draft is the operator\'s work, not the server\'s', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    // The 409 path keeps the draft deliberately, so nothing about dirtiness may change.
    app.rateLimitFieldError = 'Rate limits were changed by someone else since this page was loaded.';

    assert.equal(app.rateLimitsDirty, true, 'a refused save has saved nothing');
    assert.ok(app.onBeforeUnload(unloadEvent()));
    assert.equal(app.settingsTabs.find(t => t.key === 'limits').badge, '1');
  });

  await t.test('a refresh that keeps a dirty draft keeps the guard armed', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    // fetchRateLimits returns early rather than overwriting a dirty draft; the guard must agree.
    assert.equal(app.rateLimitsDirty, true);
    assert.ok(app.onBeforeUnload(unloadEvent()));
  });
});

/**
 * The four editors — plan tier, rule, window, new-rule wizard — each work on a copy that reaches
 * the draft only on Done. While one is open with edits in it, `rateLimitsDirty` is still false, so
 * a reload would have taken the whole editor with no prompt: the exact loss the guard exists to
 * prevent, one level in.
 */
test('an open editor counts as unsaved work even when the draft is clean', async t => {
  /** A component with the rule drawer open on the saved rule, and nothing typed into it yet. */
  const withRuleDrawer = () => {
    const app = pristineApp();
    app.rateLimitUsage = {};
    app.openRateLimitRule('model:gpt-4');
    return app;
  };

  await t.test('an untouched editor is not unsaved work', () => {
    const cases = {
      'the tier drawer': app => app.openRateLimitTier('default', ''),
      'the rule drawer': app => { app.rateLimitUsage = {}; app.openRateLimitRule('model:gpt-4'); },
      'the new-rule wizard': app => app.openRateLimitNewRule(),
    };

    for (const [what, open] of Object.entries(cases)) {
      const app = pristineApp();
      open(app);
      assert.equal(app.rateLimitsWorkInProgress, false, `${what} must be quiet until something is entered`);
      assert.equal(app.onBeforeUnload(unloadEvent()), undefined, `${what} must not prompt`);
    }
  });

  await t.test('an untouched window form is not unsaved work', () => {
    const app = withRuleDrawer();
    app.openRateLimitWindow(-1);

    assert.equal(app.rlWindowOpen, true);
    assert.equal(app.rlWindowDirty, false, 'the blank template is not yet an answer');
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
  });

  await t.test('each editor arms the guard once something is entered', () => {
    const cases = {
      'a tier number': app => {
        app.openRateLimitTier('default', '');
        app.rlTier.rpm = 999;
      },
      'a renamed plan': app => {
        app.rlDraft.plans.standard = { rpm: 120, burst: 10, maxConcurrentStreams: 0 };
        app.rateLimits.plans.standard = { rpm: 120, burst: 10, maxConcurrentStreams: 0 };
        app.openRateLimitTier('plan', 'standard');
        app.rlTier.slug = 'premium';
      },
      'a new plan': app => app.openRateLimitTier('plan', ''),
      'a rule number': app => {
        app.rateLimitUsage = {};
        app.openRateLimitRule('model:gpt-4');
        app.rlRule.rpm = 900;
      },
      'a rule switched off in the drawer': app => {
        app.rateLimitUsage = {};
        app.openRateLimitRule('model:gpt-4');
        app.rlRule.enabled = false;
      },
      'a window name': app => {
        app.rateLimitUsage = {};
        app.openRateLimitRule('model:gpt-4');
        app.openRateLimitWindow(-1);
        app.rlWindow.name = 'off-peak';
      },
      'a wizard step': app => {
        app.openRateLimitNewRule();
        app.rlNewRule.step = 2;
      },
      'a wizard subject': app => {
        app.openRateLimitNewRule();
        app.rlNewRule.subject = 'acme';
      },
      'a wizard tier the operator typed': app => {
        app.openRateLimitNewRule();
        app.setRateLimitNewRuleTier('rpm', 25);
      },
    };

    for (const [what, edit] of Object.entries(cases)) {
      const app = pristineApp();
      edit(app);
      assert.equal(app.rateLimitsDirty, false, `${what} must not have reached the draft yet`);
      assert.equal(app.rateLimitsWorkInProgress, true, `${what} must count as unsaved work`);
      assert.ok(app.onBeforeUnload(unloadEvent()), `${what} must arm the unload guard`);
    }
  });

  await t.test('typing a value back leaves the editor quiet', () => {
    const app = withRuleDrawer();

    app.rlRule.rpm = 900;
    assert.equal(app.rlRuleDirty, true);

    app.rlRule.rpm = 600;
    assert.equal(app.rlRuleDirty, false, 'reverting in the drawer is not a pending change');
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
  });

  await t.test('applying the editor moves the work to the draft, and it stays guarded', () => {
    const app = withRuleDrawer();
    app.rlRule.rpm = 900;
    app.applyRateLimitRule();

    assert.equal(app.rlRuleDrawerOpen, false);
    assert.equal(app.rlRuleDirty, false, 'the drawer is closed, so it holds nothing');
    assert.equal(app.rateLimitsDirty, true, 'the edit is staged now');
    assert.ok(app.onBeforeUnload(unloadEvent()));
  });

  await t.test('closing an editor with nothing staged disarms the guard', () => {
    const app = withRuleDrawer();
    app.rlRule.rpm = 900;
    assert.equal(app.rateLimitsWorkInProgress, true);

    app.closeRateLimitRule();
    assert.equal(app.rateLimitsWorkInProgress, false);
    assert.equal(app.onBeforeUnload(unloadEvent()), undefined);
  });

  /**
   * The badge counts staged changes only. An open editor is on screen by definition and needs no
   * badge to announce it, so the guard is deliberately wider than the count — but the count must
   * never be the wider of the two.
   */
  await t.test('anything the badge counts also arms the guard', () => {
    const app = pristineApp();
    app.rlDraft.rules[0].rpm = 300;

    assert.ok(app.rateLimitsUnsavedCount > 0);
    assert.equal(app.rateLimitsWorkInProgress, true);
  });
});

test('an accidental dismissal asks before discarding a working copy', async t => {
  await t.test('Escape on an edited drawer asks instead of closing', () => {
    const app = pristineApp();
    app.rateLimitUsage = {};
    app.openRateLimitRule('model:gpt-4');
    app.rlRule.rpm = 900;

    app.dismissRateLimitRule();

    assert.equal(app.rlRuleDrawerOpen, true, 'the drawer stays open behind the question');
    assert.ok(app.confirmDialog, 'the operator is asked');
    assert.equal(app.confirmDialog.confirmLabel, 'Discard');
  });

  await t.test('confirming discards, and the edits are gone', async () => {
    const app = pristineApp();
    app.rateLimitUsage = {};
    app.openRateLimitRule('model:gpt-4');
    app.rlRule.rpm = 900;
    app.dismissRateLimitRule();

    await app.confirmDialog.onConfirm();

    assert.equal(app.rlRuleDrawerOpen, false);
    assert.equal(app.rlDraft.rules[0].rpm, 600, 'the draft never saw the edit');
  });

  await t.test('an untouched editor closes without a question', () => {
    const cases = {
      'the rule drawer': [app => { app.rateLimitUsage = {}; app.openRateLimitRule('model:gpt-4'); },
        app => app.dismissRateLimitRule(), app => app.rlRuleDrawerOpen],
      'the tier drawer': [app => app.openRateLimitTier('default', ''),
        app => app.dismissRateLimitTier(), app => app.rlTierDrawerOpen],
      'the wizard': [app => app.openRateLimitNewRule(),
        app => app.dismissRateLimitNewRule(), app => app.rlNewRuleOpen],
    };

    for (const [what, [open, dismiss, isOpen]] of Object.entries(cases)) {
      const app = pristineApp();
      open(app);
      dismiss(app);

      assert.equal(isOpen(app), false, `${what} must close straight away`);
      assert.equal(app.confirmDialog, null, `${what} must not ask when there is nothing to lose`);
    }
  });

  await t.test('Cancel and Done stay immediate — they say what they do', () => {
    const app = pristineApp();
    app.rateLimitUsage = {};
    app.openRateLimitRule('model:gpt-4');
    app.rlRule.rpm = 900;

    app.closeRateLimitRule();

    assert.equal(app.rlRuleDrawerOpen, false);
    assert.equal(app.confirmDialog, null);
  });
});
