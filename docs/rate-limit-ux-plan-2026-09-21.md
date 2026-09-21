# Admin → Settings → Rate limits: UI/UX improvement plan

Date: 2026-09-21 · Based on `main` @ `e625d73` · Plan only — no code was changed.

All claims below were read from the repository. Nothing was run in a browser for this plan, so
anything that depends on rendered layout is marked **verify in browser**. Things that could not be
established from code are marked **unknown**.

Paths are relative to the repo root. `APP` = `src/33pol.App/wwwroot/admin/admin-app.js`,
`HTML` = `src/33pol.App/wwwroot/admin/index.html`, `CSS` = `src/33pol.App/wwwroot/admin/admin.css`.

---

## 1. Executive summary

**Strengths (keep).** The page already has a sound editing model: a draft separate from the server
copy (`rlDraft` vs `rateLimits`), one versioned save with `If-Match`, a 409 path that keeps the
operator's work, server-side schedule and window previews instead of duplicated date maths, honest
wording about what the counters mean ("since restart", "subject-level", "— means unknown"), a
who × model creation form with a tested combobox, EN/FA help, and a rules table that already
card-ifies at narrow width. The modal/focus machinery is shared and correct. None of this should be
redesigned.

**Primary weaknesses.**

1. *The page describes the draft as if it were production.* The header, the red "disabled" notice
   and the rules table's Limit/Now cells all read `rlDraft`. After an unsaved edit the page says
   "Rate limits are not enforced" or shows a new RPM while the gateway is still doing the old thing.
2. *Operational data goes stale silently.* Activity is fetched once on entry and never again; the
   rules table's Refused column is fed by it, with the only "as of" stamp three sections further down.
3. *The answer to "is anything being refused right now?" is below the fold*, after Rules, Protective
   limits and Tenant tiers. The header spends its space on configuration counts (Rules, Windows).
4. *The review before Save is a list of names.* No before → after values, destructive items look
   like the rest, and the panel renders below the sticky bar at the very end of the page.
5. *Adaptive limiting and the master switch do not reach the rows.* A model rule being held at 70 %
   by the governor, or every rule being moot because enforcement is off, looks identical to normal.
6. *Scale.* The server accepts 2,000 rules; the table renders all of them, recomputing each row with
   two payload builds, two `JSON.stringify` calls and two linear scans on every reactive change.

**Highest-value improvements.** (a) Split *saved/enforced* from *draft* everywhere a status is
shown; (b) auto-refresh Activity on the console's existing poll and show its age where its numbers
are used; (c) replace the status card's config counts with an operational summary built from data
the usage and schedule reports already return; (d) turn the review into a before → after diff with
per-item undo and show the other party's change after a 409; (e) surface adaptive and
master-switch state in rows; (f) index row computation and adopt the console's "Show 100 more".

**Direction.** Re-order the page to follow
*configured policy → effective now → traffic → pressure/refusals → scheduled changes → unsaved
changes*, reuse existing classes (`.mini-stats`, `.load-track`, `.tag`, `.status-chip`,
`.th-sort`, `.pager`/"show more", `.empty-state`, `.notice`), add no new visual language, and keep
per-rule utilisation out of the UI until the backend can actually attribute it (§15).

---

## 2. Current page — simple explanation

- **Purpose.** Show and edit the gateway's rate-limit configuration, and show live figures about
  whether limits are being reached. A limit is three numbers: RPM, burst, max concurrent streams.
  Over a limit a request gets `429` + `Retry-After`; nothing is queued.
- **Displays.** Status card (master switch, adaptive switch, counts of rules/windows, windows active
  now, next change) → Rules table → Protective limits (2 cards) → Tenant tiers (cards) → Activity
  (windowed totals, traffic by subject, refusals by limit since restart, adaptive adjustments,
  bucket occupancy) → Calendar (timeline, "Coming up", "Preview at") → sticky save bar + review list.
- **Rule creation.** "New rule" drawer: *Who* (API key / tenant / everyone) × *On which model*
  (one / all) maps to one of six scopes (`rlIntents`, APP:2971); combobox pickers; RPM/burst/streams;
  a list of other limits on the same requests; "Create rule" / "Create and schedule". Also reachable
  from the API keys page (`limitRateForKey`, APP:4469).
- **Rule editing.** Rule drawer: enable switch, base tier, windows list, "This week" strip, Activity
  (refusals by this limit; subject traffic), Delete permanently (confirm), Done/Cancel. Row switches
  toggle a rule without opening it.
- **Drafts and saving.** Every edit changes `rlDraft` only. Save = one `PUT /admin/api/rate-limits`
  replacing the whole rule set, with `If-Match: W/"<version>"`. 409 → draft kept, baseline refreshed,
  review opened (`rlRecoverFromConflict`, APP:3598). 503 → page becomes read-only. Save is blocked
  while a draft rule has no target. Dirty = canonical JSON compare (`rlCanonical`).
- **Monitoring.** `GET …/usage?minutes&take=200`, fetched when the sub-tab is entered (if older than
  60 s), on Refresh, and when the window (15 min / 1 h / 3 h) changes. No auto-refresh. On failure
  the previous report is kept and marked stale.
- **Scheduling.** Windows hang off a rule: `weekly` (days, start, end, zone) or `once` (from, until),
  each with a tier or "pause", optional priority and validity dates. The server evaluates them:
  `GET …/schedule` for the saved set, `POST …/schedule/preview` for the draft,
  `POST …/windows/preview` while composing one window. The schedule report reloads itself at the
  next transition (cap 30 min) while the page is visible.
- **Protective limits.** `anonymous` and `auth_failure` are stored as rules but listed as two fixed
  cards; same drawer. They never appear in the usage report.
- **Tenant tiers.** `default` tier + one tier per plan slug; edited in a small drawer. A `tenant`
  rule overrides the plan tier; RPM 0 on a tenant rule keeps the plan's rate.
- **Search/filter/sort.** Text search (target, display name, scope name, window names); chips Who /
  Model / Scheduled / Off with counts; Clear filters; sort on Who, Model, Limit (rpm), Refused. No
  pagination. State is in memory only.
- **Persisted vs live.** Persisted: enabled, adaptiveEnabled, default, plans, rules, windows, version.
  Live/in-memory (reset on restart): everything in the usage report. Computed on request, never
  stored: the schedule report.

**Current page in one sentence:** A Settings sub-page that shows and edits rate-limit rules, tiers,
protective limits and time windows as a client-side draft saved in one versioned write, with
in-memory activity figures and a server-computed calendar.

**Primary workflow:** open → scan/filter rules → open a rule or New rule → edit in drawer → Done →
review in save bar → Save.

**Main visible information:** enforcement/adaptive state; rule/window counts; per rule who, model,
rpm/burst/streams, schedule state, refusals since restart; protective limits; tiers; windowed totals
and per-subject traffic; cumulative refusals by limit; adaptive adjustments; timeline and upcoming
changes; unsaved-change list.

**Supported actions:** toggle enforcement/adaptive; create, edit, switch on/off, delete rules; add,
edit, remove windows; edit default tier and plans; configure protective limits; search, filter,
sort; change activity window/grouping, calendar range/zone; preview at an instant; reload, discard,
review, save; open help (EN/FA).

**Important technical constraints:**
- Whole-set replacement on save; no per-rule endpoint.
- Usage report: in memory, ≤180 min, per-dimension key ceiling (default 500) with **no eviction —
  new keys past the ceiling are silently ignored** (`RateLimitUsageTracker.cs:254-259`).
- `violations[].hits` are cumulative since start, never windowed; only the first refusing scope is
  recorded; for `tenant` it is plan ∘ rule; `auth_failure` and control-plane are never recorded.
- `RateLimitUsageRow.ConfiguredRpm/EffectiveRpm` are **last-writer-wins from whichever scope was
  tightest or refused on the subject's most recent rate decision** (`Ring.Add`, :388-392) — not the
  row's own rule. `Utilization` inherits that.
- Per-minute rings exist inside the tracker but `BuildReport` only returns sums — no time series.
- Without a model-scoped rule, admitted requests carry no model (review RL-025), so Model and
  Tenant × model tabs can be empty on a default install.
- One auth policy (`Operator`) covers every route; there is no read-only role. The console's 403
  "may view but not change" branch (APP:3584) is unreachable against this server.
- Alpine CSP build: templates resolve property paths only. Every new binding needs a precomputed
  view-object field (no `!`, no calls with arguments, no object literals in `:class`/`:style`).
- Markup and copy are pinned by tests (§3.6). Moving markup means updating pins in the same change.

---

## 3. Current implementation map

### 3.1 Frontend
| What | Where |
|---|---|
| Page markup | HTML:1793-2214 (`x-show="isSettingsLimits"`) |
| Help drawer / rule drawer + window pane / tier drawer / new-rule drawer | HTML:2509 / 2543-2785 / 2790-2840 / 2846-2990 |
| Shared confirm dialog | HTML:2993-3004, `openConfirm` APP:2401, `confirmView` APP:9539 |
| State fields | `rateLimits`, `rlDraft`, `rlSchedule`, `rlPreview`, `rateLimitUsage*`, `rlFilter*`, `rlSortKey/Dir`, `rlRule`, `rlWindow`, `rlTier`, `rlNewRule`, `rlReadOnlyReason`, `rlSaving`, `rlReviewOpen` |
| Scope model | `rlScopeCatalog` APP:2953, `rlIntents` :2971, `rlIdentity` :2999 |
| Load / draft / save | `fetchRateLimits` :3123, `applyRateLimitsData` :3091, `reloadRateLimits` :3169, `discardRateLimitChanges` :3189, `buildRateLimitsPayload` :3397, `saveRateLimits` :3494, `rlAdoptSaved` :3544, `rlSaveFailed` :3577, `rlRecoverFromConflict` :3598, `rlSaveErrorTarget` :3449 |
| Master switch | `setRateLimitEnforcement` :3204 (setter wired at :5362) |
| Usage | `loadRateLimitUsage` :3619, `setRateLimitUsageMinutes` :3648, `rlUsageTake` :3617 (=200) |
| Schedule | `loadRateLimitSchedule` :3840, `queueRateLimitScheduleRefresh` :3832, `runRateLimitPreview` :3897, `rlWindowWhen` :3801 |
| Filters / sort | `rlRuleMatchesFilter` :7912, `rlScopeChips` :7934, `setRateLimitSort` :3948, `rlSortView` :7962 |
| View models | `rlStatusView` :7841, `rlTierCards` :7884, `rlForceFor` :7976, `rlRuleRow` :8005, `rlRuleRows` :8088, `rlProtectiveCards` :8110, `rlTimelineView` :8172, `rlTransitionRows` :8245, `rlPreviewView` :8278, `rlRuleDrawerView` :8305, `rlWindowView` :8390, `rlNewRuleView` :8854, `rlDirtyView` :7794, `rlSaveErrorView` :7710, `rlActivityView` :9109, `rlUsageSubjectView` :9292 |
| Attribution helpers | `rlResolveTenantId` :9179, `rlUsageKeyFor` :9192, `rlRefusalsFor/View` :9210/:9229, `rlSubjectTrafficFor` :9254, `rlRuleUsageView` :9347, `rlLimitLabel` :9385, `rlRuleForUsageKey` :9404 |
| Entry on sub-tab | `setSettingsSubTab` :862 (lazy loads usage, schedule, overview tenants) |
| Help content | `src/33pol.App/wwwroot/admin/admin-rate-limit-help.js`, check/render scripts in `scripts/` |
| RL styles | CSS:1920-2262 (`.rl-*`), breakpoints at 64rem, 48rem, 560px, `pointer: coarse` |

### 3.2 API (`src/33pol.Api/Endpoints/AdminRateLimitEndpoints.cs`, policy `Operator`)
`GET /` (ETag + `version`) · `PUT /` (`If-Match`; 400/409/503/500; audited incl. refusals) ·
`GET /usage?minutes=1..180&take=1..1000` (503 if no tracker) · `GET /schedule` (≤62 days; caps 500
transitions, 2,000 occurrences, both report `…Total`/`…Truncated`) · `POST /schedule/preview` ·
`POST /windows/preview`.

### 3.3 Models
`AdminRateLimitsDto`, `AdminRateLimitRuleDto`, `AdminRateLimitWindowDto`
(`src/33pol.Api/Contracts/AdminRateLimitsDto.cs`); `RateLimitUsageReport` and rows
(`src/33pol.Core/RateLimiting/RateLimitUsageReport.cs`); `RateLimitScheduleReport`,
`ScheduleRuleStatus`, `ScheduleOccurrence`, `ScheduleTransition`, `RateLimitWindowPreview`
(`…/RateLimitScheduleReport.cs`).

### 3.4 Validation (`src/33pol.Core/Configuration/RateLimitConfigValidation.cs`)
`MaxRules` 2,000 · `MaxWindowsPerRule` 16 · tier RPM 1–1,000,000 · burst 0–1,000,000 · streams
0–10,000 · priority 0–1000 · window name/plan slug ≤64 · target ≤256. Rule RPM 0 allowed in every
scope except `auth_failure`, only with burst 0 and streams > 0. `auth_failure` is rate-only.
Singletons (`*` target): `global`, `anonymous`, `auth_failure`. Error messages name their subject
(`rule 'scope:target'`, `plans['slug']`, `default`), which `rlSaveErrorTarget` parses. Client mirror:
`rlLimits()` APP:3320, pinned to the C# constants by
`AdminConsoleRateLimitSafetyTests.ConsoleLimits_MirrorTheServerConstants`.

### 3.5 Reusable Admin Panel patterns (non-RL pages)
| Need | Existing pattern |
|---|---|
| Section header | `.section-header` CSS:562, `.eyebrow` :252 |
| Compact stats | `.mini-stats/.mini-stat(.warn/.error)` CSS:836; clickable `<button class="mini-stat">` HTML:406-411 |
| Utilisation bar | `.load-track/.load-fill(.is-hot ≥80 %/.is-over ≥100 %)` CSS:1500; `.budget-row` CSS:1580; `quotaRows` APP:6166 (no ARIA today; number restated as text) |
| Ranked bars | `.hbar` CSS:887, `barWidth` APP:1354 |
| Tags / chips | `.tag(.muted/.warn/.live/.accent/.level-error/.level-warning)` CSS:990-1097, 2017-2044; `.status-chip(.ok/.fail/.warn/.muted)` CSS:489 |
| Sortable header with `aria-sort` | `.th-sort` CSS:2012 (RL only; other pages use `th.sortable` without `aria-sort`) |
| Filters | `.toolbar` :684, `.filter-field` :690, `.inline-select` :709, `.preset` chips :713, Errors "Clear filters" HTML:1578 |
| Result count | `.hint` beside header, e.g. `usageRollupsCountText` APP:7170 |
| Paging | Client "Show 100 more": `usageRollupLimit` APP:156, `showMoreUsageRollups` :4894, HTML:1096. Offset `.pager` (Errors) CSS:1131 |
| Freshness | `updatedLineText` APP:6975 ("Updated 12s ago · the last refresh failed…"), `showStaleNotice` :6482 |
| Polling | single `syncPoll` 2 s interval APP:1164, `document.hidden` and `connectionStatus === 'fail'` guards; every-15th-tick (30 s) slot used by `loadOverviewSlow` |
| Row → detail | `tr.request-row role="button"` + sibling detail row HTML:848-893 (**not** usable for RL rows: a test forbids `tabindex` on `rl-row`; keep the chevron button) |
| Row actions | `.row-actions` + `.icon-btn` strip CSS:633/617 (no kebab menus anywhere) |
| Drawer / confirm / focus | `.drawer` CSS:1338, `.rl-drawer` :2107, `syncModalFocus` APP:2482, `_visibleModalSurface` :2432, `onModalKeydown` :2594 |
| Empty / loading | `.empty-state` CSS:1233, `.skeleton-row` :1152, `<p class="hint" role="status">` |
| Deep links | `openLink({tab, params})` APP:5622; settings supports `sub` only |
| Sparklines | `sparkLine/sparkFill` APP:1278-1303 (generic over a numeric array) |

### 3.6 Tests that pin this page
- `tests/admin-console/rate-limit-*.test.js` (node `vm` harness; drive getters/methods **and**
  regex-match `index.html`): `drawers`, `rule-list`, `activity`, `client-validation`, `save-state`,
  `protective-warning`, plus help tests.
- `tests/33pol.Integration.Tests/Admin/AdminConsoleRateLimitSafetyTests.cs`,
  `AdminConsoleRateLimitHelpTests.cs` (fetch static assets, `Should().Contain` literals).
- `scripts/check-rate-limit-help.mjs` (EN/FA key parity).
- Pinned **present**: `<th class="rl-col-on">On</th>`; the `th-sort` header markup for Who/Refused;
  `>Refused<…<span class="rl-th-sub">since restart</span>`; `means unknown, not zero`;
  `Refusals by limit &mdash; since restart`; `>Decisions<`, `>Avg req/min<`, `>Last limit seen<`;
  `@click="clearRateLimitFilters">Clear filters`; new-rule drawer section order; footer buttons;
  `x-text="rlTimelineView.sourceText"`; 9–12 `<details class="rl-help"` page-wide.
- Pinned **absent**: `Limit in force`, `Limits being hit`, `Nothing has been refused in this window`,
  `class="stat-row"`, `<tr class="rl-row" … tabindex`, `.rl-row.off td { opacity`.
  → Do not name anything "Limit in force" / "Limits being hit". Any task below that touches a
  pinned line must update the pin in the same commit.

---

## 4. Current page anatomy (top to bottom)

1. Load-error notice / skeleton / read-only notice (only after a refused save).
2. **Status card** — master switch + title (bound to the *draft*), adaptive switch, "changes apply
   without a restart"; stats Rules · Windows · Windows active now · Next change; full-width action
   row: Guide, EN/FA toggle, Activity (scroll), Reload.
3. Red notice when `rlDraft.enabled` is false; 429/Retry-After lede.
4. **Rules** — header + help + New rule; toolbar (search, Who, Model, Scheduled/Off chips); empty /
   no-match states; table On · Who · Model · Limit (rpm/burst/streams) · Now · Refused · chevron;
   footnote explaining Refused, `*`, `—`.
5. **Protective limits** — two cards.
6. **Tenant tiers** — default + plan cards (big numerals), Add plan.
7. **Activity** — window picker + Refresh + "As of"; totals; Traffic by subject (4 tabs, table);
   Refusals by limit — since restart (table, rows link to rules); Load-aware adjustments (table, only
   when present); buckets/back-off footer.
8. **Calendar** — range + zone selects; timeline; legend; Preview at; side card "Coming up".
9. **Sticky save bar** (only when dirty) → below it, in normal flow at page end, the review card
   (`rlReviewOpen`) and the save help `<details>`; then `rateLimitFieldError` and the
   "open the rule this error is about" link.

---

## 5. UX/UI findings

### F1
**Issue:** The header and the red "disabled" notice describe the *draft* master switch as the live
state.
**Evidence:** `rlStatusView.title` reads `this.rlDraft.enabled` (APP:7841-7853);
`rateLimitsDisabled` = `!this.rlDraft.enabled` (APP:7707) drives "Rate limiting is disabled — clients
can send unlimited requests" (HTML:1839). `setRateLimitEnforcement` flips the draft before save.
**Impact:** Between confirming the switch and pressing Save, the page states something false about
production in its most prominent text. The reverse also holds: switching it back on in the draft
hides the red notice while the gateway is still unenforced. Breaks core question 1.
**Recommended improvement:** Title and red notice read `rateLimits.enabled` (saved). When draft ≠
saved, append a `.tag.accent` "unsaved: will stop enforcing on save" / "…will resume enforcing on
save". The switch itself keeps showing the draft value (it is the control).
**Implementation considerations:** Add `savedEnabled`, `pendingEnforcementText` to `rlStatusView`;
add `rateLimitsSavedDisabled` getter; keep `rateLimitsDisabled` for callers that mean the draft.
Update `rate-limit-save-state` tests.
**Dependency:** Frontend-only
**Priority:** P0

### F2
**Issue:** A changed rule's row shows draft numbers as "Limit" and replaces its schedule state with
"unsaved", so nothing on the row says what is enforced now.
**Evidence:** `rlRuleRow` (APP:8005-8086): `force = off ? … : changed ? {sub:'unsaved · in force once
saved'} : rlForceFor(rule)`; `now` precedence `off → unsaved → window active → …`. When the draft is
dirty `loadRateLimitSchedule` fetches `/schedule/preview` for the **draft** (APP:3848), so
`rlStatusFor` answers for the draft for *every* row, and the status card's "Windows active now" and
"Next change" describe the draft too.
**Impact:** During an incident an operator edits one rule and loses the live state of all of them
until they save or discard. Breaks questions 5 and 8.
**Recommended improvement:** Keep two schedule reads: `rlScheduleSaved` (always `GET /schedule`,
drives the "Enforcing now" column, status summary and the 30 s tick) and `rlSchedule` (draft preview
when dirty; drives Calendar, Coming up, Preview at, drawer "This week"). In the row: Limit shows the
draft tier with a second line `was 600 / 60 / 10` when changed; "Enforcing now" always comes from
`rlScheduleSaved` + saved `enabled`; the unsaved marker becomes its own small tag in the Who cell
(plus the existing left accent bar).
**Implementation considerations:** `GET /schedule` with `take=1` and default range is cheap; reuse
the transition timer on the saved report. New rules (no saved counterpart) show "not saved yet".
**Dependency:** Existing API/data
**Priority:** P0

### F3
**Issue:** Activity never refreshes while the page is open, and its age is shown only inside the
Activity section.
**Evidence:** `loadRateLimitUsage` callers: `setSettingsSubTab` (>60 s), `setRateLimitUsageMinutes`,
Refresh, `scrollToRateLimitUsage`, `openRateLimitRule` (only if never loaded). `syncPoll`
(APP:1164-1211) has no RL branch. `asOfText` is rendered only at HTML:1999, while the rules table's
Refused column (HTML:1918) and the rule drawer read the same report.
**Impact:** An operator watching the rules table during throttling sees frozen refusal counts with
no cue that they are old. The endpoint is documented as "cheap enough to poll and safe to call
during an incident" (`AdminRateLimitEndpoints.cs:235`).
**Recommended improvement:** In `syncPoll`, on every 15th tick (30 s), when
`tab==='settings' && isSettingsLimits && !document.hidden`, call `loadRateLimitUsage()` unless a
request is in flight. Show "Activity updated 12 s ago" (reuse `updatedLineText` wording incl. "the
last refresh failed, showing the previous result") in the operational summary (§8) and as the
`rl-th-sub` title of the Refused header. Add an "Auto-refresh" checkbox in the Activity header using
the Logs/Errors `label.checkbox.field-inline` pattern, default on.
**Implementation considerations:** `_rlUsageSeq` already makes overlapping loads safe. Change the
stale notice from `role="alert"` to `role="status"` or a failing poll re-announces every 30 s.
2 req/min is negligible against the control-plane budget (**unknown** exact budget per key; the
2026-09-14 review cites ~600 rpm).
**Dependency:** Frontend-only
**Priority:** P0

### F4
**Issue:** The pre-save review lists names only, does not mark destructive changes, and opens out of
sight.
**Evidence:** `rlDirtyView.items` are strings like `'plan standard'`, `'Model gpt-4'`,
`'deleted rule …'` (APP:7794-7833). The review card is after the sticky bar in normal flow
(HTML:2194); the bar is `position: sticky; bottom` (CSS:2100), so from mid-page the card appears
below the viewport (**verify in browser**). "Review changes" has no `aria-expanded`/`aria-controls`.
**Impact:** Save replaces the whole rule set in production; the operator cannot see `600 → 60 rpm`,
cannot tell a deletion from a tweak at a glance, and may not see the review at all.
**Recommended improvement:** Render the review *inside* the save bar container, above the buttons
(bar grows upward; `max-height: 50vh; overflow:auto`). Each item becomes a row: kind tag
(`new` `.tag.accent` / `changed` `.tag` / `switched off` `.tag.warn` / `deleted` `.tag.level-error` /
`enforcement off` `.tag.level-error`), subject, `before → after` in mono using `rlTierText`, window
count delta (`2 → 3 windows`), and an **Undo** `.rl-link` that restores that item from `rateLimits`.
Destructive items sort first. Save button label becomes "Save 3 changes".
**Implementation considerations:** Both payloads are already built in `rlDirtyView`. Undo for a rule
= replace/insert/remove that identity in `rlDraft.rules` from the baseline clone, then
`queueRateLimitScheduleRefresh()`. Keep `items[].text` for the bar's one-line detail. Add
`aria-expanded` + `aria-controls="rl-review"`.
**Dependency:** Frontend-only
**Priority:** P0

### F5
**Issue:** After a 409 the page says someone else changed the configuration but not what they changed.
**Evidence:** `rlRecoverFromConflict` refreshes the baseline and opens the review, which then diffs
draft vs *new* baseline; the old baseline is discarded in `applyRateLimitsData`.
**Impact:** The operator must decide whether to overwrite a colleague's change without seeing it.
**Recommended improvement:** Before refreshing, keep `const previous = this.rateLimits`. After it,
compute `diff(previous, rateLimits)` with the same item builder as F4 and show it as a second group
in the review: "Changed by someone else since you loaded" (read-only rows), above "Your changes".
Items touching the same identity in both groups get a `.tag.warn` "also edited by you".
**Implementation considerations:** Extract the diff out of `rlDirtyView` into
`rlDiff(beforePayload, afterPayload)`. Clear the "theirs" group on successful save/discard/reload.
**Dependency:** Frontend-only
**Priority:** P1

### F6
**Issue:** Master-switch-off and adaptive reduction do not reach rule rows, protective cards or
tier cards.
**Evidence:** `rlRuleRow`/`rlForceFor` never read `enabled` (global) or `rateLimitUsage.adaptive`.
Adaptive factors are only in the Activity table (`rateLimitAdaptiveRows`), which is rendered only
when non-empty. `adaptive.enabled`, `adaptive.lastEvaluatedUtc`, `models[].updatedUtc` are unused.
**Impact:** Question 8 ("is current behaviour caused by schedules or adaptive limiting?") requires
scrolling to Activity and mentally joining by model id. With enforcement off, rows still read
"window active".
**Recommended improvement:** (a) When saved `enabled` is false, every "Enforcing now" cell reads
"not enforced" (`.tag.level-error`) with sub "master switch off"; tiers/protective cards get the
same tag. (b) For `model`-scope rules whose model id appears in `adaptive.models` with
`factor < 1`, add `.tag.warn` "adaptive ×0.70" and sub "≈ 420 of 600 rpm · <reason>". (c) Show
"Adaptive: evaluated 8 s ago" in the summary when adaptive is on.
**Implementation considerations:** Whether the governor also scales `tenant_model`/`api_key_model`
buckets is **unknown** — check `src/33pol.Policy/RateLimiting/AdaptiveRateLimitGovernor.cs` and the
resolver before tagging pair scopes; `rlLimitBand(…, {scaled})` (APP:8707) suggests the console
already has an opinion. Build a `Map(modelId → row)` once per usage load.
**Dependency:** Existing API/data
**Priority:** P1

### F7
**Issue:** The status card answers "how much is configured", not "is traffic being refused".
**Evidence:** Stats are Rules, Windows, Windows active now, Next change (HTML:1825-1828). Refusal
rate and refusing subjects are in section 5 of 6. A full-width action row holds Guide, EN/FA toggle,
Activity, Reload.
**Impact:** Questions 1, 7, 9 need three scroll positions. Rule and window counts duplicate the
filter chips' "All n".
**Recommended improvement:** See §8. Replace the stats with an operational strip built from
`.mini-stat` buttons that scroll/filter to their source.
**Implementation considerations:** All values exist in `rateLimitUsage` and `rlScheduleSaved`.
**Dependency:** Existing API/data
**Priority:** P1

### F8
**Issue:** "—" carries three meanings and rule/global/window "not enforcing" states use three
unrelated words.
**Evidence:** `rpm: '—'` = RPM 0, rule does not limit rate (APP:8056); Now `—` = no schedule, base
applies (HTML:1915, explained only in `sr-only`); Refused `—` = unknown (footnote). States: rule
"off", global "not enforced"/"disabled", window "paused"; drawer windows use `.status-chip` Active /
Next / Expired / Skipped / Not saved yet while rows use `.tag`.
**Impact:** Scanning a column, a dash may mean "fine", "nothing" or "we don't know".
**Recommended improvement:** Adopt the status model in §7.3. Concretely: Now/Enforcing cell never
shows a dash — it shows the effective tier text and the word "base"; RPM 0 shows "no rate limit"
(or "plan rate" for tenant) instead of "—"; "—" is reserved for unknown and always has a visible
`?`-less text alternative in `sr-only` ("unknown").
**Implementation considerations:** `rate-limit-rule-list.test.js` pins `—` ≠ 0 for Refused — keep
that; update the rpm expectations.
**Dependency:** Frontend-only
**Priority:** P1

### F9
**Issue:** Row computation is quadratic and unpaginated against a 2,000-rule ceiling.
**Evidence:** `rlRuleRows` maps every draft rule through `rlRuleRow`, which does
`rateLimits.rules.find(...)` (linear), two `buildRateLimitsPayload` + `JSON.stringify` calls,
`rlStatusFor` (linear over schedule rules), `rlRefusalsView` (filter over ≤200 violations),
`rlRuleKey` lookups. The getter re-runs on any dependency change: each search keystroke (no
debounce on `mdl.rlFilterText`), `_rlTick`, usage reloads. `rlScopeChips`, `rlStatusView`,
`rlTimelineView` iterate again. All rows are in the DOM.
**Impact:** At a few hundred rules typing in the search box will stutter (**unmeasured — verify**);
at 2,000 it is ~4 M comparisons per evaluation. Auto-refresh (F3) makes this more frequent.
**Recommended improvement:** (1) Build once per input change, cached by reference: `savedByIdentity`
Map (with pre-stringified canonical rule), `scheduleByIdentity` Map (per report), `refusalsByKey`
Map (per usage report), `keysById` Map. (2) Debounce the search 200 ms (`x-model.debounce.200ms`).
(3) Adopt the Usage page's client "Show 100 more" (`rlRuleLimit = 100`, reset on filter/sort
change) with the count hint "Showing 100 of 412 rules". No virtualisation: nothing else in the
console uses it and 100-row pages keep the DOM under the ~5,500-node concern documented at APP:216.
**Implementation considerations:** Alpine getters cannot memoise by themselves; store the maps on
non-reactive underscore fields keyed by the source object identity (`_rlIdxFor === this.rateLimits`).
**Dependency:** Frontend-only
**Priority:** P1

### F10
**Issue:** Timeline labels, "Coming up" and "Preview at" show stored targets (key GUIDs) while the
table shows key names.
**Evidence:** `String(rule.target).replace('|', ' · ')` at APP:8222, :8254, :8293 vs
`rlTargetDisplay`/`rlKeyName` in `rlRuleRow`.
**Impact:** "a3f1…c9 · gpt-4 → 120 rpm in 12 m" cannot be matched to a row without opening rules.
**Recommended improvement:** Use `rlTargetDisplay(scope, target)` in all three, with the raw target
as `title`. Make "Coming up" rows and preview rows buttons that open the rule (timeline labels
already do).
**Implementation considerations:** None.
**Dependency:** Frontend-only
**Priority:** P1

### F11
**Issue:** The calendar can be silently partial.
**Evidence:** The report carries `occurrencesTotal`/`occurrencesTruncated` (cap 2,000); APP never
reads them (0 matches). Transitions truncation *is* surfaced (`rlTransitionsView.truncatedNote`).
**Impact:** With many scheduled rules on a 30-day range, bands stop appearing with no notice.
**Recommended improvement:** Add `rlTimelineView.truncatedNote` — "Showing the first 2,000 of N
occurrences; narrow the range." — as a `.hint` under the legend.
**Implementation considerations:** Mirror the transitions wording.
**Dependency:** Existing API/data
**Priority:** P1

### F12
**Issue:** Activity tables cannot be sorted, searched or connected back to rules.
**Evidence:** `t-rl-usage` has plain `<th>`; order is whatever the server returns; subject rows are
not interactive (HTML:2042-2050). Only the refusals table links to a rule (`v.open`).
**Impact:** "Which key is being refused most?" relies on server order; from a refusing tenant there
is no path to the rules that govern it.
**Recommended improvement:** Add `.th-sort` to Decisions, Refused, Avg req/min (default Refused
desc, then Decisions desc). Add a `.filter-field.narrow` above the table. Add a trailing
`.row-actions` cell with two `.icon-btn`s: "Show rules for this subject" (sets `rlFilterText` to the
subject's display name, scrolls to Rules) and, for API-key rows when editable, "Limit this key"
(`startRateLimitNewRule({ key })`, same as the API keys page).
**Implementation considerations:** Reuse `rlSortView`'s column factory with a second state pair
(`rlUsageSortKey/Dir`).
**Dependency:** Frontend-only
**Priority:** P1

### F13
**Issue:** Returned per-subject fields are hidden or compressed into one text column.
**Evidence:** `admitted` is unused per subject; `configuredRpm`/`effectiveRpm` appear only as "Last
limit seen" text; `Utilization` is unused; `store.streamPartitions` unused.
**Impact:** No at-a-glance pressure indication per subject.
**Recommended improvement:** Add a "Load vs last limit" column to *Traffic by subject* using
`.load-track/.load-fill` (`is-hot` ≥ 0.8, `is-over` ≥ 1) + the percentage as text, only when
`effectiveRpm > 0`; tooltip and footnote keep the existing caveat (window average ÷ the tightest or
refusing limit on the subject's latest request — may be a wider limit). Do **not** put this in the
Rules table and do **not** count "subjects near limit" in the summary: by construction
(`Ring.Add` last-writer-wins) the denominator is not the rule's own limit (see §15 B1).
Add `admitted` only in the rule drawer's traffic line; the table already has Decisions and Refused.
**Implementation considerations:** Compute client-side (`requestsPerMinute / effectiveRpm`); the
JSON may or may not serialise the computed `Utilization` property (**unknown** — check the payload).
**Dependency:** Existing API/data
**Priority:** P1

### F14
**Issue:** The console reports "no decisions from X" when the tracker may simply not be tracking X.
**Evidence:** Tracker dimensions stop accepting new keys at `UsageReportMaxKeys` (default 500) with
no eviction; `rlSubjectTrafficFor` returns `absent` → "No decisions recorded from … in this window"
and `rlRefusalsFor` returns `ok / 0` when the violations list is short.
**Impact:** On a busy gateway, a newly created key's rule can show `0` refused and "no decisions"
while being refused. The console cannot detect this today.
**Recommended improvement:** Backend: add `trackedKeys`/`maxKeys`/`droppedKeys` per dimension to the
report (§15 B3). Frontend, once available: when a dimension is full, `absent` and `ok/0` become
"unknown — the activity tracker is full (500 subjects); restart clears it".
**Implementation considerations:** Until the backend lands, no honest frontend fix exists; do not
guess from `store.requestPartitions` (different table).
**Dependency:** Backend/API required
**Priority:** P1

### F15
**Issue:** Read-only mode is discovered only by a failed save.
**Evidence:** `rlReadOnlyReason` is set only in `rlSaveFailed` (503/403). The 403 branch is
unreachable (one `Operator` policy on the whole group).
**Impact:** On a gateway without a database an operator can build a complete draft and lose the time.
**Recommended improvement:** Backend: add `writable: bool` (+ `readOnlyReason`) to `GET /` (§15 B4);
console sets `rlReadOnlyReason` on load. Frontend now: delete the unreachable 403 copy path or leave
it, but do not design around it.
**Implementation considerations:** `RateLimitConfigAdminService` already knows whether an
`IRateLimitSettingsRepository` resolves (:209-215).
**Dependency:** Backend/API required
**Priority:** P1

### F16
**Issue:** Section order follows configuration type, not the operator's questions; baselines take
the most space for the least-changed data.
**Evidence:** Order is Rules → Protective → Tiers → Activity → Calendar. Tier cards use
`.rl-bignum`-style numerals in a card each (HTML:1975-1986); protective limits are two more cards.
**Impact:** Activity — the troubleshooting surface — is ~3 screens down on a laptop (**verify**).
**Recommended improvement:** Order per §6. Merge Tiers and Protective limits into one "Baselines"
section rendered as two compact tables (same columns as the Limit cell), placed after Activity.
Add an in-page anchor row under the summary: `Rules (n) · Activity · Schedule · Baselines`, built
from `.rl-link` buttons using the existing `scrollToRateLimitUsage` approach.
**Implementation considerations:** Help tests count `<details class="rl-help"` (9–12) and pin
`rlHelpView.open.tiers/scopes` buttons — move them with their sections. Card → table changes
`rate-limit-rule-list` protective tests.
**Dependency:** Frontend-only
**Priority:** P1

### F17
**Issue:** No result count; missing operational filters that the data does support.
**Evidence:** Chips show per-group totals but there is no "Showing x of y". Filters: Who, Model,
Scheduled, Off.
**Impact:** After filtering, it is unclear how much is hidden; "show me what is refusing" needs a
sort plus reading.
**Recommended improvement:** Add a `.hint` count ("12 of 340 rules"). Add flag chips: **Refused**
(`refusals.known && hits > 0`; chip title "refused at least once since the gateway started"),
**Window active** (from `rlScheduleSaved.activeWindow`/suspended), **Unsaved** (`changed`). Make
flags multi-select (`rlFilterFlags` Set) since Scheduled+Refused is a real query. Do not add
"near limit" or a utilisation threshold (unsupported — F13).
**Implementation considerations:** `toggleRateLimitFlagFilter` currently enforces single-select;
tests in `rate-limit-rule-list` cover it.
**Dependency:** Frontend-only
**Priority:** P1

### F18
**Issue:** On narrow screens the table cannot be sorted.
**Evidence:** `@media (max-width: 48rem)` hides `thead` (CSS:2225); sort buttons live in `thead`.
**Impact:** "Most refused first" is unavailable on a phone/tablet during an incident.
**Recommended improvement:** Add `<select class="inline-select" aria-label="Sort rules">` in the
toolbar, shown only ≤48rem (`.rl-sort-select`), options Who / Model / RPM / Refused ↑↓, bound to the
same `rlSortKey/Dir`.
**Implementation considerations:** `x-model` on a composite value needs a getter/setter pair in the
`mdl` proxy (pattern at APP:5362).
**Dependency:** Frontend-only
**Priority:** P2

### F19
**Issue:** Protective cards give no activity signal and do not say why.
**Evidence:** `rlRefusalsFor` returns `untracked` for both scopes; cards omit Refused entirely.
`auth_failure` is only in Prometheus (`gateway_rate_limit_rejections_total{reason="auth_failure"}`);
anonymous refusals land as `tenant`-scope violations keyed `anon:<block>`.
**Impact:** Operators look for a number that does not exist.
**Recommended improvement:** In the Baselines table add a muted note per row: Failed sign-ins —
"refusals are in /metrics only" with a link to Settings → Observability; Anonymous — "refusals
appear under Activity as Anonymous · <address>", with a button that filters the refusals table.
**Implementation considerations:** Copy must also go into the EN/FA help module if placed in help.
**Dependency:** Frontend-only
**Priority:** P2

### F20
**Issue:** Model and Tenant × model tabs can be empty for a reason the empty text does not give.
**Evidence:** RL-025: without any model-scoped rule, `modelId` is null on admission;
`rlUsageSubjectView.emptyText` says "Nothing recorded under this heading in this window."
**Impact:** Looks like a bug or like no traffic.
**Recommended improvement:** When the tab is `model`/`tenantModel`, totals > 0, rows empty, and the
draft has no `model`/`*_model` rule, show: "Requests are attributed to a model only while at least
one per-model rule exists."
**Implementation considerations:** Confirm RL-025 is still open at HEAD before shipping the copy
(**unknown** — the review predates several fixes).
**Dependency:** Frontend-only
**Priority:** P2

### F21
**Issue:** Accessibility gaps specific to this page. See §13 for the list and fixes.
**Evidence:** `<th class="rl-col-on">On</th>` has no `scope`; `*` is `aria-hidden` with no text
alternative; `∞`/`—` rely on `title`; stale notice is `role="alert"`; Review button lacks
`aria-expanded`; bars (proposed) need text equivalents.
**Impact:** Screen-reader users miss qualifiers that change the meaning of a number.
**Recommended improvement:** §13.
**Implementation considerations:** `<th class="rl-col-on">On</th>` is pinned literally — update pin.
**Dependency:** Frontend-only
**Priority:** P2

### F22
**Issue:** Header action row spends a full row on secondary controls.
**Evidence:** `.rl-status-actions { flex: 1 1 100%; border-top }` (CSS:1935) with Guide, EN/FA
segment, Activity, Reload. The language toggle also exists inside the help drawer and every inline
help block.
**Impact:** Vertical space before the first rule; language toggle is prominent though it affects
help text only.
**Recommended improvement:** Move Guide + Reload to the right of the summary strip as `.icon-btn`s
with labels at ≥64rem; drop the header EN/FA segment (kept in the help drawer and inline blocks);
"Activity" is replaced by the anchor row (F16).
**Implementation considerations:** Help tests pin `rlHelpView.open.overview` — keep the button.
**Dependency:** Frontend-only
**Priority:** P2

### F23
**Issue:** The window list in the rule drawer needs three fields read together to understand a window.
**Evidence:** `.rl-win` shows name+kind, tier, when, state as four spans (HTML:2618-2623);
`rlWindowWhen` omits the tier.
**Impact:** Minor; slows review of multi-window rules.
**Recommended improvement:** One sentence per window:
`Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm · 20 burst · 4 streams` (or `→ paused`), with the
state chip right-aligned and, when the server reports it, "outranked by <name>" from the window
preview (`OutrankedBy`) — never computed client-side.
**Implementation considerations:** `rlDaysText` already collapses day ranges. Outrank info is only
available from `/windows/preview` per candidate; show it in the window pane only (as today) unless a
per-rule call is acceptable (one POST per window on drawer open — avoid).
**Dependency:** Frontend-only
**Priority:** P2

### F24
**Issue:** Usage `take` is fixed at 200 while up to 2,000 rules may need attribution.
**Evidence:** `rlUsageTake() { return 200; }`; endpoint allows 1,000. Rows beyond become
`truncated` → "—".
**Impact:** More "—" than necessary on large installs.
**Recommended improvement:** `take = clamp(rules.length, 200, 1000)`.
**Implementation considerations:** Payload size ×5 worst case; still in-memory and cheap.
**Dependency:** Existing API/data
**Priority:** P2

**Deliberately not changed (works, tested, recently redesigned):** new-rule drawer structure and
comboboxes; window form + server preview; conflict/draft state machine; confirm-only-on-ambiguous-exit
dismissal; delete-confirm wording; help system; the narrow-width card layout of rule rows.

---

## 6. Proposed information architecture

| # | Section | Why here |
|---|---|---|
| 0 | Load error / read-only notice (on load once B4 exists) | Must precede any editing. |
| 1 | **Operational summary** (saved enforcement switch + state, refusals in window, refusing subjects, adaptive, windows active, next change, freshness; Guide/Reload) | Answers Q1, Q7, Q8, Q9 in one row. Replaces config counts. |
| 2 | Anchor row: Rules (n) · Activity · Schedule · Baselines | The page is long by nature; third-level nav without a third tab bar. |
| 3 | **Rules** (toolbar, table, footnote) | Primary surface: configured → enforcing now → refused, per rule. Q2–Q5. |
| 4 | **Activity** (window totals; traffic by subject with load bar; refusals by limit since restart; adaptive adjustments; tracker occupancy) | Traffic → pressure/refusals. Directly after the rules it explains. |
| 5 | **Schedule** (timeline, Coming up, Preview at) | Future changes. Renamed from "Calendar" to match the chips ("Scheduled") and help. |
| 6 | **Baselines** (Tenant tiers table + Protective limits table) | Configured policy that changes rarely; compact tables instead of 2 + N cards. |
| 7 | Sticky **save bar with in-bar review** | Unsaved changes, always reachable. |

Configuration, monitoring and scheduling stay on one page (they share the draft and cross-link),
but each gets exactly one section; the Rules table is the only place the three meet, and there
each has its own column.

---

## 7. Proposed Rules table

### 7.1 Schema

| Column | Purpose | Presentation | Sort | Filter | Responsive behavior |
| ------ | ------- | ------------ | ---- | ------ | ------------------- |
| On | Draft on/off for this rule | `.rl-switch.sm` (unchanged); `scope="col"` | — | chip **Off** | Stays; grid area `on` |
| Who | Subject | Line 1 name (`.rl-target`, title = stored target); line 2 kind + short id (`.rl-scope`); draft marker tag `new` / `unsaved` after the name | A–Z | search; chips Who; chip **Unsaved** | Stays; area `who` |
| Model | Model coverage | model id or muted "All models" | A–Z | search; chips Model | Stays; area `model` |
| Limit (configured) | Draft tier | three right-aligned mono numbers rpm / burst / streams; RPM 0 → "no rate limit" / "plan rate"; streams 0 → "∞" + sr-only "unlimited"; when changed, second line `was 600 / 60 / 10` muted | by rpm | — | ≤48rem: inline with unit words (existing `.rl-limit-nums i`) |
| Enforcing now | What production applies *now*, from saved config + `rlScheduleSaved` + adaptive | Line 1 effective tier text (mono). Line 2 source: `base` · `window: off-peak · until 17:00 (in 2 h)` (`.tag.live`) · `paused by maintenance · until …` (`.tag.warn`) · `adaptive ×0.70` (`.tag.warn`) · `off` (`.tag.warn`) · `not enforced` (`.tag.level-error`) · `not saved yet` (`.tag.accent`) · `unknown — schedule unavailable` (`.tag.level-error`) · `window skipped: <error>` (`.tag.level-error`). If windows exist and none active: muted `next: off-peak Mon 09:00`. | — (not meaningfully ordinal) | chips **Scheduled**, **Window active** | ≤48rem: area `now`; ≤64rem: line 2 wraps |
| Refused | Requests this limit refused | number; `—` + sr-only "unknown"; tenant rules append visible "†" with sr-only "tenant's whole allowance"; header sub-label "since restart"; header title carries activity age | high → low default on first click; unknown last | chip **Refused** | ≤48rem: area `refused` with "Refused" word label (existing `.rl-cell-label`) |
| Traffic (≥ 80rem only) | Subject-level context for the rule's subject | `3.4 req/min · 12 refused` muted, header "Subject traffic · last 60 min", title explains subject-level; blank when `nosection`; "—" when unknown | by window refused | — | Hidden < 80rem (available in drawer) |
| (open) | Open rule | chevron `.icon-btn` with full `aria-label` (unchanged) | — | — | Stays; area `open` |

No utilisation column: not attributable per rule with current data (F13, §15 B1).

### 7.2 Behaviour
- **Row interaction:** unchanged — cell click and chevron open the drawer; the switch cell stops
  propagation; rows stay non-focusable (test-pinned); the chevron button is the keyboard target.
- **Row actions:** none added. Delete stays in the drawer next to the explanation of "switch off
  instead". Toggle stays instant and draft-only (documented rationale at APP:4133).
- **Draft indicators:** left accent bar (existing `.rl-row.changed`) + text tag in Who (`new` /
  `unsaved`) + `was …` line under Limit. Deleted rules are not shown as rows; they appear in the
  review with Undo.
- **Detail:** rule drawer. Add at its top a two-line "Now in production" / "After you save" block
  when the rule is changed (values from saved rule + `rlScheduleSaved` vs drawer working copy).
- **Pagination:** client-side "Show 100 more" (F9) + "Showing 100 of 412 rules" hint; reset to 100
  when filters/sort change. No virtualisation.
- **Default sort:** Who A–Z (unchanged). While any limit has refused since restart, the summary's
  "limits refusing" stat sets sort = Refused desc + chip Refused when clicked.

### 7.3 Status model (page-wide)

Three independent axes, never merged into one badge:

| Axis | Values | Visual | Where |
|---|---|---|---|
| **Configuration** | On · Off · (global) Not enforced | switch state + word; `.tag.warn` "off"; `.tag.level-error` "not enforced" | On column, Enforcing now, summary |
| **Effective source** | Base · Window: *name* · Paused by *name* · Adaptive ×*f* · Window skipped · Unknown | `.tag` (muted for base, `.live`, `.warn`, `.warn`, `.level-error`, `.level-error`) + sentence | Enforcing now, drawer force bar, Baselines |
| **Draft** | — · New · Unsaved · (Deleted, review only) | `.tag.accent` + left accent bar | Who cell, review |
| **Data quality** (activity only) | Fresh · Stale (refresh failed) · Unavailable · Unknown | "updated 12 s ago" text · `.tag.warn` "stale" · `.hint` · "—"+sr text | summary, Activity header, Refused |

Vocabulary rules: "off" = one rule's switch; "not enforced" = master switch; "paused" = a suspend
window; never "disabled"/"inactive". "Refused" everywhere (not rejected/blocked/violations) in UI
copy. Window states in the drawer keep `.status-chip` (Active / Next … / Expired / Skipped / Not
saved yet) — chips for schedule objects, tags for rule rows. No state is colour-only: every tag has
a word; dots (`.rl-dot`) stay `aria-hidden` decorations next to text.

Not introduced: "Near limit", "Overridden", "Healthy/Constrained" badges per rule — no reliable
per-rule data.

---

## 8. Proposed operational summary

One `.card` row: master switch + title on the left, a `.mini-stats` strip of **buttons** in the
middle, Guide/Reload on the right.

| Element | Source | Why it belongs |
|---|---|---|
| Title: "Rate limits are enforced" / "…are **not** enforced" + pending tag | `rateLimits.enabled` (saved) vs draft (F1) | Q1. The only thing that makes every other number moot. |
| Adaptive: "on · evaluated 8 s ago · 2 models reduced" / "off" | draft switch; `adaptive.lastEvaluatedUtc`; `adaptive.models.filter(factor<1)` | Q8. Click → Activity adaptive table. |
| **Refused · last 60 min**: `1,204 (2.1 %) · 1,100 rate · 104 streams` | `totals.rejected`, `requests`, `rateRejected`, `concurrencyRejected` | Q7; `.mini-stat.warn` when > 0. Click → Activity. |
| **Refused subjects · last 60 min**: `3 keys · 1 tenant` | count of `byApiKey`/`byTenant` rows with `rejected > 0` (windowed, reliable; "200+" when the section is full) | "Who is hurting now" without implying per-rule attribution. Click → Traffic by subject sorted by Refused. |
| **Limits that refused · since restart**: `4` | `violations.length` ("200+" if full) | Bridges to the Rules table; click → chip Refused + sort Refused desc. Labelled "since restart" so it is not read as current. |
| **Windows active now**: `2` | `rlScheduleSaved.rules.filter(activeWindow)` | Q8. Click → chip Window active. |
| **Next change**: `in 12 m · Mon 09:00` | min `nextChangeAt` of `rlScheduleSaved` | Q9. Click → Schedule. |
| Freshness: "Activity updated 12 s ago" / `.tag.warn` stale / "Activity tracking is not enabled" | `rateLimitUsageLoadedAt`, error, 503 | Every number above depends on it. |

Removed: Rules count, Windows count (duplicated by chip counts and the anchor row).
Overall reading — healthy / constrained / refusing — is **text derived from these**, not a new
badge: title suffix "· nothing refused in the last 60 min" / "· refusing 2.1 % of requests".
No "subjects near limit" (F13). No sparklines (no series in the report — §15 B2).

---

## 9. Activity / monitoring improvements

- **Three time bases stay visually separate** (already true; keep the sub-headings and tags):
  *window* (totals, traffic by subject), *since restart* (refusals by limit), *current* (adaptive,
  tracker occupancy). Persisted configuration never appears inside this card.
- **Auto-refresh** every 30 s via `syncPoll` + checkbox (F3); relative "updated … ago" using
  `_nowTick`; stale → `.tag.warn` + `role="status"` notice with Retry.
- **Traffic by subject:** sortable headers, narrow search, "Load vs last limit" bar column with
  caveat, row actions "Show rules" / "Limit this key" (F12, F13). Keep 4 tabs. Keep the
  truncation line; raise `take` (F24). Better empty text for model tabs (F20).
- **Refusals by limit:** keep as is (already links to rules); add `.th-sort` on Refused; show
  scope-less rows (plan-tier refusals with no rule) with a link "Edit tier" → opens the tier drawer
  when `rlTenantLabel` resolves a plan (**only if** the plan is known from `overviewTenants`).
- **Adaptive adjustments:** always render the sub-heading when adaptive is on, with "No model is
  being reduced right now · evaluated 8 s ago" as the empty line (today the block vanishes, which
  reads as "feature absent").
- **Footer:** add stream partitions: "412 of 10,000 request buckets · 38 stream slots tracked ·
  0 callers held to a longer retry".
- **Visualisation:** only `.load-track` bars. No charts until a series exists.

---

## 10. Scheduling improvements

- Rename section "Calendar" → "Schedule"; keep timeline, Coming up, Preview at, range and zone.
- Names instead of ids everywhere; rows clickable (F10). Truncation note for occurrences (F11).
- Timeline rows: cap at 20 scheduled rules with "Show all n" (same idiom as transitions) — every
  scheduled rule is currently a row.
- Window sentence format in drawer list, timeline `title`, and legend (F23):
  `Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm · 20 burst · 4 streams`;
  `Sat 22:00 → Sun 06:00 (next day) · UTC → paused`; `From 3 Oct 09:00, open-ended → 60 rpm`.
- "Enforcing now" column and summary use the **saved** schedule (F2); the Schedule section keeps the
  `draft` tag and `sourceText` when showing the draft.
- Preview at: keep server evaluation; add two shortcuts next to the input — "Now" and "Next change"
  (fills `rlPreviewAt` from the next transition) — so the common checks need no typing.
- Priority/overlap: unchanged — the window pane already shows the server's
  `Overlaps / OutrankedBy / Outranks`; do not re-implement ranking client-side.

---

## 11. Editing and save UX

- **Draft:** unchanged model. Add visible separation of *saved/enforced* vs *draft* (F1, F2).
  Settings tab badge (existing) stays.
- **Validation:** unchanged client mirror + server messages; keep `rlSaveErrorView` link. When the
  server names a rule, also scroll the review to that item and mark it `.tag.level-error` "refused".
- **Review:** in-bar diff with kind tags, before → after, per-item Undo, destructive first (F4).
  Item wording examples:
  `deleted · API key "prod-chatbot" on gpt-4 · was 600 / 60 / 10 · 2 windows · Undo`
  `changed · Model gpt-4 · 600 → 300 rpm · burst 60 → 30 · Undo`
  `switched off · Tenant acme · tier kept · Undo`
  `enforcement off · every rule and window stops applying · Undo`.
- **Save:** button "Save n changes"; disabled while `rlSaving`/locked (unchanged). If the diff
  contains a deletion or enforcement-off, the review auto-opens on first Save click and the button
  becomes "Save n changes (1 deletion)" — a second click saves. No extra modal.
- **Conflict (409):** keep draft; show "Changed by someone else" group (F5); Save again is explicit.
- **Destructive:** delete confirm stays (it explains the reversible alternative). Master switch
  confirm stays. No new confirms.
- **Reload/Discard:** unchanged. Discard toast gains "Undo" only if trivial (**skip otherwise**).
- **Read-only:** on-load once B4 exists; until then unchanged.

---

## 12. Page states

| State | Behaviour |
|---|---|
| Initial loading | Existing skeleton table + "Loading rate limits…"; summary strip renders with `—` placeholders and no switches. |
| Refreshing (config) | Reload button disabled; page stays interactive (existing seq guards). |
| Refreshing (activity) | Refresh disabled; numbers stay; "updating…" replaces the age text; no layout shift. |
| Empty (no rules) | Existing `.empty-state` + New rule. Summary still shows activity (plan tiers refuse too). |
| No results | Existing `.rl-no-match` + Clear filters; count hint "0 of 340 rules". |
| API error (config, first load) | Existing `.notice.error` + Retry; nothing else renders. |
| API error (config, refresh) | Keep page; toast + notice (existing). |
| Partial data (schedule failed) | Enforcing now: "unknown — schedule unavailable" with Retry link in the column header hint; Schedule section shows its error notice (existing). Activity unaffected. |
| Partial data (usage truncated / tracker full) | Truncation lines (existing); "unknown — tracker full" once B3 exists. |
| Stale metrics | `.tag.warn` "stale" in summary and Activity; Refused header sub-label "since restart · stale"; `role="status"`. |
| Read-only (503 / `writable:false`) | Existing notice; switches disabled; drawers open in view mode (existing); New rule/Add plan/Add window hidden; save bar never appears. |
| Permission denied (401/403 on GET) | Existing "Connect with an Admin API key…"; page body hidden. |
| Disabled enforcement (saved) | Title red + `.notice.error`; every Enforcing-now cell "not enforced"; Schedule note "Schedules are paused, not deleted" (existing copy). Draft-only off → accent tag, no red notice (F1). |
| Unavailable usage (503) | Summary activity stats replaced by one muted line "Activity tracking is not enabled in this deployment"; Refused column "—"; Activity section shows the existing hint; no polling. |

---

## 13. Accessibility improvements

1. `<th class="rl-col-on">On</th>` → add `scope="col"` (update literal pin in tests).
2. Refused qualifier `*` is `aria-hidden` with no alternative → visible `†` + `<span class="sr-only">tenant's whole allowance, plan and rule together</span>`.
3. `∞` → add `<span class="sr-only">unlimited</span>` and `aria-hidden` on the glyph (rows, tier and protective numbers). `—` for unknown → same with "unknown". Stop using `—` for "no rate limit" and "base" (F8).
4. `title`-only explanations (rpmTitle, streamsTitle, nowTitle, refusedTitle) are not keyboard reachable → move the essential ones into `sr-only` text or the row's `aria-label`; keep `title` as a mouse nicety.
5. Stale-activity notice `role="alert"` → `role="status"` (required by auto-refresh).
6. "Review changes" → `aria-expanded`, `aria-controls="rl-review"`; review list gets `id`, `aria-label="Unsaved changes"`; Undo buttons get full names ("Undo deletion of …").
7. `.load-track` bars: `aria-hidden="true"` on the track; the percentage is rendered as adjacent text (console convention — no `role="progressbar"` anywhere today; do not introduce a one-off).
8. Summary stat buttons: `aria-label` with the full sentence ("1,204 requests refused in the last 60 minutes; go to Activity").
9. Anchor row → `<nav aria-label="Rate limits sections">`.
10. New mobile sort `<select>` gets a visible label at ≤48rem.
11. Headings: sections are `h3` under the Settings `h2` — keep; Baselines sub-tables get `h4`.
12. Contrast: `.tag.muted` uses `--text-tertiary` on transparent with dashed border — **verify** ≥ 4.5:1 in both themes before using it for the word "base".
13. Focus: after Undo in the review, focus moves to the next item's Undo or, if none, to Save. After "Show 100 more", focus moves to the first newly added row's chevron.

---

## 14. Responsive strategy

| Viewport | Behaviour |
|---|---|
| ≥ 80rem (large desktop) | All columns incl. Traffic. Summary in one row. Schedule 2-column (timeline + Coming up). Baselines as two side-by-side tables. |
| 64–80rem (laptop) | Traffic column hidden. Summary wraps to two rows (title+switches / stats). |
| 48–64rem (constrained / tablet landscape) | Existing: `.rl-cal` single column, stats full width. Baselines stack. Enforcing-now line 2 wraps. Activity subject table drops "Load vs last limit" bar, keeps the % text. |
| ≤ 48rem (tablet portrait / phone) | Existing card layout for rule rows (grid areas on/who/model/limit/now/refused/open). Add sort `<select>`. Chips already scroll horizontally. Summary stats become a 2-column grid of buttons. Activity tables: Decisions + Refused + subject only; other columns move to a second line inside the subject cell (same `.rl-cell-label` technique). Timeline label column 6rem (existing). |
| ≤ 560px | Drawers 100vw (existing). Save bar: review scrolls inside at `max-height: 60vh`; primary button full width (existing). |

Tables other than Rules keep `.table-wrap` horizontal scroll as the fallback, consistent with the
rest of the console.

---

## 15. Backend / data opportunities

**B1 — Per-limit usage (enables real utilisation, "near limit", windowed refusals).**
Missing: the tracker records per *subject*, and only the tightest/refusing scope's RPM last-writer-wins.
Why: Q6 cannot be answered per rule; "Refused" is cumulative only.
Change: in `RateLimitMiddleware` record, per applied rule `(scope, partition)`, admitted/refused into
a windowed ring (same `Ring` type), plus `Remaining/Limit` from `RateLimitAcquireResult` on the
latest decision. Report: `byLimit[]: {scope, key, requests, admitted, rejected, requestsPerMinute,
configuredRpm, effectiveRpm, remaining, limit}`. Bounded by the same `_maxKeys`.
Benefit: utilisation bar + "Near limit" filter in the Rules table; windowed Refused; summary
"limits near capacity".

**B2 — Time series.**
Missing: per-minute rings exist (`long[180]`) but `BuildReport` returns sums only.
Change: optional `series=1` → totals `{minute, requests, rejected}` for the window (≤180 points);
later per-limit for the drawer.
Benefit: reuse `sparkLine/sparkFill` for a refusals trend in the summary ("rising or settling?").

**B3 — Tracker saturation visibility.**
Missing: keys past `UsageReportMaxKeys` are dropped silently.
Change: add `tracker: {maxKeys, tenants, models, apiKeys, tenantModels, violations, dropped}`.
Benefit: honest "unknown — tracker full" instead of false zeros (F14).

**B4 — Writability on read.**
Missing: read-only is learnt from a failed PUT.
Change: `GET /` adds `writable` + `readOnlyReason`.
Benefit: read-only state before editing (F15).

**B5 — Protective-scope activity.**
Missing: `auth_failure` never reaches the tracker; anonymous is indistinguishable from a tenant.
Change: record both as their own violation scopes (partition = address block, already bounded).
Benefit: Refused numbers on the two protective rows.

**B6 — Change history.**
Missing: no endpoint returns the `rate_limits.update` audit entries (whether the general audit/log
page can filter them is **unknown**).
Change: `GET /admin/api/rate-limits/history?take=` from the audit store, or a deep link into Logs.
Benefit: "who changed this and when" from the rule drawer; pairs with the 409 flow.

---

## 16. Implementation plan

### Phase 1 — Core clarity, safety, and scalability

**1.1 Saved-vs-draft truth (F1, F2)**
- Objective: every "now" statement describes production.
- Files: APP (`rlStatusView`, `rateLimitsDisabled`, `loadRateLimitSchedule`, `rlForceFor`, `rlRuleRow`, `rlProtectiveCards`, `rlRuleDrawerView`), HTML:1807-1840, 1886-1920, CSS `.rl-*`.
- Approach: add `rlScheduleSaved` + loader; move the transition timer to it; split row view into `limit*` (draft) and `enforcing*` (saved); add `was …` line and draft tag.
- Dependencies: none. Risks: two schedule requests when dirty (cheap; debounce kept). Test pins on header markup.
- Acceptance: toggling the master switch in the draft leaves the title unchanged and shows a pending tag; editing a rule's RPM leaves its Enforcing-now cell and all other rows' cells identical to before the edit; status "Windows active now" is unchanged by draft edits; node tests updated and green.

**1.2 Activity freshness (F3)**
- Files: APP (`syncPoll`, `rlActivityView`, new `rlActivityAgeText`), HTML Activity header + summary.
- Approach: 30 s slot in `syncPoll`; checkbox `rlUsageAutoRefresh` (default true); `role="status"`.
- Risks: re-render cost at scale → do 1.4 first or together.
- Acceptance: with the page open and visible, `/usage` is requested every ~30 s; hidden tab → none; failure shows "stale" without losing numbers and without a repeated assertive announcement.

**1.3 Diff review in the save bar (F4)**
- Files: APP (`rlDiff` extracted from `rlDirtyView`, `undoRateLimitChange(id)`), HTML:2185-2212, CSS `.rl-savebar`, `.rl-review`.
- Acceptance: each item shows kind tag + before → after; deletions and enforcement-off first; Undo restores exactly that item and the count drops; review visible without scrolling when opened from any scroll position; first Save click with a deletion opens review instead of saving.

**1.4 Row indexing + paging (F9)**
- Files: APP (`rlRuleRows`, `rlRuleRow`, `rlStatusFor`, `rlRefusalsFor`, `rlScopeChips`), HTML toolbar + "Show 100 more".
- Acceptance: with a 2,000-rule fixture in the node harness, one `rlRuleRows` evaluation performs O(n) lookups (assert via call counters on `buildRateLimitsPayload` ≤ n+2); DOM shows 100 rows + "Showing 100 of 2,000 rules"; filter change resets to 100.

**1.5 Schedule truncation note + names (F10, F11)**
- Acceptance: a stub report with `occurrencesTruncated: true` shows the note; key rules show key names in timeline, Coming up and Preview.

### Phase 2 — Operational insight and workflow improvements

**2.1 Operational summary (F7, F22)** — files: HTML:1807-1838, APP `rlStatusView` → `rlSummaryView`. Acceptance: each stat matches §8's source; each is a button that lands on its section/filter; unavailable usage collapses to one line.

**2.2 Adaptive + master-switch in rows (F6)** — verify governor scope first. Acceptance: stub usage with `adaptive.models[{modelId:'m', factor:0.7}]` tags the `model:m` row "adaptive ×0.70 · ≈ 420 of 600 rpm".

**2.3 Status vocabulary (F8)** — acceptance: no `—` rendered for RPM 0 or base; Enforcing-now never empty.

**2.4 IA re-order + Baselines tables + anchor row (F16)** — acceptance: DOM order Summary → Rules → Activity → Schedule → Baselines; help `<details>` count still 9–12; protective/tier drawers open from table rows.

**2.5 Activity table tools (F12, F13, F24)** — acceptance: sort by Refused works; "Show rules" filters the rules table and moves focus to the filter input; load bar only when `effectiveRpm > 0`.

**2.6 Filters + count (F17)** — acceptance: Refused + Scheduled can be active together; count hint correct.

**2.7 Conflict "theirs" group (F5)** — acceptance: simulated 409 where the server changed rule X shows X under "Changed by someone else" with before → after.

### Phase 3 — Accessibility, responsiveness, and polish

3.1 §13 items 1–13. 3.2 Mobile sort select (F18). 3.3 Window sentence format (F23). 3.4 Protective
notes (F19), model-tab empty copy (F20). 3.5 Responsive second-line cells for Activity tables.
3.6 Timeline row cap. Acceptance for the phase: axe-style checks in the existing chromium recipe
report no new violations on the page; at 400 px no horizontal page scroll; every new string present
in both `en` and `fa` where it lives in the help module (`node scripts/check-rate-limit-help.mjs`).

Backend items B1–B6 are independent tracks; F14 and F15 ship with B3 and B4.

---

## 17. Prioritized backlog

**[P0] Header and "disabled" notice read the saved enforcement state**
- Problem: title/notice bound to `rlDraft.enabled` (F1).
- Files: APP `rlStatusView`, `rateLimitsDisabled`; HTML:1813, 1839; `tests/admin-console/rate-limit-save-state.test.js`.
- Change: add `rateLimitsSavedDisabled`; title/notice use saved; add pending `.tag.accent`.
- Dependency: Frontend-only.
- Acceptance: draft toggle does not change title text; pending tag appears; after save title flips.

**[P0] Separate saved schedule report for "now" statements**
- Problem: dirty draft replaces the live schedule view for all rows (F2).
- Files: APP `loadRateLimitSchedule`, new `loadRateLimitScheduleSaved`, `rlStatusFor`, `rlStatusView`.
- Change: `rlScheduleSaved` always from `GET /schedule?take=1`; timer on it; `rlSchedule` keeps draft/calendar role.
- Dependency: Existing API/data.
- Acceptance: with a dirty draft, summary/rows use saved report; Schedule section still tagged `draft`.

**[P0] Rules row: "Limit" (draft, with `was …`) and "Enforcing now" (saved) columns**
- Problem: row cannot show production state of a changed rule (F2).
- Files: APP `rlRuleRow`, `rlForceFor`; HTML:1886-1920; CSS `.rl-col-now`→`.rl-col-enforcing` widths and 48rem grid; `rate-limit-rule-list.test.js`.
- Change: per §7.1; never use labels "Limit in force"/"Limits being hit".
- Dependency: Frontend-only (after previous task).
- Acceptance: §16-1.1 criteria.

**[P0] Auto-refresh Activity on the shared poll + visible age**
- Problem: F3. Files: APP `syncPoll`, `rlActivityView`; HTML:1995-2011. Change: §16-1.2.
- Dependency: Frontend-only. Acceptance: §16-1.2.

**[P0] In-bar diff review with before → after, destructive-first, Undo**
- Problem: F4. Files: APP `rlDirtyView`→`rlDiff`, new `undoRateLimitChange`; HTML:2185-2212; CSS:2100-2104, 2248-2250.
- Dependency: Frontend-only. Acceptance: §16-1.3.

**[P1] Index maps for rule rows; debounce search; "Show 100 more"** — F9; Frontend-only; §16-1.4.

**[P1] Names, not ids, in timeline / Coming up / Preview; rows open the rule** — F10; APP:8222, 8254, 8293; Frontend-only.

**[P1] Surface `occurrencesTruncated`** — F11; APP `rlTimelineView`; HTML legend; Existing API/data.

**[P1] Operational summary strip replaces config counts** — F7/F22; §8; Existing API/data.

**[P1] Adaptive and master-switch state in Enforcing-now** — F6; verify governor scope; Existing API/data.

**[P1] Status vocabulary: no dash for RPM 0/base; tags per §7.3** — F8; Frontend-only.

**[P1] Re-order sections; Baselines tables; anchor nav** — F16; Frontend-only; update help/rule-list pins.

**[P1] Activity subject table: sort, search, load bar, row actions; `take` by rule count** — F12/F13/F24.

**[P1] Filters: Refused, Window active, Unsaved (multi-select) + result count** — F17; Frontend-only.

**[P1] 409: show what the other save changed** — F5; Frontend-only.

**[P1] Report tracker saturation (B3) → "unknown — tracker full"** — F14; Backend/API required.
- Files: `src/33pol.Observability/RateLimiting/RateLimitUsageTracker.cs`, `RateLimitUsageReport.cs`, APP `rlRefusalsFor`, `rlSubjectTrafficFor`.
- Acceptance: with `UsageReportMaxKeys=10` and 11 subjects, report says `dropped>0`; console shows unknown, not 0.

**[P1] `writable` on GET (B4) → read-only on load** — F15; Backend/API required.
- Files: `RateLimitConfigAdminService.GetCurrent`, `RateLimitAdminConfig`, `AdminRateLimitsDto`, APP `applyRateLimitsData`.
- Acceptance: gateway without DB opens read-only before any edit.

**[P2] Accessibility fixes §13 (1–13)** — Frontend-only; update literal pins.

**[P2] Mobile sort select** — F18. **[P2] Window sentence format** — F23. **[P2] Protective activity notes** — F19. **[P2] Model-tab empty copy (confirm RL-025 first)** — F20. **[P2] Adaptive block always shown when on; stream partitions in footer** — §9. **[P2] Timeline row cap; Preview shortcuts "Now"/"Next change"** — §10. **[P2] Responsive second-line cells in Activity tables** — §14.

**[P2, Backend] B1 per-limit usage · B2 series · B5 protective activity · B6 history** — each unlocks the UI named in §15; do not build that UI before the data exists.

---

## 18. Final page blueprint

```
Settings ▸ [Runtime] [Rate limits •3] [CORS] [Model access] [Observability]

┌ read-only notice (only when GET says writable:false) ───────────────────────────────────────────┐
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ (●) Rate limits are enforced · refusing 2.1 % of requests            [unsaved: will stop on save]│
│ (○) Adaptive on · evaluated 8 s ago · 2 models reduced                          [Guide] [Reload] │
│ ┌REFUSED·60 MIN──────┐┌REFUSED SUBJECTS·60 MIN┐┌LIMITS REFUSED·SINCE RESTART┐┌WINDOWS ACTIVE┐┌NEXT CHANGE──┐│
│ │1,204 · 1,100 rate  ││ 3 keys · 1 tenant     ││ 4                          ││ 2            ││ in 12 m      ││
│ │       · 104 streams││                       ││                            ││              ││ Mon 09:00    ││
│ └────────────────────┘└───────────────────────┘└────────────────────────────┘└──────────────┘└──────────────┘│
│ Activity updated 12 s ago · auto-refresh on                                                      │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
 Rules (340) · Activity · Schedule · Baselines                         429 + Retry-After; nothing is queued

 RULES                                                                        (?) [+ New rule]
 [🔍 Find a model, tenant, key or window ] WHO [All 340][API keys 210][Tenants 90][Everyone 40]
 MODEL [Any][One model][All models]   [Scheduled 12][Window active 2][Refused 4][Off 6][Unsaved 3]
 Showing 100 of 340 rules                                        (≤48rem: Sort [Refused ↓ ▾])
┌────┬─────────────────────────┬──────────────┬───────────────────┬───────────────────────────────┬────────────┬──────────────────┬───┐
│ On │ Who ▲                   │ Model        │ Limit             │ Enforcing now                 │ Refused    │ Subject traffic  │   │
│    │                         │              │  rpm  burst strm  │                               │since restart│ last 60 min     │   │
├────┼─────────────────────────┼──────────────┼───────────────────┼───────────────────────────────┼────────────┼──────────────────┼───┤
│(●) │ prod-chatbot            │ gpt-4        │  600    60    10  │ 120 rpm · 20 burst · 4 streams│        812 │ 9.4/min · 40 ref │ › │
│    │ API key · a3f1c2d9…     │              │                   │ [window: off-peak] until 17:00│            │                  │   │
│▌(●)│ acme  [unsaved]         │ All models   │  300    30     ∞  │ 600 rpm · 60 burst · ∞        │       12 † │ 3.1/min · 2 ref  │ › │
│    │ Tenant                  │              │ was 600 / 60 / ∞  │ base · next: nights Mon 22:00 │            │                  │   │
│(●) │ Everyone                │ llama-70b    │ 1,200  100    50  │ ≈ 840 of 1,200 rpm            │        340 │ 610/min · 300 ref│ › │
│    │ All callers             │              │                   │ [adaptive ×0.70] queue depth  │            │                  │   │
│( ) │ batch-runner            │ All models   │ no rate limit 0 4 │ [off] tier kept               │          — │ —                │ › │
└────┴─────────────────────────┴──────────────┴───────────────────┴───────────────────────────────┴────────────┴──────────────────┴───┘
 Refused = refused by this limit itself since the gateway started; first refusing limit only.
 † tenant's whole allowance. — = unknown, not zero.                              [Show 100 more]

 ACTIVITY  [live · in memory]        updated 12 s ago  [15 min|1 hour|3 hours] [☑ Auto-refresh] [Refresh]
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ In the selected window · last 60 min   DECISIONS 57,210  ADMITTED 56,006  REFUSED 1,204  RATE 2.1%│
│ Traffic by subject [subject-level]  [Tenant|API key|Model|Tenant × model]      [🔍 filter ]      │
│  API key            Decisions  Refused ▼  Avg req/min  Load vs last limit       Last limit seen  │
│  prod-chatbot          12,400       812        206.7   ▓▓▓▓▓▓▓▓▓░ 172 %          120              │ [rules][limit]
│  …                                                                                               │
│ Refusals by limit — since restart [cumulative]      Limit · Control · Refused ▼  (rows open rule)│
│ Load-aware adjustments [current]  llama-70b  ×0.70  saturation 0.93  queue depth · evaluated 8 s │
│ Current: 412 of 10,000 request buckets · 38 stream slots · 0 callers held to a longer retry      │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘

 SCHEDULE [draft]   12 Sep – 18 Sep · drawn from your unsaved draft      (?) [Next 7 days ▾][Europe/London ▾]
┌ timeline (names, not ids; ≤20 rows + Show all) ─────────────────────┐┌ Coming up ───────────────────┐
│ prod-chatbot·gpt-4 ░░▓▓▓░░░▓▓▓░░░▓▓▓░░ | now                         ││ Mon 09:00 in 12 m            │
│ legend · Showing first 2,000 of 2,310 occurrences                    ││ prod-chatbot·gpt-4 off-peak  │
│ Preview at [____-__-__ __:__] [Now] [Next change] [Show]             ││ 600 → 120 rpm                │
└──────────────────────────────────────────────────────────────────────┘└──────────────────────────────┘

 BASELINES                                                                     (?) [+ Add plan]
 Tenant tiers                         rpm  burst streams        Protective limits (per client address)
 default   tenants without a plan     600    60     10  ›       (●) Anonymous callers   30  10  2   base ›
 standard  14 tenants               1,200   120     20  ›           refusals appear under Activity as Anonymous
 premium   3 tenants                6,000   600    100  ›       (●) Failed sign-ins     20  10  —   base ›
                                                                    refusals are in /metrics only

┌ sticky ────────────────────────────────────────────────────────────────────────────────────────┐
│ ● 3 unsaved changes                                                                             │
│  [deleted]      API key "old-batch" on gpt-4 · was 600 / 60 / 10 · 2 windows            Undo    │
│  [changed]      Tenant acme · 600 → 300 rpm · burst 60 → 30                             Undo    │
│  [switched off] Model llama-70b · tier kept                                             Undo    │
│  Changed by someone else since you loaded (after a 409): [changed] plan standard 1,200 → 900    │
│                                   [Discard] [Hide review] [Save 3 changes (1 deletion)]         │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 19. Target operator experience

### Within 5 seconds
Whether limits are enforced **in production** (not in the draft); whether anything was refused in
the last hour and roughly how much; whether adaptive limiting is reducing any model; whether a
window is active and when the next change happens; how old those numbers are.

### Within 30 seconds
Which keys/tenants were refused in the window (one click from the summary); which limits have
refused since restart and the rule behind each (one click, table pre-filtered and sorted); for any
rule, its configured tier versus what is enforced now and why (base / window / paused / adaptive /
off / not enforced).

### During a throttling incident
Summary shows "refusing 2.1 %" → click **Refused subjects** → Traffic by subject sorted by Refused →
the key at the top → **Show rules** filters the Rules table to that key → Enforcing-now says
`120 rpm · window: off-peak until 17:00` (schedule is the cause) or `adaptive ×0.70` (load is the
cause) or `base` with a high Refused count (the tier is too small). If nothing matches, Refusals by
limit shows a tenant allowance with no rule → the plan tier is the cause → open it from Baselines.
Numbers keep updating every 30 s; a stale tag appears if they stop.

### While editing a rule
The drawer shows "Now in production" next to "After you save"; the row shows the new tier with
`was …` beneath and an unsaved tag, while Enforcing-now keeps describing production. The Schedule
section is tagged `draft` and previews the staged windows using the server's evaluation.

### Before saving
The save bar states the number of changes; the in-bar review lists each as before → after with
deletions and enforcement-off first, each with Undo; after a conflict it also lists what the other
person changed. The Save button repeats the count and any deletion. One click saves; nothing else
is applied.

---

## 20. Implementation status (2026-09-21, uncommitted working tree)

Frontend-only and existing-API items were implemented the same day; backend items were not.

| Backlog item | Status | Notes / deviation |
|---|---|---|
| P0 Header + "disabled" notice read saved state (F1) | Done | `rlStatusView`, `rateLimitsSavedDisabled`; pending tag says what Save will do |
| P0 Separate saved schedule report (F2) | Done | `rlScheduleSaved` loaded beside `rlSchedule` in `loadRateLimitSchedule` (one GET when clean, GET + preview when dirty) |
| P0 Limit (draft, `was …`) vs Enforcing now (saved) (F2) | Done | `rlEnforcingView`; `rlForceFor` removed. RPM 0 renders "none" (not "no rate limit") to fit the numeric column; the sentence is in `sr-only` + title |
| P0 Activity auto-refresh + age (F3) | Done | `rateLimitActivityPollDue` in `syncPoll`, `rlActivityFreshView`; not polled when the tracker is unavailable (503) |
| P0 In-bar diff review, Undo, destructive first (F4) | Done | `rlDiff`, `undoRateLimitChange`; first Save press with a deletion / enforcement-off opens the review |
| P1 Index maps, debounce, Show 100 more (F9) | Done (extended in the second pass, see §20.1) | `rlIndex`, `rlMatchingRules`, `rlRuleCountView`; saved-side canonical/payload cached. Measured in chromium at 1,995 rules: dirty check 88 → 24 ms, row getter 179 → 72 ms, "Show 100 more" 7.4 → 2.9 s. Getters are still re-evaluated once per binding (Alpine has no computed cache) — further gains need fewer bindings per getter |
| P1 Names in calendar / Coming up / Preview (F10) | Done | rows open the rule |
| P1 `occurrencesTruncated` (F11) | Done | |
| P1 Operational summary (F7, F22) | Done | `rlSummaryView`; header EN/FA toggle removed (kept in help drawer and inline help) |
| P1 Adaptive + master switch in rows (F6) | Done | verified in `RateLimitPlanResolver`: model, tenant_model and api_key_model are scaled, so all three are tagged |
| P1 Status vocabulary (F8) | Done | Enforcing cell is never empty / never a dash |
| P1 Re-order + anchors (F16) | Done | Order, anchor nav, and (second pass) tiers and protective limits as two tables. **Deviation kept:** section keeps the name "Calendar" because the EN/FA guide calls it that |
| P1 Activity table tools (F12, F13, F24) | Done | sort, filter, Load vs last limit (caveat kept), Show rules / New rule for key, `take` by rule count. Refusals-by-limit table left server-sorted |
| P1 Filters + count (F17) | Done | flags are multi-select: Scheduled, Window active, Refused, Off, Unsaved |
| P1 409 "theirs" group (F5) | Done | **Deviation:** the per-item flag is "undone if you save" + **Keep theirs** rather than "also edited by you" — because the draft predates their save, saving it reverts every one of their changes, not only rules both sides touched |
| P1 Tracker saturation (F14 / B3) | Done (third pass) | see §20.2 |
| P1 `writable` on GET (F15 / B4) | Done (third pass) | see §20.2 |
| P2 a11y §13 | Done | all 13 items; 4, 12, 13 in the second pass (§20.1) |
| P2 Mobile sort select (F18) | Done | |
| P2 Protective notes (F19), model-tab empty copy (F20), adaptive idle line, stream slots | Done | F20 shown only when no per-model rule is saved |
| P2 Window sentence (F23), timeline row cap, Preview shortcuts, second-line cells in Activity tables | Done | second pass, §20.1 |
| B1, B2, B5, B6 | Done (third pass) | see §20.2 — built on gateway-reported per-limit data, never on the per-subject rows |

Verified in chromium (stub API) at 1440, 1280, 1024, 768 and 400 px: no horizontal overflow, review
opens inside the viewport above the buttons, draft/production distinction, conflict list, stale
state, drawer production bar. Tests: `node --test tests/admin-console/` and the
`AdminConsole*` / `AdminRateLimit*` integration tests.

### 20.1 Second pass (2026-09-21, same working tree)

| Item | Status | Implementation notes |
|---|---|---|
| Baselines as tables | Completed | Two tables (`.t-rl-tiers`, `.t-rl-protect`) — not merged, a tier has no switch and a budget has no plan. Row header per line, draft tag + "Enforcing now" + activity note kept under the protective name; card CSS removed |
| Window sentence (F23) | Completed | `rlWindowSentence` / `rlDaysCompact`: `Mon–Fri · 09:00–17:00 · Europe/London → 120 rpm`, `Once · 21 Sep 14:00–18:00 · UTC → paused`, open-ended, overnight, all day, validity bounds, explicit priority. Restates stored fields only; running/next/skipped still come from the server report and are shown beside it. Used in the drawer window list and the calendar legend. Dates follow the browser locale like the rest of the page |
| Timeline row cap | Completed | 20 rows, running-now first then soonest change; "Showing 20 of N scheduled rules … folded away, not missing" + Show all toggle. Stated separately from the server's occurrence truncation note |
| Preview shortcuts | Completed | **Now** and **Next change · <when>** only pick the instant (next transition + 1 min, in the display zone) and call the existing `runRateLimitPreview`; disabled label says "none in range"; result region is `role="status"` |
| Narrow Activity layout | Completed | ≤48rem: rate / load / last-limit (and adaptive saturation / why) fold into a second line under the subject, no sideways scroll; ≤30rem: each row is two lines (subject, then labelled counts + actions). `td:not(.rl-wide-col)` guards against re-showing folded cells |
| Focus after Undo / Keep theirs | Completed | `rlFocusInReview`: same position → last → other list → Save → (nothing unsaved) rules filter. Verified with the keyboard in chromium, including the fading-bar case |
| Focus after Show 100 more | Completed | first new row's Open button; rows still carry no `tabindex` |
| `.tag.muted` contrast | Verified, no change | measured 4.56:1 (light) and 4.54:1 (dark) at 11 px — passes AA, narrowly. Related fix: expired/skipped windows used `opacity: .65`; now colour tokens |
| `title`-only content | Completed for what matters | visible: subject-traffic caveat, bucket-eviction note, schedule-unavailable line in the summary, disabled "Next change" reason. Screen-reader text: why a Refused count is unknown, status-filter meanings; adaptive switch has `aria-describedby`. Incidental titles (raw ids, open labels) left |
| Performance near 2,000 rules | Completed | see table below |
| B1–B6 | Done in the third pass | §20.2 |

Performance, chromium headless, 1,995 rules, stub API, in-page timing (action → DOM updated → two frames). The machine was shared with another build, so "after" is the range over seven runs; "before" is one run on the first-pass tree.

| Measure | Before | After |
|---|---|---|
| One draft edit (toggle a rule) → painted | 11,336 ms | 264–771 ms |
| Discard | 30,475 ms | 1,134–2,328 ms |
| Show 100 more | 2,310 ms | 652–1,362 ms (one outlier 2,529) |
| Filter text | 2,613 ms | 799–1,324 ms |
| Sort | 1,692 ms | 157–439 ms |
| Initial visible rows (re-enter page) | 564 ms | 187–431 ms |
| `rateLimitsDirty` (exact, one call) | 42 ms | 40 ms (unchanged by design — it is now asked once per change, not ~30 times) |
| Row getter, in an effect | 93 ms | 5 ms |
| Count / chips / dirty view / timeline / status / summary getters, per binding | 72 / 138 / 64 / 45 / 11 / 15 ms | 0 ms (one effect each) |

How: `rlStartLiveViews` computes each heavy view-model in one effect and bindings read the stored value (exact getters kept for methods and tests); closed drawers return their last view; the matching list is shared; row view-models are reused by signature so unchanged rows do not re-bind; `rlIndex` is held during a pass. Remaining cost is Alpine re-tracking the draft when it is replaced wholesale (Discard / Reload) and DOM creation for new rows.

### 20.2 Third pass — backend items B1–B6 (2026-09-21, same working tree, uncommitted)

Order followed: B3 → B4 → B1 → B5 → B2 → B6. Each was inspected, given semantics, implemented,
tested at the backend, exposed, tested at the API, and only then shown in the console.

#### What the enforcement path actually does (Phase 0)

- One inference request resolves one cached `RateLimitPlan` of up to six `RateLimitRule`s: `global`,
  `tenant`, `api_key` (stage one, before the body is parsed) and `model`, `tenant_model`,
  `api_key_model` (stage two). **Every** rule must give a token. Tokens are taken in order; when rule
  *i* refuses, rules before it are refunded and rules after it are never asked. A stage-two refusal
  also refunds stage one. Stage two is skipped for a model the caller is not granted.
- The `tenant` rule is a *composition*: an override with a rate owns the bucket; an override with
  rpm 0 keeps the plan/default rate and replaces only the stream cap; anonymous callers get
  `anonymous` composed over `default`. So one bucket can be governed by two controls (rate, streams).
- A scheduled window changes the tier in the projected snapshot, not the control. Adaptive scaling
  multiplies `model`, `tenant_model` and `api_key_model` tiers; `ConfiguredRpm` is kept beside it.
- Stream slots are taken in the router (`TryAcquireStreamSlots`), after the rate stage admitted.
- `auth_failure` is enforced by its own middleware (peek → run → debit on a rejected credential) and
  never builds a `RateLimitRule`.
- Tracker capacity was enforced per dimension by silently ignoring new keys. Totals were summed from
  the (bounded) tenant dimension, so they under-counted once it was full.
- A save needs exactly one thing beyond validation: `IRateLimitSettingsRepository` being registered.
- GET and PUT share the single `Operator` policy. There is no read/write permission split, so
  `permission` is not a reason the gateway can report and none was invented.
- Audit entries are JSON lines in `FileAuditLogger`'s file (+ one rolled generation), already read
  back by `IAuditLogReader` for the Overview.

#### Stable identity

`RateLimitRule` now carries `LimitId` (control that supplies the rate), `StreamLimitId` (control that
supplies the stream cap) and `AnonymousBucket`. Ids are the rule identity `scope:target`, lower-cased —
exactly what the console's `rlIdentity` produces — plus `default` and `plan:<slug>` for tiers
(`RateLimitLimitIds`). They are stamped when the plan is built, which is cached, so the request path
pays nothing for them. A `tenant`/`tenant_model` rule written against a slug and one written against
an id are different rules and get different ids (the target it was *found under*).

#### B3 — tracker saturation · Done

- **Semantics.** `tracker.isSaturated` is true when at least one decision since `trackingSinceUtc`
  was not counted because a dimension was full. Per dimension (`tenants`, `models`, `apiKeys`,
  `tenantModels`, `violations`, `limits`): `trackedKeys`, `maxKeys`, `atCapacity`,
  `droppedDecisions`, `firstDroppedUtc`. `atCapacity` alone is **not** saturation — nothing has been
  lost yet. Dropped *decisions* are counted, not dropped *subjects*: which subjects were turned away
  is precisely what is not stored. Process-local; cleared by restart and by `Reset()`.
- **Also fixed:** totals (and the gateway series) now come from a ring of their own that no key
  ceiling applies to, so they stay exact while a section is full.
- **API.** `GET /usage` → `tracker{…}` (additive).
- **Console.** Warning in Activity and an "activity incomplete" tag in the summary; existing rows stay;
  a rule with no per-limit row reads **unknown** (not "no decisions") while `limits` is lossy; a
  Refused cell with no row reads "—" while `violations` is lossy. A full-but-lossless section is a
  plain note.
- **Tests.** Tracker: below capacity, at capacity, first drop, later drops, reset, limits dimension,
  totals exact under saturation. API: serialisation. Console: `rate-limit-observability.test.js`.

#### B4 — writable on GET · Done

- **Semantics.** `writable` is decided by `RateLimitConfigAdminService.GetWriteAvailability()`, which
  resolves the repository exactly as `UpdateAsync` does. `readOnlyReason`: `store_unavailable` or
  null. No other reason is determinable: there is no read-only mode and no permission split.
- **API.** `GET /admin/api/rate-limits` → `writable: bool`, `readOnlyReason: string|null`. Ignored on PUT.
- **Console.** `applyRateLimitsData` sets/clears the existing read-only state from the field; an
  older gateway that omits it keeps the old "learnt from a refused save" behaviour. The flag never
  enters the draft or the dirty check.
- **Tests.** Integration: writable; store removed → `writable:false` **and** PUT 503 (parity);
  echoing the field on PUT is harmless; 401 without a key. Console: initial render read-only.

#### B1 — per-limit usage · Done

- **Counters** (`limits[]`, one row per limit id with activity in the window; each is a count of
  *decisions by that limit*, never of requests to the gateway):
  `evaluations` — asked for a token; `charged` — token kept (the request passed every rate limit);
  `refusedByRate` — this limit answered 429; `passedThenRefunded` — gave a token and got it back
  (always `evaluations − charged − refusedByRate`); `streamsStarted` / `refusedByStreams` — stream
  slots under this limit's cap (a slot taken and released on a refused request is not a start);
  `chargedPerMinute`; `peakChargedInOneMinute` + `peakMinuteUtc` (UTC calendar minute);
  `configuredRpm` / `effectiveRpm` — what the limit last enforced before/after adaptive scaling (the
  window's tier while a window is active; exact per limit because every bucket of one control has
  the same tier); `lastDecisionUtc`.
- **`singleBucket`** is true for `global`, `tenant`, `api_key`, `model`, `tenant_model`,
  `api_key_model`. Only then is `peakUtilization = peak / effectiveRpm` reported (may exceed 1: a
  bucket also holds burst). Tiers and protective scopes have one bucket per caller, so they get
  counts and **no** utilisation. A `model` rule's anonymous bucket is its own row (`anonymousBucket`).
- **Recording.** `RecordRateStage(rules, outcome, refusedPartitionKey)` from `RateLimitMiddleware`
  (stage one is recorded once the request's fate is known, so a stage-two refusal reports stage one
  as refunded, not charged) and `RecordStreamStage` from the router.
- **Console.** Rules table column "Limit activity" (replaces "Subject traffic", which stays in the
  drawer and in Activity with its caveat): `N passed · M refused`, `peak P/min of R rpm · U%` and a
  bar where `singleBucket`; sortable; a **Near limit** status filter (`peakUtilization ≥ 0.8`);
  tier rows get counts; the drawer breaks the six counters out with their meanings. Joined by id only.
- **Tests.** Resolver ids (default, plan, unknown plan, override by id and by slug, streams-only
  override, all six scopes, adaptive, scheduled window through the real projection, anonymous);
  tracker (charged / refused / never-asked / refunded-by-later-stage / zero-capacity / adaptive /
  tier has no utilisation / anonymous bucket / streams); middleware end-to-end with the real store;
  router stream stage; console.

#### B5 — protective-scope activity · Done

- **`protective[]`** always has both rows; their counters are reserved outside the key ceiling, so a
  zero is a real zero. Windowed like the rest; process-local.
  `auth_failure`: `checked` — credentialed requests checked against an address block's budget;
  `charged` — credentials then rejected (one token debited each); `refused` — answered 429. A check
  that passes with a valid credential is never charged. `enforcedRpm` is the default tier's rate
  when no rule is set, because that is what the limiter falls back to.
  `anonymous`: `checked` / `charged` / `refused` / `refusedByStreams` with the B1 meanings, counted
  **only while the anonymous rule sets a rate**; otherwise anonymous callers are held to, and counted
  under, `default`. Said on the row.
- **Console.** An activity line on each protective row and in its drawer, in its own words; the old
  "see the metrics endpoint" note remains only for a gateway that does not send the section.

#### B2 — time series · Done

- `GET /admin/api/rate-limits/usage/timeseries?minutes=1..180&bucketMinutes=1..60[&limitId=…&anonymousBucket=true]`.
  Out-of-range → 400 (not clamped). Unknown limit → 404; known-but-quiet → 200 with zeros.
- Buckets are aligned to the Unix epoch in UTC and whole: the range is rounded outwards to cover what
  was asked, never beyond the 180-minute ring. ≤180 points, contiguous, oldest first, each with an
  explicit `startUtc`. `covered: false` marks a bucket that ended before counting began (restart or
  reset) so its zeros are not read as "quiet". Reuses the existing per-minute rings; no second
  accounting system. Gateway points: `decisions / admitted / refusedByRate / refusedByStreams`.
  Limit points: `decisions` = evaluations, `admitted` = charged.
- **Console.** One small refusals trend in the summary (own scale — against total decisions a rising
  refusal count is a flat line) and a passed/refused trend in the rule drawer. Uncovered buckets are
  left out and the page says when counting began. Charts are `aria-hidden` with a text summary.

#### B6 — change history · Done

- `GET /admin/api/rate-limits/history?take=1..100&before=<utc>` — a filtered view of the existing
  audit trail (`rate_limits.update`, `rate_limits.update_refused`), newest first, `hasMore` +
  `nextBefore`. `available:false` when no trail exists (not the same as "no changes").
  `IAuditLogReader.ReadRecentAsync(AuditLogQuery)` is bounded twice: rows returned and rows scanned
  (20,000); `scanLimitReached` says when it stopped early.
- Audit details gained `changedRules[{kind, ruleId, before, after}]`, `version`, `basedOnVersion`,
  `previousVersion` (the sentence list `changes[]` is kept for grep compatibility). A rule's on/off
  switch now counts as a change. Entries from before this pass are shown as their sentence with no
  rule id claimed. The endpoint publishes named fields only; key ids, never keys.
- **Console.** A collapsed "Change history" section after Protective limits, and "Saved changes to
  this rule" in the rule drawer (matched by `ruleId`, saying how far back it looked).

#### Performance and memory

| | Bound |
|---|---|
| Limit rows | `UsageReportMaxKeys` (default 500, clamp 10–20,000); saturation is reported, never silent |
| Per limit | 180 × (8 + 5×4) B ≈ 5 KB → 2.5 MB at 500, ≈100 MB at the 20,000 ceiling |
| Protective | 2 fixed rings; totals 1 fixed ring |
| Series | ≤180 points per request; no subject × model × rule × time product is ever built |
| History | ≤100 entries/request, ≤200 changes/entry, ≤20,000 lines scanned |

Request path: per-limit writes are interlocked increments (the lock is taken once per minute per ring
to roll a slot), 0 bytes allocated, ids precomputed in the cached plan, no persistence. Micro-benchmark
(Release, busy machine, worst case of six limits per request, all threads on the same six rings):
existing `Record` ≈ 0.5–0.9 µs → with per-limit ≈ 1.2–1.5 µs single-threaded; 3.8 → 7.1 µs at 8
threads. `BuildReport` with 500 limits ≈ 25–80 ms; a 180-point series ≈ 2–4 ms.

#### Known limitations

- Counters are process-local and in-memory: one gateway process, reset on restart.
- With `UsageReportMaxKeys` at its default 500 and up to 2,000 rules, a busy gateway can saturate the
  `limits` section; the console then says "unknown". Raising the option is the remedy.
- `peakUtilization` is a busiest-minute figure against the *last enforced* rate; across a schedule
  boundary the peak may have happened under a different tier.
- "Limit activity" is hidden at ≤1280 px (the column does not fit beside "Enforcing now"); it is in
  the drawer at every width.
- `readOnlyReason` has one value. No read/write permission split exists to report.
- History lists tier changes, on/off and window *count* changes — not the contents of a window — and
  master-switch/tier-only saves appear as "no rule changed".
- A stream refusal still counts one admitted *and* one refused decision in the gateway totals
  (pre-existing: the rate stage admits before the router refuses). Per-limit counters do not mix them.

