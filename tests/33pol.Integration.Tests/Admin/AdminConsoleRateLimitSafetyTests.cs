using System.Net;
using Pol33.Core.Configuration;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The rate-limit console is a static asset bundle, so nothing else in the suite would notice if the
/// safety affordances were dropped: the endpoint tests keep passing while the wizard hands an
/// operator a number that weakens the control they came to strengthen.
/// </summary>
public sealed class AdminConsoleRateLimitSafetyTests
{
    private static async Task<string> GetAssetAsync(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The regression this exists for: one seed of 600 rpm for every scope. The shipped
    /// auth-failure tier is 60/20, so a wizard default of 600 was ten times looser than what the
    /// gateway already enforced — an operator adding credential-guessing protection and keeping the
    /// default would have relaxed it while believing they had tightened it.
    /// </summary>
    [Fact]
    public async Task NewRuleWizard_SeedsTheProtectiveScopesTighterThanTheGenericOne()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("rlDefaultTierFor(scope)");
        js.Should().Contain("if (scope === 'auth_failure') return { rpm: 20, burst: 10, maxConcurrentStreams: 0 };");
        js.Should().Contain("if (scope === 'anonymous') return { rpm: 30, burst: 10, maxConcurrentStreams: 2 };");
    }

    /// <summary>
    /// There is no gateway-wide rate that is right for every deployment, and a seeded one throttles
    /// every caller at once. The wizard leaves it blank and the create path refuses until it is named.
    /// </summary>
    [Fact]
    public async Task NewRuleWizard_LeavesTheGatewayCeilingBlankAndRefusesAnUnnamedOne()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("if (scope === 'global') return { rpm: '', burst: 0, maxConcurrentStreams: 0 };");
        js.Should().Contain("Name the ceiling: set rpm above zero.");
    }

    /// <summary>
    /// Switching scope re-seeds only while the numbers are still the console's; once the operator has
    /// typed, they own them.
    /// </summary>
    [Fact]
    public async Task NewRuleWizard_DoesNotReseedOverNumbersTheOperatorTyped()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("if (this.rlNewRule.touched) return;");
        js.Should().Contain("setRateLimitNewRuleTier(field, value)");
    }

    /// <summary>
    /// Every numeric input carries the server's own ceiling, so an out-of-range number is refused by
    /// the control rather than by a save that has already left the drawer behind.
    /// </summary>
    [Fact]
    public async Task NumericInputs_CarryTheServerBounds()
    {
        var html = await GetAssetAsync("/admin/index.html");

        RateLimitConfigValidation.MaxRpm.Should().Be(1_000_000);
        RateLimitConfigValidation.MaxBurst.Should().Be(1_000_000);
        RateLimitConfigValidation.MaxMaxConcurrentStreams.Should().Be(10_000);

        // Rule drawer, tier drawer, window form and the wizard's third step.
        html.Should().Contain("max=\"1000000\" x-model.number=\"mdl.rlRule.rpm\"");
        html.Should().Contain("max=\"1000000\" x-model.number=\"mdl.rlTier.rpm\"");
        html.Should().Contain("max=\"1000000\" x-model.number=\"mdl.rlWindow.rpm\"");
        html.Should().Contain("max=\"1000000\" x-model.number=\"mdl.rlNewRule.rpm\"");
        html.Should().Contain("max=\"10000\" x-model.number=\"mdl.rlRule.maxConcurrentStreams\"");
        html.Should().Contain("max=\"10000\" x-model.number=\"mdl.rlTier.maxConcurrentStreams\"");
        html.Should().Contain("max=\"10000\" x-model.number=\"mdl.rlWindow.maxConcurrentStreams\"");
        html.Should().Contain("max=\"10000\" x-model.number=\"mdl.rlNewRule.maxConcurrentStreams\"");
    }

    /// <summary>
    /// Disabling every limit at once staged as one line in a change list understates it. The switch
    /// asks, and a cancel puts it back — which is why the shared dialog grew an onCancel.
    /// </summary>
    [Fact]
    public async Task EnforcementSwitch_AsksBeforeTurningEveryLimitOff()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("setRateLimitEnforcement(value)");
        js.Should().Contain("title: 'Stop enforcing rate limits?'");
        js.Should().Contain("onCancel: () => { if (this.rlDraft) this.rlDraft.enabled = true; }");

        // Escape and backdrop dismissal both land in cancelConfirm, so no path leaves it half-applied.
        js.Should().Contain("if (d?.onCancel) await d.onCancel();");
    }

    /// <summary>
    /// A refused save names its subject — a rule identity, a plan slug, the default tier — so the
    /// console can offer the way back instead of leaving the operator to find it.
    /// </summary>
    [Fact]
    public async Task ARefusedSave_OffersTheWayBackToTheDrawerThatOwnsIt()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("rlSaveErrorTarget()");
        js.Should().Contain("message.match(/rule '([^']+)'/)");
        js.Should().Contain("message.match(/plans\\['([^']+)'\\]/)");
        html.Should().Contain("x-show=\"rlSaveErrorView.show\" @click=\"rlSaveErrorView.open\"");
    }

    /// <summary>
    /// The console never said what an exceeded limit does to a caller, so an operator could not tell
    /// whether a limit was survivable, and some would assume requests queue rather than fail.
    /// </summary>
    [Fact]
    public async Task TheConsole_SaysWhatARefusalLooksLike()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("<code>429</code>");
        html.Should().Contain("<code>Retry-After</code>");
        html.Should().Contain("Nothing is queued or slowed");
    }

    /// <summary>
    /// The toggle groups on this surface reported no pressed state, while the same console reported
    /// it on the theme switch and the date presets. Screen-reader users could not tell which days or
    /// which scope filter were selected.
    /// </summary>
    [Fact]
    public async Task ToggleGroups_ReportTheirPressedState()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain(":aria-pressed=\"c.pressed\"");  // scope filter chips
        html.Should().Contain(":aria-pressed=\"k.pressed\"");  // window kind
        html.Should().Contain(":aria-pressed=\"d.pressed\"");  // weekday picker
    }

    /// <summary>
    /// role="radio" promises arrow-key traversal and a single tab stop; without a roving tabindex and
    /// a key handler the role described a widget the markup did not implement.
    /// </summary>
    [Fact]
    public async Task ScopeRadiogroup_IsTraversableByKeyboard()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        html.Should().Contain("@keydown=\"s.onKey\"");
        html.Should().Contain(":tabindex=\"s.tabIndex\"");
        js.Should().Contain("rateLimitScopeKeydown(event, id)");
        js.Should().Contain("tabIndex: n.scope === s.id ? '0' : '-1'");
    }

    /// <summary>
    /// The count comes from the overview's top consumers this month, which is a floor rather than a
    /// roster, so the dialog names where the number came from instead of asserting a total.
    /// </summary>
    [Fact]
    public async Task RemovePlanDialog_NamesTheScaleWithoutOverstatingIt()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("' seen this month '");
        js.Should().Contain("'Every tenant on it falls back to the default tier ('");
    }

    /// <summary>
    /// The calendar, the transitions panel and "Preview at" all read the saved configuration, which
    /// is the wrong question while a change is being composed: the schedule an operator is checking
    /// is the one about to be saved. With a dirty draft they answer for it instead.
    /// </summary>
    [Fact]
    public async Task TheCalendar_AnswersForTheDraftWhileOneIsStaged()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        // Both panels switch on the same condition, so they cannot describe different configurations.
        js.Should().Contain("this.rlSchedule = this.rateLimitsDirty");
        js.Should().Contain("this.rlPreview = this.rateLimitsDirty");
        js.Should().Contain("'/admin/api/rate-limits/schedule/preview'");

        // And the draft is redrawn as it changes, not only when the tab is entered.
        js.Should().Contain("queueRateLimitScheduleRefresh()");
    }

    /// <summary>
    /// The panel used to say "unsaved edits are not drawn until you save" and mean it. Now that it
    /// draws them, it has to say which configuration is on screen — a calendar that silently
    /// switches between saved and draft is worse than one that only ever showed saved.
    /// </summary>
    [Fact]
    public async Task TheCalendar_SaysWhichConfigurationItIsDrawing()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        html.Should().NotContain("unsaved edits are not drawn until you save");
        html.Should().Contain("x-text=\"rlTimelineView.sourceText\"");
        html.Should().Contain("x-show=\"rlTimelineView.draft\"");
        js.Should().Contain("'Drawn from your unsaved draft, so you can check a schedule before saving it.'");

        // The per-rule strip in the drawer is drawn from the same report, so it says so too.
        html.Should().Contain("x-text=\"rlRuleDrawerView.bandsSource\"");
        js.Should().Contain("bandsSource: this.rateLimitsDirty ? 'as drafted' : 'as saved'");
    }

    /// <summary>
    /// The rule lifecycle was create / edit / delete, so stopping enforcement during an incident meant
    /// authoring a suspend window with concrete times — and the reachable action was Delete, which
    /// takes the schedule with it. The switch is the fast, reversible one, in the row and the drawer.
    /// </summary>
    [Fact]
    public async Task EachRule_CanBeSwitchedOffWithoutDeletingIt()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        // In the row, where an incident reaches for it; the cell stops the click opening the drawer.
        html.Should().Contain("<th class=\"rl-col-on\">On</th>");
        html.Should().Contain("<td class=\"rl-col-on\" @click.stop>");
        html.Should().Contain("@change=\"r.toggle\"");

        // And in the drawer, next to what the rule is currently enforcing.
        html.Should().Contain("x-model=\"mdl.rlRule.enabled\"");
        js.Should().Contain("setRateLimitRuleEnabled(identity, enabled)");
        js.Should().Contain("toggleRateLimitRuleEnabled(identity)");
    }

    /// <summary>
    /// Switching off and deleting sit one click apart and only one can be undone after a save, so the
    /// destructive one says what it costs and names the reversible alternative.
    /// </summary>
    [Fact]
    public async Task Delete_IsDistinguishedFromSwitchingOff()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        html.Should().Contain(">Delete permanently</button>");
        js.Should().Contain("title: 'Delete this rule permanently?'");
        js.Should().Contain("switch it off instead.");
        js.Should().Contain("' schedule window'");
    }

    /// <summary>
    /// Switching a rule off changes no number, so the save bar would otherwise report a bare
    /// "rule X" for the one edit whose effect is invisible in the numbers.
    /// </summary>
    [Fact]
    public async Task TheChangeList_NamesASwitchedRule()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("'switched on rule '");
        js.Should().Contain("'switched off rule '");
    }

    /// <summary>
    /// A disabled rule reports that it enforces nothing rather than a tier it is not applying, and
    /// says the tier is kept — which is the whole difference from having deleted it.
    /// </summary>
    [Fact]
    public async Task ASwitchedOffRule_ReadsAsOffRatherThanAsItsTier()
    {
        var css = await GetAssetAsync("/admin/admin.css");
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("text: 'off', sub: this.rlTierText(rule) + ' kept'");
        js.Should().Contain("enabledText: off ? 'Switched off — the tier and windows below are kept' : 'Enforced'");

        // Dimmed, not struck through: it is a rule an operator is coming back to.
        css.Should().Contain(".rl-row.off td { opacity: 0.62; }");
    }

    /// <summary>
    /// An absent `enabled` means enforced. Were it read as false, a gateway that predates the flag
    /// would appear to have every rule switched off the moment the console loaded it.
    /// </summary>
    [Fact]
    public async Task AnAbsentEnabledField_ReadsAsEnforced()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("enabled: (r.enabled ?? r.Enabled) !== false,");
        js.Should().Contain("enabled: row.enabled !== false,");
    }

    /// <summary>
    /// Staged rate-limit edits live only in memory, and a reload discarded them with no prompt. The
    /// listener that already tore down the live stream now also asks — but only while something is
    /// actually staged, so a pristine page still leaves in silence.
    /// </summary>
    /// <remarks>
    /// The behaviour is exercised in <c>tests/admin-console/rate-limit-unsaved-guard.test.js</c>
    /// (<c>node --test tests/admin-console/</c>); what is pinned here is that the console still wires
    /// it up, which no JavaScript test can see.
    /// </remarks>
    [Fact]
    public async Task ReloadingWithStagedEdits_IsGuarded()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("window.addEventListener('beforeunload', (e) => this.onBeforeUnload(e));");
        js.Should().Contain("onBeforeUnload(event)");
        js.Should().Contain("if (!this.rateLimitsDirty) return undefined;");

        // The stream teardown the listener already did must not become conditional on the draft.
        js.Should().Contain("this.stopLive();\n      if (!this.rateLimitsDirty)");
    }

    /// <summary>
    /// The sticky save bar that reports staged edits sits inside the Rate limits sub-tab, so stepping
    /// over to CORS or Model access hid every trace of a draft that was still there. The count rides
    /// on the sub-tab instead, which is visible from all of them.
    /// </summary>
    [Fact]
    public async Task StagedEdits_AreVisibleFromTheOtherSettingsSubTabs()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var css = await GetAssetAsync("/admin/admin.css");
        var js = await GetAssetAsync("/admin/admin-app.js");

        html.Should().Contain("<span class=\"sub-nav-badge\" x-show=\"t.badge\" x-text=\"t.badge\" :aria-label=\"t.badgeLabel\">");
        css.Should().Contain(".sub-nav-badge {");
        js.Should().Contain("get rateLimitsUnsavedCount()");
        js.Should().Contain("badge: id === 'limits' && unsaved > 0 ? String(unsaved) : '',");
    }

    /// <summary>
    /// Ticking "Pause this rule instead" hid the three tier inputs but left their heading behind,
    /// which reads as a rendering fault at the moment the operator is choosing a mode.
    /// </summary>
    [Fact]
    public async Task PausingAWindow_HidesTheTierHeadingWithItsInputs()
    {
        var html = await GetAssetAsync("/admin/index.html");

        // Asserted by position rather than by an exact string, so reindenting the markup does not
        // fail the test while leaving the defect fixed.
        var guard = html.IndexOf("x-show=\"rlWindowView.showTier\"", StringComparison.Ordinal);
        var heading = html.IndexOf("Limit during the window", StringComparison.Ordinal);
        var inputs = html.IndexOf("mdl.rlWindow.rpm", StringComparison.Ordinal);

        guard.Should().BeGreaterThan(-1);
        heading.Should().BeGreaterThan(guard, "the heading must sit inside the container that hides the tier inputs");
        inputs.Should().BeGreaterThan(heading, "the heading still introduces the inputs it names");
    }
}
