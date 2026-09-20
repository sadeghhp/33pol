/*
 * Rate-limit help: the bilingual (English / Persian) explanations the Settings → Rate limits page
 * and its drawers show, plus the full guide that opens in the help drawer.
 *
 * Content only — no behaviour. `admin-app.js` reads `window.RateLimitHelp[lang]` and turns it into
 * the view objects the templates bind to. Both languages MUST carry the same keys: the console
 * looks a topic up by key in whichever language is selected, and a missing key under the Alpine
 * CSP build is not a fallback but a silently empty element. `scripts/check-rate-limit-help.mjs`
 * asserts the two trees match; `scripts/render-rate-limit-help.mjs` renders the guide to
 * `docs/rate-limit-guide.en.md` and `docs/rate-limit-guide.fa.md` from the same source.
 *
 * Every number quoted here is one the gateway really enforces (see docs/runbooks/rate-limit-admin.md):
 * the shipped `anonymous` and `auth_failure` tiers are 60/20, the wizard seeds them tighter at
 * 30/10/2 and 20/10, a rule may carry 16 windows, priority runs 0–1000.
 *
 * Shape (identical under `en` and `fa`):
 *   ui        – labels for the help chrome itself
 *   fields    – short inline explainers, keyed by where they appear; `topic` names the guide section
 *   scopes    – one entry per rule scope, keyed by the scope id the API uses
 *   sections  – the guide, in reading order; `items` are term/text/example rows
 */
(function () {
  const en = {
    ui: {
      help: 'Help',
      helpTitle: 'Open the rate-limit guide',
      eyebrow: 'Guide',
      guide: 'Rate limits, explained',
      guideSub: 'What each setting does, how they combine, and worked examples.',
      contents: 'Contents',
      language: 'Language',
      otherLang: 'فارسی',
      example: 'Example:',
      tip: 'Tip:',
      more: 'Read more in the guide',
      close: 'Close',
      about: 'About',
      scopeHelpTitle: 'What this choice means'
    },

    fields: {
      master: {
        title: 'What does the main switch do?',
        text: 'Enforce rate limits is the master switch. Off, nothing on this page is enforced: no tier, rule or window. The numbers stay saved and apply again the moment you switch it back on. Quotas, budgets and the failed sign-in protection are not affected by it.',
        example: 'During an incident you switch enforcement off for ten minutes to let a backlog drain, then on again. No rule had to be edited or re-created.',
        topic: 'overview'
      },
      adaptive: {
        title: 'What is adaptive load shedding?',
        text: 'When the gateway itself is saturated, model limits may be lowered below the numbers set here and raised back as load falls. Only model scopes are affected, and the response header X-33pol-RateLimit-Adaptive is present exactly while a scope is being held down.',
        example: 'A model rule says 600 rpm. Under heavy load the governor holds it at 420 for a while; clients see 420/600 in the adaptive header until load eases.',
        topic: 'overview'
      },
      numbers: {
        title: 'RPM, Burst and Streams — the three numbers',
        text: 'RPM is the sustained rate: how many requests a minute a caller may keep making. Burst is extra room on top of RPM that an idle caller may spend at once, so the most that can arrive in one go is RPM + Burst. Streams is how many streaming responses may be open at the same time; 0 means no cap on streams.',
        example: '60 rpm, 20 burst, 5 streams: a quiet caller may fire 80 requests in a moment, then settle to one a second, holding at most 5 streams open.',
        topic: 'numbers'
      },
      ruleNumbers: {
        title: 'RPM 0 in a rule',
        text: 'In a rule, RPM 0 means this rule does not limit the request rate at all; it then may only cap streams, and Burst must be 0 too. In a tenant rule, RPM 0 keeps the rate of the tenant’s plan and only caps its streams. Tiers are different: the default and plan tiers need RPM of at least 1, because they are the gateway’s universal limit.',
        example: 'A model rule with 0 rpm, 0 burst, 8 streams: unlimited request rate for that model, but never more than 8 streams open at once.',
        topic: 'numbers'
      },
      tiers: {
        title: 'What is a tier, and who gets which one?',
        text: 'A tier is the allowance a tenant gets. Every tenant has the default tier unless it is on a plan that has its own tier, and a tenant rule overrides both. All API keys a tenant holds share one bucket; add a key rule to bound a single key.',
        example: 'Default 60 rpm. Plan “pro” 600 rpm. Tenant acme is on “pro” but has a tenant rule of 1,200 rpm, so acme gets 1,200. Every other “pro” tenant gets 600.',
        topic: 'tiers'
      },
      planSlug: {
        title: 'What is the plan slug?',
        text: 'The short name tenants carry as their plan, exactly as spelled on the tenant. Tenants whose plan matches get this tier instead of the default. It must start with a letter and may contain letters, digits, hyphens and underscores.',
        example: 'Tenants provisioned with plan “standard” use the “standard” tier here. A tenant with no plan, or a plan with no tier, gets the default.',
        topic: 'tiers'
      },
      rules: {
        title: 'What is a rule?',
        text: 'A rule limits one specific thing: a model, a tenant, a key, a tenant on a model, a key on a model, the whole gateway, anonymous callers, or failed sign-ins. Every rule that applies to a request must admit it, so adding a rule can only tighten, never loosen. A rule has a base tier and may carry schedule windows.',
        example: 'A “gpt-4” model rule of 600 rpm and a tenant rule of 1,000 rpm for acme: acme may still only take 600 a minute on gpt-4, shared with every other caller of that model.',
        topic: 'scopes'
      },
      scope: {
        title: 'Who, and on which model?',
        text: 'Say who the limit is for and whether it covers one model or all of them; the console works out the rest. An API key is one credential. A tenant is all of one customer’s keys together. Everyone is every caller sharing one budget. One model counts only requests to that model; All models is one count across everything. Anonymous callers and Failed sign-ins are the two protective budgets, and have neither a subject nor a model.',
        example: 'A customer keeps flooding your most expensive model but is fine elsewhere: choose “A tenant” and “One model”, not “All models”.',
        topic: 'scopes'
      },
      target: {
        title: 'Which key, tenant or model?',
        text: 'Pick from the list. An API key is found by its name, prefix or id and has to exist: the rule is stored against the key’s id, never its name and never the secret. A model alias is stored as the model’s own id, because that is what limits are matched on. A tenant is its id or its slug; both match the same tenant. Tenants are not checked against what exists: a typo is stored and simply never matches.',
        example: 'Tenant “acme” on model “gpt-4” is stored as the target acme|gpt-4; a key on a model is stored as the key’s id, then |gpt-4. Confirm it bites afterwards in the Usage report: a working rule appears under Limits being hit or changes a row’s limit in force.',
        topic: 'combine'
      },
      limit: {
        title: 'How much?',
        text: 'The base tier for this rule. The summary below states what this scope enforces today, so you can see whether the number you are entering is tighter. A number no tighter than what is already in force does nothing but is allowed.',
        example: 'The gateway ceiling is 5,000 rpm. A model rule of 6,000 rpm changes nothing; one of 600 rpm does.',
        topic: 'numbers'
      },
      baseTier: {
        title: 'What is the base tier?',
        text: 'The numbers this rule enforces whenever no schedule window is active, and whenever a window cannot be evaluated (for instance an unknown time zone). Windows replace these numbers for a span of time; the base tier is what applies the rest of the time.',
        example: 'Base tier 600 rpm with an off-peak window of 1,200 rpm from 19:00 to 07:00: during the day the model allows 600 a minute, at night 1,200.',
        topic: 'windows'
      },
      windows: {
        title: 'What is a schedule window?',
        text: 'A window gives a rule different numbers for a span of time: weekly (the same hours on chosen days) or once (one span with a start and an optional end). A window can instead pause the rule entirely. A rule may carry up to 16 windows. Windows of the same kind may not overlap; a one-time window outranks a weekly one.',
        example: 'A weekly “off-peak” window raises a model’s rpm every night, and a one-time “launch day” window raises it further for 48 hours; on launch night the one-time window wins.',
        topic: 'windows'
      },
      windowKind: {
        title: 'Weekly or once?',
        text: 'Weekly repeats: pick the days it starts, a start and an end time, and the time zone those times are read in. An end at or before the start runs into the next morning; 00:00 to 24:00 is a whole day. Once is a single span: a start, and an end you may leave empty for a change that starts then and stays in force.',
        example: 'Weekly, Mon–Fri, 19:00 to 07:00, Europe/Berlin: every weekday evening until the next morning. Once, from 1 Oct 00:00 with no end: a planned step change that takes effect on time without anyone editing the base numbers at midnight.',
        topic: 'windows'
      },
      suspend: {
        title: 'What does “pause this rule” do?',
        text: 'While the window runs the rule is treated as absent: nothing is enforced by it. Every other rule and tier still applies, so a paused model rule leaves callers held to their tenant tiers. A paused tenant rule hands the tenant back to its plan tier.',
        example: 'A maintenance window on Sunday 02:00–04:00 pauses the strict rule on a model while a batch job runs; the tenant tiers still bound each caller.',
        topic: 'windows'
      },
      timeZone: {
        title: 'Which time zone?',
        text: 'Weekly times are wall-clock times in the zone you pick, with that zone’s daylight-saving rules. A start that falls into a spring-forward gap moves to the first valid moment; a window across the autumn repeat lasts an hour longer. Every weekly window on one rule must use the same zone.',
        example: 'A 02:30 start in Europe/Berlin on the night clocks jump from 02:00 to 03:00 begins at 03:00 that day.',
        topic: 'windows'
      },
      priority: {
        title: 'Priority and validity',
        text: 'When several windows are active at once, the highest priority wins; without priorities a one-time window beats a weekly one. Two windows that could be active together with the same priority are refused at save. Valid from and Valid until bound when a weekly window exists at all, so it can start next quarter or end after a trial.',
        example: 'A weekly evening window with priority 10 and a one-time promo window with priority 5: the evening window wins while both run. Set Valid until to 31 Dec to retire the evening window automatically.',
        topic: 'windows'
      },
      calendar: {
        title: 'Reading the calendar',
        text: 'Each row is a rule with windows; the coloured bands show when a window is in force and the grey band is the base tier. The line is now. Coming up lists the next moments a rule’s numbers change. Preview at answers what every rule would enforce at a chosen moment, including windows you have added but not saved.',
        example: 'Set Preview at to next Monday 09:00 to check that the off-peak window has ended and the base tier is back before the working day.',
        topic: 'calendar'
      },
      combine: {
        title: 'How rules, tiers and windows combine',
        text: 'A request needs room in every rule that applies to it: the gateway ceiling, the caller’s tenant tier, its key rule, the model rule, and the tenant-on-model and key-on-model rules. Nothing overrides anything else, so adding a rule can only tighten what a caller may do. The one exception is inside the tenant tier, where a tenant rule beats the plan tier, which beats the default. A window changes its own rule’s numbers for a span of time; when no window is active the base tier applies.',
        example: 'Model gpt-4 at 600 rpm, tenant acme at 1,200 rpm, acme|gpt-4 at 60 rpm: acme gets at most 60 a minute on gpt-4, 1,200 across everything else, and shares the model’s 600 with everyone.',
        topic: 'combine'
      },
      save: {
        title: 'When do changes take effect?',
        text: 'Edits in a drawer are staged into a draft when you press Done. Nothing reaches the gateway until you press Save in the bar at the bottom; Review changes lists what will be sent and Discard throws the draft away. A save applies without a restart. If someone else saved in the meantime the save is refused; reload and reapply your change.',
        example: 'You edit three rules and add a window. The bar reads 4 changes; Review shows each; Save sends them together. A tightened limit bites on the next request; a raised one fills up at the new rate rather than all at once.',
        topic: 'saving'
      }
    },

    scopes: {
      model: {
        name: 'Everyone on one model',
        what: 'Caps one model’s total request rate and open streams across every caller. The bucket is shared: whoever is fastest takes the most. Anonymous callers of a public model count in a bucket of their own so they cannot starve tenants, and a key that is not granted the model is never charged against it.',
        when: 'Use it when the model itself is the scarce thing: an expensive upstream, a single GPU, a provider quota.',
        example: 'Model “gpt-4”: 600 rpm, 60 burst, 40 streams. All tenants together may not exceed 600 a minute on gpt-4.'
      },
      tenant: {
        name: 'A tenant, all models',
        what: 'Replaces the tenant’s plan or default tier with these numbers. Every key the tenant holds shares this one bucket. RPM 0 here keeps the plan’s rate and only caps streams.',
        when: 'Use it when one customer needs more, or less, than its plan allows, without moving it to another plan.',
        example: 'Tenant “acme”: 1,200 rpm, 200 burst, 20 streams, while its plan tier is 600 rpm. acme now gets 1,200; other tenants on the plan are unchanged.'
      },
      api_key: {
        name: 'An API key, all models',
        what: 'Caps one credential on its own, inside whatever its tenant is allowed. The key still counts against the tenant’s bucket; this bounds how much of that bucket one key may take.',
        when: 'Use it for a noisy integration or a key handed to a partner, so it cannot spend the whole tenant allowance.',
        example: 'Key 6f1c…: 30 rpm, 0 burst, 2 streams. A tenant with 600 rpm can never see this key take more than 30 of them.'
      },
      global: {
        name: 'Everyone, all models (the whole gateway)',
        what: 'A ceiling on every inference request whoever sends it. It does not meter the admin API or the model list, which have a separate control-plane budget in appsettings.',
        when: 'Use it to protect the gateway or a shared upstream contract from the sum of all tenants. There is no seeded number: one that fits every deployment does not exist.',
        example: 'Whole gateway: 5,000 rpm, 500 burst. Even with generous tenant tiers, the gateway forwards at most 5,000 requests a minute.'
      },
      tenant_model: {
        name: 'A tenant on one model',
        what: 'One tenant’s share of one model. It stacks with the model rule and the tenant tier; the request needs room in all of them.',
        when: 'Use it to hand out a fair slice of a scarce model, or to keep one customer’s heavy use of one model from affecting its other traffic.',
        example: 'acme|gpt-4: 60 rpm, 10 burst, 4 streams. acme keeps its 1,200 rpm elsewhere but may only take 60 a minute on gpt-4.'
      },
      api_key_model: {
        name: 'An API key on one model',
        what: 'The narrowest limit: one credential on one model. Only that key’s requests to that model are counted; its other models, and other keys on the same model, are not.',
        when: 'Use it when a single integration should be limited on a single model only, for instance a demo key on the flagship model.',
        example: '6f1c…|gpt-4: 10 rpm, 0 burst, 1 stream. The key is unrestricted on other models beyond its tenant’s tier.'
      },
      anonymous: {
        name: 'Anonymous callers',
        what: 'Requests with no API key at all, which are only possible on models marked public. Each client address (an IPv6 /64 block) gets its own bucket of these numbers instead of the default tier. RPM 0 keeps the default rate and only caps streams.',
        when: 'Have exactly one whenever any model is public; without it anonymous callers fall back to the default tier and the gateway logs a warning. A fresh install ships 60 rpm, 20 burst, 2 streams; the wizard seeds a tighter 30/10/2.',
        example: 'Anonymous callers: 30 rpm, 10 burst, 2 streams. One address may make 30 requests a minute to public models and hold 2 streams open.'
      },
      auth_failure: {
        name: 'Failed sign-ins',
        what: 'Requests that present a credential and are refused by authentication: an unknown, expired or revoked key. Counted per client address, on inference and admin paths. It is rate-only: RPM must be above 0 and Streams 0. This budget is not switched off by the master switch; it has its own appsettings flag.',
        when: 'Keep it: it is what stops credential guessing. A valid key from the same address still passes, so a stale key on a shared NAT cannot lock out its neighbours. A fresh install ships 60 rpm, 20 burst; the wizard seeds 20/10.',
        example: 'Failed sign-ins: 20 rpm, 10 burst. After 30 rejected credentials in a minute, further bad keys from that address are refused with 429 without a database lookup.'
      }
    },

    sections: [
      {
        id: 'overview',
        title: 'What rate limiting does here',
        intro: 'Rate limiting bounds how fast callers may send inference requests through the gateway and how many streaming responses they may hold open. Over a limit a request is refused with HTTP 429 and a Retry-After header saying when to come back. Nothing is queued or slowed: a refused request is a failed one, and the client is expected to retry after the wait.',
        items: [
          { term: 'The master switch', text: 'Enforce rate limits turns everything on this page on or off at once. Off, every tier, rule and window stops applying, but nothing is deleted. Quotas, budgets and the failed sign-in protection are separate controls and keep working.', example: 'Switch it off for ten minutes during an incident; switch it on and every rule is back exactly as it was.' },
          { term: 'Adaptive load shedding', text: 'When on, the gateway may hold model limits below the configured numbers while it is saturated and restore them as load falls. It only ever tightens, and only model scopes. Clients can see it happening in the X-33pol-RateLimit-Adaptive header.', example: 'Header X-33pol-RateLimit-Adaptive: 420/600 means the scope is configured for 600 rpm and currently held at 420.' },
          { term: 'Applies without a restart', text: 'Saving the page writes to the gateway database and the new numbers are in force for the next request. Tightening bites immediately; a raised limit fills up at the new rate rather than jumping to full.', example: '' },
          { term: 'Response headers', text: 'Every inference response carries X-33pol-RateLimit-Limit (RPM + Burst), -Remaining, -Reset (seconds until full) and -Scope, which names the scope that refused, or on success the one closest to refusing. That is how a client tells “my key is exhausted” from “this model is busy”.', example: 'X-33pol-RateLimit-Scope: model with Remaining: 0 tells the client to try another model, not to slow its whole integration.' }
        ],
        tip: 'The Usage report at the bottom of the page shows live counters per tenant and model and which limits are being hit. It is the fastest way to see whether a rule is doing anything.'
      },
      {
        id: 'numbers',
        title: 'The three numbers: RPM, Burst, Streams',
        intro: 'Every tier, rule and window is the same three numbers. They describe a token bucket: a bucket that refills steadily and that a request takes one token from.',
        items: [
          { term: 'RPM — requests per minute', text: 'The sustained rate. The bucket refills at RPM ÷ 60 tokens a second, so a caller that keeps going may make this many requests a minute on average.', example: '600 rpm refills ten tokens a second.' },
          { term: 'Burst', text: 'Extra tokens above RPM. The bucket holds RPM + Burst tokens when full, so a caller that has been quiet may spend that many at once before it is held to the sustained rate. Burst is what lets a page load fire ten calls in parallel without being refused.', example: '600 rpm + 60 burst: after a quiet spell, 660 requests may arrive in one moment; then the rate settles to 600 a minute.' },
          { term: 'Streams — concurrent streaming responses', text: 'How many streaming responses one partition may hold open at the same time. 0 means unlimited, not “streaming denied”. Lowering it never aborts a stream already open.', example: '5 streams: the sixth streaming request from the same tenant is refused until one of the five finishes.' },
          { term: 'Zero in a rule', text: 'In a scoped rule RPM 0 means “this rule does not limit the rate”; the rule then only caps streams, and Burst must also be 0 because a burst is extra tokens above a rate that does not exist. A tenant rule with RPM 0 keeps the plan’s RPM and Burst and applies only its stream cap; the anonymous rule composes the same way against the default tier.', example: 'Model “llama-70b”: 0 rpm, 0 burst, 8 streams. Requests are not rate-limited by this rule, but at most 8 streams may be open on the model at once.' },
          { term: 'Zero in a tier', text: 'The default and plan tiers cannot have RPM 0. They are the one limit every tenant is held to, and a zero there would switch the gateway’s only universal control off silently. Use the master switch if you really want no limits.', example: '' },
          { term: 'Bounds', text: 'RPM 1 to 1,000,000 (0 allowed in rules), Burst 0 to 1,000,000, Streams 0 to 10,000. The drawer that owns a field refuses a value outside these before you can save.', example: '' }
        ],
        tip: 'Think “RPM for the steady state, Burst for the first moment, Streams for long-running answers”. Most deployments need a modest burst (10–20 % of RPM) so clients that fan out a few calls are not refused for being briefly busy.'
      },
      {
        id: 'tiers',
        title: 'Tiers: default, plan, tenant',
        intro: 'A tier is what a tenant is allowed. All API keys a tenant holds draw on one bucket, so a tenant with three keys gets one allowance, not three.',
        items: [
          { term: 'Default tier', text: 'What every tenant gets unless something more specific matches. It is the gateway’s universal limit, which is why its RPM must be at least 1.', example: 'Default: 60 rpm, 10 burst, 5 streams. A newly created tenant with no plan gets exactly this.' },
          { term: 'Plan tiers', text: 'A tier for every tenant on a given plan, matched by the plan slug the tenant carries. Add one per commercial plan and tenants move between them by changing their plan, without touching this page.', example: 'Plan “standard”: 120 rpm, 20 burst, 10 streams. Plan “pro”: 600 rpm, 60 burst, 40 streams.' },
          { term: 'Tenant rule', text: 'A rule with scope “A tenant” overrides the tenant’s plan and the default for that one tenant. This is the only place on the page where one setting overrides another: tenant rule beats plan tier beats default. Everywhere else, rules stack.', example: 'acme is on “standard” (120 rpm) but has a tenant rule of 1,200 rpm. acme gets 1,200. Removing the rule drops acme back to 120.' },
          { term: 'Key rules inside a tenant', text: 'A tenant’s keys share its tier. To bound one key separately, add a rule with scope “An API key”; the key is then held to both its own rule and the tenant’s bucket.', example: 'acme at 1,200 rpm hands a partner a key with a key rule of 100 rpm. The partner may take at most 100 of acme’s 1,200.' }
        ],
        tip: 'Prefer plan tiers to tenant rules for anything that is really a pricing decision. Tenant rules are for exceptions, and each one is a thing to remember later.'
      },
      {
        id: 'scopes',
        title: 'Rule scopes: the eight kinds',
        intro: 'A rule limits one thing. The scope says what kind of thing; the target says which one. Here is every scope, what it counts, when to reach for it, and an example.',
        items: [
          { term: 'A model', text: 'The model’s whole capacity, shared by every caller. Anonymous callers of a public model are counted in a separate bucket, and keys not granted the model are never charged against it. Use it when the model itself is scarce.', example: 'Model “gpt-4”: 600 rpm, 60 burst, 40 streams.' },
          { term: 'A tenant', text: 'Replaces the tenant’s tier. RPM 0 keeps the plan rate and only caps streams. Use it for a customer that needs an exception.', example: 'Tenant “acme”: 1,200 rpm, 200 burst, 20 streams.' },
          { term: 'An API key', text: 'One credential, inside its tenant’s allowance. Target is the key id from the API keys page, never the secret.', example: 'Key 6f1c…: 30 rpm, 0 burst, 2 streams.' },
          { term: 'Whole gateway', text: 'A ceiling on all inference traffic. It does not cover the admin API or the model list, which are metered by a separate control-plane budget in appsettings so that tightening a tenant can never lock an operator out.', example: 'Whole gateway: 5,000 rpm, 500 burst.' },
          { term: 'A tenant on a model', text: 'One customer’s share of one model. Target tenant|model; the tenant may be named by id or slug.', example: 'acme|gpt-4: 60 rpm, 10 burst, 4 streams.' },
          { term: 'A key on a model', text: 'One credential on one model, the narrowest scope. Target keyId|model.', example: '6f1c…|gpt-4: 10 rpm, 0 burst, 1 stream.' },
          { term: 'Anonymous callers', text: 'Callers with no key, only possible on public models, metered per client address (IPv6 by /64 block). One such rule replaces the default tier for them. Fresh installs ship 60/20/2; the wizard seeds 30/10/2.', example: 'Anonymous callers: 30 rpm, 10 burst, 2 streams.' },
          { term: 'Failed sign-ins', text: 'Credentials refused by authentication, per client address, on inference and admin paths. Rate-only (Streams must be 0), independent of the master switch. A key that validates still passes, so a shared address is not locked out by one stale key. Fresh installs ship 60/20; the wizard seeds 20/10.', example: 'Failed sign-ins: 20 rpm, 10 burst.' }
        ],
        tip: 'The wizard seeds the two protective scopes tighter than the generic 600/60 on purpose. Keeping a seed there tightens the control; keeping 600 would have loosened it.'
      },
      {
        id: 'combine',
        title: 'How rules, tiers and windows combine',
        intro: 'A request must find room in every scope that applies to it: the gateway ceiling, the caller’s tenant tier, its key rule, the model rule, and the tenant-on-model and key-on-model rules. Nothing overrides anything else, with the single exception inside the tenant tier described above.',
        items: [
          { term: 'Adding a rule only tightens', text: 'Because every applicable rule must admit the request, a new rule can never let a caller do more than before. There is no “most specific wins” to reason about. To loosen something, raise the number that is binding or remove the rule.', example: 'Model 600 rpm plus tenant 1,000 rpm: the tenant gets at most 600 on that model. Raising the tenant to 2,000 changes nothing there.' },
          { term: 'Refunds on refusal', text: 'Tokens are taken from each bucket in turn and handed back if a later scope refuses. A caller blocked by a narrow model rule does not also burn its tenant budget on every retry.', example: '' },
          { term: 'Targets are not validated', text: 'A rule for a model, tenant or key that does not exist is stored and never matches, which is useful while provisioning and a trap when it is a typo. Tenants match by id or slug; keys only by id.', example: 'A rule on “gpt4” while the model is registered as “gpt-4” does nothing. The Usage report shows no hits on it; fix the target.' },
          { term: 'Which scope refused?', text: 'The X-33pol-RateLimit-Scope response header names it, and the Usage report’s Limits being hit table lists refusals by scope and target.', example: '' },
          { term: 'Windows change a rule, not the rules around it', text: 'A window replaces its own rule’s numbers for a span of time. Other rules and tiers still apply as before, so an off-peak window on a model does not raise the tenants’ tiers.', example: 'Model 600 rpm with a night window of 1,200: a tenant on a 120 rpm plan still gets 120 at night.' }
        ],
        tip: 'When a caller reports unexpected 429s with a generous rule, look for another scope binding tighter: usually the tenant tier or the gateway ceiling.'
      },
      {
        id: 'windows',
        title: 'Schedule windows',
        intro: 'A rule keeps a base tier and may carry up to 16 windows, each giving different numbers for a span of time, or pausing the rule. The base tier applies whenever no window is active, and whenever a window cannot be evaluated.',
        items: [
          { term: 'Weekly window', text: 'Recurs on the days you tick, from a start to an end time, read in the chosen time zone. “Days” are the days the window starts. An end at or before the start runs into the next day; 00:00 to 24:00 is a whole day. All weekly windows on one rule share a time zone.', example: '“off-peak”, Mon–Fri, 19:00 to 07:00, Europe/Berlin, 1,200 rpm / 200 burst / 80 streams. Every weekday evening the model allows twice its daytime rate until the next morning.' },
          { term: 'One-time window', text: 'A single span from a start to an optional end. With no end it is a step change: the new numbers from that moment on, until you edit the rule.', example: '“launch”, from 1 Oct 00:00 to 3 Oct 00:00, 3,000 rpm. Or “new baseline”, from 1 Jan 00:00 with no end, to raise a limit on time without a midnight edit.' },
          { term: 'Pause this rule instead', text: 'The window suspends its rule: while it runs the rule is treated as absent and enforces nothing. Every other rule and tier still applies. A paused tenant rule hands the tenant back to its plan tier.', example: '“maintenance”, weekly, Sun 02:00–04:00: the strict model rule is lifted while a batch job runs.' },
          { term: 'Priority', text: 'When several windows are active at once, the highest priority (0–1000) wins. Without priorities a one-time window outranks a weekly one. Two windows that could run together with the same priority are refused when you save; same-kind overlaps are refused unless their priorities differ.', example: 'Evening window priority 10, promo window priority 5: evenings win while both run.' },
          { term: 'Valid from / Valid until', text: 'Optional bounds on when a weekly window exists at all. Use them to start a schedule next quarter or to let a trial arrangement expire on its own.', example: 'Valid until 31 Dec 23:59: the window stops recurring in the new year without anyone deleting it.' },
          { term: 'Time zones and daylight saving', text: 'Weekly times are wall-clock times in their zone. A start inside a spring-forward gap moves to the first valid instant; a window across the autumn repeat lasts an hour longer. A zone the server cannot resolve makes the window invalid: the base tier applies and the page marks the rule.', example: 'A 02:30 start on the night clocks jump 02:00 → 03:00 begins at 03:00.' },
          { term: 'At a boundary', text: 'Tightening applies on the partition’s next request. A raised limit refills at the new rate rather than jumping to full. Lowering a stream cap never aborts open streams. The adaptive governor scales whatever the schedule put in force. With the master switch off, windows enforce nothing either.', example: '' },
          { term: 'Preview before you apply', text: 'The window form shows its next occurrence, whether it is running now, which windows it overlaps and which it outranks, live as you type. Nothing is stored until you press Add and then Save.', example: '' }
        ],
        tip: 'Name windows for their purpose (“off-peak”, “launch”, “maintenance”): the name is what the calendar, the Coming up list and the transitions show.'
      },
      {
        id: 'calendar',
        title: 'Calendar, Coming up and Preview',
        intro: 'The calendar shows when limits change over the chosen range, in the chosen time zone. It includes windows you have added but not yet saved, and says so with a “draft” tag.',
        items: [
          { term: 'Rows and bands', text: 'Each row is a rule that has windows. A coloured band is a window in force; grey is the base tier; a faded band is in the past. The vertical line is now. Hover a band for its numbers; click a row label to open the rule.', example: '' },
          { term: 'Coming up', text: 'The next moments any rule’s numbers change, with the rule, the window and how the limit moves. Long lists are truncated; the toggle shows them all.', example: 'Fri 19:00 · gpt-4 · off-peak starts · 600 → 1,200 rpm.' },
          { term: 'Preview at', text: 'Pick a moment and the page answers what every rule would enforce then, which window would be responsible, and when it changes next. Use it to check a schedule before you save it.', example: 'Preview at Monday 09:00 confirms the night window has ended and the day tier is back.' }
        ],
        tip: 'Change the “Show times in” zone when you operate across regions: the windows are stored in their own zones, so the calendar can render them in yours.'
      },
      {
        id: 'saving',
        title: 'Drafts, review and saving',
        intro: 'The page is a summary; editing happens in drawers. A drawer’s Done stages the change into a draft. The gateway only changes when you Save.',
        items: [
          { term: 'The bar at the bottom', text: 'Appears while the draft differs from what the server has, counts the changes, and offers Discard, Review changes and Save. Leaving the tab with a dirty draft asks first.', example: '' },
          { term: 'Review changes', text: 'Lists every difference the save would send: tiers changed, rules added or removed, windows added, the master switch. Read it before a save that touches many rules.', example: '' },
          { term: 'Save sends the whole set', text: 'The API replaces the rate-limit configuration wholesale, so what you see on the page is exactly what will be enforced. If another operator saved since you loaded, the save is refused rather than overwriting their work; press Reload and apply your change again.', example: '' },
          { term: 'Switching a rule off', text: 'The On switch on a rule stages it as not enforced while keeping its tier and windows, so a rule can be retired for a while and brought back unchanged. Delete permanently removes it.', example: '' },
          { term: 'Read-only', text: 'When the page cannot be edited it says why at the top: the key lacks the admin role, or the server refused the last save. Editing controls are disabled until the reason is gone.', example: '' }
        ],
        tip: 'A rule that is dangerous to get wrong (the gateway ceiling, Failed sign-ins) deserves a look at the Usage report a few minutes after saving.'
      },
      {
        id: 'recipes',
        title: 'Worked examples',
        intro: 'Common situations and the rule that answers them. Each is one rule or one window; the surrounding tiers keep applying.',
        items: [
          { term: 'Protect an expensive model', text: 'New rule → Everyone → One model → pick the model → 600 rpm, 60 burst, 40 streams. Every caller together may not exceed that on the model.', example: 'The upstream provider allows 10,000 tokens a second; you set the model rule so total request rate stays inside it.' },
          { term: 'Give one customer a bigger share of one model', text: 'New rule → A tenant → One model → the tenant slug, then the model → 200 rpm. The tenant keeps its ordinary tier elsewhere.', example: 'acme|gpt-4 at 200 rpm while other tenants share what the model rule leaves.' },
          { term: 'Cap a noisy integration key', text: 'New rule → An API key → All models → pick the key → 30 rpm, 0 burst, 2 streams. Its tenant’s other keys are unaffected.', example: 'A partner’s webhook retries in a loop; the key rule holds it to 30 a minute without touching the tenant.' },
          { term: 'Raise a model’s limit at night', text: 'Open the model rule → Add window → Weekly → Mon–Fri, 19:00 to 07:00, your zone → higher numbers. Daytime keeps the base tier.', example: '“off-peak”: 1,200 rpm instead of 600 every weekday night.' },
          { term: 'A launch day', text: 'Open the model rule → Add window → Once → From the launch start, Until two days later → higher numbers. It outranks any weekly window while it runs.', example: '“launch”: 3,000 rpm from 1 Oct to 3 Oct.' },
          { term: 'A planned baseline change', text: 'Add a Once window with a From and no Until. The new numbers take effect on time and stay, without anyone editing at midnight.', example: '“new plan year”: 900 rpm from 1 Jan 00:00, open-ended.' },
          { term: 'Lift a rule during maintenance', text: 'Add a window with Pause this rule instead. While it runs the rule enforces nothing; every other limit still applies.', example: '“maintenance”, Sun 02:00–04:00 pauses the strict model rule for the weekly batch.' },
          { term: 'Stop credential guessing', text: 'Keep a Failed sign-ins rule. Fresh installs have 60/20; the wizard proposes 20/10. Streams must stay 0.', example: 'Failed sign-ins 20 rpm, 10 burst: an address is refused after 30 bad keys in a minute.' },
          { term: 'Bound anonymous traffic to public models', text: 'Keep an Anonymous callers rule whenever any model is public. Each address gets its own bucket.', example: 'Anonymous callers 30 rpm, 10 burst, 2 streams.' }
        ],
        tip: 'Add the rule, save, then open the Usage report: a rule that bites shows up under Limits being hit within a few minutes of real traffic.'
      },
      {
        id: 'faq',
        title: 'When something looks wrong',
        intro: 'The usual causes, in the order to check them.',
        items: [
          { term: 'My rule does nothing', text: 'Check the target for a typo: targets are not validated, so a wrong id is stored and never matches. Check the master switch and the rule’s On switch. Check whether a window is pausing it right now (the rule row says “In force now”).', example: '' },
          { term: 'A caller gets 429 despite a generous rule', text: 'Another scope is binding tighter: its tenant tier, a key rule, or the gateway ceiling. The X-33pol-RateLimit-Scope header on the refused response names the scope. Preview at the current time lists what every rule enforces.', example: '' },
          { term: 'I raised a limit and nothing changed yet', text: 'A raised limit refills at the new rate; it does not jump to full. Within a minute the caller sees the new rate.', example: '' },
          { term: 'Rate limiting is off but bad keys still get 429', text: 'Expected. Failed sign-ins is a security control and is not governed by the master switch; it has its own flag in appsettings.', example: '' },
          { term: 'The save was refused with a conflict', text: 'Someone else saved since you loaded the page. Press Reload, then apply your change again. Nothing of yours was written.', example: '' },
          { term: 'A rule is marked with a schedule error', text: 'One of its windows cannot be evaluated, usually a time zone the server does not know. The base tier applies meanwhile. Open the rule and fix the window.', example: '' }
        ],
        tip: 'The full operator reference lives in docs/runbooks/rate-limit-admin.md in the repository, including the JSON the API accepts.'
      }
    ]
  };

  const fa = {
    ui: {
      help: 'راهنما',
      helpTitle: 'راهنمای محدودیت نرخ را باز کن',
      eyebrow: 'راهنما',
      guide: 'محدودیت نرخ به زبان ساده',
      guideSub: 'هر تنظیم چه می‌کند، چطور با هم ترکیب می‌شوند، و مثال‌های کاربردی.',
      contents: 'فهرست',
      language: 'زبان',
      otherLang: 'English',
      example: 'مثال:',
      tip: 'نکته:',
      more: 'بیشتر بخوانید در راهنما',
      close: 'بستن',
      about: 'درباره',
      scopeHelpTitle: 'این انتخاب یعنی چه'
    },

    fields: {
      master: {
        title: 'کلید اصلی چه کار می‌کند؟',
        text: '«اعمال محدودیت نرخ» کلید اصلی است. وقتی خاموش باشد هیچ‌چیز این صفحه اعمال نمی‌شود: نه سطح، نه قاعده و نه پنجره زمانی. اعداد ذخیره می‌مانند و به‌محض روشن‌کردن دوباره اعمال می‌شوند. سهمیه‌ها، بودجه‌ها و محافظت در برابر ورود ناموفق از این کلید تأثیر نمی‌گیرند.',
        example: 'در یک حادثه، اعمال محدودیت را ده دقیقه خاموش می‌کنید تا صف درخواست‌ها خالی شود، بعد دوباره روشن می‌کنید. هیچ قاعده‌ای لازم نبود ویرایش یا دوباره ساخته شود.',
        topic: 'overview'
      },
      adaptive: {
        title: 'کاهش بار تطبیقی چیست؟',
        text: 'وقتی خودِ گیت‌وی زیر بار سنگین است، محدودیت مدل‌ها ممکن است موقتاً پایین‌تر از اعداد این صفحه بیاید و با کم‌شدن بار دوباره بالا برود. فقط دامنه‌های مدل تأثیر می‌گیرند، و هدر پاسخ X-33pol-RateLimit-Adaptive دقیقاً تا وقتی وجود دارد که یک دامنه پایین نگه داشته شده است.',
        example: 'قاعده مدل می‌گوید ۶۰۰ RPM. زیر بار سنگین، مدتی روی ۴۲۰ نگه داشته می‌شود؛ کلاینت‌ها در هدر تطبیقی 420/600 می‌بینند تا بار کم شود.',
        topic: 'overview'
      },
      numbers: {
        title: 'RPM، Burst و Streams — سه عدد اصلی',
        text: 'RPM نرخ پایدار است: هر درخواست‌دهنده در هر دقیقه چند درخواست می‌تواند بفرستد. Burst ظرفیت اضافه روی RPM است که یک درخواست‌دهندهٴ بیکار می‌تواند یک‌جا خرج کند؛ بنابراین بیشترین تعداد درخواستی که یک‌باره پذیرفته می‌شود RPM + Burst است. Streams تعداد پاسخ‌های استریمی است که هم‌زمان می‌توانند باز باشند؛ ۰ یعنی بدون سقف.',
        example: '۶۰ RPM، ۲۰ Burst، ۵ Streams: یک درخواست‌دهندهٴ آرام می‌تواند در یک لحظه ۸۰ درخواست بفرستد، بعد به یکی در ثانیه می‌رسد، و حداکثر ۵ استریم باز نگه می‌دارد.',
        topic: 'numbers'
      },
      ruleNumbers: {
        title: 'RPM صفر در یک قاعده',
        text: 'در یک قاعده، RPM صفر یعنی این قاعده اصلاً نرخ درخواست را محدود نمی‌کند؛ در این حالت فقط می‌تواند استریم‌ها را سقف بزند و Burst هم باید ۰ باشد. در قاعدهٴ تننت، RPM صفر نرخ پلن تننت را نگه می‌دارد و فقط استریم‌های آن را محدود می‌کند. سطح‌ها فرق دارند: سطح پیش‌فرض و سطح پلن باید RPM دست‌کم ۱ داشته باشند، چون محدودیت همگانی گیت‌وی هستند.',
        example: 'قاعدهٴ مدل با ۰ RPM، ۰ Burst، ۸ Streams: نرخ درخواست برای آن مدل نامحدود، اما هیچ‌وقت بیش از ۸ استریم هم‌زمان باز نمی‌شود.',
        topic: 'numbers'
      },
      tiers: {
        title: 'سطح چیست و هر کس کدام را می‌گیرد؟',
        text: 'سطح، سهمیه‌ای است که یک تننت (سازمان مشتری) می‌گیرد. هر تننت سطح پیش‌فرض را دارد مگر روی پلنی باشد که سطح خودش را دارد، و قاعدهٴ تننت روی هر دو غلبه می‌کند. همهٴ کلیدهای API یک تننت از یک سطل مشترک برمی‌دارند؛ برای محدودکردن یک کلید به‌تنهایی، قاعدهٴ کلید بسازید.',
        example: 'پیش‌فرض ۶۰ RPM. پلن «pro» ۶۰۰ RPM. تننت acme روی «pro» است اما قاعدهٴ تننت ۱٬۲۰۰ RPM دارد، پس acme ۱٬۲۰۰ می‌گیرد. بقیهٴ تننت‌های «pro» ۶۰۰ می‌گیرند.',
        topic: 'tiers'
      },
      planSlug: {
        title: 'اسلاگ پلن چیست؟',
        text: 'نام کوتاهی که تننت‌ها به‌عنوان پلن خود دارند، دقیقاً با همان املا. تننت‌هایی که پلنشان با این نام یکی است، به‌جای سطح پیش‌فرض این سطح را می‌گیرند. باید با حرف شروع شود و می‌تواند حرف، عدد، خط تیره و زیرخط داشته باشد.',
        example: 'تننت‌هایی که با پلن «standard» ساخته شده‌اند، سطح «standard» این صفحه را می‌گیرند. تننتی که پلن ندارد، یا پلنش سطح ندارد، سطح پیش‌فرض را می‌گیرد.',
        topic: 'tiers'
      },
      rules: {
        title: 'قاعده چیست؟',
        text: 'یک قاعده یک چیز مشخص را محدود می‌کند: یک مدل، یک تننت، یک کلید، یک تننت روی یک مدل، یک کلید روی یک مدل، کل گیت‌وی، درخواست‌دهندگان ناشناس، یا ورودهای ناموفق. هر قاعده‌ای که به یک درخواست مربوط است باید آن را بپذیرد، پس اضافه‌کردن قاعده فقط می‌تواند سخت‌گیرانه‌تر کند، نه آسان‌تر. هر قاعده یک سطح پایه دارد و می‌تواند پنجره‌های زمانی داشته باشد.',
        example: 'قاعدهٴ مدل «gpt-4» با ۶۰۰ RPM و قاعدهٴ تننت acme با ۱٬۰۰۰ RPM: acme روی gpt-4 همچنان فقط ۶۰۰ در دقیقه می‌گیرد، آن هم مشترک با همهٴ درخواست‌دهندگان دیگرِ آن مدل.',
        topic: 'scopes'
      },
      scope: {
        title: 'برای چه کسی، و روی کدام مدل؟',
        text: 'بگویید محدودیت برای چه کسی است و یک مدل را می‌پوشاند یا همه را؛ بقیه را کنسول خودش تعیین می‌کند. «کلید API» یک اعتبارنامه است. «تننت» همهٴ کلیدهای یک مشتری با هم است. «همه» یعنی همهٴ درخواست‌دهندگان با یک بودجهٴ مشترک. «یک مدل» فقط درخواست‌های همان مدل را می‌شمارد؛ «همهٴ مدل‌ها» یک شمارش روی همه‌چیز است. «درخواست‌دهندگان ناشناس» و «ورودهای ناموفق» دو بودجهٴ حفاظتی‌اند و نه مخاطب دارند و نه مدل.',
        example: 'یک مشتری مدام گران‌ترین مدل شما را غرق درخواست می‌کند اما جای دیگر مشکلی ندارد: «تننت» و «یک مدل» را انتخاب کنید، نه «همهٴ مدل‌ها».',
        topic: 'scopes'
      },
      target: {
        title: 'کدام کلید، تننت یا مدل؟',
        text: 'از فهرست انتخاب کنید. کلید API با نام، پیشوند یا شناسه‌اش پیدا می‌شود و باید وجود داشته باشد: قاعده روی شناسهٴ کلید ذخیره می‌شود، نه نام آن و نه خودِ رمز. نام مستعار مدل به شناسهٴ اصلی مدل ذخیره می‌شود، چون محدودیت‌ها با همان تطبیق داده می‌شوند. تننت با شناسه یا اسلاگش نوشته می‌شود؛ هر دو به همان تننت می‌رسند. وجود تننت بررسی نمی‌شود: غلط تایپی ذخیره می‌شود و هرگز تطبیق نمی‌کند.',
        example: 'تننت «acme» روی مدل «gpt-4» به شکل هدف acme|gpt-4 ذخیره می‌شود؛ کلید روی مدل به شکل شناسهٴ کلید و سپس ‎|gpt-4. بعداً در گزارش مصرف مطمئن شوید که اثر دارد: قاعدهٴ کارآمد در جدول «محدودیت‌های برخوردشده» ظاهر می‌شود یا «محدودیت جاری» یک ردیف را تغییر می‌دهد.',
        topic: 'combine'
      },
      limit: {
        title: 'چقدر؟',
        text: 'سطح پایهٴ این قاعده. خلاصهٴ زیر می‌گوید این دامنه امروز چه چیزی را اعمال می‌کند، تا ببینید عددی که وارد می‌کنید سخت‌گیرانه‌تر است یا نه. عددی که سخت‌گیرانه‌تر از وضع فعلی نباشد کاری نمی‌کند، اما مجاز است.',
        example: 'سقف گیت‌وی ۵٬۰۰۰ RPM است. قاعدهٴ مدل با ۶٬۰۰۰ RPM هیچ‌چیز را تغییر نمی‌دهد؛ ۶۰۰ RPM تغییر می‌دهد.',
        topic: 'numbers'
      },
      baseTier: {
        title: 'سطح پایه چیست؟',
        text: 'اعدادی که این قاعده هر وقت هیچ پنجرهٴ زمانی فعال نیست اعمال می‌کند، و همچنین هر وقت پنجره‌ای قابل ارزیابی نباشد (مثلاً منطقهٴ زمانی ناشناخته). پنجره‌ها این اعداد را برای یک بازهٴ زمانی جایگزین می‌کنند؛ سطح پایه در باقی زمان‌ها اعمال می‌شود.',
        example: 'سطح پایه ۶۰۰ RPM با پنجرهٴ «خارج از پیک» ۱٬۲۰۰ RPM از ۱۹:۰۰ تا ۰۷:۰۰: در روز مدل ۶۰۰ در دقیقه می‌پذیرد، در شب ۱٬۲۰۰.',
        topic: 'windows'
      },
      windows: {
        title: 'پنجرهٴ زمانی چیست؟',
        text: 'پنجره به یک قاعده برای یک بازهٴ زمانی اعداد متفاوتی می‌دهد: هفتگی (همان ساعت‌ها در روزهای انتخابی) یا یک‌باره (یک بازه با شروع و پایانِ اختیاری). پنجره می‌تواند به‌جای آن قاعده را کلاً متوقف کند. هر قاعده تا ۱۶ پنجره می‌تواند داشته باشد. پنجره‌های هم‌نوع نمی‌توانند هم‌پوشانی داشته باشند؛ پنجرهٴ یک‌باره بر هفتگی برتری دارد.',
        example: 'پنجرهٴ هفتگی «خارج از پیک» هر شب RPM یک مدل را بالا می‌برد، و پنجرهٴ یک‌بارهٴ «روز عرضه» برای ۴۸ ساعت بیشتر بالا می‌برد؛ در شبِ عرضه، پنجرهٴ یک‌باره برنده است.',
        topic: 'windows'
      },
      windowKind: {
        title: 'هفتگی یا یک‌باره؟',
        text: 'هفتگی تکرار می‌شود: روزهای شروع، ساعت شروع و پایان، و منطقهٴ زمانی‌ای که این ساعت‌ها در آن خوانده می‌شوند را انتخاب کنید. پایانی که برابر یا قبل از شروع باشد تا صبح روز بعد ادامه می‌یابد؛ ۰۰:۰۰ تا ۲۴:۰۰ یک روز کامل است. یک‌باره یک بازهٴ تکی است: شروع، و پایانی که می‌توانید خالی بگذارید تا تغییری باشد که از آن لحظه شروع شود و برقرار بماند.',
        example: 'هفتگی، دوشنبه تا جمعه، ۱۹:۰۰ تا ۰۷:۰۰، Europe/Berlin: هر عصرِ روز کاری تا صبح بعد. یک‌باره، از ۱ اکتبر ۰۰:۰۰ بدون پایان: یک تغییر برنامه‌ریزی‌شده که سر وقت اعمال می‌شود بی‌آنکه کسی نیمه‌شب اعداد پایه را ویرایش کند.',
        topic: 'windows'
      },
      suspend: {
        title: '«توقف موقت این قاعده» چه می‌کند؟',
        text: 'در مدت اجرای پنجره، قاعده مثل این است که وجود ندارد: هیچ‌چیز توسط آن اعمال نمی‌شود. همهٴ قاعده‌ها و سطح‌های دیگر همچنان اعمال می‌شوند، پس قاعدهٴ مدلِ متوقف‌شده، درخواست‌دهندگان را در سطح تننت خودشان نگه می‌دارد. قاعدهٴ تننتِ متوقف‌شده، تننت را به سطح پلن خودش برمی‌گرداند.',
        example: 'پنجرهٴ نگهداری یکشنبه ۰۲:۰۰ تا ۰۴:۰۰ قاعدهٴ سخت‌گیرانهٴ یک مدل را در زمان اجرای کار دسته‌ای متوقف می‌کند؛ سطح‌های تننت همچنان هر درخواست‌دهنده را محدود می‌کنند.',
        topic: 'windows'
      },
      timeZone: {
        title: 'کدام منطقهٴ زمانی؟',
        text: 'ساعت‌های هفتگی، ساعت دیواری در منطقه‌ای هستند که انتخاب می‌کنید، با قواعد ساعت تابستانی همان منطقه. شروعی که در شکاف جلوکشیدن ساعت بیفتد به اولین لحظهٴ معتبر منتقل می‌شود؛ پنجره‌ای که روی تکرار ساعت پاییزی بیفتد یک ساعت طولانی‌تر است. همهٴ پنجره‌های هفتگی یک قاعده باید یک منطقهٴ زمانی داشته باشند.',
        example: 'شروع ۰۲:۳۰ در Europe/Berlin در شبی که ساعت از ۰۲:۰۰ به ۰۳:۰۰ می‌پرد، آن روز از ۰۳:۰۰ آغاز می‌شود.',
        topic: 'windows'
      },
      priority: {
        title: 'اولویت و بازهٴ اعتبار',
        text: 'وقتی چند پنجره هم‌زمان فعال‌اند، بالاترین اولویت برنده است؛ بدون اولویت، پنجرهٴ یک‌باره بر هفتگی برتری دارد. دو پنجره که می‌توانند هم‌زمان فعال باشند و اولویت یکسان دارند، در زمان ذخیره رد می‌شوند. «معتبر از» و «معتبر تا» تعیین می‌کنند پنجرهٴ هفتگی اصلاً از چه زمانی تا چه زمانی وجود دارد، تا بتواند از فصل بعد شروع شود یا بعد از دورهٴ آزمایشی تمام شود.',
        example: 'پنجرهٴ عصرگاهی هفتگی با اولویت ۱۰ و پنجرهٴ تبلیغاتی یک‌باره با اولویت ۵: وقتی هر دو اجرا می‌شوند، پنجرهٴ عصرگاهی برنده است. «معتبر تا» را ۳۱ دسامبر بگذارید تا پنجرهٴ عصرگاهی خودکار بازنشسته شود.',
        topic: 'windows'
      },
      calendar: {
        title: 'خواندن تقویم',
        text: 'هر ردیف یک قاعدهٴ دارای پنجره است؛ نوارهای رنگی نشان می‌دهند پنجره کِی برقرار است و نوار خاکستری سطح پایه است. خط عمودی «اکنون» است. «در پیش» لحظه‌های بعدی تغییر اعداد یک قاعده را فهرست می‌کند. «پیش‌نمایش در» می‌گوید هر قاعده در لحظهٴ انتخابی چه چیزی را اعمال می‌کند، حتی برای پنجره‌هایی که اضافه کرده‌اید اما هنوز ذخیره نکرده‌اید.',
        example: '«پیش‌نمایش در» را روی دوشنبهٴ بعد ۰۹:۰۰ بگذارید تا مطمئن شوید پنجرهٴ خارج از پیک تمام شده و سطح پایه پیش از ساعت کاری برگشته است.',
        topic: 'calendar'
      },
      combine: {
        title: 'قاعده‌ها، سطح‌ها و پنجره‌ها چطور ترکیب می‌شوند',
        text: 'یک درخواست باید در هر قاعده‌ای که به آن مربوط است جا داشته باشد: سقف گیت‌وی، سطح تننتِ درخواست‌دهنده، قاعدهٴ کلیدش، قاعدهٴ مدل، و قاعده‌های تننت‌روی‌مدل و کلید‌روی‌مدل. هیچ‌چیز بر چیز دیگری غلبه نمی‌کند، پس اضافه‌کردن قاعده فقط می‌تواند اجازهٴ درخواست‌دهنده را کمتر کند. تنها استثنا درون سطح تننت است: قاعدهٴ تننت بر سطح پلن، و سطح پلن بر پیش‌فرض برتری دارد. پنجره اعداد قاعدهٴ خودش را برای یک بازه تغییر می‌دهد؛ وقتی هیچ پنجره‌ای فعال نیست، سطح پایه اعمال می‌شود.',
        example: 'مدل gpt-4 با ۶۰۰ RPM، تننت acme با ۱٬۲۰۰ RPM، acme|gpt-4 با ۶۰ RPM: acme روی gpt-4 حداکثر ۶۰ در دقیقه می‌گیرد، روی بقیه ۱٬۲۰۰، و ۶۰۰ِ مدل را با همه به اشتراک می‌گذارد.',
        topic: 'combine'
      },
      save: {
        title: 'تغییرات کِی اعمال می‌شوند؟',
        text: 'ویرایش‌های یک کشو با زدن «انجام شد» در یک پیش‌نویس ذخیره می‌شوند. تا وقتی «ذخیره» را در نوار پایین نزنید هیچ‌چیز به گیت‌وی نمی‌رسد؛ «بازبینی تغییرات» فهرست آنچه ارسال می‌شود را نشان می‌دهد و «لغو» پیش‌نویس را دور می‌ریزد. ذخیره بدون راه‌اندازی مجدد اعمال می‌شود. اگر در این میان کس دیگری ذخیره کرده باشد، ذخیرهٴ شما رد می‌شود؛ بازخوانی کنید و تغییر را دوباره اعمال کنید.',
        example: 'سه قاعده را ویرایش می‌کنید و یک پنجره اضافه می‌کنید. نوار می‌گوید ۴ تغییر؛ بازبینی هر یک را نشان می‌دهد؛ ذخیره همه را با هم می‌فرستد. محدودیت سخت‌تر از درخواست بعدی اثر می‌کند؛ محدودیت بالاتر با نرخ جدید پر می‌شود، نه یک‌باره.',
        topic: 'saving'
      }
    },

    scopes: {
      model: {
        name: 'همه روی یک مدل',
        what: 'کل نرخ درخواست و استریم‌های باز یک مدل را برای همهٴ درخواست‌دهندگان سقف می‌زند. سطل مشترک است: هر کس سریع‌تر باشد بیشتر می‌گیرد. درخواست‌دهندگان ناشناس یک مدل عمومی در سطل جداگانه‌ای شمرده می‌شوند تا نتوانند تننت‌ها را محروم کنند، و کلیدی که به آن مدل دسترسی ندارد هیچ‌وقت از این سطل کم نمی‌کند.',
        when: 'وقتی خودِ مدل منبع کمیاب است: یک سرویس بالادستی گران، یک GPU تکی، سهمیهٴ یک ارائه‌دهنده.',
        example: 'مدل «gpt-4»: ۶۰۰ RPM، ۶۰ Burst، ۴۰ Streams. همهٴ تننت‌ها با هم نمی‌توانند روی gpt-4 از ۶۰۰ در دقیقه بیشتر بفرستند.'
      },
      tenant: {
        name: 'یک تننت، همهٴ مدل‌ها',
        what: 'سطح پلن یا پیش‌فرض تننت را با این اعداد جایگزین می‌کند. همهٴ کلیدهای تننت از همین یک سطل استفاده می‌کنند. RPM صفر در اینجا نرخ پلن را نگه می‌دارد و فقط استریم‌ها را محدود می‌کند.',
        when: 'وقتی یک مشتری بیشتر یا کمتر از پلنش لازم دارد، بی‌آنکه به پلن دیگری منتقل شود.',
        example: 'تننت «acme»: ۱٬۲۰۰ RPM، ۲۰۰ Burst، ۲۰ Streams، در حالی که سطح پلنش ۶۰۰ RPM است. acme حالا ۱٬۲۰۰ می‌گیرد؛ تننت‌های دیگرِ همان پلن تغییری نمی‌کنند.'
      },
      api_key: {
        name: 'یک کلید API، همهٴ مدل‌ها',
        what: 'یک اعتبارنامه را به‌تنهایی محدود می‌کند، درون هر آنچه تننتِ آن مجاز است. کلید همچنان از سطل تننت هم کم می‌کند؛ این قاعده تعیین می‌کند یک کلید حداکثر چقدر از آن سطل می‌تواند بگیرد.',
        when: 'برای یک اتصال پرسروصدا یا کلیدی که به یک شریک داده شده، تا نتواند همهٴ سهمیهٴ تننت را خرج کند.',
        example: 'کلید 6f1c…: ۳۰ RPM، ۰ Burst، ۲ Streams. تننتی با ۶۰۰ RPM هیچ‌وقت نمی‌بیند این کلید بیش از ۳۰ تای آن را بگیرد.'
      },
      global: {
        name: 'همه، همهٴ مدل‌ها (کل گیت‌وی)',
        what: 'سقفی روی هر درخواست استنتاج، فارغ از فرستنده. API مدیریتی و فهرست مدل‌ها را نمی‌سنجد؛ آن‌ها بودجهٴ جداگانه‌ای در appsettings دارند.',
        when: 'برای محافظت از گیت‌وی یا قرارداد مشترک بالادستی در برابر مجموع همهٴ تننت‌ها. عدد پیشنهادی ندارد: عددی که برای همهٴ استقرارها مناسب باشد وجود ندارد.',
        example: 'کل گیت‌وی: ۵٬۰۰۰ RPM، ۵۰۰ Burst. حتی با سطح‌های سخاوتمندانهٴ تننت‌ها، گیت‌وی در هر دقیقه حداکثر ۵٬۰۰۰ درخواست به بالادست می‌فرستد.'
      },
      tenant_model: {
        name: 'یک تننت روی یک مدل',
        what: 'سهم یک تننت از یک مدل. روی قاعدهٴ مدل و سطح تننت سوار می‌شود؛ درخواست باید در همهٴ آن‌ها جا داشته باشد.',
        when: 'برای دادن سهم منصفانه از یک مدل کمیاب، یا برای اینکه مصرف سنگین یک مشتری روی یک مدل، به ترافیک دیگرش آسیب نزند.',
        example: 'acme|gpt-4: ۶۰ RPM، ۱۰ Burst، ۴ Streams. acme جای دیگر ۱٬۲۰۰ RPM خود را نگه می‌دارد اما روی gpt-4 فقط ۶۰ در دقیقه می‌گیرد.'
      },
      api_key_model: {
        name: 'یک کلید API روی یک مدل',
        what: 'باریک‌ترین محدودیت: یک اعتبارنامه روی یک مدل. فقط درخواست‌های همان کلید به همان مدل شمرده می‌شود؛ مدل‌های دیگرِ آن کلید و کلیدهای دیگر روی همان مدل شمرده نمی‌شوند.',
        when: 'وقتی یک اتصال فقط روی یک مدل باید محدود شود، مثلاً کلید دمو روی مدل اصلی.',
        example: '6f1c…|gpt-4: ۱۰ RPM، ۰ Burst، ۱ Stream. این کلید روی مدل‌های دیگر فقط به سطح تننت خودش محدود است.'
      },
      anonymous: {
        name: 'درخواست‌دهندگان ناشناس',
        what: 'درخواست‌هایی بدون هیچ کلید API، که فقط روی مدل‌های عمومی ممکن‌اند. هر نشانی کلاینت (برای IPv6 بلوک /64) به‌جای سطح پیش‌فرض، سطلی از این اعداد می‌گیرد. RPM صفر نرخ پیش‌فرض را نگه می‌دارد و فقط استریم‌ها را محدود می‌کند.',
        when: 'هر وقت مدلی عمومی است دقیقاً یکی از این قاعده داشته باشید؛ بدون آن، ناشناس‌ها به سطح پیش‌فرض می‌افتند و گیت‌وی هشدار می‌دهد. نصب تازه با ۶۰ RPM، ۲۰ Burst، ۲ Streams می‌آید؛ جادوگر مقدار سخت‌گیرانه‌ترِ ۳۰/۱۰/۲ را پیشنهاد می‌کند.',
        example: 'درخواست‌دهندگان ناشناس: ۳۰ RPM، ۱۰ Burst، ۲ Streams. یک نشانی می‌تواند در دقیقه ۳۰ درخواست به مدل‌های عمومی بفرستد و ۲ استریم باز نگه دارد.'
      },
      auth_failure: {
        name: 'ورودهای ناموفق',
        what: 'درخواست‌هایی که اعتبارنامه ارائه می‌دهند و احراز هویت آن‌ها را رد می‌کند: کلید ناشناخته، منقضی یا باطل‌شده. به‌ازای هر نشانی کلاینت، در مسیرهای استنتاج و مدیریتی شمرده می‌شود. فقط نرخی است: RPM باید بالای ۰ و Streams برابر ۰ باشد. این بودجه با کلید اصلی خاموش نمی‌شود؛ پرچم خودش را در appsettings دارد.',
        when: 'آن را نگه دارید: همین است که حدس‌زدن اعتبارنامه را متوقف می‌کند. کلید معتبر از همان نشانی همچنان می‌گذرد، پس یک کلید قدیمی روی NAT مشترک همسایه‌هایش را قفل نمی‌کند. نصب تازه با ۶۰ RPM، ۲۰ Burst می‌آید؛ جادوگر ۲۰/۱۰ پیشنهاد می‌کند.',
        example: 'ورودهای ناموفق: ۲۰ RPM، ۱۰ Burst. پس از ۳۰ اعتبارنامهٴ ردشده در یک دقیقه، کلیدهای نامعتبر بعدی از آن نشانی بدون مراجعه به پایگاه داده با 429 رد می‌شوند.'
      }
    },

    sections: [
      {
        id: 'overview',
        title: 'محدودیت نرخ اینجا چه می‌کند',
        intro: 'محدودیت نرخ تعیین می‌کند درخواست‌دهندگان با چه سرعتی می‌توانند درخواست استنتاج از گیت‌وی بگذرانند و چند پاسخ استریمی را هم‌زمان باز نگه دارند. بالای محدودیت، درخواست با کد HTTP 429 و هدر Retry-After رد می‌شود که می‌گوید کِی برگردد. هیچ‌چیز صف یا آهسته نمی‌شود: درخواست ردشده یعنی درخواست ناموفق، و از کلاینت انتظار می‌رود پس از انتظار دوباره تلاش کند.',
        items: [
          { term: 'کلید اصلی', text: '«اعمال محدودیت نرخ» همه‌چیزِ این صفحه را یک‌جا روشن یا خاموش می‌کند. وقتی خاموش است هیچ سطح، قاعده یا پنجره‌ای اعمال نمی‌شود، اما هیچ‌چیز حذف نمی‌شود. سهمیه‌ها، بودجه‌ها و محافظت در برابر ورود ناموفق کنترل‌های جدا هستند و به کارشان ادامه می‌دهند.', example: 'در یک حادثه ده دقیقه خاموشش کنید؛ روشن کنید و همهٴ قاعده‌ها دقیقاً همان‌طور که بودند برمی‌گردند.' },
          { term: 'کاهش بار تطبیقی', text: 'وقتی روشن است، گیت‌وی می‌تواند در زمان اشباع، محدودیت مدل‌ها را زیر اعداد تنظیم‌شده نگه دارد و با کم‌شدن بار برگرداند. فقط سخت‌گیرانه‌تر می‌کند، و فقط دامنه‌های مدل را. کلاینت‌ها آن را در هدر X-33pol-RateLimit-Adaptive می‌بینند.', example: 'هدر X-33pol-RateLimit-Adaptive: 420/600 یعنی این دامنه برای ۶۰۰ RPM تنظیم شده و اکنون روی ۴۲۰ نگه داشته شده است.' },
          { term: 'بدون راه‌اندازی مجدد اعمال می‌شود', text: 'ذخیرهٴ صفحه در پایگاه دادهٴ گیت‌وی نوشته می‌شود و اعداد جدید برای درخواست بعدی برقرارند. سخت‌ترکردن فوراً اثر می‌کند؛ محدودیت بالاتر با نرخ جدید پر می‌شود و یک‌باره پر نمی‌شود.', example: '' },
          { term: 'هدرهای پاسخ', text: 'هر پاسخ استنتاج، هدرهای X-33pol-RateLimit-Limit (RPM + Burst)، -Remaining، -Reset (ثانیه تا پرشدن) و -Scope را دارد؛ Scope دامنه‌ای را نام می‌برد که رد کرده، یا در موفقیت، نزدیک‌ترین به ردکردن را. کلاینت از همین می‌فهمد «کلید من تمام شده» یا «این مدل شلوغ است».', example: 'X-33pol-RateLimit-Scope: model با Remaining: 0 به کلاینت می‌گوید مدل دیگری را امتحان کند، نه اینکه کل اتصالش را آهسته کند.' }
        ],
        tip: 'گزارش مصرف در پایین صفحه، شمارنده‌های زنده به‌ازای تننت و مدل و محدودیت‌های برخوردشده را نشان می‌دهد. سریع‌ترین راه برای فهمیدن این‌که یک قاعده کاری می‌کند یا نه، همین است.'
      },
      {
        id: 'numbers',
        title: 'سه عدد: RPM، Burst، Streams',
        intro: 'هر سطح، قاعده و پنجره همین سه عدد است. این‌ها یک «سطل توکن» را توصیف می‌کنند: سطلی که با نرخ ثابت پر می‌شود و هر درخواست یک توکن از آن برمی‌دارد.',
        items: [
          { term: 'RPM — درخواست در دقیقه', text: 'نرخ پایدار. سطل با RPM ÷ ۶۰ توکن در ثانیه پر می‌شود، پس درخواست‌دهنده‌ای که پیوسته می‌فرستد، به‌طور متوسط این تعداد درخواست در دقیقه می‌تواند بدهد.', example: '۶۰۰ RPM یعنی ده توکن در ثانیه پر می‌شود.' },
          { term: 'Burst — ظرفیت لحظه‌ای', text: 'توکن‌های اضافه روی RPM. سطلِ پر، RPM + Burst توکن دارد، پس درخواست‌دهنده‌ای که مدتی ساکت بوده می‌تواند این تعداد را یک‌جا خرج کند و بعد به نرخ پایدار محدود شود. Burst همان چیزی است که اجازه می‌دهد بارگذاری یک صفحه ده فراخوانی هم‌زمان بزند و رد نشود.', example: '۶۰۰ RPM + ۶۰ Burst: پس از یک دورهٴ سکوت، ۶۶۰ درخواست در یک لحظه پذیرفته می‌شود؛ بعد نرخ به ۶۰۰ در دقیقه می‌رسد.' },
          { term: 'Streams — پاسخ‌های استریمی هم‌زمان', text: 'یک بخش (پارتیشن) چند پاسخ استریمی را می‌تواند هم‌زمان باز نگه دارد. ۰ یعنی نامحدود، نه «استریم ممنوع». کم‌کردن آن هیچ استریم بازی را قطع نمی‌کند.', example: '۵ Streams: ششمین درخواست استریمی از همان تننت رد می‌شود تا یکی از پنج‌تا تمام شود.' },
          { term: 'صفر در یک قاعده', text: 'در قاعدهٴ دامنه‌دار، RPM صفر یعنی «این قاعده نرخ را محدود نمی‌کند»؛ قاعده فقط استریم‌ها را سقف می‌زند، و Burst هم باید ۰ باشد چون Burst توکن اضافه روی نرخی است که وجود ندارد. قاعدهٴ تننت با RPM صفر، RPM و Burst پلن را نگه می‌دارد و فقط سقف استریم خودش را اعمال می‌کند؛ قاعدهٴ ناشناس هم به همین شکل با سطح پیش‌فرض ترکیب می‌شود.', example: 'مدل «llama-70b»: ۰ RPM، ۰ Burst، ۸ Streams. درخواست‌ها با این قاعده محدودیت نرخ ندارند، اما حداکثر ۸ استریم روی مدل هم‌زمان باز می‌شود.' },
          { term: 'صفر در یک سطح', text: 'سطح پیش‌فرض و سطح پلن نمی‌توانند RPM صفر داشته باشند. آن‌ها تنها محدودیتی هستند که هر تننت به آن مقید است، و صفر در آنجا بی‌سروصدا تنها کنترل همگانی گیت‌وی را خاموش می‌کرد. اگر واقعاً می‌خواهید هیچ محدودیتی نباشد، از کلید اصلی استفاده کنید.', example: '' },
          { term: 'حدود', text: 'RPM از ۱ تا ۱٬۰۰۰٬۰۰۰ (در قاعده‌ها ۰ هم مجاز است)، Burst از ۰ تا ۱٬۰۰۰٬۰۰۰، Streams از ۰ تا ۱۰٬۰۰۰. کشویی که فیلد را دارد، مقدار خارج از این حدود را پیش از ذخیره رد می‌کند.', example: '' }
        ],
        tip: 'این‌طور فکر کنید: «RPM برای حالت پایدار، Burst برای لحظهٴ اول، Streams برای پاسخ‌های طولانی». اغلب استقرارها به Burst متوسطی (۱۰ تا ۲۰ درصد RPM) نیاز دارند تا کلاینت‌هایی که چند فراخوانی هم‌زمان می‌زنند به‌خاطر شلوغیِ لحظه‌ای رد نشوند.'
      },
      {
        id: 'tiers',
        title: 'سطح‌ها: پیش‌فرض، پلن، تننت',
        intro: 'سطح یعنی یک تننت چقدر مجاز است. همهٴ کلیدهای API یک تننت از یک سطل برمی‌دارند، پس تننتی با سه کلید یک سهمیه می‌گیرد، نه سه.',
        items: [
          { term: 'سطح پیش‌فرض', text: 'آنچه هر تننت می‌گیرد مگر چیز مشخص‌تری منطبق شود. این محدودیت همگانی گیت‌وی است، و به همین دلیل RPM آن باید دست‌کم ۱ باشد.', example: 'پیش‌فرض: ۶۰ RPM، ۱۰ Burst، ۵ Streams. تننتی که تازه ساخته شده و پلن ندارد دقیقاً همین را می‌گیرد.' },
          { term: 'سطح‌های پلن', text: 'سطحی برای همهٴ تننت‌های یک پلن، با تطبیق اسلاگ پلنی که تننت دارد. برای هر پلن تجاری یکی بسازید؛ تننت‌ها با تغییر پلنشان بین آن‌ها جابه‌جا می‌شوند، بدون دست‌زدن به این صفحه.', example: 'پلن «standard»: ۱۲۰ RPM، ۲۰ Burst، ۱۰ Streams. پلن «pro»: ۶۰۰ RPM، ۶۰ Burst، ۴۰ Streams.' },
          { term: 'قاعدهٴ تننت', text: 'قاعده‌ای با دامنهٴ «یک تننت» برای همان یک تننت، پلن و پیش‌فرض را کنار می‌زند. این تنها جای صفحه است که یک تنظیم بر دیگری غلبه می‌کند: قاعدهٴ تننت بر سطح پلن، سطح پلن بر پیش‌فرض. در همه‌جای دیگر، قاعده‌ها روی هم سوار می‌شوند.', example: 'acme روی «standard» (۱۲۰ RPM) است اما قاعدهٴ تننت ۱٬۲۰۰ RPM دارد. acme ۱٬۲۰۰ می‌گیرد. حذف قاعده، acme را به ۱۲۰ برمی‌گرداند.' },
          { term: 'قاعدهٴ کلید درون یک تننت', text: 'کلیدهای یک تننت سطح آن را به اشتراک می‌گذارند. برای محدودکردن جداگانهٴ یک کلید، قاعده‌ای با دامنهٴ «یک کلید API» بسازید؛ آن کلید هم به قاعدهٴ خودش و هم به سطل تننت مقید می‌شود.', example: 'acme با ۱٬۲۰۰ RPM به یک شریک کلیدی می‌دهد با قاعدهٴ کلید ۱۰۰ RPM. شریک حداکثر ۱۰۰ تا از ۱٬۲۰۰ acme را می‌تواند بگیرد.' }
        ],
        tip: 'برای هر چیزی که واقعاً یک تصمیم قیمت‌گذاری است، سطح پلن را به قاعدهٴ تننت ترجیح دهید. قاعدهٴ تننت برای استثناهاست، و هر کدام چیزی است که بعداً باید به یاد بیاورید.'
      },
      {
        id: 'scopes',
        title: 'دامنه‌های قاعده: هشت نوع',
        intro: 'یک قاعده یک چیز را محدود می‌کند. دامنه می‌گوید چه نوع چیزی؛ هدف می‌گوید کدام یکی. اینجا همهٴ دامنه‌ها را می‌بینید: چه چیزی را می‌شمارد، کِی به کار می‌آید، و یک مثال.',
        items: [
          { term: 'یک مدل', text: 'کل ظرفیت مدل، مشترک بین همهٴ درخواست‌دهندگان. ناشناس‌های یک مدل عمومی در سطل جداگانه شمرده می‌شوند، و کلیدهایی که به مدل دسترسی ندارند هیچ‌وقت از آن کم نمی‌کنند. وقتی خودِ مدل کمیاب است به کار می‌آید.', example: 'مدل «gpt-4»: ۶۰۰ RPM، ۶۰ Burst، ۴۰ Streams.' },
          { term: 'یک تننت', text: 'سطح تننت را جایگزین می‌کند. RPM صفر نرخ پلن را نگه می‌دارد و فقط استریم‌ها را محدود می‌کند. برای مشتری‌ای که استثنا لازم دارد.', example: 'تننت «acme»: ۱٬۲۰۰ RPM، ۲۰۰ Burst، ۲۰ Streams.' },
          { term: 'یک کلید API', text: 'یک اعتبارنامه، درون سهمیهٴ تننت خودش. هدف، شناسهٴ کلید از صفحهٴ کلیدهای API است، هرگز خودِ رمز نیست.', example: 'کلید 6f1c…: ۳۰ RPM، ۰ Burst، ۲ Streams.' },
          { term: 'کل گیت‌وی', text: 'سقفی روی همهٴ ترافیک استنتاج. API مدیریتی و فهرست مدل‌ها را پوشش نمی‌دهد؛ آن‌ها با بودجهٴ جداگانه‌ای در appsettings سنجیده می‌شوند تا سخت‌کردن یک تننت هیچ‌وقت نتواند اپراتور را بیرون قفل کند.', example: 'کل گیت‌وی: ۵٬۰۰۰ RPM، ۵۰۰ Burst.' },
          { term: 'یک تننت روی یک مدل', text: 'سهم یک مشتری از یک مدل. هدف tenant|model؛ تننت با شناسه یا اسلاگ نام‌برده می‌شود.', example: 'acme|gpt-4: ۶۰ RPM، ۱۰ Burst، ۴ Streams.' },
          { term: 'یک کلید روی یک مدل', text: 'یک اعتبارنامه روی یک مدل، باریک‌ترین دامنه. هدف keyId|model.', example: '6f1c…|gpt-4: ۱۰ RPM، ۰ Burst، ۱ Stream.' },
          { term: 'درخواست‌دهندگان ناشناس', text: 'درخواست‌دهندگان بدون کلید، که فقط روی مدل‌های عمومی ممکن‌اند، به‌ازای هر نشانی کلاینت (IPv6 با بلوک /64). یک چنین قاعده‌ای سطح پیش‌فرض را برای آن‌ها جایگزین می‌کند. نصب تازه ۶۰/۲۰/۲ دارد؛ جادوگر ۳۰/۱۰/۲ پیشنهاد می‌کند.', example: 'درخواست‌دهندگان ناشناس: ۳۰ RPM، ۱۰ Burst، ۲ Streams.' },
          { term: 'ورودهای ناموفق', text: 'اعتبارنامه‌هایی که احراز هویت رد می‌کند، به‌ازای هر نشانی کلاینت، در مسیرهای استنتاج و مدیریتی. فقط نرخی (Streams باید ۰ باشد)، مستقل از کلید اصلی. کلید معتبر همچنان می‌گذرد، پس یک نشانی مشترک با یک کلید قدیمی قفل نمی‌شود. نصب تازه ۶۰/۲۰ دارد؛ جادوگر ۲۰/۱۰ پیشنهاد می‌کند.', example: 'ورودهای ناموفق: ۲۰ RPM، ۱۰ Burst.' }
        ],
        tip: 'جادوگر دو دامنهٴ محافظتی را عمداً سخت‌گیرانه‌تر از ۶۰۰/۶۰ عمومی پیشنهاد می‌کند. نگه‌داشتن پیشنهاد در آنجا کنترل را سخت‌تر می‌کند؛ نگه‌داشتن ۶۰۰ آن را شل می‌کرد.'
      },
      {
        id: 'combine',
        title: 'قاعده‌ها، سطح‌ها و پنجره‌ها چطور ترکیب می‌شوند',
        intro: 'یک درخواست باید در هر دامنه‌ای که به آن مربوط است جا پیدا کند: سقف گیت‌وی، سطح تننتِ درخواست‌دهنده، قاعدهٴ کلیدش، قاعدهٴ مدل، و قاعده‌های تننت‌روی‌مدل و کلید‌روی‌مدل. هیچ‌چیز بر چیز دیگری غلبه نمی‌کند، جز آن یک استثنای درون سطح تننت که بالاتر گفته شد.',
        items: [
          { term: 'اضافه‌کردن قاعده فقط سخت‌تر می‌کند', text: 'چون هر قاعدهٴ مربوط باید درخواست را بپذیرد، قاعدهٴ جدید هیچ‌وقت نمی‌تواند به درخواست‌دهنده اجازهٴ بیشتری از قبل بدهد. «مشخص‌تر برنده است» وجود ندارد که لازم باشد به آن فکر کنید. برای آسان‌کردن، عددِ محدودکننده را بالا ببرید یا قاعده را حذف کنید.', example: 'مدل ۶۰۰ RPM به‌علاوهٴ تننت ۱٬۰۰۰ RPM: تننت روی آن مدل حداکثر ۶۰۰ می‌گیرد. بالابردن تننت به ۲٬۰۰۰ در آنجا هیچ‌چیز را تغییر نمی‌دهد.' },
          { term: 'بازگشت توکن در رد', text: 'توکن‌ها به‌ترتیب از هر سطل برداشته می‌شوند و اگر دامنهٴ بعدی رد کند، پس داده می‌شوند. درخواست‌دهنده‌ای که با قاعدهٴ باریک مدل مسدود شده، با هر تلاش مجدد بودجهٴ تننتش را هم نمی‌سوزاند.', example: '' },
          { term: 'هدف‌ها اعتبارسنجی نمی‌شوند', text: 'قاعده‌ای برای مدل، تننت یا کلیدی که وجود ندارد ذخیره می‌شود و هیچ‌وقت منطبق نمی‌شود؛ در زمان راه‌اندازی مفید است و در اشتباه تایپی، یک تله. تننت با شناسه یا اسلاگ منطبق می‌شود؛ کلید فقط با شناسه.', example: 'قاعده روی «gpt4» وقتی مدل با نام «gpt-4» ثبت شده، کاری نمی‌کند. گزارش مصرف هیچ برخوردی روی آن نشان نمی‌دهد؛ هدف را درست کنید.' },
          { term: 'کدام دامنه رد کرد؟', text: 'هدر پاسخ X-33pol-RateLimit-Scope آن را نام می‌برد، و جدول «محدودیت‌های برخوردشده» در گزارش مصرف، ردها را به‌ازای دامنه و هدف فهرست می‌کند.', example: '' },
          { term: 'پنجره‌ها قاعدهٴ خودشان را تغییر می‌دهند، نه قاعده‌های اطراف را', text: 'پنجره اعداد قاعدهٴ خودش را برای یک بازه جایگزین می‌کند. قاعده‌ها و سطح‌های دیگر مثل قبل اعمال می‌شوند، پس پنجرهٴ خارج از پیک روی یک مدل، سطح تننت‌ها را بالا نمی‌برد.', example: 'مدل ۶۰۰ RPM با پنجرهٴ شبانهٴ ۱٬۲۰۰: تننتی روی پلن ۱۲۰ RPM در شب هم ۱۲۰ می‌گیرد.' }
        ],
        tip: 'وقتی یک درخواست‌دهنده با قاعدهٴ سخاوتمندانه از 429 های غیرمنتظره خبر می‌دهد، دنبال دامنهٴ دیگری بگردید که سخت‌تر محدود می‌کند: معمولاً سطح تننت یا سقف گیت‌وی.'
      },
      {
        id: 'windows',
        title: 'پنجره‌های زمانی',
        intro: 'هر قاعده یک سطح پایه دارد و می‌تواند تا ۱۶ پنجره داشته باشد که هر یک برای بازه‌ای اعداد متفاوتی می‌دهد، یا قاعده را متوقف می‌کند. سطح پایه هر وقت هیچ پنجره‌ای فعال نیست اعمال می‌شود، و هر وقت پنجره‌ای قابل ارزیابی نباشد.',
        items: [
          { term: 'پنجرهٴ هفتگی', text: 'در روزهایی که تیک می‌زنید تکرار می‌شود، از ساعت شروع تا پایان، در منطقهٴ زمانی انتخابی. «روزها» روزهایی هستند که پنجره شروع می‌شود. پایانی برابر یا قبل از شروع تا روز بعد ادامه دارد؛ ۰۰:۰۰ تا ۲۴:۰۰ یک روز کامل است. همهٴ پنجره‌های هفتگی یک قاعده منطقهٴ زمانی مشترک دارند.', example: '«خارج از پیک»، دوشنبه تا جمعه، ۱۹:۰۰ تا ۰۷:۰۰، Europe/Berlin، ۱٬۲۰۰ RPM / ۲۰۰ Burst / ۸۰ Streams. هر عصرِ روز کاری، مدل تا صبح بعد دو برابر نرخ روزانه‌اش می‌پذیرد.' },
          { term: 'پنجرهٴ یک‌باره', text: 'یک بازهٴ تکی از شروع تا پایانِ اختیاری. بدون پایان، یک تغییر پله‌ای است: اعداد جدید از آن لحظه به بعد، تا وقتی قاعده را ویرایش کنید.', example: '«عرضه»، از ۱ اکتبر ۰۰:۰۰ تا ۳ اکتبر ۰۰:۰۰، ۳٬۰۰۰ RPM. یا «پایهٴ جدید»، از ۱ ژانویه ۰۰:۰۰ بدون پایان، تا محدودیتی سر وقت بالا برود بدون ویرایش نیمه‌شب.' },
          { term: 'توقف موقت این قاعده', text: 'پنجره قاعده‌اش را متوقف می‌کند: در مدت اجرا، قاعده مثل نبوده رفتار می‌کند و هیچ‌چیز اعمال نمی‌کند. قاعده‌ها و سطح‌های دیگر همچنان اعمال می‌شوند. قاعدهٴ تننتِ متوقف، تننت را به سطح پلنش برمی‌گرداند.', example: '«نگهداری»، هفتگی، یکشنبه ۰۲:۰۰ تا ۰۴:۰۰: قاعدهٴ سخت مدل در زمان اجرای کار دسته‌ای برداشته می‌شود.' },
          { term: 'اولویت', text: 'وقتی چند پنجره هم‌زمان فعال‌اند، بالاترین اولویت (۰ تا ۱۰۰۰) برنده است. بدون اولویت، پنجرهٴ یک‌باره بر هفتگی برتری دارد. دو پنجره که می‌توانند هم‌زمان اجرا شوند و اولویت یکسان دارند در زمان ذخیره رد می‌شوند؛ هم‌پوشانی پنجره‌های هم‌نوع رد می‌شود مگر اولویت‌هایشان متفاوت باشد.', example: 'پنجرهٴ عصرگاهی با اولویت ۱۰، پنجرهٴ تبلیغاتی با اولویت ۵: وقتی هر دو اجرا می‌شوند، عصرگاهی برنده است.' },
          { term: 'معتبر از / معتبر تا', text: 'حدود اختیاری برای این‌که پنجرهٴ هفتگی اصلاً کِی وجود دارد. برای شروع یک زمان‌بندی از فصل بعد، یا برای این‌که یک قرار آزمایشی خودش تمام شود.', example: 'معتبر تا ۳۱ دسامبر ۲۳:۵۹: پنجره در سال جدید دیگر تکرار نمی‌شود بی‌آنکه کسی آن را حذف کند.' },
          { term: 'منطقهٴ زمانی و ساعت تابستانی', text: 'ساعت‌های هفتگی، ساعت دیواری در منطقهٴ خودشان‌اند. شروعی که در شکاف جلوکشیدن ساعت بیفتد به اولین لحظهٴ معتبر می‌رود؛ پنجره‌ای روی تکرار پاییزی یک ساعت طولانی‌تر است. منطقه‌ای که سرور نمی‌شناسد پنجره را نامعتبر می‌کند: سطح پایه اعمال می‌شود و صفحه قاعده را علامت می‌زند.', example: 'شروع ۰۲:۳۰ در شبی که ساعت از ۰۲:۰۰ به ۰۳:۰۰ می‌پرد، از ۰۳:۰۰ آغاز می‌شود.' },
          { term: 'در لحظهٴ مرز', text: 'سخت‌ترشدن با درخواست بعدیِ آن بخش اعمال می‌شود. محدودیت بالاتر با نرخ جدید پر می‌شود و یک‌باره پر نمی‌شود. کم‌کردن سقف استریم هیچ استریم بازی را قطع نمی‌کند. کنترل تطبیقی روی هر آنچه زمان‌بندی برقرار کرده اعمال می‌شود. با کلید اصلیِ خاموش، پنجره‌ها هم چیزی اعمال نمی‌کنند.', example: '' },
          { term: 'پیش‌نمایش قبل از اعمال', text: 'فرم پنجره، وقوع بعدی، این‌که اکنون در حال اجراست یا نه، با کدام پنجره‌ها هم‌پوشانی دارد و بر کدام‌ها برتری دارد را هم‌زمان با تایپ نشان می‌دهد. تا «اضافه‌کردن» و بعد «ذخیره» را نزنید، هیچ‌چیز ثبت نمی‌شود.', example: '' }
        ],
        tip: 'پنجره‌ها را با هدفشان نام‌گذاری کنید («خارج از پیک»، «عرضه»، «نگهداری»): همین نام است که در تقویم، فهرست «در پیش» و تغییرها نشان داده می‌شود.'
      },
      {
        id: 'calendar',
        title: 'تقویم، «در پیش» و پیش‌نمایش',
        intro: 'تقویم نشان می‌دهد در بازهٴ انتخابی و منطقهٴ زمانی انتخابی، محدودیت‌ها کِی تغییر می‌کنند. پنجره‌هایی که اضافه کرده‌اید اما هنوز ذخیره نکرده‌اید را هم شامل می‌شود و با برچسب «پیش‌نویس» این را می‌گوید.',
        items: [
          { term: 'ردیف‌ها و نوارها', text: 'هر ردیف یک قاعدهٴ دارای پنجره است. نوار رنگی یعنی پنجره برقرار است؛ خاکستری سطح پایه است؛ نوار کم‌رنگ در گذشته است. خط عمودی «اکنون» است. روی نوار بروید تا اعدادش را ببینید؛ روی برچسب ردیف بزنید تا قاعده باز شود.', example: '' },
          { term: 'در پیش', text: 'لحظه‌های بعدی که اعداد هر قاعده تغییر می‌کند، با نام قاعده، پنجره و جهت تغییر محدودیت. فهرست‌های طولانی بریده می‌شوند؛ دکمه همه را نشان می‌دهد.', example: 'جمعه ۱۹:۰۰ · gpt-4 · شروع خارج از پیک · ۶۰۰ → ۱٬۲۰۰ RPM.' },
          { term: 'پیش‌نمایش در', text: 'لحظه‌ای را انتخاب کنید و صفحه می‌گوید هر قاعده در آن لحظه چه چیزی را اعمال می‌کند، کدام پنجره مسئول است، و تغییر بعدی کِی است. برای بررسی یک زمان‌بندی پیش از ذخیره به کار می‌آید.', example: 'پیش‌نمایش در دوشنبه ۰۹:۰۰ تأیید می‌کند پنجرهٴ شبانه تمام شده و سطح روزانه برگشته است.' }
        ],
        tip: 'وقتی در چند منطقه فعالیت می‌کنید، «نمایش زمان‌ها در» را تغییر دهید: پنجره‌ها در منطقهٴ خودشان ذخیره می‌شوند، پس تقویم می‌تواند آن‌ها را در منطقهٴ شما نمایش دهد.'
      },
      {
        id: 'saving',
        title: 'پیش‌نویس، بازبینی و ذخیره',
        intro: 'صفحه یک خلاصه است؛ ویرایش در کشوها انجام می‌شود. «انجام شد» در یک کشو، تغییر را در پیش‌نویس می‌گذارد. گیت‌وی فقط وقتی تغییر می‌کند که «ذخیره» بزنید.',
        items: [
          { term: 'نوار پایین', text: 'وقتی پیش‌نویس با آنچه سرور دارد فرق کند ظاهر می‌شود، تغییرها را می‌شمارد، و «لغو»، «بازبینی تغییرات» و «ذخیره» را پیشنهاد می‌کند. خروج از تب با پیش‌نویسِ تغییریافته اول می‌پرسد.', example: '' },
          { term: 'بازبینی تغییرات', text: 'هر تفاوتی که ذخیره ارسال می‌کند را فهرست می‌کند: سطح‌های تغییریافته، قاعده‌های اضافه یا حذف‌شده، پنجره‌های اضافه‌شده، کلید اصلی. پیش از ذخیره‌ای که به قاعده‌های زیادی دست می‌زند آن را بخوانید.', example: '' },
          { term: 'ذخیره کل مجموعه را می‌فرستد', text: 'API پیکربندی محدودیت نرخ را یک‌جا جایگزین می‌کند، پس آنچه در صفحه می‌بینید دقیقاً همان است که اعمال می‌شود. اگر اپراتور دیگری از زمان بارگذاری شما ذخیره کرده باشد، ذخیرهٴ شما رد می‌شود تا کار او بازنویسی نشود؛ «بازخوانی» بزنید و تغییرتان را دوباره اعمال کنید.', example: '' },
          { term: 'خاموش‌کردن یک قاعده', text: 'کلید «روشن» روی یک قاعده، آن را به‌عنوان اعمال‌نشده در پیش‌نویس می‌گذارد و سطح و پنجره‌هایش را نگه می‌دارد، پس قاعده می‌تواند مدتی کنار گذاشته شود و بدون تغییر برگردد. «حذف دائمی» آن را پاک می‌کند.', example: '' },
          { term: 'فقط‌خواندنی', text: 'وقتی صفحه قابل ویرایش نیست، بالای صفحه می‌گوید چرا: کلید نقش مدیر ندارد، یا سرور آخرین ذخیره را رد کرده است. کنترل‌های ویرایش تا رفع دلیل غیرفعال‌اند.', example: '' }
        ],
        tip: 'قاعده‌ای که اشتباهش خطرناک است (سقف گیت‌وی، ورودهای ناموفق) ارزش دارد چند دقیقه بعد از ذخیره در گزارش مصرف بررسی شود.'
      },
      {
        id: 'recipes',
        title: 'مثال‌های کاربردی',
        intro: 'موقعیت‌های رایج و قاعده‌ای که جوابشان است. هر یک، یک قاعده یا یک پنجره است؛ سطح‌های اطراف همچنان اعمال می‌شوند.',
        items: [
          { term: 'محافظت از یک مدل گران', text: 'قاعدهٴ جدید ← همه ← یک مدل ← انتخاب مدل ← ۶۰۰ RPM، ۶۰ Burst، ۴۰ Streams. همهٴ درخواست‌دهندگان با هم نمی‌توانند روی مدل از آن بیشتر بفرستند.', example: 'ارائه‌دهندهٴ بالادستی ۱۰٬۰۰۰ توکن در ثانیه اجازه می‌دهد؛ قاعدهٴ مدل را طوری می‌گذارید که نرخ کل درخواست درون آن بماند.' },
          { term: 'سهم بیشتر از یک مدل به یک مشتری', text: 'قاعدهٴ جدید ← تننت ← یک مدل ← اسلاگ تننت، سپس مدل ← ۲۰۰ RPM. تننت در جای دیگر سطح معمولی‌اش را نگه می‌دارد.', example: 'acme|gpt-4 با ۲۰۰ RPM، در حالی که تننت‌های دیگر آنچه قاعدهٴ مدل باقی می‌گذارد را به اشتراک می‌گذارند.' },
          { term: 'محدودکردن یک کلید پرسروصدا', text: 'قاعدهٴ جدید ← کلید API ← همهٴ مدل‌ها ← انتخاب کلید ← ۳۰ RPM، ۰ Burst، ۲ Streams. کلیدهای دیگر تننت تأثیر نمی‌گیرند.', example: 'وب‌هوک یک شریک در حلقه تلاش مجدد می‌کند؛ قاعدهٴ کلید آن را در ۳۰ در دقیقه نگه می‌دارد بی‌آنکه به تننت دست بزند.' },
          { term: 'بالابردن محدودیت یک مدل در شب', text: 'قاعدهٴ مدل را باز کنید ← اضافه‌کردن پنجره ← هفتگی ← دوشنبه تا جمعه، ۱۹:۰۰ تا ۰۷:۰۰، منطقهٴ شما ← اعداد بالاتر. روز، سطح پایه را نگه می‌دارد.', example: '«خارج از پیک»: ۱٬۲۰۰ RPM به‌جای ۶۰۰ در هر شبِ روز کاری.' },
          { term: 'روز عرضه', text: 'قاعدهٴ مدل را باز کنید ← اضافه‌کردن پنجره ← یک‌باره ← از شروع عرضه، تا دو روز بعد ← اعداد بالاتر. در مدت اجرا بر هر پنجرهٴ هفتگی برتری دارد.', example: '«عرضه»: ۳٬۰۰۰ RPM از ۱ تا ۳ اکتبر.' },
          { term: 'تغییر برنامه‌ریزی‌شدهٴ سطح پایه', text: 'پنجرهٴ یک‌باره با «از» و بدون «تا» اضافه کنید. اعداد جدید سر وقت اعمال می‌شوند و می‌مانند، بدون ویرایش نیمه‌شب.', example: '«سال جدید پلن»: ۹۰۰ RPM از ۱ ژانویه ۰۰:۰۰، بی‌پایان.' },
          { term: 'برداشتن یک قاعده در زمان نگهداری', text: 'پنجره‌ای با «توقف موقت این قاعده» اضافه کنید. در مدت اجرا، قاعده هیچ‌چیز اعمال نمی‌کند؛ هر محدودیت دیگری همچنان برقرار است.', example: '«نگهداری»، یکشنبه ۰۲:۰۰ تا ۰۴:۰۰ قاعدهٴ سخت مدل را برای کار دسته‌ای هفتگی متوقف می‌کند.' },
          { term: 'جلوگیری از حدس‌زدن اعتبارنامه', text: 'قاعدهٴ «ورودهای ناموفق» را نگه دارید. نصب تازه ۶۰/۲۰ دارد؛ جادوگر ۲۰/۱۰ پیشنهاد می‌کند. Streams باید ۰ بماند.', example: 'ورودهای ناموفق ۲۰ RPM، ۱۰ Burst: یک نشانی پس از ۳۰ کلید نامعتبر در یک دقیقه رد می‌شود.' },
          { term: 'محدودکردن ترافیک ناشناس به مدل‌های عمومی', text: 'هر وقت مدلی عمومی است قاعدهٴ «درخواست‌دهندگان ناشناس» را نگه دارید. هر نشانی سطل خودش را می‌گیرد.', example: 'درخواست‌دهندگان ناشناس ۳۰ RPM، ۱۰ Burst، ۲ Streams.' }
        ],
        tip: 'قاعده را اضافه کنید، ذخیره کنید، بعد گزارش مصرف را باز کنید: قاعده‌ای که اثر می‌کند چند دقیقه پس از ترافیک واقعی در «محدودیت‌های برخوردشده» ظاهر می‌شود.'
      },
      {
        id: 'faq',
        title: 'وقتی چیزی درست به نظر نمی‌رسد',
        intro: 'علت‌های معمول، به ترتیبی که باید بررسی شوند.',
        items: [
          { term: 'قاعده‌ام کاری نمی‌کند', text: 'هدف را برای اشتباه تایپی بررسی کنید: هدف‌ها اعتبارسنجی نمی‌شوند، پس شناسهٴ غلط ذخیره می‌شود و هیچ‌وقت منطبق نمی‌شود. کلید اصلی و کلید «روشن» قاعده را بررسی کنید. ببینید پنجره‌ای همین حالا آن را متوقف نکرده باشد (ردیف قاعده می‌گوید «اکنون برقرار»).', example: '' },
          { term: 'درخواست‌دهنده با قاعدهٴ سخاوتمندانه 429 می‌گیرد', text: 'دامنهٴ دیگری سخت‌تر محدود می‌کند: سطح تننتش، یک قاعدهٴ کلید، یا سقف گیت‌وی. هدر X-33pol-RateLimit-Scope در پاسخ ردشده دامنه را نام می‌برد. «پیش‌نمایش در» با زمان فعلی نشان می‌دهد هر قاعده چه چیزی را اعمال می‌کند.', example: '' },
          { term: 'محدودیت را بالا بردم و هنوز تغییری نکرده', text: 'محدودیت بالاتر با نرخ جدید پر می‌شود؛ یک‌باره پر نمی‌شود. در کمتر از یک دقیقه درخواست‌دهنده نرخ جدید را می‌بیند.', example: '' },
          { term: 'محدودیت نرخ خاموش است اما کلیدهای نامعتبر هنوز 429 می‌گیرند', text: 'طبیعی است. «ورودهای ناموفق» یک کنترل امنیتی است و تابع کلید اصلی نیست؛ پرچم خودش را در appsettings دارد.', example: '' },
          { term: 'ذخیره با تداخل رد شد', text: 'کس دیگری از زمان بارگذاری صفحه ذخیره کرده است. «بازخوانی» بزنید، بعد تغییرتان را دوباره اعمال کنید. هیچ‌چیز از شما نوشته نشده است.', example: '' },
          { term: 'قاعده‌ای با خطای زمان‌بندی علامت خورده', text: 'یکی از پنجره‌هایش قابل ارزیابی نیست، معمولاً منطقهٴ زمانی‌ای که سرور نمی‌شناسد. در این مدت سطح پایه اعمال می‌شود. قاعده را باز کنید و پنجره را درست کنید.', example: '' }
        ],
        tip: 'مرجع کامل اپراتور در مخزن، در docs/runbooks/rate-limit-admin.md است، شامل JSON ای که API می‌پذیرد.'
      }
    ]
  };

  window.RateLimitHelp = {
    langs: [
      { id: 'en', label: 'EN', name: 'English', dir: 'ltr' },
      { id: 'fa', label: 'فا', name: 'فارسی', dir: 'rtl' }
    ],
    en,
    fa
  };
})();
