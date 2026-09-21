# Rate limits, explained

> Generated from `src/33pol.App/wwwroot/admin/admin-rate-limit-help.js` by `scripts/render-rate-limit-help.mjs`. Edit the source, not this file. The same text opens in the admin console under Settings → Rate limits → Help.
>
> Persian version: [rate-limit-guide.fa.md](./rate-limit-guide.fa.md)

## Contents

- [What rate limiting does here](#overview)
- [The three numbers: RPM, Burst, Streams](#numbers)
- [Tiers: default, plan, tenant](#tiers)
- [Rule scopes: the eight kinds](#scopes)
- [How rules, tiers and windows combine](#combine)
- [Schedule windows](#windows)
- [Calendar, Coming up and Preview](#calendar)
- [Drafts, review and saving](#saving)
- [Worked examples](#recipes)
- [When something looks wrong](#faq)
- [The eight scopes at a glance](#scopes-table)
- [Quick answers shown next to each control](#fields)

<a id="overview"></a>

## What rate limiting does here

Rate limiting bounds how fast callers may send inference requests through the gateway and how many streaming responses they may hold open. Over a limit a request is refused with HTTP 429 and a Retry-After header saying when to come back. Nothing is queued or slowed: a refused request is a failed one, and the client is expected to retry after the wait.

**The master switch** — Enforce rate limits turns everything on this page on or off at once. Off, every tier, rule and window stops applying, but nothing is deleted. Quotas, budgets and the failed sign-in protection are separate controls and keep working.

> *Example:* Switch it off for ten minutes during an incident; switch it on and every rule is back exactly as it was.

**Adaptive load shedding** — When on, the gateway may hold model limits below the configured numbers while it is saturated and restore them as load falls. It only ever tightens, and only model scopes. Clients can see it happening in the X-33pol-RateLimit-Adaptive header.

> *Example:* Header X-33pol-RateLimit-Adaptive: 420/600 means the scope is configured for 600 rpm and currently held at 420.

**Applies without a restart** — Saving the page writes to the gateway database and the new numbers are in force for the next request. Tightening bites immediately; a raised limit fills up at the new rate rather than jumping to full.

**Response headers** — Every inference response carries X-33pol-RateLimit-Limit (RPM + Burst), -Remaining, -Reset (seconds until full) and -Scope, which names the scope that refused, or on success the one closest to refusing. That is how a client tells “my key is exhausted” from “this model is busy”.

> *Example:* X-33pol-RateLimit-Scope: model with Remaining: 0 tells the client to try another model, not to slow its whole integration.

> **Tip** The Activity section shows live counters per tenant, key and model for the window you pick, and — counted since the gateway last started — which limits refused requests. The Refused column in the rules list is the same count per rule. It is the fastest way to see whether a rule is doing anything.

<a id="numbers"></a>

## The three numbers: RPM, Burst, Streams

Every tier, rule and window is the same three numbers. They describe a token bucket: a bucket that refills steadily and that a request takes one token from.

**RPM — requests per minute** — The sustained rate. The bucket refills at RPM ÷ 60 tokens a second, so a caller that keeps going may make this many requests a minute on average.

> *Example:* 600 rpm refills ten tokens a second.

**Burst** — Extra tokens above RPM. The bucket holds RPM + Burst tokens when full, so a caller that has been quiet may spend that many at once before it is held to the sustained rate. Burst is what lets a page load fire ten calls in parallel without being refused.

> *Example:* 600 rpm + 60 burst: after a quiet spell, 660 requests may arrive in one moment; then the rate settles to 600 a minute.

**Streams — concurrent streaming responses** — How many streaming responses one partition may hold open at the same time. 0 means unlimited, not “streaming denied”. Lowering it never aborts a stream already open.

> *Example:* 5 streams: the sixth streaming request from the same tenant is refused until one of the five finishes.

**Zero in a rule** — In a scoped rule RPM 0 means “this rule does not limit the rate”; the rule then only caps streams, and Burst must also be 0 because a burst is extra tokens above a rate that does not exist. A tenant rule with RPM 0 keeps the plan’s RPM and Burst and applies only its stream cap; the anonymous rule composes the same way against the default tier.

> *Example:* Model “llama-70b”: 0 rpm, 0 burst, 8 streams. Requests are not rate-limited by this rule, but at most 8 streams may be open on the model at once.

**Zero in a tier** — The default and plan tiers cannot have RPM 0. They are the one limit every tenant is held to, and a zero there would switch the gateway’s only universal control off silently. Use the master switch if you really want no limits.

**Bounds** — RPM 1 to 1,000,000 (0 allowed in rules), Burst 0 to 1,000,000, Streams 0 to 10,000. The drawer that owns a field refuses a value outside these before you can save.

> **Tip** Think “RPM for the steady state, Burst for the first moment, Streams for long-running answers”. Most deployments need a modest burst (10–20 % of RPM) so clients that fan out a few calls are not refused for being briefly busy.

<a id="tiers"></a>

## Tiers: default, plan, tenant

A tier is what a tenant is allowed. All API keys a tenant holds draw on one bucket, so a tenant with three keys gets one allowance, not three.

**Default tier** — What every tenant gets unless something more specific matches. It is the gateway’s universal limit, which is why its RPM must be at least 1.

> *Example:* Default: 60 rpm, 10 burst, 5 streams. A newly created tenant with no plan gets exactly this.

**Plan tiers** — A tier for every tenant on a given plan, matched by the plan slug the tenant carries. Add one per commercial plan and tenants move between them by changing their plan, without touching this page.

> *Example:* Plan “standard”: 120 rpm, 20 burst, 10 streams. Plan “pro”: 600 rpm, 60 burst, 40 streams.

**Tenant rule** — A rule with scope “A tenant” overrides the tenant’s plan and the default for that one tenant. This is the only place on the page where one setting overrides another: tenant rule beats plan tier beats default. Everywhere else, rules stack.

> *Example:* acme is on “standard” (120 rpm) but has a tenant rule of 1,200 rpm. acme gets 1,200. Removing the rule drops acme back to 120.

**Key rules inside a tenant** — A tenant’s keys share its tier. To bound one key separately, add a rule with scope “An API key”; the key is then held to both its own rule and the tenant’s bucket.

> *Example:* acme at 1,200 rpm hands a partner a key with a key rule of 100 rpm. The partner may take at most 100 of acme’s 1,200.

> **Tip** Prefer plan tiers to tenant rules for anything that is really a pricing decision. Tenant rules are for exceptions, and each one is a thing to remember later.

<a id="scopes"></a>

## Rule scopes: the eight kinds

A rule limits one thing. The scope says what kind of thing; the target says which one. Here is every scope, what it counts, when to reach for it, and an example.

**A model** — The model’s whole capacity, shared by every caller. Anonymous callers of a public model are counted in a separate bucket, and keys not granted the model are never charged against it. Use it when the model itself is scarce.

> *Example:* Model “gpt-4”: 600 rpm, 60 burst, 40 streams.

**A tenant** — Replaces the tenant’s tier. RPM 0 keeps the plan rate and only caps streams. Use it for a customer that needs an exception.

> *Example:* Tenant “acme”: 1,200 rpm, 200 burst, 20 streams.

**An API key** — One credential, inside its tenant’s allowance. Target is the key id from the API keys page, never the secret.

> *Example:* Key 6f1c…: 30 rpm, 0 burst, 2 streams.

**Whole gateway** — A ceiling on all inference traffic. It does not cover the admin API or the model list, which are metered by a separate control-plane budget in appsettings so that tightening a tenant can never lock an operator out.

> *Example:* Whole gateway: 5,000 rpm, 500 burst.

**A tenant on a model** — One customer’s share of one model. Target tenant|model; the tenant may be named by id or slug.

> *Example:* acme|gpt-4: 60 rpm, 10 burst, 4 streams.

**A key on a model** — One credential on one model, the narrowest scope. Target keyId|model.

> *Example:* 6f1c…|gpt-4: 10 rpm, 0 burst, 1 stream.

**Anonymous callers** — Callers with no key, only possible on public models, metered per client address (IPv6 by /64 block). One such rule replaces the default tier for them. Fresh installs ship 60/20/2; the wizard seeds 30/10/2.

> *Example:* Anonymous callers: 30 rpm, 10 burst, 2 streams.

**Failed sign-ins** — Credentials refused by authentication, per client address, on inference and admin paths. Rate-only (Streams must be 0), independent of the master switch. A key that validates still passes, so a shared address is not locked out by one stale key. Fresh installs ship 60/20; the wizard seeds 20/10.

> *Example:* Failed sign-ins: 20 rpm, 10 burst.

> **Tip** The wizard seeds the two protective scopes tighter than the generic 600/60 on purpose. Keeping a seed there tightens the control; keeping 600 would have loosened it.

<a id="combine"></a>

## How rules, tiers and windows combine

A request must find room in every scope that applies to it: the gateway ceiling, the caller’s tenant tier, its key rule, the model rule, and the tenant-on-model and key-on-model rules. Nothing overrides anything else, with the single exception inside the tenant tier described above.

**Adding a rule only tightens** — Because every applicable rule must admit the request, a new rule can never let a caller do more than before. There is no “most specific wins” to reason about. To loosen something, raise the number that is binding or remove the rule.

> *Example:* Model 600 rpm plus tenant 1,000 rpm: the tenant gets at most 600 on that model. Raising the tenant to 2,000 changes nothing there.

**Refunds on refusal** — Tokens are taken from each bucket in turn and handed back if a later scope refuses. A caller blocked by a narrow model rule does not also burn its tenant budget on every retry.

**Targets are not validated** — A rule for a model, tenant or key that does not exist is stored and never matches, which is useful while provisioning and a trap when it is a typo. Tenants match by id or slug; keys only by id.

> *Example:* A rule on “gpt4” while the model is registered as “gpt-4” does nothing. Its Refused count stays at 0 and it never appears under Refusals by limit; fix the target.

**Which scope refused?** — The X-33pol-RateLimit-Scope response header names it, and the Refusals by limit table under Activity lists refusals by limit since the gateway last started. When several limits apply to a request, only the first one to refuse it is counted.

**Windows change a rule, not the rules around it** — A window replaces its own rule’s numbers for a span of time. Other rules and tiers still apply as before, so an off-peak window on a model does not raise the tenants’ tiers.

> *Example:* Model 600 rpm with a night window of 1,200: a tenant on a 120 rpm plan still gets 120 at night.

> **Tip** When a caller reports unexpected 429s with a generous rule, look for another scope binding tighter: usually the tenant tier or the gateway ceiling.

<a id="windows"></a>

## Schedule windows

A rule keeps a base tier and may carry up to 16 windows, each giving different numbers for a span of time, or pausing the rule. The base tier applies whenever no window is active, and whenever a window cannot be evaluated.

**Weekly window** — Recurs on the days you tick, from a start to an end time, read in the chosen time zone. “Days” are the days the window starts. An end at or before the start runs into the next day; 00:00 to 24:00 is a whole day. All weekly windows on one rule share a time zone.

> *Example:* “off-peak”, Mon–Fri, 19:00 to 07:00, Europe/Berlin, 1,200 rpm / 200 burst / 80 streams. Every weekday evening the model allows twice its daytime rate until the next morning.

**One-time window** — A single span from a start to an optional end. With no end it is a step change: the new numbers from that moment on, until you edit the rule.

> *Example:* “launch”, from 1 Oct 00:00 to 3 Oct 00:00, 3,000 rpm. Or “new baseline”, from 1 Jan 00:00 with no end, to raise a limit on time without a midnight edit.

**Pause this rule instead** — The window suspends its rule: while it runs the rule is treated as absent and enforces nothing. Every other rule and tier still applies. A paused tenant rule hands the tenant back to its plan tier.

> *Example:* “maintenance”, weekly, Sun 02:00–04:00: the strict model rule is lifted while a batch job runs.

**Priority** — When several windows are active at once, the highest priority (0–1000) wins. Without priorities a one-time window outranks a weekly one. Two windows that could run together with the same priority are refused when you save; same-kind overlaps are refused unless their priorities differ.

> *Example:* Evening window priority 10, promo window priority 5: evenings win while both run.

**Valid from / Valid until** — Optional bounds on when a weekly window exists at all. Use them to start a schedule next quarter or to let a trial arrangement expire on its own.

> *Example:* Valid until 31 Dec 23:59: the window stops recurring in the new year without anyone deleting it.

**Time zones and daylight saving** — Weekly times are wall-clock times in their zone. A start inside a spring-forward gap moves to the first valid instant; a window across the autumn repeat lasts an hour longer. A zone the server cannot resolve makes the window invalid: the base tier applies and the page marks the rule.

> *Example:* A 02:30 start on the night clocks jump 02:00 → 03:00 begins at 03:00.

**At a boundary** — Tightening applies on the partition’s next request. A raised limit refills at the new rate rather than jumping to full. Lowering a stream cap never aborts open streams. The adaptive governor scales whatever the schedule put in force. With the master switch off, windows enforce nothing either.

**Preview before you apply** — The window form shows its next occurrence, whether it is running now, which windows it overlaps and which it outranks, live as you type. Nothing is stored until you press Add and then Save.

> **Tip** Name windows for their purpose (“off-peak”, “launch”, “maintenance”): the name is what the calendar, the Coming up list and the transitions show.

<a id="calendar"></a>

## Calendar, Coming up and Preview

The calendar shows when limits change over the chosen range, in the chosen time zone. It includes windows you have added but not yet saved, and says so with a “draft” tag.

**Rows and bands** — Each row is a rule that has windows. A coloured band is a window in force; grey is the base tier; a faded band is in the past. The vertical line is now. Hover a band for its numbers; click a row label to open the rule.

**Coming up** — The next moments any rule’s numbers change, with the rule, the window and how the limit moves. Long lists are truncated; the toggle shows them all.

> *Example:* Fri 19:00 · gpt-4 · off-peak starts · 600 → 1,200 rpm.

**Preview at** — Pick a moment and the page answers what every rule would enforce then, which window would be responsible, and when it changes next. Use it to check a schedule before you save it.

> *Example:* Preview at Monday 09:00 confirms the night window has ended and the day tier is back.

> **Tip** Change the “Show times in” zone when you operate across regions: the windows are stored in their own zones, so the calendar can render them in yours.

<a id="saving"></a>

## Drafts, review and saving

The page is a summary; editing happens in drawers. A drawer’s Done stages the change into a draft. The gateway only changes when you Save.

**The bar at the bottom** — Appears while the draft differs from what the server has, counts the changes, and offers Discard, Review changes and Save. Leaving the tab with a dirty draft asks first.

**Review changes** — Lists every difference the save would send: tiers changed, rules added or removed, windows added, the master switch. Read it before a save that touches many rules.

**Save sends the whole set** — The API replaces the rate-limit configuration wholesale, so what you see on the page is exactly what will be enforced. If another operator saved since you loaded, the save is refused rather than overwriting their work; press Reload and apply your change again.

**Switching a rule off** — The On switch on a rule stages it as not enforced while keeping its tier and windows, so a rule can be retired for a while and brought back unchanged. Delete permanently removes it.

**Read-only** — When the page cannot be edited it says why at the top: the key lacks the admin role, or the server refused the last save. Editing controls are disabled until the reason is gone.

> **Tip** A rule that is dangerous to get wrong (the gateway ceiling, Failed sign-ins) deserves a look at Activity a few minutes after saving.

<a id="recipes"></a>

## Worked examples

Common situations and the rule that answers them. Each is one rule or one window; the surrounding tiers keep applying.

**Protect an expensive model** — New rule → Everyone → One model → pick the model → 600 rpm, 60 burst, 40 streams. Every caller together may not exceed that on the model.

> *Example:* The upstream provider allows 10,000 tokens a second; you set the model rule so total request rate stays inside it.

**Give one customer a bigger share of one model** — New rule → A tenant → One model → the tenant slug, then the model → 200 rpm. The tenant keeps its ordinary tier elsewhere.

> *Example:* acme|gpt-4 at 200 rpm while other tenants share what the model rule leaves.

**Cap a noisy integration key** — New rule → An API key → All models → pick the key → 30 rpm, 0 burst, 2 streams. Its tenant’s other keys are unaffected.

> *Example:* A partner’s webhook retries in a loop; the key rule holds it to 30 a minute without touching the tenant.

**Raise a model’s limit at night** — Open the model rule → Add window → Weekly → Mon–Fri, 19:00 to 07:00, your zone → higher numbers. Daytime keeps the base tier.

> *Example:* “off-peak”: 1,200 rpm instead of 600 every weekday night.

**A launch day** — Open the model rule → Add window → Once → From the launch start, Until two days later → higher numbers. It outranks any weekly window while it runs.

> *Example:* “launch”: 3,000 rpm from 1 Oct to 3 Oct.

**A planned baseline change** — Add a Once window with a From and no Until. The new numbers take effect on time and stay, without anyone editing at midnight.

> *Example:* “new plan year”: 900 rpm from 1 Jan 00:00, open-ended.

**Lift a rule during maintenance** — Add a window with Pause this rule instead. While it runs the rule enforces nothing; every other limit still applies.

> *Example:* “maintenance”, Sun 02:00–04:00 pauses the strict model rule for the weekly batch.

**Stop credential guessing** — Keep a Failed sign-ins rule. Fresh installs have 60/20; the wizard proposes 20/10. Streams must stay 0.

> *Example:* Failed sign-ins 20 rpm, 10 burst: an address is refused after 30 bad keys in a minute.

**Bound anonymous traffic to public models** — Keep an Anonymous callers rule whenever any model is public. Each address gets its own bucket.

> *Example:* Anonymous callers 30 rpm, 10 burst, 2 streams.

> **Tip** Add the rule, save, then look at Activity: a rule that bites gains a Refused count, and a row under Refusals by limit, within a few minutes of real traffic.

<a id="faq"></a>

## When something looks wrong

The usual causes, in the order to check them.

**My rule does nothing** — Check the target for a typo: targets are not validated, so a wrong id is stored and never matches. Check the master switch and the rule’s On switch. Check whether a window is pausing it right now (the rule row says “In force now”).

**A caller gets 429 despite a generous rule** — Another scope is binding tighter: its tenant tier, a key rule, or the gateway ceiling. The X-33pol-RateLimit-Scope header on the refused response names the scope. Preview at the current time lists what every rule enforces.

**I raised a limit and nothing changed yet** — A raised limit refills at the new rate; it does not jump to full. Within a minute the caller sees the new rate.

**Rate limiting is off but bad keys still get 429** — Expected. Failed sign-ins is a security control and is not governed by the master switch; it has its own flag in appsettings.

**The save was refused with a conflict** — Someone else saved since you loaded the page. Press Reload, then apply your change again. Nothing of yours was written.

**A rule is marked with a schedule error** — One of its windows cannot be evaluated, usually a time zone the server does not know. The base tier applies meanwhile. Open the rule and fix the window.

> **Tip** The full operator reference lives in docs/runbooks/rate-limit-admin.md in the repository, including the JSON the API accepts.

<a id="scopes-table"></a>

## The eight scopes at a glance

| | What it counts | When to use it | Example |
|---|---|---|---|
| **Everyone on one model** (`model`) | Caps one model’s total request rate and open streams across every caller. The bucket is shared: whoever is fastest takes the most. Anonymous callers of a public model count in a bucket of their own so they cannot starve tenants, and a key that is not granted the model is never charged against it. | Use it when the model itself is the scarce thing: an expensive upstream, a single GPU, a provider quota. | Model “gpt-4”: 600 rpm, 60 burst, 40 streams. All tenants together may not exceed 600 a minute on gpt-4. |
| **A tenant, all models** (`tenant`) | Replaces the tenant’s plan or default tier with these numbers. Every key the tenant holds shares this one bucket. RPM 0 here keeps the plan’s rate and only caps streams. | Use it when one customer needs more, or less, than its plan allows, without moving it to another plan. | Tenant “acme”: 1,200 rpm, 200 burst, 20 streams, while its plan tier is 600 rpm. acme now gets 1,200; other tenants on the plan are unchanged. |
| **An API key, all models** (`api_key`) | Caps one credential on its own, inside whatever its tenant is allowed. The key still counts against the tenant’s bucket; this bounds how much of that bucket one key may take. | Use it for a noisy integration or a key handed to a partner, so it cannot spend the whole tenant allowance. | Key 6f1c…: 30 rpm, 0 burst, 2 streams. A tenant with 600 rpm can never see this key take more than 30 of them. |
| **Everyone, all models (the whole gateway)** (`global`) | A ceiling on every inference request whoever sends it. It does not meter the admin API or the model list, which have a separate control-plane budget in appsettings. | Use it to protect the gateway or a shared upstream contract from the sum of all tenants. There is no seeded number: one that fits every deployment does not exist. | Whole gateway: 5,000 rpm, 500 burst. Even with generous tenant tiers, the gateway forwards at most 5,000 requests a minute. |
| **A tenant on one model** (`tenant_model`) | One tenant’s share of one model. It stacks with the model rule and the tenant tier; the request needs room in all of them. | Use it to hand out a fair slice of a scarce model, or to keep one customer’s heavy use of one model from affecting its other traffic. | acme\|gpt-4: 60 rpm, 10 burst, 4 streams. acme keeps its 1,200 rpm elsewhere but may only take 60 a minute on gpt-4. |
| **An API key on one model** (`api_key_model`) | The narrowest limit: one credential on one model. Only that key’s requests to that model are counted; its other models, and other keys on the same model, are not. | Use it when a single integration should be limited on a single model only, for instance a demo key on the flagship model. | 6f1c…\|gpt-4: 10 rpm, 0 burst, 1 stream. The key is unrestricted on other models beyond its tenant’s tier. |
| **Anonymous callers** (`anonymous`) | Requests with no API key at all, which are only possible on models marked public. Each client address (an IPv6 /64 block) gets its own bucket of these numbers instead of the default tier. RPM 0 keeps the default rate and only caps streams. | Have exactly one whenever any model is public; without it anonymous callers fall back to the default tier and the gateway logs a warning. A fresh install ships 60 rpm, 20 burst, 2 streams; the wizard seeds a tighter 30/10/2. | Anonymous callers: 30 rpm, 10 burst, 2 streams. One address may make 30 requests a minute to public models and hold 2 streams open. |
| **Failed sign-ins** (`auth_failure`) | Requests that present a credential and are refused by authentication: an unknown, expired or revoked key. Counted per client address, on inference and admin paths. It is rate-only: RPM must be above 0 and Streams 0. This budget is not switched off by the master switch; it has its own appsettings flag. | Keep it: it is what stops credential guessing. A valid key from the same address still passes, so a stale key on a shared NAT cannot lock out its neighbours. A fresh install ships 60 rpm, 20 burst; the wizard seeds 20/10. | Failed sign-ins: 20 rpm, 10 burst. After 30 rejected credentials in a minute, further bad keys from that address are refused with 429 without a database lookup. |

<a id="fields"></a>

## Quick answers shown next to each control

### What does the main switch do?

Enforce rate limits is the master switch. Off, nothing on this page is enforced: no tier, rule or window. The numbers stay saved and apply again the moment you switch it back on. Quotas, budgets and the failed sign-in protection are not affected by it.

> *Example:* During an incident you switch enforcement off for ten minutes to let a backlog drain, then on again. No rule had to be edited or re-created.

### What is adaptive load shedding?

When the gateway itself is saturated, model limits may be lowered below the numbers set here and raised back as load falls. Only model scopes are affected, and the response header X-33pol-RateLimit-Adaptive is present exactly while a scope is being held down.

> *Example:* A model rule says 600 rpm. Under heavy load the governor holds it at 420 for a while; clients see 420/600 in the adaptive header until load eases.

### RPM, Burst and Streams — the three numbers

RPM is the sustained rate: how many requests a minute a caller may keep making. Burst is extra room on top of RPM that an idle caller may spend at once, so the most that can arrive in one go is RPM + Burst. Streams is how many streaming responses may be open at the same time; 0 means no cap on streams.

> *Example:* 60 rpm, 20 burst, 5 streams: a quiet caller may fire 80 requests in a moment, then settle to one a second, holding at most 5 streams open.

### RPM 0 in a rule

In a rule, RPM 0 means this rule does not limit the request rate at all; it then may only cap streams, and Burst must be 0 too. In a tenant rule, RPM 0 keeps the rate of the tenant’s plan and only caps its streams. Tiers are different: the default and plan tiers need RPM of at least 1, because they are the gateway’s universal limit.

> *Example:* A model rule with 0 rpm, 0 burst, 8 streams: unlimited request rate for that model, but never more than 8 streams open at once.

### What is a tier, and who gets which one?

A tier is the allowance a tenant gets. Every tenant has the default tier unless it is on a plan that has its own tier, and a tenant rule overrides both. All API keys a tenant holds share one bucket; add a key rule to bound a single key.

> *Example:* Default 60 rpm. Plan “pro” 600 rpm. Tenant acme is on “pro” but has a tenant rule of 1,200 rpm, so acme gets 1,200. Every other “pro” tenant gets 600.

### What is the plan slug?

The short name tenants carry as their plan, exactly as spelled on the tenant. Tenants whose plan matches get this tier instead of the default. It must start with a letter and may contain letters, digits, hyphens and underscores.

> *Example:* Tenants provisioned with plan “standard” use the “standard” tier here. A tenant with no plan, or a plan with no tier, gets the default.

### What is a rule?

A rule limits one specific thing: a model, a tenant, a key, a tenant on a model, a key on a model, the whole gateway, anonymous callers, or failed sign-ins. Every rule that applies to a request must admit it, so adding a rule can only tighten, never loosen. A rule has a base tier and may carry schedule windows.

> *Example:* A “gpt-4” model rule of 600 rpm and a tenant rule of 1,000 rpm for acme: acme may still only take 600 a minute on gpt-4, shared with every other caller of that model.

### Who, and on which model?

Say who the limit is for and whether it covers one model or all of them; the console works out the rest. An API key is one credential. A tenant is all of one customer’s keys together. Everyone is every caller sharing one budget. One model counts only requests to that model; All models is one count across everything. Anonymous callers and Failed sign-ins are the two protective budgets, and have neither a subject nor a model.

> *Example:* A customer keeps flooding your most expensive model but is fine elsewhere: choose “A tenant” and “One model”, not “All models”.

### Which key, tenant or model?

Pick from the list. An API key is found by its name, prefix or id and has to exist: the rule is stored against the key’s id, never its name and never the secret. A model alias is stored as the model’s own id, because that is what limits are matched on. A tenant is its id or its slug; both match the same tenant. Tenants are not checked against what exists: a typo is stored and simply never matches.

> *Example:* Tenant “acme” on model “gpt-4” is stored as the target acme|gpt-4; a key on a model is stored as the key’s id, then |gpt-4. Confirm it bites afterwards under Activity: a rule that refuses requests gains a Refused count in the rules list and a row under Refusals by limit, counted since the gateway last started.

### How much?

The base tier for this rule. The summary below states what this scope enforces today, so you can see whether the number you are entering is tighter. On an ordinary rule a number no tighter than what is already in force never binds: it does nothing but is allowed. The two protective limits (failed sign-ins, anonymous callers) are different: a configured tier replaces the default-tier fallback instead of adding to it, so a higher number loosens that budget.

> *Example:* The gateway ceiling is 5,000 rpm. A model rule of 6,000 rpm changes nothing; one of 600 rpm does. The default tier is 60 rpm: a failed-sign-ins limit of 600 rpm raises that budget to 600.

### What is the base tier?

The numbers this rule enforces whenever no schedule window is active, and whenever a window cannot be evaluated (for instance an unknown time zone). Windows replace these numbers for a span of time; the base tier is what applies the rest of the time.

> *Example:* Base tier 600 rpm with an off-peak window of 1,200 rpm from 19:00 to 07:00: during the day the model allows 600 a minute, at night 1,200.

### What is a schedule window?

A window gives a rule different numbers for a span of time: weekly (the same hours on chosen days) or once (one span with a start and an optional end). A window can instead pause the rule entirely. A rule may carry up to 16 windows. Windows of the same kind may not overlap; a one-time window outranks a weekly one.

> *Example:* A weekly “off-peak” window raises a model’s rpm every night, and a one-time “launch day” window raises it further for 48 hours; on launch night the one-time window wins.

### Weekly or once?

Weekly repeats: pick the days it starts, a start and an end time, and the time zone those times are read in. An end at or before the start runs into the next morning; 00:00 to 24:00 is a whole day. Once is a single span: a start, and an end you may leave empty for a change that starts then and stays in force.

> *Example:* Weekly, Mon–Fri, 19:00 to 07:00, Europe/Berlin: every weekday evening until the next morning. Once, from 1 Oct 00:00 with no end: a planned step change that takes effect on time without anyone editing the base numbers at midnight.

### What does “pause this rule” do?

While the window runs the rule is treated as absent: nothing is enforced by it. Every other rule and tier still applies, so a paused model rule leaves callers held to their tenant tiers. A paused tenant rule hands the tenant back to its plan tier.

> *Example:* A maintenance window on Sunday 02:00–04:00 pauses the strict rule on a model while a batch job runs; the tenant tiers still bound each caller.

### Which time zone?

Weekly times are wall-clock times in the zone you pick, with that zone’s daylight-saving rules. A start that falls into a spring-forward gap moves to the first valid moment; a window across the autumn repeat lasts an hour longer. Every weekly window on one rule must use the same zone.

> *Example:* A 02:30 start in Europe/Berlin on the night clocks jump from 02:00 to 03:00 begins at 03:00 that day.

### Priority and validity

When several windows are active at once, the highest priority wins; without priorities a one-time window beats a weekly one. Two windows that could be active together with the same priority are refused at save. Valid from and Valid until bound when a weekly window exists at all, so it can start next quarter or end after a trial.

> *Example:* A weekly evening window with priority 10 and a one-time promo window with priority 5: the evening window wins while both run. Set Valid until to 31 Dec to retire the evening window automatically.

### Reading the calendar

Each row is a rule with windows; the coloured bands show when a window is in force and the grey band is the base tier. The line is now. Coming up lists the next moments a rule’s numbers change. Preview at answers what every rule would enforce at a chosen moment, including windows you have added but not saved.

> *Example:* Set Preview at to next Monday 09:00 to check that the off-peak window has ended and the base tier is back before the working day.

### How rules, tiers and windows combine

A request needs room in every rule that applies to it: the gateway ceiling, the caller’s tenant tier, its key rule, the model rule, and the tenant-on-model and key-on-model rules. Nothing overrides anything else, so adding a rule can only tighten what a caller may do. The one exception is inside the tenant tier, where a tenant rule beats the plan tier, which beats the default. A window changes its own rule’s numbers for a span of time; when no window is active the base tier applies.

> *Example:* Model gpt-4 at 600 rpm, tenant acme at 1,200 rpm, acme|gpt-4 at 60 rpm: acme gets at most 60 a minute on gpt-4, 1,200 across everything else, and shares the model’s 600 with everyone.

### When do changes take effect?

Edits in a drawer are staged into a draft when you press Done. Nothing reaches the gateway until you press Save in the bar at the bottom; Review changes lists what will be sent and Discard throws the draft away. A save applies without a restart. If someone else saved in the meantime the save is refused; reload and reapply your change.

> *Example:* You edit three rules and add a window. The bar reads 4 changes; Review shows each; Save sends them together. A tightened limit bites on the next request; a raised one fills up at the new rate rather than all at once.

Operator reference: [runbooks/rate-limit-admin.md](./runbooks/rate-limit-admin.md).
