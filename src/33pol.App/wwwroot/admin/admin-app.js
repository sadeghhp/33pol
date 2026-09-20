/**
 * The console runs on Alpine's CSP-friendly build so the admin surface can keep `script-src 'self'`
 * (see AdminSecurityHeaders.cs). That build's evaluator resolves a directive's value as a property
 * path and nothing else — no operators, no ternaries, no calls with arguments — so everything the
 * markup needs is exposed from here as a getter, a zero-argument method, or a {get,set} pair for
 * x-model. Row-level actions ride on the row objects as bound closures, which is how a template
 * reaches copyText(id) without writing an argument. See the "CSP view layer" section below.
 */
function adminApp() {
  const TABS = ['dashboard', 'usage', 'routing', 'keys', 'logs', 'errors', 'settings'];

  /** Key status filters, in the order the select offers them. Also the allow-list for ?filter=. */
  const KEY_FILTERS = ['active', 'revoked', 'archived', 'all'];

  /**
   * Push-stream staleness budget: 3x the server's idle heartbeat interval
   * (AdminControlPlaneEndpoints.LiveHeartbeat = 15s). See checkLiveStale().
   */
  const LIVE_STALE_MS = 3 * 15000;

  /**
   * How still the pointer and keyboard have to be before the wallboard hides its cursor and its
   * exit button. Long enough that an operator walking up to the screen is not fighting a vanishing
   * control, short enough that a mouse parked on a NOC panel does not burn into it.
   */
  const WALLBOARD_IDLE_MS = 8000;

  /**
   * How old the Overview's figures may get before the wallboard says so instead of displaying them
   * as if they were current. Generous next to the 2s poll and the 15s stream heartbeat: this fires
   * only when data has genuinely stopped arriving, not on one missed tick.
   */
  const WALLBOARD_STALE_MS = 20000;

  /**
   * How long a polled fetch may be in flight before the next tick stops waiting for it.
   *
   * Deliberately far longer than any healthy admin request. It exists only as a release valve:
   * nothing here aborts a fetch, so a request to a wedged gateway can hang for as long as the
   * browser lets it, and a single-flight guard with no deadline would park the poll permanently
   * with no way back.
   */
  const POLL_STALE_MS = 30000;

  /** Time-range presets for the Errors tab, in hours. `all` drops the lower bound entirely. */
  const ERROR_RANGES = [
    ['1h', 'Last hour', 1],
    ['24h', 'Last 24h', 24],
    ['7d', 'Last 7 days', 168],
    ['30d', 'Last 30 days', 720],
    ['all', 'All time', 0]
  ];
  /** Trailing windows the Overview can show; the ids match the server's `summary.windows[].window`. */
  const OVERVIEW_WINDOWS = [
    ['1m', '1 min', 60],
    ['5m', '5 min', 300],
    ['1h', '1 hour', 3600],
    ['24h', '24 h', 86400]
  ];
  const DEFAULT_OVERVIEW_WINDOW = '5m';
  const WINDOW_STORAGE_KEY = '33pol-admin-overview-window';
  /** Snapshots of pinned request rows, so a pinned request survives its eviction from the 25-row feed. */
  const PINNED_REQUESTS = new Map();
  const LEGACY = {
    backends: { tab: 'routing', routingSubTab: 'backends' },
    models: { tab: 'routing', routingSubTab: 'models' }
  };

  /**
   * Request ids the feed has shown, with the time each was first seen. Kept out of the reactive
   * component state on purpose: it is bookkeeping written from inside a getter, and a reactive Map
   * mutated during render would schedule that render again.
   */
  const SEEN_REQUEST_IDS = new Map();

  // The canonical taxonomy is served by GET /admin/api/model-types and loaded during init, so the
  // UI never keeps its own copy to drift from. This bootstrap list is only what renders before that
  // request completes (and if it fails); it is replaced wholesale on load.
  const BOOTSTRAP_MODEL_TYPES = [
    { value: 'text-generation', label: 'Text generation', testEndpoint: '/v1/chat/completions', aliases: [] }
  ];

  return {
    modelTypeCatalog: BOOTSTRAP_MODEL_TYPES,
    tab: 'dashboard',
    /**
     * Tabs whose panel has been built. A panel enters on first visit and never leaves: unmounting
     * on every switch would only move the cost from load to navigation.
     */
    mountedTabs: ['dashboard'],
    routingSubTab: 'models',
    showApiKey: false,
    showModelApiKey: false,
    showChangeKey: false,
    /** Draft key on the sign-in gate only; do not bind the gate to store.apiKey or the shell appears on first keystroke. */
    gateApiKey: '',
    /**
     * Draft key for the header's "Change key" panel, for the same reason the gate has one: bound
     * straight to store.apiKey, every keystroke replaced the credential the 2s poll and the
     * connection watchdog are actively using, so typing a new key 401'd the live session, flipped
     * the header to "Invalid key" and raised the error banner — and abandoning the panel left the
     * in-memory key truncated until a reload restored it from localStorage.
     */
    headerApiKey: '',
    poll: null,
    summary: null,
    summaryUpdatedAt: null,
    pollFailCount: 0,
    overviewStale: false,
    /** Selected trailing window on the Overview (`1m`|`5m`|`1h`|`24h`); persisted and mirrored in the hash. */
    overviewWindow: DEFAULT_OVERVIEW_WINDOW,
    /** Attention keys (code + subject) the operator dismissed this session; kept in sessionStorage. */
    attentionDismissed: [],
    attentionCollapsed: false,
    /** Database-backed Overview sections, polled every ~30s; null until loaded or when the gateway has no such data. */
    overviewFinops: null,
    overviewPolicy: null,
    overviewControlPlane: null,
    overviewActivity: null,
    overviewTenants: null,
    /** Per-section load failure text ('' when fine), keyed finops|policy|controlPlane|activity|tenants. */
    overviewSectionErrors: { finops: '', policy: '', controlPlane: '', activity: '', tenants: '' },
    overviewSlowLoadedAt: null,
    /** Wallboard mode: chrome hidden, type scaled to room-reading size (`#dashboard?wall=1`). */
    wallboard: false,
    /** Screen Wake Lock state while the wallboard is up: '' | 'held' | 'released' | 'denied' | 'unsupported'. */
    wallboardWakeLock: '',
    /** True once the pointer and keyboard have been still for WALLBOARD_IDLE_MS; hides the cursor and the exit button. */
    wallboardIdle: false,
    /** True only while the wallboard is the one that put the document into fullscreen. */
    wallboardFullscreen: false,
    _wbWakeLock: null,
    _wbIdleTimer: null,
    _wbActivityHandler: null,
    /** Live tail filters, pause and pins. */
    requestsModelFilter: '',
    requestsTenantFilter: '',
    requestsStatusClass: '',
    requestsSlowOnly: false,
    requestsPaused: false,
    _pausedFrame: null,
    /**
     * Start times for the two fetches the 2s poll drives; 0 when idle. See `_beginPoll`.
     */
    _summaryInFlight: 0,
    _requestsInFlight: 0,
    pinnedRequestIds: [],
    usage: null,
    usageEvents: null,
    usageEventsHasMore: false,
    usageEventsCursor: null,
    usageFrom: '',
    usageTo: '',
    /** Anonymous (no-key, public-model) usage is priced like everything else, so it is shown by default. */
    usageIncludeAnonymous: localStorage.getItem('33pol-usage-anon') !== 'false',
    usageRollupLimit: 100,
    /** The range the current report was loaded for — the inputs may have been edited since. */
    usageLoadedFrom: '',
    usageLoadedTo: '',
    _usageSeriesCache: null,
    forecast: null,
    backends: [],
    backendsFilter: '',
    models: [],
    modelsFilter: '',
    modelDrawerOpen: false,
    showAdvancedModel: false,
    keysDrawerOpen: false,
    keysCreatedAck: false,
    keyAccessDrawerOpen: false,
    keyAccessEdit: null,
    keyAccessSelected: [],
    tenantGrantRestricted: false,
    tenantGrantSelected: [],
    keys: [],
    selectedKeyIds: [],
    keysFilter: 'active',
    keysTextFilter: '',
    keysEditDrawerOpen: false,
    keyEdit: { id: '', keyPrefix: '', label: '', assignee: '', description: '', costCenter: '' },
    usageFilterCostCenter: '',
    usageFilterApiKeyId: '',
    requests: [],
    requestsErrorsOnly: false,
    expandedRequestId: null,
    /**
     * Push channel state for the Overview. 'stream' while the server-sent-event stream is
     * delivering frames, 'reconnecting' between a drop and the next attempt (polling covers the
     * gap), 'polling' when the stream cannot be established at all, '' when not on the Overview.
     */
    liveMode: '',
    liveVersion: null,
    liveFrameAt: null,
    _liveAbort: null,
    _liveRetryTimer: null,
    _liveRetryDelay: 1000,
    _liveFrames: 0,
    /**
     * Wall-clock time of the last byte received on the stream — data frames AND heartbeat comments —
     * or of the connection attempt while nothing has arrived yet. The tick timer compares it against
     * LIVE_STALE_MS: a half-open connection (proxy/NAT silently dropped it) never errors on its own,
     * so without this the Overview would sit on "Streaming" with data that stopped updating.
     */
    _liveLastDataAt: 0,
    /**
     * Reactive clock, ticked twice a second while the Overview is on screen. Reading it from a
     * getter is what makes an in-flight row's elapsed time and the "updated Ns ago" line advance
     * between frames instead of jumping only when data arrives.
     */
    _nowTick: Date.now(),
    _tickTimer: null,
    logs: [],
    logsLevel: 'all',
    logsSearch: '',
    logsCapacity: 0,
    /**
     * 200 rows put ~5,500 DOM nodes and ~4,000 bindings on the page — each row also carries a hidden
     * detail row — and none of it is released when you leave the tab. 50 matches what the Errors tab
     * already pages at, and the search and level filters are the way to reach further back.
     */
    logsPageSize: 50,
    logsAutoRefresh: false,
    logsTotal: 0,
    logsLoadError: '',
    expandedLogId: null,
    // Named errorGroups, not errors: `error`/`errorTitle`/`errorDetail` are already the global
    // banner's getters, and a near-miss name is exactly the silent-binding failure the CSP asset
    // test exists to catch.
    errorGroups: [],
    errorGroupsTotal: 0,
    errorOccurrenceTotal: 0,
    errorsStoredTotal: 0,
    errorsDroppedTotal: 0,
    errorsPersistFailedTotal: 0,
    errorsPrunedTotal: 0,
    errorsRetainedSince: null,
    errorsDegraded: false,
    errorsFacetsError: false,
    expandedOccurrenceKey: null,
    errorsPersisted: true,
    errorsFacets: null,
    errorsRange: '24h',
    errorsModel: '',
    errorsStatus: '',
    errorsCode: '',
    errorsLevel: 'all',
    errorsSearch: '',
    errorsPageSize: 50,
    errorsOffset: 0,
    errorsAutoRefresh: true,
    errorsLoadError: '',
    expandedErrorKey: null,
    errorOccurrences: {},
    configStatus: null,
    rateLimits: null,
    rlDraft: null,
    rateLimitUsage: null,
    rateLimitUsageError: '',
    rateLimitUsageMinutes: 60,
    // A failed refresh keeps the last good report and says so, rather than blanking the card.
    rateLimitUsageStale: false,
    rateLimitUsageLoading: false,
    // 503: the deployment runs without the tracker. Not an error to retry, a fact to state.
    rateLimitUsageUnavailable: false,
    rateLimitUsageLoadedAt: 0,
    rlUsageTab: 'tenant',
    rateLimitFieldError: '',
    rateLimitsLoadError: '',
    rlReadOnlyReason: '',
    rlSaving: false,
    rlSchedule: null,
    rlScheduleError: '',
    rlScheduleLoadedAt: 0,
    _rlScheduleTimer: null,
    _rlPreviewTimer: null,
    _rlWindowBaseline: '',
    _rlPreviewSeq: 0,
    _rlTick: Date.now(),
    _rlZoneCache: null,
    rlZone: '',
    rlRangeDays: 7,
    rlPreviewAt: '',
    rlPreview: null,
    rlPreviewError: '',
    rlShowAllTransitions: false,
    rlCombo: { field: '', index: -1, closed: {} },
    rlFilterText: '',
    // The list is filtered by the same two questions a rule is created with, never by stored scope.
    rlFilterWho: 'all',
    rlFilterWhere: 'any',
    rlFilterFlag: '',
    rlSortKey: 'who',
    rlSortDir: 1,
    rlReviewOpen: false,
    rlRuleDrawerOpen: false,
    rlRule: { identity: '', scope: 'model', target: '', rpm: 0, burst: 0, maxConcurrentStreams: 0, schedule: [] },
    rlRuleError: '',
    rlTierDrawerOpen: false,
    rlTier: { kind: 'default', slug: '', originalSlug: '', isNew: false, rpm: 0, burst: 0, maxConcurrentStreams: 0 },
    rlTierError: '',
    rlWindowOpen: false,
    rlWindowEditIndex: -1,
    rlWindow: { name: '', kind: 'weekly', rpm: 0, burst: 0, maxConcurrentStreams: 0, suspend: false, priority: '', fromLocal: '', untilLocal: '', days: [], start: '19:00', end: '07:00', timeZone: '', validFromLocal: '', validUntilLocal: '', showAdvanced: false },
    rlWindowPreview: null,
    rlWindowError: '',
    rlNewRuleOpen: false,
    // The new-rule form holds what the operator means, never a stored scope: who is limited, on one
    // model or all, and the two texts. The scope and target are derived (rlScopeFor, rlNewRuleBuild).
    // Seeded per scope by rlSeedNewRuleTier(); `touched` records that the operator typed over the
    // seed, so changing scope afterwards does not overwrite their numbers. `picked` remembers, per
    // field, the suggestion the operator chose: the field shows a key's name while the rule is stored
    // against its id, and the two are tied only while the text is still what the pick wrote there.
    // `tried` is "Create was pressed", which is when errors start to show; `opened` is what the form
    // held as it opened, so a form another page prefilled is not unsaved work until it is changed.
    rlNewRule: { who: 'key', where: 'one', subject: '', model: '', rpm: 0, burst: 0, maxConcurrentStreams: 0, touched: false, picked: {}, tried: false, opened: '' },
    // The key list as the rate-limit page needs it: 'idle' | 'loading' | 'ready' | 'failed'. Its own
    // state because Settings can be the first tab opened, and a failure here must not fail the page.
    rlKeysState: 'idle',
    // Bilingual help: the guide drawer and the inline explainers read window.RateLimitHelp in this
    // language. Persisted like the theme, so an operator who reads Persian is not asked twice.
    rlHelpOpen: false,
    rlHelpTopic: 'overview',
    rlHelpLang: localStorage.getItem('33pol-admin-help-lang') || 'en',
    corsOrigins: null,
    corsFieldError: '',
    corsLoadError: '',
    healthLive: null,
    healthReady: null,
    confirmDialog: null,
    /** The element focus returns to when the open modal surface closes. */
    _modalReturnFocus: null,
    /** Set by openConfirm when the caller knows better than document.activeElement. */
    _modalReturnFocusOverride: null,
    /** The surface currently holding focus, and what was made inert behind it. */
    _modalSurface: null,
    _modalStack: [],
    _modalInerted: [],
    revokeConfirmId: null,
    /** The key awaiting permanent deletion, plus the prefix the operator has to type back. */
    deleteConfirmKey: null,
    deleteConfirmText: '',
    modelTestDialog: null,
    editModel: {
      id: '', url: '', maxContextLength: 8192, aliasesText: '',
      apiKey: '', clearApiKey: false, hasUpstreamCredential: false,
      publicAccess: false, upstreamAuth: null, modelType: 'text-generation', _existing: false
    },
    modelFieldError: '',
    newKey: { role: 'Inference', label: '', assignee: '', description: '', costCenter: '' },
    createdKey: '',
    sort: {
      models: { key: 'id', dir: 1 },
      backends: { key: 'modelId', dir: 1 },
      keys: { key: 'createdAt', dir: -1 },
      requests: { key: 'timestampUtc', dir: -1 },
      usageRollups: { key: 'usageDate', dir: -1 }
    },
    _saveModelInFlight: false,
    _createKeyInFlight: false,
    settingsSubTab: 'runtime',
    vitalsHistory: [],
    _pollTick: 0,
    themeMode: (function () {
      const explicit = localStorage.getItem('33pol-admin-theme');
      if (explicit === 'light' || explicit === 'dark' || explicit === 'system') return explicit;
      const legacy = localStorage.getItem('33pol-admin-dark');
      if (legacy === 'true') return 'dark';
      if (legacy === 'false') return 'light';
      return 'system';
    })(),

    // One getter per panel: Alpine's CSP build resolves a directive to a property path and nothing
    // else, so `mountedTabs.includes('logs')` cannot be written in the markup.
    get mountDashboard() { return this.mountedTabs.includes('dashboard'); },
    get mountUsage() { return this.mountedTabs.includes('usage'); },
    get mountRouting() { return this.mountedTabs.includes('routing'); },
    get mountKeys() { return this.mountedTabs.includes('keys'); },
    get mountLogs() { return this.mountedTabs.includes('logs'); },
    get mountErrors() { return this.mountedTabs.includes('errors'); },
    get mountSettings() { return this.mountedTabs.includes('settings'); },

    get store() { return Alpine.store('admin'); },
    // Read-only on purpose. The live key changes only through store.persistApiKey, so no template
    // binding can leave the session holding a half-typed credential.
    get apiKey() { return this.store.apiKey; },
    get connectionStatus() { return this.store.connectionStatus; },
    get connectionDegraded() { return this.store.connectionDegraded; },
    get error() { return this.store.error; },
    get errorTitle() { return this.store.errorTitle; },
    get errorDetail() { return this.store.errorDetail; },
    get toasts() { return this.store.toasts; },

    init() {
      this.applyTheme();
      const media = window.matchMedia('(prefers-color-scheme: dark)');
      media.addEventListener?.('change', () => { if (this.themeMode === 'system') this.applyTheme(); });
      this.initUsageDates();
      this.loadModelTypes();
      this.restoreTab();
      this.restoreDismissedAttention();
      window.addEventListener('hashchange', () => this.applyHashTab());
      document.addEventListener('visibilitychange', () => {
        this.syncPoll();
        this.syncLive();
        // The browser drops a screen wake lock whenever the document is hidden and never gives it
        // back on its own, so a wallboard behind another tab for a minute would quietly lose it.
        if (!document.hidden) this.syncWallboardEffects();
      });
      document.addEventListener('fullscreenchange', () => this.onFullscreenChange());
      // Safety net: an API failure that escapes a fire-and-forget call site still lands in the
      // banner instead of only in the devtools console.
      window.addEventListener('unhandledrejection', ev => {
        const e = ev.reason;
        if (e && (e.title || e.global !== undefined)) {
          ev.preventDefault();
          if (!e._reported) this.handleCatch(e);
        }
      });
      window.addEventListener('beforeunload', (e) => this.onBeforeUnload(e));
      // Teardown belongs on pagehide, not beforeunload: beforeunload can now be cancelled ("Stay on
      // page") and the stream has to survive that. pagehide fires only when the page really goes,
      // bfcache included — and pageshow brings the stream back if it comes out of bfcache.
      window.addEventListener('pagehide', () => this.stopLive());
      window.addEventListener('pageshow', (e) => { if (e && e.persisted) this.syncLive(); });
      // One effect for every modal surface. It reads the flags (which is what subscribes it) and
      // then inspects the DOM on the next tick, once x-show has been applied.
      Alpine.effect(() => {
        const open = this.anyModalOpen;
        this.$nextTick(() => this.syncModalFocus(open));
      });
      this._tickTimer = setInterval(() => {
        if (document.hidden) return;
        // Only the Overview reads the clock; ticking it elsewhere would re-render for nothing.
        if (this.tab === 'dashboard' && this.apiKey) {
          const now = Date.now();
          // Written at ~1Hz, not on every 500ms tick. _nowTick is read by a getter on every visible
          // row and every age line, so each write invalidates all of them; none of those render
          // finer than a second, so half of that work produced no visible change.
          if (now - this._nowTick >= 950) this._nowTick = now;
          // A session the watchdog has declared dead must stop receiving as well as stop asking:
          // the poll already suspends itself, and without this the push stream could keep filling
          // the vitals underneath a line that says they are no longer updating.
          if (this.connectionStatus === 'fail' && this.liveMode) this.syncLive();
          this.checkLiveStale();
          // The wallboard's staleness and severity switches live on <html>, out of reach of any
          // binding inside the panel, so the clock is what keeps them honest.
          if (this.wallboard) this.applyWallboard();
        }
        // The Rate limits page draws a "now" line and "in 12 m" texts; a coarse tick keeps them
        // moving without re-rendering the timeline twice a second.
        if (this.tab === 'settings' && this.isSettingsLimits && this.apiKey && Date.now() - this._rlTick >= 30000) {
          this._rlTick = Date.now();
        }
      }, 500);
      if (this.apiKey) {
        this.store.startConnectionWatch(() => this.editModelUrl());
        this.saveKey();
      }
    },

    /** Default window: the last 30 UTC calendar days, today included. */
    initUsageDates() {
      const { from, to } = this.usagePresetRange(30);
      this.usageFrom = from;
      this.usageTo = to;
    },

    resolveHash(hash) {
      const raw = (hash || '').replace(/^#\/?/, '');
      // Split the query off before matching, so a deep link like #/errors?model=gpt-4o still
      // resolves to the errors tab rather than falling through to the saved tab.
      const q = raw.indexOf('?');
      const h = q >= 0 ? raw.slice(0, q) : raw;
      const params = q >= 0 ? new URLSearchParams(raw.slice(q + 1)) : null;
      if (LEGACY[h]) return LEGACY[h];
      if (TABS.includes(h)) return { tab: h, params };
      return null;
    },

    restoreTab() {
      const savedWindow = sessionStorage.getItem(WINDOW_STORAGE_KEY);
      if (savedWindow && OVERVIEW_WINDOWS.some(([id]) => id === savedWindow)) this.overviewWindow = savedWindow;
      const resolved = this.resolveHash(location.hash);
      if (resolved) {
        // Only the Errors tab's own link may set the Errors filters. Applied unconditionally, a
        // `#/keys?model=gpt-4o` deep link quietly pre-filtered a panel the operator had not opened.
        if (resolved.tab === 'errors') this.applyErrorHashParams(resolved.params);
        if (resolved.tab === 'dashboard') this.applyDashboardHashParams(resolved.params);
        this.applyTab(resolved.tab, resolved.routingSubTab, false);
        return;
      }
      const saved = sessionStorage.getItem('33pol-admin-tab');
      const sub = sessionStorage.getItem('33pol-admin-routing-sub');
      if (saved && TABS.includes(saved)) {
        this.applyTab(saved, sub || 'models', false);
      }
    },

    applyHashTab() {
      const resolved = this.resolveHash(location.hash);
      if (!resolved) return;
      // Same-tab parameter changes (back/forward between windows) never reach the branch below.
      if (resolved.tab === 'dashboard') this.applyDashboardHashParams(resolved.params);
      if (resolved.tab !== this.tab || (resolved.routingSubTab && resolved.routingSubTab !== this.routingSubTab)) {
        if (resolved.tab === 'errors') this.applyErrorHashParams(resolved.params);
        this.applyTab(resolved.tab, resolved.routingSubTab, false);
      }
    },

    /** Applies #/errors?model=&status=&code=&range= before the tab loads, so it fetches once. */
    applyErrorHashParams(params) {
      if (!params) return;
      this.errorsModel = params.get('model') || '';
      this.errorsStatus = params.get('status') || '';
      this.errorsCode = params.get('code') || '';
      const range = params.get('range');
      if (range && ERROR_RANGES.some(([key]) => key === range)) this.errorsRange = range;
      this.errorsOffset = 0;
    },

    /** Applies #/dashboard?window=5m; unknown values are ignored so a bad link cannot break the page. */
    applyDashboardHashParams(params) {
      if (!params) return;
      const w = params.get('window');
      if (w && OVERVIEW_WINDOWS.some(([id]) => id === w)) this.setOverviewWindow(w, false);
      if (params.has('wall')) this.setWallboard(params.get('wall') === '1', false);
    },

    /** The Overview's hash, carrying the selected window (and wallboard) so the view is linkable and survives back/forward. */
    dashboardHash() {
      return '#dashboard?window=' + this.overviewWindow + (this.wallboard ? '&wall=1' : '');
    },

    setWallboard(on, updateHash = true) {
      const next = !!on;
      if (next === this.wallboard) return;
      this.wallboard = next;
      if (next) this.prepareWallboard();
      else this.restoreFromWallboard();
      this.applyWallboard();
      this.syncWallboardEffects();
      if (updateHash && this.tab === 'dashboard') {
        const hash = this.dashboardHash();
        if (location.hash !== hash) location.hash = hash;
      }
    },

    /**
     * Resolves the transient state a desk session accumulates, because the controls for it are
     * about to disappear. A paused feed is the one that actually breaks a board — Pause lives in
     * the filter row the wallboard hides, so a frozen tail would sit there with nothing to say it
     * is frozen and no way to release it. Tail filters are kept: a board deliberately pinned to one
     * model is a legitimate setup, and it is stated at board scale instead (wallboardFilterText).
     */
    prepareWallboard() {
      this.expandedRequestId = null;
      if (this.requestsPaused) {
        this.toggleRequestsPause();
        this.toast('Live tail resumed — the wallboard has no pause control.');
      }
    },

    /** Leaving gives back everything the board switched off: fullscreen, and the sections it stopped polling. */
    restoreFromWallboard() {
      this.exitPresentationFullscreen();
      this.loadOverviewSlow(true).catch(() => {});
    },

    /**
     * The wallboard's switches live on <html>, not in bindings, because they gate CSS across the
     * whole document — rail, topbar, cursor, type scale — which no directive inside the panel
     * reaches. `wallboard-stale` and `wallboard-critical` are re-evaluated by the 500ms tick.
     */
    applyWallboard() {
      const on = this.wallboard && this.tab === 'dashboard';
      const el = document.documentElement;
      el.classList.toggle('wallboard', on);
      el.classList.toggle('wallboard-idle', on && this.wallboardIdle);
      el.classList.toggle('wallboard-stale', on && this.wallboardStale);
      el.classList.toggle('wallboard-critical', on && this.hasCriticalAttention);
    },

    /** Side effects follow the mode actually in force — wallboard AND on the Overview — not the flag alone. */
    syncWallboardEffects() {
      if (this.wallboard && this.tab === 'dashboard') {
        this.startWallboardIdleWatch();
        void this.acquireWallboardWakeLock();
      } else {
        this.stopWallboardIdleWatch();
        this.releaseWallboardWakeLock();
      }
    },

    toggleWallboard() {
      const entering = !this.wallboard;
      this.setWallboard(entering);
      // Fullscreen needs a user gesture, and this click is one. Entering from #dashboard?wall=1 on
      // load cannot have one, so a URL-driven wallboard stays windowed and offers the button.
      if (entering) this.enterPresentationFullscreen();
    },
    exitWallboard() { if (this.wallboard) this.setWallboard(false); },
    get wallboardButtonText() { return this.wallboard ? 'Exit wallboard' : 'Wallboard'; },
    get wallboardButtonIcon() { return this.icon(this.wallboard ? 'minimize' : 'maximize'); },

    // ---- presentation: fullscreen, wake lock, idle chrome ----

    /**
     * A refusal is not worth a banner: kiosk shells, embedded webviews and permission policies all
     * say no to fullscreen, and the wallboard is perfectly usable in a window.
     */
    enterPresentationFullscreen() {
      const el = document.documentElement;
      if (!el.requestFullscreen || document.fullscreenElement) return;
      Promise.resolve(el.requestFullscreen()).then(
        () => { this.wallboardFullscreen = true; },
        () => { this.wallboardFullscreen = false; }
      );
    },

    exitPresentationFullscreen() {
      if (!this.wallboardFullscreen) return;
      // Cleared before the call: exitFullscreen fires fullscreenchange, and the handler must not
      // read our own exit as the operator leaving fullscreen and drop the wallboard twice.
      this.wallboardFullscreen = false;
      if (document.fullscreenElement && document.exitFullscreen) {
        document.exitFullscreen().catch(() => { /* already leaving */ });
      }
    },

    /** F11 or the browser's own Esc leaves fullscreen without telling us; treat it as leaving the board. */
    onFullscreenChange() {
      if (document.fullscreenElement || !this.wallboardFullscreen) return;
      this.wallboardFullscreen = false;
      this.setWallboard(false);
    },

    /**
     * A wallboard whose screen sleeps is not a wallboard. The lock is dropped by the browser every
     * time the document is hidden, so it is re-taken from visibilitychange for as long as the mode
     * is on. Firefox and pre-16.4 Safari have no wakeLock at all — the mode still works, the screen
     * just follows the OS timeout, and the exit hint says which of those is happening.
     */
    async acquireWallboardWakeLock() {
      if (!navigator.wakeLock) { this.wallboardWakeLock = 'unsupported'; return; }
      if (this._wbWakeLock || document.hidden) return;
      try {
        const lock = await navigator.wakeLock.request('screen');
        // The mode can have been switched off while the request was in flight.
        if (!this.wallboard) { lock.release().catch(() => {}); return; }
        this._wbWakeLock = lock;
        this.wallboardWakeLock = 'held';
        lock.addEventListener('release', () => {
          if (this._wbWakeLock !== lock) return;
          this._wbWakeLock = null;
          this.wallboardWakeLock = this.wallboard ? 'released' : '';
        });
      } catch {
        this.wallboardWakeLock = 'denied';
      }
    },

    releaseWallboardWakeLock() {
      const lock = this._wbWakeLock;
      this._wbWakeLock = null;
      this.wallboardWakeLock = '';
      if (lock) lock.release().catch(() => { /* already released */ });
    },

    startWallboardIdleWatch() {
      if (this._wbActivityHandler) return;
      const handler = () => this.noteWallboardActivity();
      this._wbActivityHandler = handler;
      window.addEventListener('mousemove', handler, { passive: true });
      window.addEventListener('mousedown', handler, { passive: true });
      window.addEventListener('keydown', handler, { passive: true });
      window.addEventListener('touchstart', handler, { passive: true });
      this.noteWallboardActivity();
    },

    stopWallboardIdleWatch() {
      const handler = this._wbActivityHandler;
      if (handler) {
        window.removeEventListener('mousemove', handler);
        window.removeEventListener('mousedown', handler);
        window.removeEventListener('keydown', handler);
        window.removeEventListener('touchstart', handler);
        this._wbActivityHandler = null;
      }
      if (this._wbIdleTimer) { clearTimeout(this._wbIdleTimer); this._wbIdleTimer = null; }
      if (!this.wallboardIdle) return;
      this.wallboardIdle = false;
      this.applyWallboard();
    },

    noteWallboardActivity() {
      if (this._wbIdleTimer) clearTimeout(this._wbIdleTimer);
      if (this.wallboardIdle) { this.wallboardIdle = false; this.applyWallboard(); }
      this._wbIdleTimer = setTimeout(() => {
        this._wbIdleTimer = null;
        this.wallboardIdle = true;
        this.applyWallboard();
      }, WALLBOARD_IDLE_MS);
    },

    // ---- wallboard readouts ----

    /**
     * Reads the reactive tick, so the board's clock advances with the rest of it. Forced to 24-hour
     * regardless of locale: an ops display wants the same reading as every log line beside it, and
     * a wrapping "06:27:03 PM" is a worse clock than "18:27:03" in every locale that has one.
     */
    get wallboardClockText() {
      return new Date(this._nowTick).toLocaleTimeString([], {
        hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit'
      });
    },

    /** The one line that says what population the giant numbers cover, and how the fleet is doing. */
    get wallboardScopeText() {
      const parts = [];
      if (this.hasBackendsSection) parts.push(this.backendsHealthText);
      const queued = this.queuedCount;
      if (queued > 0) parts.push(queued + ' queued');
      return parts.join(' · ');
    },

    get wallboardWindowText() { return this.windowLabel; },

    /**
     * Coarser than summaryAgeText, which is written for a line that is only ever a few seconds old:
     * a board that has been stale since last night must not report "48210s ago".
     */
    wallboardAgeText() {
      if (!this.summaryUpdatedAt) return '';
      const sec = Math.max(0, Math.floor((this._nowTick - this.summaryUpdatedAt) / 1000));
      if (sec < 90) return sec + 's';
      const min = Math.floor(sec / 60);
      if (min < 90) return min + ' min';
      return Math.floor(min / 60) + ' h';
    },

    /**
     * "The numbers on this screen are not current." A desk session gets a one-line notice for that;
     * a board has to say it at the size of the figures it is contradicting, because nobody standing
     * in front of it can read a hint. A rejected key, a failed refresh, or simply nothing arriving
     * for WALLBOARD_STALE_MS all count.
     */
    get wallboardStale() {
      if (!this.wallboard) return false;
      if (this.connectionStatus === 'fail') return true;
      if (this.overviewStale) return true;
      if (!this.summaryUpdatedAt) return false;
      return this._nowTick - this.summaryUpdatedAt >= WALLBOARD_STALE_MS;
    },
    get wallboardStaleTitle() { return this.connectionStatus === 'fail' ? 'DISCONNECTED' : 'STALE'; },
    get wallboardStaleText() {
      if (this.connectionStatus === 'fail') return 'The admin API key was rejected — these figures are frozen.';
      const age = this.wallboardAgeText();
      return age ? 'Last update ' + age + ' ago — these figures are not current.' : 'No data has reached this board yet.';
    },

    /**
     * The wallboard hides the filter row, so a tail narrowed to one model would otherwise read as
     * the whole gateway. This states the narrowing at board scale rather than silently lying.
     */
    get hasWallboardFilters() { return this.wallboard && this.hasRequestFilters; },
    get wallboardFilterText() {
      const parts = [];
      if (this.requestsModelFilter) parts.push('model ' + this.requestsModelFilter);
      if (this.requestsTenantFilter) parts.push('tenant ' + this.requestsTenantFilter);
      if (this.requestsStatusClass) parts.push('status ' + this.requestsStatusClass);
      if (this.requestsSlowOnly) parts.push('slow only');
      if (this.requestsErrorsOnly) parts.push('errors only');
      return parts.length ? 'Tail filtered · ' + parts.join(' · ') : '';
    },

    /** Corner hint. It also reports whether the screen is actually being held awake, which is not guessable. */
    get wallboardHintText() {
      if (this.wallboardWakeLock === 'held') return 'Esc to exit · screen kept awake';
      if (this.wallboardWakeLock === 'unsupported') return 'Esc to exit · this browser cannot hold the screen awake';
      if (this.wallboardWakeLock === 'denied' || this.wallboardWakeLock === 'released') return 'Esc to exit · the screen may sleep';
      return 'Esc to exit';
    },

    /** Offered only while the board is not already the fullscreen element — a URL-driven wallboard never is. */
    get wallboardCanGoFullscreen() { return !this.wallboardFullscreen; },

    setOverviewWindow(id, updateHash = true) {
      if (!OVERVIEW_WINDOWS.some(([key]) => key === id)) return;
      // Early return on the same value: applyHashTab re-applies params on every hashchange, and
      // writing the hash from here would otherwise ping-pong with it.
      if (id === this.overviewWindow) return;
      this.overviewWindow = id;
      sessionStorage.setItem(WINDOW_STORAGE_KEY, id);
      if (updateHash && this.tab === 'dashboard') {
        const next = this.dashboardHash();
        if (location.hash !== next) location.hash = next;
      }
    },

    applyTab(name, routingSubTab, updateHash) {
      if (!TABS.includes(name)) return;
      // Before `tab` changes, so the panel exists by the time x-show reveals it.
      if (!this.mountedTabs.includes(name)) this.mountedTabs = [...this.mountedTabs, name];
      this.tab = name;
      if (name === 'routing' && routingSubTab) {
        this.routingSubTab = routingSubTab === 'backends' ? 'backends' : 'models';
      }
      sessionStorage.setItem('33pol-admin-tab', name);
      sessionStorage.setItem('33pol-admin-routing-sub', this.routingSubTab);
      this.applyWallboard();
      this.syncWallboardEffects();
      if (updateHash) {
        const next = name === 'dashboard' ? this.dashboardHash() : '#' + name;
        if (location.hash !== next) location.hash = next;
      }
      this.syncPoll();
      this.syncLive();
      this.onTabActivated(name);
    },

    setTab(name) {
      this.applyTab(name, this.routingSubTab, true);
    },

    setRoutingSubTab(sub) {
      this.routingSubTab = sub;
      sessionStorage.setItem('33pol-admin-routing-sub', sub);
      this.onTabActivated('routing');
    },

    editModelUrl() {
      return this.editModel?.url || '';
    },

    clearMessages() {
      this.store.clearMessages();
    },

    toast(message, type) {
      this.store.pushToast(message, type || 'success');
    },

    applyTheme() {
      const el = document.documentElement;
      el.classList.remove('dark', 'light');
      if (this.themeMode === 'dark') el.classList.add('dark');
      else if (this.themeMode === 'light') el.classList.add('light');
      // 'system' => no class; the prefers-color-scheme media query governs.
    },

    setTheme(mode) {
      this.themeMode = (mode === 'light' || mode === 'dark') ? mode : 'system';
      localStorage.setItem('33pol-admin-theme', this.themeMode);
      this.applyTheme();
    },

    isTheme(mode) {
      return this.themeMode === mode;
    },

    setSettingsSubTab(sub) {
      this.settingsSubTab = sub;

      // The usage report is a separate read from the tiers, so it is fetched when its tab is opened
      // rather than on every settings load — an operator who never opens it never pays for it.
      if (sub === 'limits' && (!this.rateLimitUsage || Date.now() - (this.rateLimitUsageLoadedAt || 0) > 60000)) {
        void this.loadRateLimitUsage();
      }
      if (sub === 'limits' && this.rlDraft && Date.now() - (this.rlScheduleLoadedAt || 0) > 30000) {
        void this.loadRateLimitSchedule();
      }
      // Tier cards and the new-rule flow name tenants and their plans from the overview's tenant
      // section, which only the dashboard loads otherwise.
      if (sub === 'limits' && !this.overviewTenants && this.apiKey) {
        void this.loadOverviewTenants();
      }
    },

    /**
     * Leaving the page. Rate-limit work is the one thing on the console that lives solely in memory
     * — everything else is either already saved or re-fetched on the next load — so it is the one
     * thing worth stopping an operator over: the staged draft, and the four editors whose working
     * copies have not reached it yet (see rateLimitsWorkInProgress).
     *
     * Dirtiness is a comparison of state, not a flag set by an input event, so typing a value and
     * typing it back leaves the page pristine and silent.
     *
     * This handler does not tear the live stream down. Raising the prompt makes "Stay on page" a
     * real outcome, and the stream has to survive it; teardown sits on pagehide instead, which
     * fires only when the page really goes.
     *
     * Browsers show their own wording and ignore the text, but the text still has to be non-empty:
     * preventDefault is what current engines honour, and the two legacy paths (returnValue and the
     * returned value) arm the dialog only when the string is not empty.
     */
    onBeforeUnload(event) {
      if (!this.rateLimitsWorkInProgress) return undefined;
      // preventDefault is what current engines honour. returnValue and the return value are the
      // legacy paths, and both arm the dialog only when the string is non-empty, so this text has
      // to be real even though no browser has displayed it for years.
      const message = 'Rate-limit changes have not been saved yet.';
      if (event) {
        event.preventDefault();
        event.returnValue = message;
      }
      return message;
    },

    icon(name) {
      return window.AdminIcons ? window.AdminIcons(name) : '';
    },

    /**
     * The one place a failed request becomes something the operator can see.
     *
     * Every error now reaches them by one route or another. There used to be two independent ways
     * to reach silence — a `section` that no panel ever rendered, and `global: false`, which simply
     * fell through the `if` below — and between them a 400 or 409 whose body said exactly what was
     * wrong ("this key has been used", "label must be 64 characters or fewer") disappeared
     * completely: the drawer stayed open, nothing appeared, and the click looked like it had done
     * nothing. Only `localOnly` means "shown elsewhere", because only there has a caller actually
     * taken responsibility for it.
     */
    handleCatch(e, options) {
      if (options?.localOnly) return;

      // One rejected key produces one notice, not one per request in flight when it was rejected.
      const isAuth =
        e.credentialRejected === true ||
        e.title === 'Authentication failed' ||
        /admin API key/i.test(e.message || '');
      if (isAuth && this.connectionStatus === 'fail') return;

      const message = e.message || String(e);
      if (e.global !== false) {
        this.store.setGlobalError(e.title || 'Error', message, e.detail);
        return;
      }

      // Not page-wide — a rejected edit is not a broken console — but not nothing either. A toast
      // keeps the drawer and its inputs in place while still saying why the save did not happen.
      this.toast(e.title ? e.title + ' — ' + message : message, 'error');
    },

    async runApi(scope, label, fn, options) {
      try {
        return await this.store.withLoading(scope, label, fn);
      } catch (e) {
        this.handleCatch(e, options);
        if (e && typeof e === 'object') e._reported = true;
        throw e;
      }
    },

    async apiJson(url, options = {}) {
      return this.store.apiJson(url, options, this.editModelUrl());
    },

    isLoading(scope) {
      return this.store.isLoading(scope);
    },

    formatTime(iso) {
      if (!iso) return '—';
      try { return new Date(iso).toLocaleString(); } catch { return iso; }
    },

    /** Time of day only — the feed is a live tail; the date is in the row's title. */
    formatClock(iso) {
      if (!iso) return '—';
      try { return new Date(iso).toLocaleTimeString(undefined, { hour12: false }); } catch { return iso; }
    },

    /**
     * Money on the Usage page. null/undefined is "not priced" and renders as a dash — it must not
     * collapse into $0.00, which is what a free request costs. Sub-cent amounts keep three
     * significant digits so a $0.0000014 event is distinguishable from zero.
     */
    formatCost(value, currency) {
      if (value === null || value === undefined || value === '') return '—';
      const n = Number(value);
      if (!Number.isFinite(n)) return String(value);
      const opts = { style: 'currency', currency: currency || 'USD' };
      if (n !== 0 && Math.abs(n) < 0.01) {
        opts.maximumSignificantDigits = 3;
      } else {
        opts.minimumFractionDigits = 2;
        opts.maximumFractionDigits = 4;
      }
      try {
        return new Intl.NumberFormat(undefined, opts).format(n);
      } catch { return n.toFixed(4); }
    },

    // Prices are authored per million tokens and are often sub-cent, so they get their own
    // formatter rather than going through formatCost (which caps at 4 decimal places).
    formatModelPrice(pricing) {
      if (!pricing) return '—';
      const fmt = v => {
        const n = Number(v);
        if (!Number.isFinite(n)) return '—';
        try {
          return new Intl.NumberFormat(undefined, {
            style: 'currency', currency: pricing.currency || 'USD',
            minimumFractionDigits: 2, maximumFractionDigits: 6
          }).format(n);
        } catch { return n.toFixed(2); }
      };
      return fmt(pricing.inputPricePerMillionTokens) + ' / ' + fmt(pricing.outputPricePerMillionTokens);
    },

    summaryAgeText() {
      if (!this.summaryUpdatedAt) return '';
      const sec = Math.floor((this._nowTick - this.summaryUpdatedAt) / 1000);
      return sec < 3 ? 'just now' : sec + 's ago';
    },

    /**
     * The one filter every usage call sends: range, cost centre, key and the anonymous toggle.
     * `withRange: false` drops from/to (the forecast has its own window).
     */
    /**
     * Frozen copy of the range + filters as they are right now. Requests are built from a snapshot
     * taken when they start (not from the live inputs when they resolve), so a preset click while a
     * report is in flight cannot label or page one range's rows with another range's parameters.
     */
    usageSnapshot() {
      return {
        from: this.usageFrom,
        to: this.usageTo,
        costCenter: (this.usageFilterCostCenter || '').trim(),
        apiKeyId: this.usageFilterApiKeyId,
        includeAnonymous: !!this.usageIncludeAnonymous
      };
    },

    usageParams(extra, withRange = true) {
      return this.usageParamsFrom(this.usageSnapshot(), extra, withRange);
    },

    usageParamsFrom(snap, extra, withRange = true) {
      const q = new URLSearchParams();
      if (withRange && snap.from) q.set('from', snap.from);
      if (withRange && snap.to) q.set('to', snap.to);
      if (snap.costCenter) q.set('costCenter', snap.costCenter);
      if (snap.apiKeyId) q.set('apiKeyId', snap.apiKeyId);
      if (snap.includeAnonymous) q.set('includeAnonymous', 'true');
      for (const [k, v] of Object.entries(extra || {})) {
        if (v !== undefined && v !== null && v !== '') q.set(k, String(v));
      }
      return q.toString();
    },

    /** Today's UTC calendar date as YYYY-MM-DD; rollups and event bounds are UTC days. */
    utcToday() { return new Date().toISOString().slice(0, 10); },

    /** "Last N days" means N UTC calendar days ending today, so `from` is today minus N-1. */
    usagePresetRange(days) {
      const to = new Date();
      const from = new Date(to);
      if (days === 'mtd') {
        from.setUTCDate(1);
      } else {
        from.setUTCDate(from.getUTCDate() - (Number(days) - 1));
      }
      return { from: from.toISOString().slice(0, 10), to: to.toISOString().slice(0, 10) };
    },

    async setUsagePreset(days) {
      const { from, to } = this.usagePresetRange(days);
      this.usageFrom = from;
      this.usageTo = to;
      if (this.apiKey) await this.applyUsageRange().catch(() => {});
    },

    /** Inline validation for the range; the server enforces the same rules with a 400. */
    get usageRangeError() {
      if (!this.usageFrom || !this.usageTo) return '';
      if (this.usageFrom > this.usageTo) return '"From" must be on or before "To".';
      const days = (Date.parse(this.usageTo) - Date.parse(this.usageFrom)) / 86400000 + 1;
      if (days > 366) return 'The range may span at most 366 days.';
      return '';
    },
    get usageRangeInvalid() { return !!this.usageRangeError; },

    setUsageIncludeAnonymous(on) {
      this.usageIncludeAnonymous = !!on;
      localStorage.setItem('33pol-usage-anon', this.usageIncludeAnonymous ? 'true' : 'false');
    },

    async copyText(text, successMsg) {
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        this.toast(successMsg || 'Copied to clipboard.');
      } catch {
        this.store.setGlobalError('Copy failed', 'Could not access clipboard.');
      }
    },

    sortToggle(table, key) {
      const s = this.sort[table];
      const next = s.key === key ? { key, dir: -s.dir } : { key, dir: 1 };
      this.sort = { ...this.sort, [table]: next };
    },

    sortIndicator(table, key) {
      const s = this.sort[table];
      if (s.key !== key) return '';
      const cls = s.dir > 0 ? '' : ' icon-flip';
      const svg = window.AdminIcons ? window.AdminIcons('chevron-up') : (s.dir > 0 ? ' ▲' : ' ▼');
      return '<span class="sort-icon' + cls + '">' + svg + '</span>';
    },

    sortedList(list, table) {
      const arr = [...(list || [])];
      const { key, dir } = this.sort[table];
      arr.sort((a, b) => {
        let av = a[key]; let bv = b[key];
        if (key === 'createdAt' || key === 'timestampUtc' || key === 'lastUsedAt') {
          av = av ? new Date(av).getTime() : 0;
          bv = bv ? new Date(bv).getTime() : 0;
        } else if (typeof av === 'number' || typeof bv === 'number') {
          av = Number(av) || 0;
          bv = Number(bv) || 0;
        } else {
          av = (av ?? '').toString().toLowerCase();
          bv = (bv ?? '').toString().toLowerCase();
        }
        if (av < bv) return -dir;
        if (av > bv) return dir;
        return 0;
      });
      return arr;
    },

    requestStatusClass(code) {
      const c = Number(code);
      if (c >= 500) return 'row-error';
      if (c >= 400) return 'row-warn';
      return '';
    },

    requestRowClass(r) {
      if (r?.isInFlight) return 'row-live';
      const statusClass = this.requestStatusClass(r?.statusCode);
      if (r?.errorCode) return statusClass || 'row-error';
      return statusClass;
    },

    errorsByModelRows() {
      const map = this.summary?.errorsPerModel;
      if (!map || typeof map !== 'object') return [];
      return Object.entries(map)
        .filter(([, count]) => Number(count) > 0)
        .map(([modelId, count]) => ({ modelId, count: Number(count) }))
        .sort((a, b) => b.count - a.count);
    },

    shortRequestId(id) {
      if (!id) return '—';
      return id.length > 8 ? id.slice(0, 8) : id;
    },

    toggleRequestDetails(id) {
      this.expandedRequestId = this.expandedRequestId === id ? null : id;
    },

    isRequestExpanded(id) {
      return this.expandedRequestId === id;
    },

    syncPoll() {
      if (this.poll) clearInterval(this.poll);
      this.poll = null;
      if (!this.apiKey) return;
      this._pollTick = 0;
      // Poll on every tab so the live-vitals bar stays current wherever you are.
      this.poll = setInterval(() => {
        if (document.hidden) return;
        // A rejected key does not recover by being retried: the connection watchdog re-checks it on
        // its own schedule, and polling on regardless meant a stale tab sent a 401 every 2s forever,
        // filling the gateway's admin audit trail.
        if (this.connectionStatus === 'fail') {
          // Suspended, and the figures on screen stop moving with it. Marking them stale here is
          // what keeps the Overview honest: this branch returns before any request can fail, so
          // pollFailCount never rises and nothing else would ever set it.
          this.overviewStale = true;
          return;
        }
        // While the push stream is delivering frames it owns the summary and the feed; polling on
        // top of it would only double the load for data that is already fresher than 2s.
        const streaming = this.liveMode === 'stream' && this.tab === 'dashboard';
        if (!streaming) this.loadSummary(true);
        // The live tail is only rendered on Overview, but it has to actually be live there — it used
        // to refresh solely on tab activation, so a request that arrived while you were watching the
        // page never appeared until you clicked Refresh.
        if (this.tab === 'dashboard' && !streaming) this.loadRequests(true);
        if (this._pollTick % 5 === 0) this.loadHealth();
        // The database-backed cards move slowly and are memoised server-side; every 15th tick (30s).
        if (this.tab === 'dashboard' && this._pollTick > 0 && this._pollTick % 15 === 0) this.loadOverviewSlow(true).catch(() => {});
        // Every 5th tick (10s) — the log buffer does not move fast enough to justify 2s polling,
        // and only while the tab is actually on screen.
        if (this.logsAutoRefresh && this.tab === 'logs' && this._pollTick % 5 === 0) this.loadLogs(true);
        // Same cadence for errors. Facets are not polled — they move slowly, and they are refreshed
        // on tab activation and after a clear.
        if (this.errorsAutoRefresh && this.tab === 'errors' && this._pollTick % 5 === 0) this.loadErrors(true);
        this._pollTick++;
      }, 2000);
    },

    recordVitals() {
      const s = this.summary;
      if (!s) return;
      const sample = {
        t: Date.now(),
        requests: Number(s.totalInferenceRequests ?? 0),
        errors: Number(s.totalErrors ?? 0),
        latency: Number(s.averageLatencyMs ?? 0),
        streams: Number(s.activeStreams ?? 0),
        inflight: Number(s.activeRequests ?? 0)
      };
      const h = this.vitalsHistory;
      const last = h[h.length - 1];
      if (last && sample.t - last.t < 500) return; // de-dupe near-simultaneous refreshes
      h.push(sample);
      if (h.length > 60) h.shift();
    },

    /**
     * Sparkline values from the server's per-minute series when the gateway provides one: the same
     * trend for every operator, and it survives a reload. Returns null when the series is absent so
     * the caller falls back to the in-browser sample history.
     */
    _seriesValues(metric) {
      const points = this.summary?.series?.points;
      if (!Array.isArray(points) || points.length < 2) return null;
      const step = Math.max(1, Number(this.summary.series.stepSeconds ?? 60));
      return points.map(p => {
        const requests = Number(p.requests ?? 0);
        switch (metric) {
          case 'throughput': return requests / step;
          case 'errorRate': return requests > 0 ? Number(p.errors ?? 0) / requests : 0;
          case 'latency': return Number(p.latencyP95Ms ?? 0);
          case 'ttft': return Number(p.ttftP95Ms ?? 0);
          case 'inflight': return Number(p.inFlight ?? 0);
          case 'cost': return Number(p.cost ?? 0);
          default: return 0;
        }
      });
    },

    _sparkValues(metric) {
      const fromServer = this._seriesValues(metric);
      if (fromServer) return fromServer;
      const h = this.vitalsHistory;
      if (h.length < 2) return [];
      if (metric === 'ttft' || metric === 'cost') return [];
      if (metric === 'throughput' || metric === 'errorRate') {
        const key = metric === 'throughput' ? 'requests' : 'errors';
        const out = [];
        for (let i = 1; i < h.length; i++) {
          const dt = Math.max(1, (h[i].t - h[i - 1].t) / 1000);
          out.push(Math.max(0, (h[i][key] - h[i - 1][key]) / dt));
        }
        return out;
      }
      // Gauges (latency, in-flight) are plotted as-is; only the cumulative counters above become rates.
      const key = metric === 'latency' ? 'latency' : (metric === 'streams' ? 'streams' : 'inflight');
      return h.map(s => Number(s[key] ?? 0));
    },

    hasSpark(metric) {
      return this._sparkValues(metric).length >= 2;
    },

    sparkLine(metric) {
      const v = this._sparkValues(metric);
      if (v.length < 2) return '';
      const max = Math.max(...v, 1e-9);
      const n = v.length;
      return v.map((val, i) => {
        const x = (i / (n - 1)) * 100;
        const y = 94 - Math.min(88, (val / max) * 88);
        return x.toFixed(2) + ',' + y.toFixed(2);
      }).join(' ');
    },

    sparkFill(metric) {
      const v = this._sparkValues(metric);
      if (v.length < 2) return '';
      const max = Math.max(...v, 1e-9);
      const n = v.length;
      let d = 'M0,100';
      v.forEach((val, i) => {
        const x = (i / (n - 1)) * 100;
        const y = 94 - Math.min(88, (val / max) * 88);
        d += ' L' + x.toFixed(2) + ',' + y.toFixed(2);
      });
      d += ' L100,100 Z';
      return d;
    },

    currentThroughput() {
      const v = this._sparkValues('throughput');
      return v.length ? v[v.length - 1] : 0;
    },

    errorRatePct() {
      const req = Number(this.summary?.totalInferenceRequests ?? 0);
      const err = Number(this.summary?.totalErrors ?? 0);
      if (req <= 0) return 0;
      return (err / req) * 100;
    },

    /** "812 ms" / "2.1 s" as separate value and unit, so the tile can style the unit. */
    formatMsParts(ms) {
      const x = Number(ms);
      if (!Number.isFinite(x) || x < 0) return { value: '—', unit: '' };
      if (x >= 10000) return { value: (x / 1000).toFixed(0), unit: 's' };
      if (x >= 1000) return { value: (x / 1000).toFixed(1), unit: 's' };
      return { value: x < 10 ? x.toFixed(1) : Math.round(x).toString(), unit: 'ms' };
    },

    formatMsShort(ms) {
      const p = this.formatMsParts(ms);
      return p.unit ? p.value + ' ' + p.unit : p.value;
    },

    formatNum(n) {
      const x = Number(n);
      if (!Number.isFinite(x)) return n ?? '—';
      return x.toLocaleString();
    },

    formatCompact(n) {
      const x = Number(n);
      if (!Number.isFinite(x)) return n ?? '—';
      try {
        return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(x);
      } catch { return this.formatNum(x); }
    },

    requestsByModelRows() {
      const map = this.summary?.requestsPerModel;
      if (!map || typeof map !== 'object') return [];
      return Object.entries(map)
        .filter(([, count]) => Number(count) > 0)
        .map(([modelId, count]) => ({ modelId, count: Number(count) }))
        .sort((a, b) => b.count - a.count);
    },

    barWidth(value, rows) {
      const max = Math.max(1, ...(rows || []).map(r => Number(r.count) || 0));
      return Math.round((Number(value) / max) * 100) + '%';
    },

    /**
     * One entry per UTC day across the whole requested range, zero-filled — days with no traffic
     * must show as gaps, otherwise the x-axis silently stops being linear. Memoised on the report
     * object because several getters and every chart column read it during one render.
     */
    usageDailySeries() {
      const usage = this.usage;
      const cache = this._usageSeriesCache;
      const from = this.usageLoadedFrom;
      const to = this.usageLoadedTo;
      if (cache && cache.usage === usage && cache.from === from && cache.to === to) {
        return cache.series;
      }
      const rollups = usage?.rollups || [];
      const byDate = new Map();
      for (const r of rollups) {
        const d = r.usageDate;
        const cur = byDate.get(d) || { date: d, cost: 0, prompt: 0, completion: 0, requests: 0 };
        cur.cost += Number(r.totalCost || 0);
        cur.prompt += Number(r.promptTokens || 0);
        cur.completion += Number(r.completionTokens || 0);
        cur.requests += Number(r.requestCount || 0);
        byDate.set(d, cur);
      }
      let series = [];
      if (byDate.size) {
        const dates = [...byDate.keys()].sort();
        const start = from && from <= dates[0] ? from : dates[0];
        const end = to && to >= dates[dates.length - 1] ? to : dates[dates.length - 1];
        const cur = new Date(start + 'T00:00:00Z');
        const last = new Date(end + 'T00:00:00Z');
        for (let guard = 0; cur <= last && guard < 400; guard++) {
          const key = cur.toISOString().slice(0, 10);
          series.push(byDate.get(key) || { date: key, cost: 0, prompt: 0, completion: 0, requests: 0 });
          cur.setUTCDate(cur.getUTCDate() + 1);
        }
      }
      this._usageSeriesCache = { usage, from, to, series };
      return series;
    },

    usageMaxCost() {
      return Math.max(0, ...this.usageDailySeries().map(d => d.cost));
    },

    colHeight(value) {
      const max = this.usageMaxCost();
      const n = Number(value) || 0;
      if (n <= 0 || max <= 0) return '0%';
      return Math.max(1, Math.round((n / max) * 100)) + '%';
    },

    shortDate(iso) {
      if (!iso) return '';
      const s = String(iso);
      return s.length >= 10 ? s.slice(5, 10) : s;
    },

    async saveKey() {
      // A draft wins over the live key: whichever panel is open is the operator's intent. With both
      // empty this is init() re-verifying the key restored from localStorage.
      const key = ((this.headerApiKey || this.gateApiKey || this.apiKey) || '').trim();
      if (!key) {
        this.store.error = 'Enter an admin API key.';
        return;
      }
      await this.runApi('auth', 'Connecting…', async () => {
        this.clearMessages();
        if (key !== this.apiKey) {
          // Verify the candidate first; verifyConnection persists it only on success, so a mistyped
          // key can never overwrite the working one in localStorage (a rejected candidate throws and
          // runApi reports it, with the previous key and its polling left untouched).
          await this.store.verifyConnection(this.editModelUrl(), key);
        } else {
          // init() re-verifying the key restored from localStorage.
          await this.store.verifyConnection(this.editModelUrl());
        }
        this.gateApiKey = '';
        this.headerApiKey = '';
        this.store.startConnectionWatch(() => this.editModelUrl());
        this.showChangeKey = false;
        this.syncPoll();
        await this.loadOverviewData();
        this.syncLive();
        this.onTabActivated(this.tab);
      });
    },

    clearSession() {
      if (this.poll) clearInterval(this.poll);
      this.poll = null;
      this.stopLive();
      this.store.stopConnectionWatch();
      this.store.persistApiKey('');
      this.gateApiKey = '';
      this.headerApiKey = '';
      this.showChangeKey = false;
      this.store.connectionStatus = '';
      this.store.connectionDegraded = false;
      this.summary = null;
      this.vitalsHistory = [];
      SEEN_REQUEST_IDS.clear();
      this.usage = null;
      this.usageEvents = null;
      this.backends = [];
      this.models = [];
      this.keys = [];
      // Also strands any key request still in flight, so it cannot refill the list after sign-out.
      this._keysSeq = (this._keysSeq || 0) + 1;
      this.rlKeysState = 'idle';
      this.selectedKeyIds = [];
      this.requests = [];
      this.logs = [];
      this.expandedLogId = null;
      this.errorGroups = [];
      this.errorGroupsTotal = 0;
      this.errorOccurrenceTotal = 0;
      this.errorsStoredTotal = 0;
      this.errorOccurrences = {};
      this.errorsFacets = null;
      this.expandedErrorKey = null;
      this.createdKey = '';
      this.modelDrawerOpen = false;
      this.keysDrawerOpen = false;
      // Rate limits: the drawers sit outside the signed-in shell, and the refresh timer would
      // otherwise fire one more unauthenticated read after sign-out.
      if (this._rlScheduleTimer) { clearTimeout(this._rlScheduleTimer); this._rlScheduleTimer = null; }
      if (this._rlPreviewTimer) { clearTimeout(this._rlPreviewTimer); this._rlPreviewTimer = null; }
      this.closeRateLimitDrawers();
      this.rateLimits = null;
      this.rlDraft = null;
      this.rlSchedule = null;
      this.rlPreview = null;
      this.rlReadOnlyReason = '';
      this.rateLimitUsage = null;
      this.rateLimitUsageStale = false;
      this.rateLimitUsageError = '';
      this.rateLimitUsageLoadedAt = 0;
      this.clearMessages();
      this.toast('Signed out — API key cleared from this browser.');
    },

    onTabActivated(name) {
      if (!this.apiKey) return;
      if (name === 'dashboard') this.loadOverviewData();
      if (name === 'usage') {
        // Fire-and-forget entry points: runApi already reports failures, so swallow the rethrow
        // here rather than leaking an unhandled rejection to the console.
        if (!this.keys?.length) this.runApi('usage', 'Loading keys…', () => this.fetchKeys()).catch(() => {});
        this.applyUsageRange().catch(() => {});
      }
      if (name === 'routing') {
        if (this.routingSubTab === 'backends') this.loadBackends();
        else this.loadModels();
      }
      if (name === 'keys') this.loadKeys();
      if (name === 'logs') this.loadLogs();
      if (name === 'errors') { this.loadErrorFacets(); this.loadErrors(); }
      if (name === 'settings') this.loadSettings();
    },

    logsQuery() {
      const params = new URLSearchParams({ limit: String(this.logsPageSize) });
      if (this.logsLevel && this.logsLevel !== 'all') params.set('level', this.logsLevel);
      const search = (this.logsSearch || '').trim();
      if (search) params.set('search', search);
      return '?' + params.toString();
    },

    /** @param quiet true for the auto-refresh tick and filter changes, which must not flash loading. */
    /** The Logs tab is the other half of the story for a request id: what was logged around the failure. */
    openLogsFromError(requestId) {
      if (!requestId) return;
      this.logsSearch = requestId;
      this.setTab('logs');
      return this.loadLogs();
    },

    async loadLogs(quiet) {
      const fetchLogs = () => this._sequenced('_logsSeq',
        () => this.apiJson('/admin/api/logs' + this.logsQuery()),
        body => {
          this.logs = body?.entries ?? [];
          this.logsTotal = Number(body?.total ?? body?.entries?.length ?? 0);
          this.logsCapacity = Number(body?.capacity ?? 0);
          this.logsLoadError = '';
        });
      if (quiet) {
        // The quiet path must not raise the global banner, but silently leaving stale rows on
        // screen is its own trap — an operator watching an incident cannot tell a calm gateway
        // from a console that stopped refreshing. The failure lands in the panel instead.
        try {
          await fetchLogs();
        } catch (e) {
          this.logsLoadError = this.describeLoadFailure(e);
        }
        return;
      }
      await this.runApi('logs', 'Loading logs…', fetchLogs);
    },

    /**
     * Filter changes reload quietly and never raise the global banner. Bound to the loud path, the
     * 400ms search debounce flipped the section into its loading state on every keystroke and could
     * throw a banner mid-word.
     */
    applyLogFilters() {
      this.expandedLogId = null;
      return this.loadLogs(true);
    },

    confirmClearLogs() {
      this.openConfirm({
        title: 'Clear the log buffer?',
        message: 'Discards every entry currently held in memory. This cannot be undone — durable logs written by the gateway\'s configured log providers are unaffected.',
        confirmLabel: 'Clear',
        danger: true,
        onConfirm: () => this.clearLogs()
      });
    },

    async clearLogs() {
      await this.runApi('logs', 'Clearing…', async () => {
        await this.apiJson('/admin/api/logs', { method: 'DELETE' });
        this.logs = [];
        this.logsTotal = 0;
        this.expandedLogId = null;
        this.toast('Log buffer cleared.');
      });
    },

    toggleLogDetails(id) {
      this.expandedLogId = this.expandedLogId === id ? null : id;
    },

    isLogExpanded(id) {
      return this.expandedLogId === id;
    },

    logLevelClass(level) {
      return 'level-' + String(level || '').toLowerCase();
    },

    logRowClass(entry) {
      const level = String(entry?.level || '').toLowerCase();
      if (level === 'error' || level === 'critical') return 'row-error';
      if (level === 'warning') return 'row-warn';
      return '';
    },

    /** Plain-text form of an entry, so an operator can paste one into a bug report or chat. */
    formatLogForCopy(entry) {
      if (!entry) return '';
      const lines = [
        `[${entry.level}] ${this.formatTime(entry.timestampUtc)} ${entry.category}` +
          (entry.eventCode ? ` (${entry.eventCode})` : ''),
        entry.message
      ];
      if (entry.repeats > 1) lines.push(`Occurrences: ${entry.repeats}, last ${this.formatTime(entry.lastTimestampUtc)}`);
      if (entry.modelId) lines.push(`Model: ${entry.modelId}`);
      if (entry.requestId) lines.push(`Request: ${entry.requestId}`);
      if (entry.hint) lines.push(`Hint: ${entry.hint}`);
      if (entry.detail) lines.push('', entry.detail);
      return lines.join('\n');
    },

    // ---- errors ----

    /**
     * Turns a failed background refresh into one sentence for the panel's own notice, reusing the
     * same classifier the global banner uses so the wording does not diverge between the two.
     */
    describeLoadFailure(error) {
      // The store classifies before it throws: `message` is a full sentence, `title` only a short
      // label, so prefer the former and punctuate whichever we end up with.
      const raw = (error?.message || error?.title || 'The request failed').trim();
      const sentence = /[.!?]$/.test(raw) ? raw : raw + '.';
      return `Could not refresh. ${sentence} Showing the last successful result.`;
    },

    /**
     * Single-flight guard for the loaders the 2s poll drives. Returns false when a previous tick's
     * request is still running, which is the tick's cue to skip.
     *
     * setInterval does not await its callback, and a GET now backs off before its retry, so a tick
     * waiting out a 429 can still be in flight when the next one fires. Answering a gateway that has
     * just asked for less traffic by sending it more is the wrong move, so overlapping ticks skip
     * instead of stacking.
     *
     * `POLL_STALE_MS` is what keeps that from becoming a worse bug than the one it fixes: a fetch
     * that never settles would otherwise hold the flag forever and stop the vitals updating for the
     * rest of the session.
     */
    _beginPoll(key) {
      const started = this[key];
      if (started && Date.now() - started < POLL_STALE_MS) return false;
      this[key] = Date.now();
      return true;
    },

    _endPoll(key) {
      this[key] = 0;
    },

    /**
     * Guards against an out-of-order response overwriting a newer one. Typing "gpt" then "gpt-4o"
     * fires two requests, and without this the slower first can land last and repaint the table
     * with results for a query the operator has already moved past.
     */
    _sequenced(key, run, apply) {
      const seq = (this[key] = (this[key] || 0) + 1);
      // `run` only fetches and returns the body; state is mutated in `apply`, and only when this
      // request is still the newest one for `key`. Applying inside `run` would defeat the guard —
      // the stale response would already have repainted the table before the check ran.
      return run().then(body => {
        if (seq !== this[key]) return undefined;
        if (apply) apply(body);
        return body;
      });
    },

    errorsRangeFrom() {
      const preset = ERROR_RANGES.find(([key]) => key === this.errorsRange);
      const hours = preset ? preset[2] : 24;
      if (!hours) return '';
      return new Date(Date.now() - hours * 3600 * 1000).toISOString();
    },

    errorsQuery(extra) {
      const params = new URLSearchParams({
        limit: String(this.errorsPageSize),
        offset: String(this.errorsOffset)
      });
      const from = this.errorsRangeFrom();
      if (from) params.set('from', from);
      if (this.errorsModel) params.set('modelId', this.errorsModel);
      if (this.errorsStatus) params.set('status', this.errorsStatus);
      if (this.errorsCode) params.set('code', this.errorsCode);
      if (this.errorsLevel && this.errorsLevel !== 'all') params.set('level', this.errorsLevel);
      const search = (this.errorsSearch || '').trim();
      if (search) params.set('search', search);
      if (extra) Object.entries(extra).forEach(([k, v]) => params.set(k, v));
      return '?' + params.toString();
    },

    /** @param quiet true for the auto-refresh tick and for filter changes, which must stay silent. */
    async loadErrors(quiet) {
      const fetchErrors = () => this._sequenced('_errorsSeq',
        () => this.apiJson('/admin/api/errors/groups' + this.errorsQuery()),
        body => {
          // An empty 200 is a failure, not a clean gateway: rendering it as "no errors" is the one
          // outcome this page must never produce by accident.
          if (!body || !Array.isArray(body.groups)) throw new Error('The errors API returned no data.');
          this.errorGroups = body.groups;
          this.errorGroupsTotal = Number(body?.total ?? 0);
          this.errorOccurrenceTotal = Number(body?.occurrenceTotal ?? 0);
          this.errorsStoredTotal = Number(body?.storedTotal ?? 0);
          this.errorsDroppedTotal = Number(body?.droppedTotal ?? 0);
          this.errorsPersistFailedTotal = Number(body?.persistFailedTotal ?? 0);
          this.errorsPrunedTotal = Number(body?.prunedTotal ?? 0);
          this.errorsRetainedSince = body?.retainedSinceUtc ?? null;
          this.errorsDegraded = body?.degraded === true;
          this.errorsPersisted = body?.persisted !== false;
          this.errorsLoadError = '';
        });

      if (quiet) {
        // See loadLogs: quiet means "no global banner", not "fail invisibly".
        try {
          await fetchErrors();
        } catch (e) {
          this.errorsLoadError = this.describeLoadFailure(e);
        }
        return;
      }
      await this.runApi('errors', 'Loading errors…', fetchErrors);
    },

    /** The template's refresh trigger passes a DOM event; loadErrors' first argument means "quiet". */
    refreshErrors() { return this.loadErrors(); },

    /**
     * Filter changes reload quietly and reset paging. Loud reloads here would flash the skeleton on
     * every keystroke and let a mid-typing failure raise the global banner.
     */
    applyErrorFilters() {
      this.errorsOffset = 0;
      this.expandedErrorKey = null;
      return this.loadErrors(true);
    },

    async loadErrorFacets() {
      try {
        const params = new URLSearchParams();
        const from = this.errorsRangeFrom();
        if (from) params.set('from', from);
        const query = params.toString();
        this.errorsFacets = await this.apiJson('/admin/api/errors/facets' + (query ? '?' + query : ''));
        this.errorsFacetsError = false;
      } catch {
        // Facets are a convenience; the free-text search still works without them — but say so,
        // or an empty model dropdown reads as "no models have errors".
        this.errorsFacets = null;
        this.errorsFacetsError = true;
      }
    },

    async loadErrorOccurrences(fingerprint) {
      if (!fingerprint) return;
      try {
        await this._sequenced('_errorsOccSeq',
          () => this.apiJson(
            '/admin/api/errors' + this.errorsQuery({ fingerprint, limit: '20', offset: '0' })
          ),
          body => {
            this.errorOccurrences = { ...this.errorOccurrences, [fingerprint]: body?.occurrences ?? [] };
          });
      } catch {
        this.errorOccurrences = { ...this.errorOccurrences, [fingerprint]: [] };
      }
    },

    toggleErrorDetails(fingerprint) {
      if (this.expandedErrorKey === fingerprint) {
        this.expandedErrorKey = null;
        return;
      }
      this.expandedErrorKey = fingerprint;
      // Fetched on first expand only: pulling occurrences for every row would multiply the cost of
      // the list by its page size for detail nobody has asked to see.
      if (!this.errorOccurrences[fingerprint]) this.loadErrorOccurrences(fingerprint);
    },

    isErrorExpanded(fingerprint) {
      return this.expandedErrorKey === fingerprint;
    },

    setErrorsRange(range) {
      this.errorsRange = range;
      this.errorsOffset = 0;
      this.loadErrorFacets();
      return this.loadErrors(true);
    },

    clearErrorFilters() {
      this.errorsModel = '';
      this.errorsStatus = '';
      this.errorsCode = '';
      this.errorsLevel = 'all';
      this.errorsSearch = '';
      this.errorsRange = '24h';
      this.errorsOffset = 0;
      this.loadErrorFacets();
      return this.loadErrors(true);
    },

    errorsPrevPage() {
      this.errorsOffset = Math.max(0, this.errorsOffset - this.errorsPageSize);
      return this.loadErrors();
    },

    errorsNextPage() {
      this.errorsOffset += this.errorsPageSize;
      return this.loadErrors();
    },

    /** Deep-links into the Errors tab, unfiltered. */
    openErrorsAll() {
      this.errorsModel = '';
      this.errorsStatus = '';
      this.errorsCode = '';
      this.errorsOffset = 0;
      this.setTab('errors');
    },

    openErrorsForModel(modelId) {
      this.errorsModel = modelId || '';
      this.errorsStatus = '';
      this.errorsCode = '';
      this.errorsOffset = 0;
      this.setTab('errors');
    },

    /** Jumps from an error to the request that produced it, on the Overview live tail. */
    openRequestFromError(requestId) {
      if (!requestId) return;
      this.requestsErrorsOnly = true;
      this.expandedRequestId = requestId;
      this.setTab('dashboard');
      this.loadRequests().then(() => {
        const found = (this.requests || []).some(r => r.requestId === requestId);
        if (!found) {
          // The feed is a bounded ring; an error older than ~500 requests has outlived its row.
          this.toast('That request is no longer in the live buffer.', 'error');
        }
      });
    },

    /** Plain-text form of a group, so an operator can paste one into a bug report or chat. */
    /**
     * Turns the wire value of an error's source into something an operator reads rather than
     * decodes. Unknown values pass through: a source added server-side should still show up.
     */
    errorSourceLabel(source) {
      if (!source) return '—';
      const labels = {
        proxy: 'Inference request',
        exception: 'Unhandled exception',
        log: 'Application log',
        modeltest: 'Model test'
      };
      return labels[String(source).toLowerCase()] || source;
    },

    formatErrorForCopy(group) {
      if (!group) return '';
      const lines = [
        `[${group.level}] ${group.message}`,
        `Occurrences: ${group.count} (first ${this.formatTime(group.firstSeenUtc)}, last ${this.formatTime(group.lastSeenUtc)})`
      ];
      if (group.source) lines.push(`Source: ${this.errorSourceLabel(group.source)}`);
      if (group.category) lines.push(`Category: ${group.category}`);
      if (group.exceptionType) lines.push(`Exception: ${group.exceptionType}`);
      if (group.statusCode) lines.push(`Status: ${group.statusCode}`);
      if (group.errorCode) lines.push(`Code: ${group.errorCode}`);
      if (group.modelId) lines.push(`Model: ${group.modelId}`);
      if (group.endpointPath) lines.push(`Endpoint: ${group.endpointMethod || ''} ${group.endpointPath}`.trim());
      if (group.upstreamTarget) lines.push(`Upstream: ${group.upstreamTarget}`);
      if (group.lastRequestId) lines.push(`Request: ${group.lastRequestId}`);
      if (group.hint) lines.push(`Hint: ${group.hint}`);
      if (group.upstreamBodySnippet) lines.push('', 'Upstream response:', group.upstreamBodySnippet);
      if (group.stackTrace) lines.push('', group.stackTrace);
      return lines.join('\n');
    },

    async downloadErrorsExport(format) {
      await this.runApi('errors', 'Preparing export…', async () => {
        const ext = format === 'csv' ? 'csv' : 'json';
        await this.store.downloadBlob(
          '/admin/api/errors/export' + this.errorsQuery({ format, limit: '5000', offset: '0' }),
          'errors-export.' + ext
        );
        this.toast('Export downloaded.');
      });
    },

    downloadErrorsJson() { return this.downloadErrorsExport('json'); },
    downloadErrorsCsv() { return this.downloadErrorsExport('csv'); },

    confirmClearErrors() {
      this.openConfirm({
        title: 'Clear all recorded errors?',
        message: 'Deletes every stored error record and resets the gateway error counters, including '
          + 'the persisted snapshot, so a restart will not bring them back. This cannot be undone — '
          + 'durable logs written by the gateway\'s configured log providers are unaffected.',
        confirmLabel: 'Clear errors',
        danger: true,
        onConfirm: () => this.clearErrors()
      });
    },

    async clearErrors() {
      await this.runApi('errors', 'Clearing…', async () => {
        const result = await this.apiJson('/admin/api/errors?confirm=true', { method: 'DELETE' });
        this.errorGroups = [];
        this.errorGroupsTotal = 0;
        this.errorOccurrenceTotal = 0;
        this.errorsStoredTotal = 0;
        this.errorOccurrences = {};
        this.expandedErrorKey = null;
        this.errorsOffset = 0;

        // vitalsHistory holds cumulative counters and the sparkline differentiates them. Leaving the
        // old samples would make the next delta a large negative — clamped to zero, then painting a
        // phantom spike the moment the first new error arrives.
        this.vitalsHistory = this.vitalsHistory.map(sample => ({ ...sample, errors: 0 }));

        // Refreshes the topbar chip, the Errors vital, the error rate and the errors-by-model bars.
        // `requests` is deliberately left alone: the live tail is a separate buffer.
        await this.loadSummary();
        await this.loadErrorFacets();
        // Re-read rather than assume empty: if the archive delete failed the rows are still there
        // and the response says so.
        await this.loadErrors(true);
        if (result && result.archiveCleared === false) {
          this.toast(result.message || 'Counters reset, but stored error records could not be deleted.', 'error');
        } else {
          this.toast('All recorded errors cleared.');
        }
      });
    },

    async loadSettings() {
      await this.runApi('settings', 'Loading settings…', async () => {
        const tasks = [
          this.fetchTenantGrants(),
          this.fetchConfigStatus(),
          this.loadRateLimits(),
          this.loadCors(),
          // Rule targets are key ids; without the key list they can be neither picked nor named.
          this.loadRateLimitKeys()
        ];
        if (!this.models?.length) tasks.unshift(this.fetchModels());
        await Promise.all(tasks);
      });
    },

    async loadOverviewData() {
      await this.runApi('overview', 'Loading overview…', async () => {
        const summaryP = this.apiJson('/admin/api/summary');
        const healthP = this.loadHealth();
        const requestsP = this.apiJson('/admin/api/requests?limit=' + this.requestsFeedLimit);
        await healthP;
        this.summary = await summaryP;
        this.requests = (await requestsP) ?? [];
        this.summaryUpdatedAt = Date.now();
        this.recordVitals();
        this.pollFailCount = 0;
        this.overviewStale = false;
      });
      // Not awaited: the vitals must never wait on the database-backed cards.
      this.loadOverviewSlow(true).catch(() => {});
    },

    /**
     * Which database-backed sections are worth fetching. The wallboard keeps only the policy card,
     * so the other four queries would otherwise run every 30s, for ever, against cards that are
     * display:none — on a screen left up for weeks that is the bulk of what the console costs the
     * gateway. Leaving the mode re-runs the full set (restoreFromWallboard).
     */
    overviewSlowLoaders() {
      if (this.wallboard) return [() => this.loadOverviewPolicy()];
      return [
        () => this.loadOverviewFinops(),
        () => this.loadOverviewPolicy(),
        () => this.loadOverviewControlPlane(),
        () => this.loadOverviewActivity(),
        () => this.loadOverviewTenants()
      ];
    },

    /**
     * Loads the slow Overview sections together. Each one fails on its own — a FinOps query error
     * leaves the backends and policy cards intact — and a 204 (the gateway has no such data) hides
     * the card rather than warning about it.
     */
    async loadOverviewSlow(quiet) {
      const results = await Promise.allSettled(this.overviewSlowLoaders().map(load => load()));
      this.overviewSlowLoadedAt = Date.now();
      if (!quiet && results.length > 0 && results.every(r => r.status === 'rejected')) {
        throw results[0].reason;
      }
    },

    _loadOverviewSection(name, url, seqKey, assign) {
      return this._sequenced(seqKey, () => this.apiJson(url), body => {
        assign(body ?? null);
        this.overviewSectionErrors[name] = '';
      }).catch(e => {
        this.overviewSectionErrors[name] = this.describeLoadFailure(e);
        throw e;
      });
    },

    loadOverviewFinops() {
      return this._loadOverviewSection('finops', '/admin/api/overview/finops', '_finopsSeq', body => { this.overviewFinops = body; });
    },
    loadOverviewPolicy() {
      return this._loadOverviewSection('policy', '/admin/api/overview/policy', '_policySeq', body => { this.overviewPolicy = body; });
    },
    loadOverviewControlPlane() {
      return this._loadOverviewSection('controlPlane', '/admin/api/overview/control-plane', '_cpSeq', body => { this.overviewControlPlane = body; });
    },
    loadOverviewActivity() {
      return this._loadOverviewSection('activity', '/admin/api/overview/activity?limit=20', '_activitySeq', body => { this.overviewActivity = body; });
    },
    loadOverviewTenants() {
      return this._loadOverviewSection('tenants', '/admin/api/overview/tenants', '_tenantsSeq', body => { this.overviewTenants = body; });
    },

    // ---- live push stream (Overview) ----

    /**
     * Opens or closes the push stream to match where the operator is: it runs only while the
     * Overview is on screen with a working key. Everything else — other tabs, a hidden window, a
     * rejected key — tears it down and leaves the 2s poll in charge.
     */
    syncLive() {
      const wanted = !!this.apiKey && this.tab === 'dashboard' && !document.hidden && this.connectionStatus !== 'fail';
      if (!wanted) { this.stopLive(); return; }
      if (this._liveAbort || this._liveRetryTimer) return;
      this.openLiveStream();
    },

    stopLive() {
      if (this._liveRetryTimer) { clearTimeout(this._liveRetryTimer); this._liveRetryTimer = null; }
      if (this._liveAbort) { this._liveAbort.abort(); this._liveAbort = null; }
      this.liveMode = '';
      this._liveRetryDelay = 1000;
    },

    /**
     * Staleness watchdog for the push stream. The server writes a heartbeat comment every 15s when
     * idle; if nothing at all has arrived for LIVE_STALE_MS (3x that) the connection is presumed
     * half-open: abort it, flip to 'reconnecting' (which hands the summary/feed back to the 2s poll)
     * and reconnect right away.
     */
    checkLiveStale() {
      if (!this._liveAbort || !this._liveLastDataAt) return;
      if (Date.now() - this._liveLastDataAt < LIVE_STALE_MS) return;
      const controller = this._liveAbort;
      this._liveAbort = null;
      controller.abort();
      this.liveMode = 'reconnecting';
      this._liveLastDataAt = 0;
      this.syncLive();
    },

    /**
     * Server-sent events over fetch rather than EventSource: the admin key travels in a header,
     * which EventSource cannot set. Frames are `event: update` + one JSON line; comment lines are
     * heartbeats. A drop schedules a reconnect with backoff and hands the page back to polling in
     * the meantime, so a proxy that cannot stream simply leaves the console on its 2s cadence.
     */
    async openLiveStream() {
      const controller = new AbortController();
      this._liveAbort = controller;
      this._liveLastDataAt = Date.now();
      if (!this.liveMode) this.liveMode = 'reconnecting';
      let gotFrame = false;
      try {
        const res = await fetch('/admin/api/live?limit=25', {
          headers: { ...this.store.headers(), Accept: 'text/event-stream' },
          cache: 'no-store',
          signal: controller.signal
        });
        if (res.status === 401) {
          // Same policy as the poll: a rejected key is not retried; the connection watchdog decides.
          this.store.connectionStatus = 'fail';
          this.store.connectionDegraded = true;
          this.stopLive();
          return;
        }
        if (!res.ok || !res.body) throw new Error('live stream unavailable: ' + res.status);
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          // Any bytes — a frame or a heartbeat comment — prove the connection is alive.
          this._liveLastDataAt = Date.now();
          buffer += decoder.decode(value, { stream: true });
          let sep;
          while ((sep = buffer.indexOf('\n\n')) >= 0) {
            const raw = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);
            const frame = this.parseSseFrame(raw);
            if (!frame) continue;
            gotFrame = true;
            this.applyLiveFrame(frame);
          }
        }
        // Server closed cleanly (deploy, restart): reconnect quickly.
        throw new Error('live stream ended');
      } catch (err) {
        if (controller.signal.aborted) return;
        if (this._liveAbort === controller) this._liveAbort = null;
        // A stream that delivered nothing before failing is treated as "cannot stream here", so
        // the badge says Polling rather than promising a reconnect that will not help.
        this.liveMode = gotFrame || this._liveFrames > 0 ? 'reconnecting' : 'polling';
        const delay = this._liveRetryDelay;
        this._liveRetryDelay = Math.min(delay * 2, 15000);
        this._liveRetryTimer = setTimeout(() => {
          this._liveRetryTimer = null;
          this.syncLive();
        }, delay);
      }
    },

    parseSseFrame(raw) {
      let event = 'message';
      const data = [];
      for (const line of raw.split('\n')) {
        if (!line || line.startsWith(':')) continue;
        if (line.startsWith('event:')) event = line.slice(6).trim();
        else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      }
      if (event !== 'update' || data.length === 0) return null;
      try { return JSON.parse(data.join('\n')); } catch { return null; }
    },

    applyLiveFrame(frame) {
      if (!frame || typeof frame !== 'object') return;
      // Paused: the summary keeps flowing (vitals stay honest) but the rows are parked until Resume,
      // so an operator can actually click into a request without the feed running away.
      if (this.requestsPaused && Array.isArray(frame.requests)) {
        this._pausedFrame = frame.requests;
        frame = { ...frame, requests: undefined };
      }
      this._liveFrames++;
      this._liveRetryDelay = 1000;
      this.liveMode = 'stream';
      this.liveVersion = frame.version ?? null;
      this.liveFrameAt = Date.now();
      if (frame.summary) this.summary = frame.summary;
      if (Array.isArray(frame.requests)) this.requests = frame.requests;
      this.summaryUpdatedAt = Date.now();
      this._nowTick = Date.now();
      this.recordVitals();
      this.pollFailCount = 0;
      this.overviewStale = false;
    },

    filteredBackends() {
      const q = (this.backendsFilter || '').trim().toLowerCase();
      let list = [...(this.backends || [])];
      list.sort((a, b) => Number(a.isHealthy) - Number(b.isHealthy));
      if (q) {
        list = list.filter(b =>
          (b.modelId || '').toLowerCase().includes(q) ||
          (b.url || '').toLowerCase().includes(q) ||
          (b.alias || '').toLowerCase().includes(q));
      }
      return this.sortedList(list, 'backends');
    },

    filteredModelsList() {
      const q = (this.modelsFilter || '').trim().toLowerCase();
      let list = this.models || [];
      if (q) {
        list = list.filter(m =>
          (m.id || '').toLowerCase().includes(q) ||
          (m.url || '').toLowerCase().includes(q) ||
          ((m.aliases || []).join(' ')).toLowerCase().includes(q));
      }
      return this.sortedList(list, 'models');
    },

    normalizeApiKeyRole(role) {
      if (role === 'Admin' || role === 1) return 'Admin';
      if (role === 'Both' || role === 2) return 'Both';
      if (role === 'Inference' || role === 0) return 'Inference';
      return role != null ? String(role) : 'Inference';
    },

    normalizeApiKeyList(list) {
      return (list || []).map(k => ({
        ...k,
        role: this.normalizeApiKeyRole(k.role),
        isRevoked: !!(k.isRevoked ?? k.revokedAt),
        isArchived: !!(k.isArchived ?? k.archivedAt),
        hasUsage: !!k.hasUsage,
        // Both eligibility flags come from the server, which is the one place the rules are enforced.
        // The `??` fallback matches how isRevoked is read: an older payload still renders sensibly.
        canArchive: !!(k.canArchive ?? ((k.isRevoked ?? k.revokedAt) && !(k.isArchived ?? k.archivedAt))),
        canDelete: !!k.canDelete
      }));
    },

    filteredKeys() {
      const list = this.keys || [];
      let filtered = list;
      // Archived keys are out of the way everywhere except their own filter — that is what archiving
      // buys. 'all' is the escape hatch for someone auditing the whole history.
      if (this.keysFilter === 'active') filtered = list.filter(k => !k.isRevoked && !k.isArchived);
      else if (this.keysFilter === 'revoked') filtered = list.filter(k => k.isRevoked && !k.isArchived);
      else if (this.keysFilter === 'archived') filtered = list.filter(k => k.isArchived);
      else if (this.keysFilter !== 'all') filtered = list.filter(k => !k.isArchived);
      const q = (this.keysTextFilter || '').trim().toLowerCase();
      if (q) {
        filtered = filtered.filter(k =>
          (k.keyPrefix || '').toLowerCase().includes(q) ||
          (k.label || '').toLowerCase().includes(q) ||
          (k.assignee || '').toLowerCase().includes(q) ||
          (k.costCenter || '').toLowerCase().includes(q));
      }
      return this.sortedList(filtered, 'keys');
    },

    keyMtdCost(key) {
      const cost = key?.usageSummary?.totalCost;
      return cost != null ? cost : null;
    },

    keyMtdRequests(key) {
      return key?.usageSummary?.requestCount ?? null;
    },

    isKeySelected(id) {
      return this.selectedKeyIds.includes(id);
    },

    selectableFilteredKeys() {
      return this.filteredKeys().filter(k => !k.isRevoked && !k.isArchived);
    },

    toggleKeySelection(id, shouldSelect) {
      if (!id) return;
      const set = new Set(this.selectedKeyIds);
      if (shouldSelect) set.add(id);
      else set.delete(id);
      this.selectedKeyIds = [...set];
    },

    toggleSelectAllFilteredKeys(shouldSelect) {
      const visibleIds = this.selectableFilteredKeys().map(k => k.id);
      if (visibleIds.length === 0) return;
      if (shouldSelect) {
        this.selectedKeyIds = [...new Set([...this.selectedKeyIds, ...visibleIds])];
        return;
      }

      const visibleSet = new Set(visibleIds);
      this.selectedKeyIds = this.selectedKeyIds.filter(id => !visibleSet.has(id));
    },

    allFilteredActiveKeysSelected() {
      const visibleIds = this.selectableFilteredKeys().map(k => k.id);
      if (visibleIds.length === 0) return false;
      const selected = new Set(this.selectedKeyIds);
      return visibleIds.every(id => selected.has(id));
    },

    someFilteredActiveKeysSelected() {
      const visibleIds = this.selectableFilteredKeys().map(k => k.id);
      if (visibleIds.length === 0) return false;
      const selected = new Set(this.selectedKeyIds);
      return visibleIds.some(id => selected.has(id));
    },

    selectedActiveKeyIds() {
      const activeIds = new Set(
        (this.keys || []).filter(k => !k.isRevoked && !k.isArchived).map(k => k.id));
      return this.selectedKeyIds.filter(id => activeIds.has(id));
    },

    selectedActiveKeyCount() {
      return this.selectedActiveKeyIds().length;
    },

    /** The feed plus any pinned rows the feed has since evicted; a live copy always wins over the snapshot. */
    requestsWithPinned() {
      const live = this.requests || [];
      if (!this.pinnedRequestIds.length) return live;
      const seen = new Set(live.map(r => r.requestId));
      for (const r of live) if (PINNED_REQUESTS.has(r.requestId)) PINNED_REQUESTS.set(r.requestId, r);
      const extra = this.pinnedRequestIds.filter(id => !seen.has(id) && PINNED_REQUESTS.has(id)).map(id => PINNED_REQUESTS.get(id));
      return extra.length ? live.concat(extra) : live;
    },

    requestsSlowThresholdMs() {
      const p95 = this.windowStats.p95Ms;
      return p95 && p95 > 0 ? Math.max(1000, p95 * 2) : 5000;
    },

    matchesRequestFilters(r) {
      if (this.requestsErrorsOnly && !(Number(r.statusCode) >= 400 || r.errorCode)) return false;
      if (this.requestsModelFilter && r.modelId !== this.requestsModelFilter) return false;
      if (this.requestsTenantFilter && (r.tenantId || '') !== this.requestsTenantFilter) return false;
      const cls = this.requestsStatusClass;
      if (cls) {
        const code = Number(r.statusCode);
        if (cls === 'inflight' && !r.isInFlight) return false;
        if (cls === '2xx' && !(code >= 200 && code < 300)) return false;
        if (cls === '4xx' && !(code >= 400 && code < 500)) return false;
        if (cls === '5xx' && !(code >= 500)) return false;
      }
      if (this.requestsSlowOnly && !(this.requestElapsedMs(r) >= this.requestsSlowThresholdMs())) return false;
      return true;
    },

    sortedRequests() {
      const list = this.requestsWithPinned().filter(r => this.matchesRequestFilters(r));
      // Whatever the sort, pinned rows stay on top, then work in progress: it is the part of the
      // feed that is changing, and a request that started 40s ago should not sink under ones that
      // finished since.
      const sorted = this.sortedList(list, 'requests');
      const pinned = sorted.filter(r => this.pinnedRequestIds.includes(r.requestId));
      const rest = sorted.filter(r => !this.pinnedRequestIds.includes(r.requestId));
      const running = rest.filter(r => r.isInFlight);
      return pinned.concat(running, rest.filter(r => !r.isInFlight));
    },

    togglePinRequest(id) {
      if (!id) return;
      if (this.pinnedRequestIds.includes(id)) {
        this.pinnedRequestIds = this.pinnedRequestIds.filter(x => x !== id);
        PINNED_REQUESTS.delete(id);
        return;
      }
      const row = (this.requests || []).find(r => r.requestId === id);
      if (row) PINNED_REQUESTS.set(id, row);
      this.pinnedRequestIds = [...this.pinnedRequestIds, id];
    },

    toggleRequestsPause() {
      this.requestsPaused = !this.requestsPaused;
      if (!this.requestsPaused && Array.isArray(this._pausedFrame)) {
        this.requests = this._pausedFrame;
        this._pausedFrame = null;
      }
    },
    get pauseButtonText() { return this.requestsPaused ? 'Resume' : 'Pause'; },
    get pauseButtonIcon() { return this.icon(this.requestsPaused ? 'play' : 'pause'); },
    get requestsPausedClass() { return this.requestsPaused ? 'action secondary is-paused' : 'action secondary'; },
    get pausedPendingCount() {
      if (!this.requestsPaused || !Array.isArray(this._pausedFrame)) return 0;
      const shown = new Set((this.requests || []).map(r => r.requestId));
      return this._pausedFrame.filter(r => !shown.has(r.requestId)).length;
    },

    clearRequestFilters() {
      this.requestsErrorsOnly = false;
      this.requestsModelFilter = '';
      this.requestsTenantFilter = '';
      this.requestsStatusClass = '';
      this.requestsSlowOnly = false;
    },
    get hasRequestFilters() {
      return !!(this.requestsErrorsOnly || this.requestsModelFilter || this.requestsTenantFilter || this.requestsStatusClass || this.requestsSlowOnly);
    },
    _optionsFrom(values) {
      const set = new Set(values.filter(Boolean));
      return Array.from(set).sort().map(v => ({ key: v, value: v, label: v }));
    },
    get requestsModelOptions() {
      const fromRows = (this.requests || []).map(r => r.modelId);
      const fromSummary = Object.keys(this.summary?.requestsPerModel || {});
      return this._optionsFrom(fromRows.concat(fromSummary));
    },
    get requestsTenantOptions() { return this._optionsFrom((this.requests || []).map(r => r.tenantId)); },
    get requestsStatusOptions() {
      return [['2xx', '2xx'], ['4xx', '4xx'], ['5xx', '5xx'], ['inflight', 'In flight']].map(([value, label]) => ({ key: value, value, label }));
    },

    urlLooksLocalhost() {
      return /localhost|127\.0\.0\.1/.test(this.editModel?.url || '');
    },

    openConfirm(dialog, returnFocusEl) {
      // Only the override is recorded here; opening the dialog is what the focus manager reacts to,
      // and it captures the current element by itself when no override is given.
      this._modalReturnFocusOverride = returnFocusEl || null;
      this.confirmDialog = dialog;
    },

    /**
     * Keyboard and screen-reader behaviour for every modal surface in the console.
     *
     * The confirm dialog used to be the only one that moved focus, and no surface trapped it or
     * took the page behind out of reach: with the model drawer open there were 118 focusable
     * elements still behind it, focus stayed on <body>, and Tab walked straight out of the dialog
     * into the page it was covering. The delete-key dialog asks the operator to type a prefix into
     * an input they had to Tab across the whole console to reach.
     *
     * Driven by an Alpine effect rather than by edits in a dozen open/close methods: the flags are
     * read here, so every surface — the ones that exist and the ones added later — is covered by
     * the same code, and a new drawer cannot forget to opt in.
     */
    get anyModalOpen() {
      return !!(this.confirmDialog || this.rlHelpOpen || this.rlWindowOpen || this.rlNewRuleOpen
        || this.rlTierDrawerOpen || this.rlRuleDrawerOpen || this.deleteConfirmKey
        || this.revokeConfirmId || this.modelTestDialog || this.modelDrawerOpen
        || this.keyAccessDrawerOpen || this.keysEditDrawerOpen || this.keysDrawerOpen);
    },

    /**
     * The surface actually on screen. Read from the DOM rather than mapped from each flag, so the
     * two cannot drift: x-show sets display:none, which is exactly what offsetParent reports.
     */
    _visibleModalSurface() {
      const visible = [];
      for (const el of document.querySelectorAll('[role="dialog"], [role="alertdialog"]')) {
        if (el.offsetParent === null) continue;
        // The help guide is the one surface that closes while another may stay open underneath.
        // x-transition keeps its backdrop displayed until the fade ends, so on the tick this runs it
        // still looks visible; its flag, not the DOM, says whether it is gone — otherwise the
        // drawer below would keep its inert attribute and be dead to the pointer.
        if (el.classList.contains('rl-help-drawer') && !this.rlHelpOpen) continue;
        visible.push(el);
      }
      // A confirmation is always the top surface: it is raised from inside a drawer (delete a rule,
      // discard edits, remove a plan) and sits later in the document than every drawer. Taking the
      // first visible surface chose the drawer underneath, which left the confirmation inert — on
      // screen, but dead to the pointer and to Tab. Its flag gates it for the same fade-out reason
      // as the help guide above.
      if (this.confirmDialog || this.deleteConfirmKey || this.revokeConfirmId) {
        const alert = visible.find(el => el.getAttribute('role') === 'alertdialog');
        if (alert) return alert;
      }
      return visible.find(el => el.getAttribute('role') !== 'alertdialog') || null;
    },

    _focusableWithin(root) {
      const selector = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), '
        + 'select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
      return [...root.querySelectorAll(selector)].filter(el => el.offsetParent !== null);
    },

    /**
     * Where the caret should land. A field first, because every drawer here opens on one the
     * operator is meant to fill in; `data-autofocus` overrides that for the dialogs whose primary
     * action is the point.
     */
    _initialModalFocus(surface) {
      return surface.querySelector('[data-autofocus]:not([disabled])')
        || surface.querySelector('input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled])')
        || this._focusableWithin(surface)[0]
        || null;
    },

    /**
     * @param open the reactive answer to "should a surface be active", which is the authority here.
     *
     * The DOM is only consulted for *which* surface, never for whether one is open. x-transition
     * keeps a closing backdrop displayed until its opacity animation ends, so a surface that has
     * just been dismissed still looks visible on the tick this runs — reading that as "unchanged"
     * left the page behind it inert for the rest of the session, with focus stranded in a drawer
     * that was no longer on screen.
     */
    syncModalFocus(open) {
      if (!open) {
        this._releaseModalSurface();
        return;
      }
      const surface = this._visibleModalSurface();
      if (!surface || surface === this._modalSurface) return;

      // Surfaces stack: a confirmation or the help guide opens over a drawer and closes back onto
      // it. Each remembers where its own focus should return, so closing the top one goes back
      // into the drawer, and closing the drawer still goes back to the row that opened it.
      const stack = this._modalStack || (this._modalStack = []);
      const resumed = stack.find(entry => entry.surface === surface);
      const comingFrom = this._modalReturnFocus;
      if (this._modalSurface) this._releaseModalSurface(false);

      if (resumed) {
        // Back on a surface that was covered: drop whatever sat above it.
        stack.length = stack.indexOf(resumed) + 1;
      } else {
        stack.push({
          surface,
          returnTo: this._modalReturnFocusOverride
            || (document.activeElement === document.body ? null : document.activeElement)
        });
      }
      this._modalReturnFocus = (resumed || stack[stack.length - 1]).returnTo;
      this._modalReturnFocusOverride = null;
      this._modalSurface = surface;

      // Everything that is not this surface stops being reachable — by pointer, by Tab, and to a
      // screen reader. aria-hidden goes on beside inert for browsers that do not support it; the
      // Tab trap below is what makes that safe rather than a way to hide focusable content.
      this._modalInerted = [];
      for (const el of document.body.children) {
        if (el.contains(surface) || el.hasAttribute('inert')) continue;
        el.inert = true;
        el.setAttribute('aria-hidden', 'true');
        this._modalInerted.push(el);
      }

      // Resuming lands where the closed surface was opened from (the Delete button, the "?"), not
      // back on the drawer's first field.
      const back = resumed && comingFrom && comingFrom.isConnected && surface.contains(comingFrom) && comingFrom.offsetParent !== null
        ? comingFrom : null;
      const target = back || this._initialModalFocus(surface);
      if (target) target.focus();
    },

    /** @param restoreFocus false while one surface hands over to another; true when the last one closes. */
    _releaseModalSurface(restoreFocus = true) {
      for (const el of this._modalInerted || []) {
        el.inert = false;
        el.removeAttribute('aria-hidden');
      }
      this._modalInerted = [];
      this._modalSurface = null;
      if (!restoreFocus) return;

      // Only back to something still on the page and on screen: a row action whose row has since
      // been re-rendered is gone, and focusing a detached or hidden node silently drops focus to
      // <body>. When the top surface's opener went away with the drawer under it (confirming a
      // delete closes both), fall back through the stack to the control that opened the drawer.
      const candidates = [this._modalReturnFocus, ...(this._modalStack || []).map(entry => entry.returnTo).reverse()];
      this._modalReturnFocus = null;
      this._modalStack = [];
      // Nothing inside a surface qualifies: every surface is closed now, and a drawer that is still
      // fading out would otherwise take the focus with it.
      const el = candidates.find(c => c && c.isConnected && c.focus && c.offsetParent !== null
        && !c.closest('[role="dialog"], [role="alertdialog"]'));
      if (el) el.focus();
    },

    /** Keeps Tab inside the open surface, in both directions. */
    _trapModalTab(e) {
      const surface = this._modalSurface;
      if (!surface) return;
      const items = this._focusableWithin(surface);
      if (items.length === 0) return;

      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      const outside = !surface.contains(active);

      if (e.shiftKey && (outside || active === first)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && (outside || active === last)) {
        e.preventDefault();
        first.focus();
      }
    },

    /**
     * Dismissing the dialog. `onCancel` exists for the case where the control that raised it has
     * already moved — a switch that flips before it asks — and must be put back; a dialog that only
     * gates an action leaves it out. Runs on Escape and on backdrop dismissal too, both of which
     * land here, so there is no path that abandons the change half-applied.
     */
    async cancelConfirm() {
      const d = this.confirmDialog;
      this.confirmDialog = null;
      if (d?.onCancel) await d.onCancel();
    },

    async confirmOk() {
      const d = this.confirmDialog;
      this.confirmDialog = null;
      if (d?.onConfirm) await d.onConfirm();
    },

    onModalKeydown(e) {
      if (e.key === 'Tab') {
        this._trapModalTab(e);
        return;
      }
      if (e.key === 'Escape') {
        if (this.confirmDialog) this.cancelConfirm();
        // The help drawer paints above the rate-limit drawers, so one Esc closes it and leaves the
        // form underneath where it was.
        else if (this.rlHelpOpen) this.closeRateLimitHelp();
        else if (this.rlWindowOpen) this.dismissRateLimitWindow();
        else if (this.rlNewRuleOpen) this.dismissRateLimitNewRule();
        else if (this.rlTierDrawerOpen) this.dismissRateLimitTier();
        else if (this.rlRuleDrawerOpen) this.dismissRateLimitRule();
        else if (this.deleteConfirmKey) this.cancelDeleteKey();
        else if (this.revokeConfirmId) this.cancelRevoke();
        else if (this.modelTestDialog) this.closeModelTestDialog();
        else if (this.modelDrawerOpen) this.closeModelDrawer();
        else if (this.keyAccessDrawerOpen) this.closeKeyAccessDrawer();
        else if (this.keysEditDrawerOpen) this.closeKeyEditDrawer();
        else if (this.keysDrawerOpen) this.closeKeysDrawer();
        // Last, so a dialog opened on top of a wallboard closes first and one Esc never does both.
        else if (this.wallboard) this.exitWallboard();
      }
    },

    closeModelTestDialog() {
      this.modelTestDialog = null;
    },

    modelTypes() {
      return this.modelTypeCatalog;
    },

    /**
     * Loads the canonical taxonomy (values, labels, health-check endpoints and every accepted alias)
     * from the gateway. On failure the bootstrap list stands, which degrades display but never
     * rewrites a model's type.
     */
    async loadModelTypes() {
      try {
        const types = await this.apiJson('/admin/api/model-types');
        if (Array.isArray(types) && types.length) this.modelTypeCatalog = types;
      } catch (e) {
        // Non-fatal: resolveModelType falls back to preserving whatever the model already has.
      }
    },

    /**
     * The type the gateway will dispatch the health check on. Mirrors ModelTypes.Resolve: explicit
     * type wins, then a single-purpose capability list, then text generation.
     *
     * Returns the model's RAW modelType when it is set but not recognised — never a coerced
     * default. Coercing it meant opening the edit dialog for a model typed with an alias the UI did
     * not know pre-selected the wrong type, and saving silently rewrote it.
     */
    resolveModelType(m) {
      if (!m) return 'text-generation';
      const canonical = t => {
        const raw = String(t || '').trim().toLowerCase();
        if (!raw) return null;
        const entry = (this.modelTypeCatalog || []).find(x =>
          x.value.toLowerCase() === raw ||
          (x.aliases || []).some(a => String(a).toLowerCase() === raw));
        return entry ? entry.value : null;
      };

      const explicit = canonical(m.modelType);
      if (explicit) return explicit;

      // Set but unrecognised: keep it verbatim so an edit round-trip cannot silently change it.
      if (String(m.modelType || '').trim()) return String(m.modelType).trim();

      const caps = [...new Set((m.capabilities || []).map(c => canonical(c)).filter(Boolean))];
      return caps.length === 1 ? caps[0] : 'text-generation';
    },

    /** True when the resolved type is not one the gateway knows, so the UI can show it as-is. */
    isUnknownModelType(type) {
      return !!type && !(this.modelTypeCatalog || []).some(t => t.value === type);
    },

    modelTypeLabel(type) {
      const entry = (this.modelTypeCatalog || []).find(t => t.value === type);
      if (entry) return entry.label;
      return type || '—';
    },

    /** What the Test button will actually send, so the dialog does not promise a chat call for an embedding model. */
    modelTestHint(modelId) {
      const m = (this.models || []).find(x => x.id === modelId);
      const type = this.resolveModelType(m);
      const entry = (this.modelTypeCatalog || []).find(t => t.value === type);
      if (!entry || !entry.testEndpoint) {
        return 'No automated health check is defined for ' + this.modelTypeLabel(type).toLowerCase() + ' models.';
      }
      if (type === 'embedding') return 'Embeds two short test sentences via ' + entry.testEndpoint + ' on the upstream.';
      if (type === 'rerank') return 'Reranks one test document via ' + entry.testEndpoint + ' on the upstream.';
      return 'Sends a short prompt to ' + entry.testEndpoint + ' on the upstream (short reply).';
    },

    async testModel(modelId) {
      if (!modelId) return;
      this.modelTestDialog = { modelId, loading: true, result: null, error: '' };
      try {
        // No payload: the gateway picks the probe from the model's type.
        const result = await this.runApi('modelTest', 'Testing model…', async () =>
          this.apiJson('/admin/api/models/' + encodeURIComponent(modelId) + '/test', {
            method: 'POST',
            body: JSON.stringify({})
          }), { localOnly: true });
        if (this.modelTestDialog?.modelId === modelId) {
          this.modelTestDialog = { modelId, loading: false, result, error: '' };
        }
        if (result?.ok) this.toast('Model test succeeded.');
        else if (result?.supported === false) this.toast('No health check for this model type.', 'error');
        else if (result) this.toast(result.detail || 'Model test failed.', 'error');
      } catch (e) {
        if (this.modelTestDialog?.modelId === modelId) {
          this.modelTestDialog = {
            modelId,
            loading: false,
            result: null,
            error: e.message || String(e)
          };
        }
      }
    },

    openModelDrawer(existing) {
      if (existing) this.startEditModel(existing);
      else this.resetModelForm();
      this.modelFieldError = '';
      this.modelDrawerOpen = true;
    },

    closeModelDrawer() {
      this.modelDrawerOpen = false;
      this.modelFieldError = '';
    },

    openKeysDrawer() {
      this.newKey = { role: 'Inference', label: '', assignee: '', description: '', costCenter: '' };
      this.keysDrawerOpen = true;
      this.createdKey = '';
      this.keysCreatedAck = false;
    },

    openKeyEditDrawer(key) {
      if (!key?.id || key.isRevoked) return;
      this.keyEdit = {
        id: key.id,
        keyPrefix: key.keyPrefix || '',
        label: key.label || '',
        assignee: key.assignee || '',
        description: key.description || '',
        costCenter: key.costCenter || ''
      };
      this.keysEditDrawerOpen = true;
    },

    closeKeyEditDrawer() {
      this.keysEditDrawerOpen = false;
      this.keyEdit = { id: '', keyPrefix: '', label: '', assignee: '', description: '', costCenter: '' };
    },

    async saveKeyEdit() {
      const key = this.keyEdit;
      if (!key?.id) return;
      await this.runApi('keys', 'Saving key…', async () => {
        await this.apiJson('/admin/api/keys/' + encodeURIComponent(key.id), {
          method: 'PATCH',
          body: JSON.stringify({
            label: key.label || null,
            assignee: key.assignee || null,
            description: key.description || null,
            costCenter: key.costCenter || null
          })
        });
        this.toast('API key updated.');
        this.closeKeyEditDrawer();
        await this.fetchKeys();
      });
    },

    viewKeyUsage(key) {
      if (!key?.id) return;
      // The key filter alone scopes the whole page (aggregated from the ledger), so the key's
      // current cost centre is deliberately not pre-filled — it would hide history recorded
      // under a previous assignment.
      this.usageFilterApiKeyId = key.id;
      this.usageFilterCostCenter = '';
      this.setUsagePreset('mtd');
      this.setTab('usage');
    },

    clearUsageFilters() {
      this.usageFilterApiKeyId = '';
      this.usageFilterCostCenter = '';
      if (this.apiKey) this.applyUsageRange().catch(() => {});
    },

    closeKeysDrawer() {
      if (this.createdKey && !this.keysCreatedAck) return;
      this.keysDrawerOpen = false;
      this.createdKey = '';
    },

    async openKeyAccess(key) {
      if (!key?.id || key.role === 'Admin') return;
      this.keyAccessEdit = key;
      this.keyAccessDrawerOpen = true;
      this.keyAccessSelected = [];
      if (!this.models?.length) await this.fetchModels();
      await this.runApi('keys', 'Loading model access…', async () => {
        const body = await this.apiJson('/admin/api/keys/' + encodeURIComponent(key.id) + '/model-grants');
        this.keyAccessSelected = [...(body?.modelIds ?? [])];
      });
    },

    closeKeyAccessDrawer() {
      this.keyAccessDrawerOpen = false;
      this.keyAccessEdit = null;
    },

    toggleKeyAccessModel(id) {
      const set = new Set(this.keyAccessSelected);
      if (set.has(id)) set.delete(id);
      else set.add(id);
      this.keyAccessSelected = [...set];
    },

    async saveKeyAccess() {
      const key = this.keyAccessEdit;
      if (!key?.id) return;
      await this.runApi('keys', 'Saving model access…', async () => {
        await this.apiJson('/admin/api/keys/' + encodeURIComponent(key.id) + '/model-grants', {
          method: 'PUT',
          body: JSON.stringify({ modelIds: this.keyAccessSelected })
        });
        this.toast('Model access updated.');
        this.closeKeyAccessDrawer();
        await this.loadKeys();
      });
    },

    async fetchTenantGrants() {
      const body = await this.apiJson('/admin/api/tenant/model-grants');
      const ids = body?.modelIds ?? [];
      this.tenantGrantRestricted = ids.length > 0;
      this.tenantGrantSelected = [...ids];
    },

    async loadTenantGrants() {
      if (!this.apiKey) return;
      await this.runApi('settings', 'Loading tenant model access…', () => this.fetchTenantGrants());
    },

    toggleTenantGrantModel(id) {
      const set = new Set(this.tenantGrantSelected);
      if (set.has(id)) set.delete(id);
      else set.add(id);
      this.tenantGrantSelected = [...set];
    },

    async saveTenantGrants() {
      const modelIds = this.tenantGrantRestricted ? this.tenantGrantSelected : [];
      await this.runApi('settings', 'Saving tenant model access…', async () => {
        await this.apiJson('/admin/api/tenant/model-grants', {
          method: 'PUT',
          body: JSON.stringify({ modelIds })
        });
        this.toast('Tenant model access updated.');
        await this.fetchTenantGrants();
      });
    },

    applyModelTemplate(kind) {
      const urls = {
        'lmstudio-docker': 'http://host.docker.internal:1234',
        'lmstudio-native': 'http://127.0.0.1:1234',
        'vllm-docker': 'http://host.docker.internal:8000',
        openrouter: 'https://openrouter.ai/api',
        together: 'https://api.together.xyz',
        groq: 'https://api.groq.com/openai',
        dashscope: 'https://dashscope-intl.aliyuncs.com/compatible-mode',
        'dashscope-beijing': 'https://dashscope.aliyuncs.com/compatible-mode'
      };
      if (urls[kind]) {
        this.editModel.url = urls[kind];
        this.toast('URL preset applied — set model name and API key if needed.');
      }
    },

    /**
     * The live vitals — from the 2s poll (`silent`) or an explicit refresh.
     *
     * `_summarySeq` is shared by both branches on purpose: a manual Refresh and a poll tick write
     * the same field, so without one sequence for the pair an overtaken reply could repaint the
     * vitals with older numbers. `_sequenced` mutates only inside `apply`, and only while this is
     * still the newest request for the key.
     */
    async loadSummary(silent) {
      const fetchSummary = () => this._sequenced('_summarySeq',
        () => this.apiJson('/admin/api/summary'),
        body => {
          this.summary = body;
          this.summaryUpdatedAt = Date.now();
          this.recordVitals();
        });

      if (silent) {
        if (!this._beginPoll('_summaryInFlight')) return;
        try {
          await fetchSummary();
          this.pollFailCount = 0;
          this.overviewStale = false;
        } catch {
          this.pollFailCount++;
          if (this.pollFailCount >= 2) this.overviewStale = true;
        } finally {
          this._endPoll('_summaryInFlight');
        }
        return;
      }
      await this.runApi('overview', 'Loading summary…', async () => {
        await fetchSummary();
        this.pollFailCount = 0;
        this.overviewStale = false;
      });
    },

    async loadHealth() {
      try {
        const [live, ready] = await Promise.all([
          fetch('/health/live').then(r => r.ok),
          fetch('/health/ready').then(r => r.ok)
        ]);
        this.healthLive = live;
        this.healthReady = ready;
      } catch {
        this.healthLive = false;
        this.healthReady = false;
      }
    },

    async fetchConfigStatus() {
      this.configStatus = await this.apiJson('/admin/api/config/status');
    },

    // ---- rate limits: server state, draft, schedule ----
    //
    // Read first, edit on demand. `rateLimits` is the last server-backed configuration and
    // `rlDraft` the copy the drawers edit; the page shows the draft, the sticky bar appears while
    // the two differ, and Save sends the whole draft (the API replaces the set wholesale). The
    // schedule report (`rlSchedule`) is read-only and always from the saved configuration.

    // The stored scopes, as they are named wherever an existing rule is listed or opened. The names
    // follow the new-rule form's two questions (rlIntents), so a rule reads the way it was made.
    rlScopeCatalog() {
      return [
        { id: 'model', name: 'Everyone on one model', short: 'Model', desc: 'Its whole capacity, shared by every caller.' },
        { id: 'tenant', name: 'A tenant, all models', short: 'Tenant', desc: 'Overrides the tenant’s plan tier.' },
        { id: 'api_key', name: 'An API key, all models', short: 'API key', desc: 'One credential inside its tenant’s allowance.' },
        { id: 'global', name: 'Whole gateway', short: 'Gateway', desc: 'Every inference request, whoever sends it.', singleton: true },
        { id: 'tenant_model', name: 'A tenant on one model', short: 'Tenant & model', desc: 'One customer’s share of one model.' },
        { id: 'api_key_model', name: 'An API key on one model', short: 'Key & model', desc: 'One credential on one model; nothing else is counted.' },
        { id: 'anonymous', name: 'Anonymous callers', short: 'Anonymous', desc: 'Per client address, on public models.', singleton: true },
        { id: 'auth_failure', name: 'Failed sign-ins', short: 'Failed sign-ins', desc: 'Credential guessing, per address. The failed-auth budget.', singleton: true }
      ];
    },

    /**
     * The one mapping between what an operator chooses — who is limited, on one model or all — and
     * the scope a rule is stored under. Read forwards by rlScopeFor and backwards by rlIntentFor;
     * nothing else may pair a choice with a scope.
     */
    rlIntents() {
      return [
        { who: 'key', where: 'one', scope: 'api_key_model' },
        { who: 'key', where: 'all', scope: 'api_key' },
        { who: 'tenant', where: 'one', scope: 'tenant_model' },
        { who: 'tenant', where: 'all', scope: 'tenant' },
        { who: 'everyone', where: 'one', scope: 'model' },
        { who: 'everyone', where: 'all', scope: 'global' },
        // The two protective budgets have no subject to choose and no model to be on.
        { who: 'anonymous', where: '', scope: 'anonymous' },
        { who: 'auth_failure', where: '', scope: 'auth_failure' }
      ];
    },

    rlScopeFor(who, where) {
      const hit = this.rlIntents().find(i => i.who === who && (i.where === '' || i.where === where));
      return hit ? hit.scope : '';
    },

    rlIntentFor(scope) {
      const hit = this.rlIntents().find(i => i.scope === String(scope || '').toLowerCase());
      return hit ? { who: hit.who, where: hit.where } : null;
    },

    rlScopeInfo(id) {
      return this.rlScopeCatalog().find(s => s.id === id) || { id, name: id, short: id, desc: '' };
    },

    rlIdentity(scope, target) {
      return String(scope || '').toLowerCase() + ':' + String(target || '').toLowerCase();
    },

    normalizeRateLimitWindow(w) {
      const iso = (v) => (v ? new Date(v).toISOString() : null);
      return {
        name: String(w.name ?? w.Name ?? ''),
        kind: String(w.kind ?? w.Kind ?? 'weekly').toLowerCase(),
        rpm: Number(w.rpm ?? w.Rpm ?? 0),
        burst: Number(w.burst ?? w.Burst ?? 0),
        maxConcurrentStreams: Number(w.maxConcurrentStreams ?? w.MaxConcurrentStreams ?? 0),
        suspend: !!(w.suspend ?? w.Suspend),
        priority: (w.priority ?? w.Priority) == null ? null : Number(w.priority ?? w.Priority),
        from: iso(w.from ?? w.From),
        until: iso(w.until ?? w.Until),
        days: Array.isArray(w.days ?? w.Days) ? (w.days ?? w.Days).map(d => String(d).toLowerCase()) : [],
        start: w.start ?? w.Start ?? null,
        end: w.end ?? w.End ?? null,
        timeZone: w.timeZone ?? w.TimeZone ?? null,
        validFrom: iso(w.validFrom ?? w.ValidFrom),
        validUntil: iso(w.validUntil ?? w.ValidUntil)
      };
    },

    normalizeRateLimitsPayload(data) {
      if (!data) return null;
      const d = data.default || data.Default || {};
      const plans = data.plans || data.Plans || {};
      const tier = (t) => ({
        rpm: t.rpm ?? t.Rpm ?? 60,
        burst: t.burst ?? t.Burst ?? 0,
        maxConcurrentStreams: t.maxConcurrentStreams ?? t.MaxConcurrentStreams ?? 0
      });
      const rules = data.rules ?? data.Rules ?? [];
      return {
        // The version this configuration was read at, sent back as If-Match so a save that is based
        // on a stale read is refused rather than erasing whatever landed in between. Rules are
        // replaced wholesale, so a stale save does not merge — it deletes.
        version: data.version ?? data.Version ?? null,
        // ?? not || so an explicit false is preserved; absent means enforcing, matching the server default.
        enabled: data.enabled ?? data.Enabled ?? true,
        // Absent means off: a gateway enforces exactly what it was configured to until someone asks
        // for something cleverer.
        adaptiveEnabled: data.adaptiveEnabled ?? data.AdaptiveEnabled ?? false,
        default: tier(d),
        plans: Object.fromEntries(
          Object.entries(plans).map(([slug, t]) => [slug, tier(t)])
        ),
        rules: (Array.isArray(rules) ? rules : []).map((r) => ({
          scope: r.scope ?? r.Scope ?? 'model',
          target: r.target ?? r.Target ?? '',
          ...tier(r),
          // ?? not || so an explicit false survives; absent means enforced, matching a gateway that
          // predates the flag.
          enabled: (r.enabled ?? r.Enabled) !== false,
          schedule: (Array.isArray(r.schedule ?? r.Schedule) ? (r.schedule ?? r.Schedule) : [])
            .map((w) => this.normalizeRateLimitWindow(w))
        }))
      };
    },

    rlClone(value) {
      return value == null ? value : JSON.parse(JSON.stringify(value));
    },

    applyRateLimitsData(data) {
      const normalized = this.normalizeRateLimitsPayload(data);
      if (!normalized) {
        this.rateLimits = null;
        this.rlDraft = null;
        return;
      }
      this.rateLimits = normalized;
      this.rlDraft = this.rlClone(normalized);
      this.rateLimitFieldError = '';
      this.rateLimitsLoadError = '';
      // The read-only reason is learnt from a refused write, so only a successful write clears it;
      // a successful read says nothing about whether the gateway can persist.
    },

    /**
     * Fetches the saved configuration. A dirty draft is kept unless `force` says otherwise: the
     * Settings tab reloads every section whenever it is entered, and an operator who stepped out
     * to look something up must not come back to an empty draft. Reload and Save ask for a
     * replacement explicitly.
     */
    async fetchRateLimits(force) {
      if (!force && this.rlDraft && this.rateLimitsDirty) {
        void this.loadRateLimitSchedule();
        return;
      }
      const data = await this.apiJson('/admin/api/rate-limits');
      this.applyRateLimitsData(data);
      void this.loadRateLimitSchedule();
    },

    async loadRateLimits(force) {
      this.rateLimitsLoadError = '';
      try {
        await this.fetchRateLimits(force);
      } catch (e) {
        // A failed refresh keeps whatever the page already shows; only a first load has nothing
        // to fall back on.
        if (!this.rlDraft) {
          this.rateLimits = null;
          this.rlDraft = null;
        }
        if (String(e.title || '').startsWith('404') || e.message?.includes('404') || /not found/i.test(e.message || '')) {
          this.rateLimitsLoadError =
            'Rate limit API is not available on this gateway (rebuild/restart the server with the latest image).';
        } else if (e.title === 'Authentication failed' || e.message?.includes('401') || e.message?.includes('403')) {
          this.rateLimitsLoadError = 'Connect with an Admin API key to load rate limits.';
        } else {
          this.rateLimitsLoadError = e.message || 'Could not load rate limits.';
        }
      }
    },

    /** Reload from the server. A dirty draft is thrown away only after the operator agrees. */
    reloadRateLimits() {
      if (!this.rateLimitsDirty) {
        void this.runApi('settings', 'Reloading rate limits…', () => this.loadRateLimits(true));
        return;
      }
      this.openConfirm({
        title: 'Reload and discard changes?',
        message: 'Reloading fetches the saved configuration and throws away your unsaved edits.',
        confirmLabel: 'Reload',
        onConfirm: () => this.runApi('settings', 'Reloading rate limits…', () => this.loadRateLimits(true))
      });
    },

    /** Back to the last server-backed state without a round trip. */
    discardRateLimitChanges() {
      if (!this.rateLimits) return;
      this.rlDraft = this.rlClone(this.rateLimits);
      this.rateLimitFieldError = '';
      this.closeRateLimitDrawers();
      this.queueRateLimitScheduleRefresh();
      this.toast('Changes discarded.');
    },

    /**
     * The master switch, with a confirmation on the way down only. Turning enforcement off stages
     * like any other edit and reads in the save bar as one change among several, which understates
     * it: every rule and every window stops applying at once. The switch moves first so the control
     * never lies about its own state, and a cancel puts it back.
     */
    setRateLimitEnforcement(value) {
      if (!this.rlDraft) return;
      this.rlDraft.enabled = value;
      if (value) return;

      const rules = (this.rlDraft.rules || []).length;
      const windows = (this.rlDraft.rules || []).reduce((n, r) => n + (r.schedule || []).length, 0);
      this.openConfirm({
        title: 'Stop enforcing rate limits?',
        message: this.formatNum(rules) + ' rule' + (rules === 1 ? '' : 's') + ' and '
          + this.formatNum(windows) + ' schedule window' + (windows === 1 ? '' : 's')
          + ' stop applying to every caller once you save. Quotas and budgets still apply, and nothing is deleted.',
        confirmLabel: 'Stop enforcing',
        danger: true,
        onConfirm: () => {},
        onCancel: () => { if (this.rlDraft) this.rlDraft.enabled = true; }
      });
    },

    closeRateLimitDrawers() {
      this.rlRuleDrawerOpen = false;
      this.rlTierDrawerOpen = false;
      this.rlWindowOpen = false;
      this.rlNewRuleOpen = false;
    },

    // ---- rate limits: help (English / Persian) ----
    //
    // The words live in admin-rate-limit-help.js; nothing here knows what they say. The guide is a
    // drawer that may open on top of the rule, tier or new-rule drawer (its "?" buttons do that), so
    // it is first in the DOM for the focus trap and one z-index above the others for paint order.

    rlHelpTopics() {
      return ['overview', 'numbers', 'tiers', 'scopes', 'combine', 'windows', 'calendar', 'saving', 'recipes', 'faq'];
    },

    openRateLimitHelp(topic) {
      const id = this.rlHelpTopics().includes(topic) ? topic : 'overview';
      this.rlHelpTopic = id;
      // Focus should come back to the "?" that opened the guide, even when that button sits inside
      // another drawer that the focus trap is about to hand over.
      this._modalReturnFocusOverride = document.activeElement === document.body ? null : document.activeElement;
      this.rlHelpOpen = true;
      this.rlHelpScrollTo(id, true);
    },

    closeRateLimitHelp() {
      this.rlHelpOpen = false;
    },

    rlHelpJump(id) {
      this.rlHelpTopic = id;
      this.rlHelpScrollTo(id, false);
    },

    rlHelpScrollTo(id, instant) {
      // The drawer is display:none until x-show runs on the next tick; the timer covers the
      // opening transition so the section is in place before we ask the browser to scroll to it.
      const go = () => document.getElementById('rl-help-' + id)?.scrollIntoView({ block: 'start', behavior: instant ? 'auto' : 'smooth' });
      this.$nextTick(() => { go(); setTimeout(go, 60); });
    },

    setRateLimitHelpLang(id) {
      const all = window.RateLimitHelp;
      if (!all || !all[id]) return;
      this.rlHelpLang = id;
      localStorage.setItem('33pol-admin-help-lang', id);
    },

    toggleRateLimitHelpLang() {
      this.setRateLimitHelpLang(this.rlHelpLang === 'fa' ? 'en' : 'fa');
    },

    rlTierPayload(t) {
      return {
        rpm: Number(t?.rpm) || 0,
        burst: Number(t?.burst) || 0,
        maxConcurrentStreams: Number(t?.maxConcurrentStreams) || 0
      };
    },

    /**
     * The server's numeric bounds, mirrored so an out-of-range number is caught in the drawer that
     * owns it rather than at Save, by which point the drawer has closed and the message speaks in
     * the API's words rather than the field's. Kept in step with RateLimitConfigValidation (MinRpm,
     * MaxRpm, MinBurst, MaxBurst, MaxMaxConcurrentStreams); the server remains the authority, this
     * only moves the message closer to the mistake.
     *
     * `floorRpm` is true for a tier, which has no "does not limit the rate" reading: a plan or the
     * default must name a rate. A scoped rule may leave rpm at zero to cap only concurrency.
     */
    rlTierBoundsError(tier, floorRpm) {
      const minRpm = floorRpm ? 1 : 0;
      if (!Number.isInteger(tier.rpm) || tier.rpm < minRpm || tier.rpm > 1000000) {
        return 'RPM must be a whole number between ' + minRpm + ' and 1,000,000.';
      }
      if (!Number.isInteger(tier.burst) || tier.burst < 0 || tier.burst > 1000000) {
        return 'Burst must be a whole number between 0 and 1,000,000.';
      }
      if (!Number.isInteger(tier.maxConcurrentStreams) || tier.maxConcurrentStreams < 0 || tier.maxConcurrentStreams > 10000) {
        return 'Streams must be a whole number between 0 and 10,000.';
      }
      return '';
    },

    rlWindowPayload(w) {
      const isOnce = w.kind === 'once';
      const payload = {
        name: String(w.name ?? '').trim(),
        kind: w.kind,
        rpm: w.suspend ? 0 : (Number(w.rpm) || 0),
        burst: w.suspend ? 0 : (Number(w.burst) || 0),
        maxConcurrentStreams: w.suspend ? 0 : (Number(w.maxConcurrentStreams) || 0),
        suspend: !!w.suspend,
        priority: w.priority === '' || w.priority == null ? null : Number(w.priority),
        from: isOnce ? (w.from || null) : null,
        until: isOnce ? (w.until || null) : null,
        days: isOnce ? null : (w.days || []),
        start: isOnce ? null : (w.start || null),
        end: isOnce ? null : (w.end || null),
        timeZone: isOnce ? null : (w.timeZone || null),
        validFrom: w.validFrom || null,
        validUntil: w.validUntil || null
      };
      return payload;
    },

    buildRateLimitsPayload(source) {
      const cfg = source || this.rlDraft || {};
      const plans = {};
      for (const [slug, t] of Object.entries(cfg.plans || {})) {
        const key = String(slug || '').trim();
        if (!key) continue;
        plans[key] = this.rlTierPayload(t);
      }
      // Rows with no target are left out of the payload: an empty target is the half-typed state of
      // a rule the operator has not finished, and the server would reject the whole save for it. The
      // save is blocked when there are any (see saveRateLimits) rather than quietly going ahead —
      // because the server replaces the rule set wholesale, sending the payload without them deletes
      // them, and the operator was told "Rate limits saved."
      const rules = (cfg.rules || [])
        .filter((row) => String(row.target ?? '').trim() !== '')
        .map((row) => ({
          scope: String(row.scope ?? '').trim(),
          target: String(row.target ?? '').trim(),
          ...this.rlTierPayload(row),
          // Sent explicitly rather than omitted when true: the payload replaces the rule set
          // wholesale, so a field left out is a field the server fills with its own default.
          enabled: row.enabled !== false,
          // Always an array: the draft is the complete truth, and an absent field would tell the
          // server to keep whatever windows it has stored.
          schedule: (row.schedule || []).map((w) => this.rlWindowPayload(w))
        }));
      return {
        enabled: cfg.enabled !== false,
        adaptiveEnabled: cfg.adaptiveEnabled === true,
        default: this.rlTierPayload(cfg.default),
        plans,
        rules
      };
    },

    /** The If-Match header for a save, when the loaded configuration told us its version. */
    rlIfMatchHeaders() {
      const version = this.rateLimits?.version;
      return version === null || version === undefined ? {} : { 'If-Match': 'W/"' + version + '"' };
    },

    /** Draft rules the server would refuse, and saving would therefore delete. */
    rlIncompleteRules() {
      return (this.rlDraft?.rules || []).filter((row) => String(row.target ?? '').trim() === '');
    },

    /**
     * The thing a refused save is complaining about, recovered from the server's message. Every
     * validation path names its subject — `rule 'model:gpt-4'`, `plans['standard']`, `default` —
     * so the console can offer the way back to the drawer that owns it rather than leaving the
     * operator to find it. Returns null when the message names nothing openable.
     */
    rlSaveErrorTarget() {
      const message = String(this.rateLimitFieldError || '');
      if (!message) return null;

      const rule = message.match(/rule '([^']+)'/);
      if (rule) {
        const identity = this.rlIdentity(...String(rule[1]).split(':'));
        const found = this.rlFindDraftRule(identity);
        if (found) {
          const info = this.rlScopeInfo(found.scope);
          const name = info.singleton ? info.name : found.target;
          return { label: 'Open ' + info.short.toLowerCase() + ' “' + name + '”', open: () => this.openRateLimitRule(identity) };
        }
        return null;
      }

      const plan = message.match(/plans\['([^']+)'\]/);
      if (plan && this.rlDraft?.plans?.[plan[1]]) {
        return { label: 'Open plan “' + plan[1] + '”', open: () => this.openRateLimitTier('plan', plan[1]) };
      }

      if (/^default\b/.test(message)) {
        return { label: 'Open the default tier', open: () => this.openRateLimitTier('default', '') };
      }

      return null;
    },

    async saveRateLimits() {
      if (!this.rlDraft) return;

      // Refused here rather than filtered out of the payload. A rule set is saved wholesale, so a row
      // the payload leaves out is a row the save deletes — and the operator would have been told the
      // save succeeded.
      const incomplete = this.rlIncompleteRules();
      if (incomplete.length) {
        this.rateLimitFieldError = incomplete.length === 1
          ? 'One rule has no target yet. Give it one, or remove it, then save.'
          : incomplete.length + ' rules have no target yet. Give them one, or remove them, then save.';
        this.toast(this.rateLimitFieldError);
        return;
      }

      this.closeRateLimitDrawers();
      await this.runApi('settings', 'Saving rate limits…', async () => {
        this.rateLimitFieldError = '';
        this.rlSaving = true;
        try {
          const body = await this.apiJson('/admin/api/rate-limits', {
            method: 'PUT',
            headers: this.rlIfMatchHeaders(),
            body: JSON.stringify(this.buildRateLimitsPayload())
          });
          this.toast(body?.message || 'Rate limits saved.');
          this.rlReadOnlyReason = '';
          await this.loadRateLimits(true);
        } catch (e) {
          const status = String(e.title || '') + ' ' + String(e.message || '');
          if (e.status === 409) {
            // Somebody else saved first. The draft is deliberately kept: it is the operator's work,
            // and reloading over it is what the confirm on Reload exists to prevent.
            this.rateLimitFieldError =
              'Rate limits were changed by someone else since this page was loaded. '
              + 'Reload to see the current configuration, then reapply your change.';
            this.toast('Someone else changed rate limits — reload before saving.');
            throw e;
          }
          if (/503/.test(status) || /configured database/i.test(status)) {
            this.rlReadOnlyReason = 'This gateway has no database, so rate limits are read-only here.';
          } else if (/403/.test(status)) {
            this.rlReadOnlyReason = 'This key may view rate limits but not change them.';
          }
          this.rateLimitFieldError = e.message || 'Failed to save rate limits.';
          throw e;
        } finally {
          this.rlSaving = false;
        }
      }, { localOnly: true });
    },

    /** Rows asked for per section. The endpoint caps at 1000; 200 keeps the rule-list join honest. */
    rlUsageTake() { return 200; },

    async loadRateLimitUsage() {
      // Last started wins: changing the window twice must not let the slower, older answer land.
      const seq = (this._rlUsageSeq || 0) + 1;
      this._rlUsageSeq = seq;
      this.rateLimitUsageLoading = true;
      try {
        const minutes = Number(this.rateLimitUsageMinutes) || 60;
        const report = await this.apiJson(
          '/admin/api/rate-limits/usage?minutes=' + minutes + '&take=' + this.rlUsageTake()
        );
        if (seq !== this._rlUsageSeq) return;
        this.rateLimitUsage = report;
        this.rateLimitUsageError = '';
        this.rateLimitUsageStale = false;
        this.rateLimitUsageUnavailable = false;
        this.rateLimitUsageLoadedAt = Date.now();
      } catch (e) {
        if (seq !== this._rlUsageSeq) return;
        // The previous report is kept on purpose. Activity is a side read: its failure must not
        // blank numbers that were true a minute ago, and never touches the configuration above it.
        this.rateLimitUsageUnavailable = e?.status === 503;
        this.rateLimitUsageStale = !!this.rateLimitUsage;
        this.rateLimitUsageError = e?.message || 'Could not load rate-limit activity.';
      } finally {
        if (seq === this._rlUsageSeq) this.rateLimitUsageLoading = false;
      }
    },

    /** The window control: picking a window is asking for it, so it loads without a second click. */
    setRateLimitUsageMinutes(minutes) {
      const value = Number(minutes) || 60;
      if (value === Number(this.rateLimitUsageMinutes) && this.rateLimitUsage && !this.rateLimitUsageStale) return;
      this.rateLimitUsageMinutes = value;
      void this.loadRateLimitUsage();
    },

    setRateLimitUsageTab(tab) {
      this.rlUsageTab = tab;
    },

    scrollToRateLimitUsage() {
      if (!this.rateLimitUsage) void this.loadRateLimitUsage();
      const el = document.getElementById('rate-limit-usage');
      if (el && el.scrollIntoView) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    },

    // ---- time helpers (display zone aware) ----

    rlZoneOrDefault() {
      return this.rlZone || this.rlBrowserZone();
    },

    rlBrowserZone() {
      try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
    },

    rlZoneParts(date, zone) {
      const fmt = new Intl.DateTimeFormat('en-US', {
        timeZone: zone, hourCycle: 'h23',
        year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'
      });
      const p = {};
      for (const part of fmt.formatToParts(date)) p[part.type] = part.value;
      return {
        year: Number(p.year), month: Number(p.month), day: Number(p.day),
        hour: Number(p.hour) % 24, minute: Number(p.minute), second: Number(p.second)
      };
    },

    /** The zone's UTC offset in minutes at an instant. */
    rlZoneOffset(date, zone) {
      const z = this.rlZoneParts(date, zone);
      const asUtc = Date.UTC(z.year, z.month - 1, z.day, z.hour, z.minute, z.second);
      return Math.round((asUtc - date.getTime()) / 60000);
    },

    /**
     * A wall-clock time in a zone as an instant. A time inside a DST gap does not exist; it is
     * moved forward by an hour, exactly as the server reads it, whichever way the zone's offset
     * happens to be signed. An ambiguous time (the repeated hour) takes the later, standard-time
     * instant, again matching the server.
     */
    rlZonedToDate(y, m, d, h, mi, zone) {
      const guess = Date.UTC(y, m - 1, d, h, mi, 0);
      // The zone's offsets a day either side of the requested time cover both sides of any
      // transition; each gives one candidate instant, kept if it reads back as the same wall clock.
      const offsets = [...new Set([-86400000, 0, 86400000].map(delta => this.rlZoneOffset(new Date(guess + delta), zone)))];
      const wall = (t) => {
        const z = this.rlZoneParts(new Date(t), zone);
        return Date.UTC(z.year, z.month - 1, z.day, z.hour, z.minute, 0);
      };
      const valid = offsets.map(off => guess - off * 60000).filter(t => wall(t) === guess);
      // Ambiguous (repeated hour): the later instant is the standard-time reading.
      if (valid.length) return new Date(Math.max(...valid));
      // In the gap: the requested wall clock exists under neither offset. Shift forward an hour
      // and read it with the offset in force after the change (the larger one).
      return new Date(guess + 3600000 - Math.max(...offsets) * 60000);
    },

    /** The instant the page treats as "now": ticks every 30 s while the page is open. */
    rlNow() {
      return this._rlTick || Date.now();
    },

    /** "2026-10-01T11:30" in a zone → ISO instant, or null when unparsable. */
    rlLocalToIso(local, zone) {
      const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(String(local || ''));
      if (!m) return null;
      const date = this.rlZonedToDate(+m[1], +m[2], +m[3], +m[4], +m[5], zone || this.rlZoneOrDefault());
      return Number.isNaN(date.getTime()) ? null : date.toISOString();
    },

    /** ISO instant → "yyyy-MM-ddTHH:mm" in a zone, for a datetime-local input. */
    rlIsoToLocal(iso, zone) {
      if (!iso) return '';
      const date = new Date(iso);
      if (Number.isNaN(date.getTime())) return '';
      const z = this.rlZoneParts(date, zone || this.rlZoneOrDefault());
      const pad = (n) => String(n).padStart(2, '0');
      return z.year + '-' + pad(z.month) + '-' + pad(z.day) + 'T' + pad(z.hour) + ':' + pad(z.minute);
    },

    rlFmt(iso, opts) {
      if (!iso) return '—';
      try {
        return new Intl.DateTimeFormat(undefined, { timeZone: this.rlZoneOrDefault(), hourCycle: 'h23', ...opts }).format(new Date(iso));
      } catch { return String(iso); }
    },

    /** "Sat 07:00", with the date once the instant is more than a week out. */
    rlFmtShort(iso) {
      if (!iso) return '—';
      const delta = new Date(iso).getTime() - Date.now();
      const far = Math.abs(delta) > 6 * 86400000;
      return this.rlFmt(iso, far
        ? { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
        : { weekday: 'short', hour: '2-digit', minute: '2-digit' });
    },

    rlFmtLong(iso) {
      return this.rlFmt(iso, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
    },

    rlRelative(iso) {
      if (!iso) return '';
      const ms = new Date(iso).getTime() - this.rlNow();
      const abs = Math.abs(ms);
      const min = Math.round(abs / 60000);
      let text;
      if (min < 1) text = 'now';
      else if (min < 60) text = min + ' m';
      else if (min < 48 * 60) text = Math.floor(min / 60) + ' h ' + (min % 60) + ' m';
      else text = Math.round(min / 1440) + ' d';
      if (text === 'now') return text;
      return ms >= 0 ? 'in ' + text : text + ' ago';
    },

    rlTierText(t) {
      if (!t) return '—';
      if (t.suspended) return 'paused';
      const parts = [];
      if (t.rpm > 0) parts.push(this.formatNum(t.rpm) + ' rpm');
      if (t.burst > 0) parts.push(this.formatNum(t.burst) + ' burst');
      if (t.maxConcurrentStreams > 0) parts.push(this.formatNum(t.maxConcurrentStreams) + ' streams');
      return parts.length ? parts.join(' · ') : 'rate unlimited';
    },

    rlDayLabels() {
      return [['mon', 'Mon'], ['tue', 'Tue'], ['wed', 'Wed'], ['thu', 'Thu'], ['fri', 'Fri'], ['sat', 'Sat'], ['sun', 'Sun']];
    },

    rlDaysText(days) {
      const order = this.rlDayLabels().map(d => d[0]);
      const set = new Set((days || []).map(d => String(d).toLowerCase()));
      const list = order.filter(d => set.has(d));
      if (list.length === 7) return 'every day';
      if (list.join() === 'mon,tue,wed,thu,fri') return 'Mon–Fri';
      if (list.join() === 'sat,sun') return 'Sat and Sun';
      return list.map(d => this.rlDayLabels().find(x => x[0] === d)[1]).join(', ');
    },

    /** A window as a sentence: "Mon–Fri 19:00 → 07:00 next day · Europe/Berlin". */
    rlWindowWhen(w) {
      if (!w) return '';
      if (w.kind === 'once') {
        const from = w.from ? this.rlFmtLong(w.from) : '?';
        if (!w.until) return 'From ' + from + ', open-ended';
        return from + ' → ' + this.rlFmtLong(w.until);
      }
      const overnight = w.start && w.end && w.end !== '24:00' && w.end <= w.start;
      return this.rlDaysText(w.days) + ' ' + (w.start || '?') + ' → ' + (w.end || '?') +
        (overnight ? ' next day' : '') + ' · ' + (w.timeZone || 'UTC');
    },

    // ---- schedule report ----

    rlRangeFromTo() {
      const zone = this.rlZoneOrDefault();
      const today = this.rlZoneParts(new Date(), zone);
      const from = this.rlZonedToDate(today.year, today.month, today.day, 0, 0, zone);
      const days = Number(this.rlRangeDays) || 7;
      // Local midnight `days` days on, not days × 24 h: a DST change inside the range would
      // otherwise end the calendar an hour into the next day.
      const to = this.rlZonedToDate(today.year, today.month, today.day + days, 0, 0, zone);
      return { from, to, days };
    },

    /**
     * Redraw the calendar after the draft changed. Debounced because a rule drawer can apply several
     * edits in a row, and each one would otherwise cost a round trip whose answer the next edit
     * immediately invalidates. Only rules feed the schedule report, so tier and plan edits do not
     * call this.
     */
    queueRateLimitScheduleRefresh() {
      if (this._rlDraftScheduleTimer) clearTimeout(this._rlDraftScheduleTimer);
      this._rlDraftScheduleTimer = setTimeout(() => {
        this._rlDraftScheduleTimer = null;
        void this.loadRateLimitSchedule();
      }, 300);
    },

    async loadRateLimitSchedule() {
      this.rlScheduleError = '';
      if (this._rlScheduleTimer) { clearTimeout(this._rlScheduleTimer); this._rlScheduleTimer = null; }
      try {
        const { from, to } = this.rlRangeFromTo();
        // A dirty draft is what the operator is actually asking about: the calendar exists to answer
        // "did I get this schedule right?", and the saved configuration cannot answer that for a
        // change that has not been saved. The preview route computes the same report over the
        // staged rules; with nothing staged the two are identical, so the GET stays the default.
        this.rlSchedule = this.rateLimitsDirty
          ? await this.apiJson('/admin/api/rate-limits/schedule/preview', {
            method: 'POST',
            body: JSON.stringify({
              rules: this.buildRateLimitsPayload().rules,
              from: from.toISOString(),
              to: to.toISOString(),
              take: 200
            })
          })
          : await this.apiJson(
            '/admin/api/rate-limits/schedule?from=' + encodeURIComponent(from.toISOString()) +
            '&to=' + encodeURIComponent(to.toISOString()) + '&take=200');
        this.rlScheduleLoadedAt = Date.now();
        // Re-read when the next window boundary passes so the "in force" column turns over on
        // its own; capped so a distant change does not pin a multi-day timer.
        const next = (this.rlSchedule?.rules || [])
          .map(r => r.nextChangeAt ? new Date(r.nextChangeAt).getTime() : NaN)
          .filter(t => Number.isFinite(t) && t > Date.now());
        if (next.length) {
          const wait = Math.min(Math.min(...next) - Date.now() + 1500, 30 * 60000);
          // Two loads in flight would each arm a timer; the later one wins.
          if (this._rlScheduleTimer) clearTimeout(this._rlScheduleTimer);
          this._rlScheduleTimer = setTimeout(() => {
            this._rlScheduleTimer = null;
            // Only while the page is actually being looked at: signed in, on this tab, visible.
            if (this.apiKey && this.tab === 'settings' && this.isSettingsLimits && !document.hidden) {
              void this.loadRateLimitSchedule();
            }
          }, wait);
        }
      } catch (e) {
        this.rlSchedule = null;
        this.rlScheduleError = e.message || 'Could not load the schedule.';
      }
    },

    setRateLimitZone(zone) {
      this.rlZone = zone;
      void this.loadRateLimitSchedule();
      if (this.rlPreview) void this.runRateLimitPreview();
    },

    setRateLimitRange(days) {
      this.rlRangeDays = Number(days) || 7;
      void this.loadRateLimitSchedule();
    },

    async runRateLimitPreview() {
      this.rlPreviewError = '';
      const local = String(this.rlPreviewAt || '').trim();
      if (!local) { this.rlPreview = null; return; }
      try {
        // Answers for the draft whenever the calendar beside it does; two panels disagreeing about
        // which configuration they describe would be worse than either being saved-only.
        this.rlPreview = this.rateLimitsDirty
          ? await this.apiJson('/admin/api/rate-limits/schedule/preview', {
            method: 'POST',
            body: JSON.stringify({
              rules: this.buildRateLimitsPayload().rules,
              atLocal: local,
              timeZone: this.rlZoneOrDefault(),
              take: 1
            })
          })
          : await this.apiJson(
            '/admin/api/rate-limits/schedule?atLocal=' + encodeURIComponent(local) +
            '&timeZone=' + encodeURIComponent(this.rlZoneOrDefault()) + '&take=1');
      } catch (e) {
        this.rlPreview = null;
        this.rlPreviewError = e.message || 'Could not evaluate that instant.';
      }
    },

    clearRateLimitPreview() {
      this.rlPreviewAt = '';
      this.rlPreview = null;
      this.rlPreviewError = '';
    },

    toggleRateLimitTransitions() {
      this.rlShowAllTransitions = !this.rlShowAllTransitions;
    },

    setRateLimitWhoFilter(id) { this.rlFilterWho = id; },
    setRateLimitWhereFilter(id) { this.rlFilterWhere = id; },
    /** Scheduled / Off: pressing the active one again releases it. */
    toggleRateLimitFlagFilter(id) { this.rlFilterFlag = this.rlFilterFlag === id ? '' : id; },

    clearRateLimitFilters() {
      this.rlFilterText = '';
      this.rlFilterWho = 'all';
      this.rlFilterWhere = 'any';
      this.rlFilterFlag = '';
      const input = document.getElementById('rl-filter-input');
      if (input && input.focus) input.focus();
    },

    /** Same column again flips the direction; a new column starts ascending, Refused descending. */
    setRateLimitSort(key) {
      if (this.rlSortKey === key) {
        this.rlSortDir = -this.rlSortDir;
        return;
      }
      this.rlSortKey = key;
      this.rlSortDir = key === 'refused' ? -1 : 1;
    },

    // ---- tiers ----

    /**
     * Escape and a click on the backdrop are the two ways to leave an editor by accident, and every
     * one of these editors holds a working copy that closing throws away. Cancel, Back, Done and the
     * close button say what they do, so they stay immediate; only the ambiguous exits ask, and only
     * when there is something to lose.
     */
    rlConfirmDismiss(dirty, message, discard) {
      if (!dirty) {
        discard();
        return;
      }
      this.openConfirm({
        title: 'Discard these changes?',
        message,
        confirmLabel: 'Discard',
        danger: true,
        onConfirm: discard
      });
    },

    dismissRateLimitTier() {
      this.rlConfirmDismiss(this.rlTierDirty, 'This tier has edits that have not been applied to the draft yet.', () => this.closeRateLimitTier());
    },

    dismissRateLimitRule() {
      this.rlConfirmDismiss(this.rlRuleDirty, 'This rule has edits that have not been applied to the draft yet.', () => this.closeRateLimitRule());
    },

    dismissRateLimitWindow() {
      this.rlConfirmDismiss(this.rlWindowDirty, 'This window has not been added to the rule yet.', () => this.closeRateLimitWindow());
    },

    dismissRateLimitNewRule() {
      this.rlConfirmDismiss(this.rlNewRuleDirty, 'This rule has not been created yet.', () => this.closeRateLimitNewRule());
    },

    openRateLimitTier(kind, slug) {
      if (!this.rlDraft) return;
      // Read-only: an existing tier opens for inspection; there is nothing to inspect in a new one.
      if (!this.rateLimitsEditable && kind === 'plan' && !slug) return;
      const tier = kind === 'default' ? this.rlDraft.default : (this.rlDraft.plans[slug] || { rpm: 60, burst: 10, maxConcurrentStreams: 5 });
      this.rlTier = {
        kind,
        slug: slug || '',
        originalSlug: slug || '',
        isNew: kind === 'plan' && !slug,
        rpm: tier.rpm, burst: tier.burst, maxConcurrentStreams: tier.maxConcurrentStreams
      };
      this.rlTierError = '';
      this.rlTierDrawerOpen = true;
    },

    closeRateLimitTier() {
      this.rlTierDrawerOpen = false;
    },

    applyRateLimitTier() {
      if (!this.rateLimitsEditable) return;
      const t = this.rlTier;
      const tier = this.rlTierPayload(t);
      // The "needs a rate" messages come first: they say why zero is refused, which a range cannot.
      if (tier.rpm < 1) {
        this.rlTierError = t.kind === 'default'
          ? 'The default tier needs at least 1 rpm; there is no unlimited value.'
          : 'A plan tier needs at least 1 rpm.';
        return;
      }
      const bounds = this.rlTierBoundsError(tier, true);
      if (bounds) {
        this.rlTierError = bounds;
        return;
      }
      if (t.kind === 'plan') {
        const slug = String(t.slug || '').trim();
        if (!/^[A-Za-z][A-Za-z0-9_-]*$/.test(slug)) {
          this.rlTierError = 'Plan slug: letters, digits, hyphen or underscore, starting with a letter.';
          return;
        }
        const clash = Object.keys(this.rlDraft.plans).find(k => k.toLowerCase() === slug.toLowerCase() && k !== t.originalSlug);
        if (clash) {
          this.rlTierError = 'A plan called “' + clash + '” already exists.';
          return;
        }
        if (t.originalSlug && t.originalSlug !== slug) delete this.rlDraft.plans[t.originalSlug];
        this.rlDraft.plans[slug] = tier;
      } else {
        this.rlDraft.default = tier;
      }
      this.rlTierDrawerOpen = false;
    },

    confirmRemoveRateLimitPlan() {
      const slug = this.rlTier.originalSlug;
      if (!slug) return;
      // The count and the tier are both already on screen behind this dialog; saying them here is
      // the difference between approving a change and approving a change whose scale is known.
      const affected = this.rlKnownTenants().filter(t => String(t.plan || '').toLowerCase() === slug.toLowerCase()).length;
      const fallback = this.rlTierText(this.rlDraft?.default);
      // Qualified deliberately: the count comes from the overview's top consumers this month, so it
      // is a floor rather than a roster. Overstating certainty in a destructive dialog is worse
      // than naming where the number came from.
      const seen = affected > 0
        ? this.formatNum(affected) + ' tenant' + (affected === 1 ? '' : 's') + ' seen this month '
          + (affected === 1 ? 'is' : 'are') + ' on this plan. '
        : '';
      this.openConfirm({
        title: 'Remove plan tier “' + slug + '”?',
        message: seen + 'Every tenant on it falls back to the default tier (' + fallback + ') once you save.',
        confirmLabel: 'Remove plan',
        danger: true,
        onConfirm: () => {
          delete this.rlDraft.plans[slug];
          this.rlTierDrawerOpen = false;
        }
      });
    },

    // ---- rules ----

    rlFindDraftRule(identity) {
      return (this.rlDraft?.rules || []).find(r => this.rlIdentity(r.scope, r.target) === identity);
    },

    openRateLimitRule(identity) {
      const rule = this.rlFindDraftRule(identity);
      if (!rule) return;
      this.rlRule = {
        identity,
        scope: rule.scope,
        target: rule.target,
        rpm: rule.rpm, burst: rule.burst, maxConcurrentStreams: rule.maxConcurrentStreams,
        enabled: rule.enabled !== false,
        schedule: this.rlClone(rule.schedule || [])
      };
      this.rlRuleError = '';
      this.rlWindowOpen = false;
      this.rlRuleDrawerOpen = true;
      if (!this.rateLimitUsage) void this.loadRateLimitUsage();
    },

    closeRateLimitRule() {
      this.rlRuleDrawerOpen = false;
      this.rlWindowOpen = false;
    },

    /** Writes the drawer's working copy back into the draft. */
    applyRateLimitRule() {
      if (!this.rateLimitsEditable) return;
      const rule = this.rlFindDraftRule(this.rlRule.identity);
      if (!rule) return;
      const tier = this.rlTierPayload(this.rlRule);
      if (tier.rpm === 0 && tier.maxConcurrentStreams === 0) {
        this.rlRuleError = 'A rule must limit something: set rpm or streams above zero.';
        return;
      }
      const bounds = this.rlTierBoundsError(tier, false);
      if (bounds) {
        this.rlRuleError = bounds;
        return;
      }
      if (rule.scope === 'tenant' && tier.rpm === 0 && tier.burst !== 0) {
        this.rlRuleError = 'A tenant rule with rpm 0 keeps the plan rate; set burst to 0 as well.';
        return;
      }
      Object.assign(rule, tier, {
        enabled: this.rlRule.enabled !== false,
        schedule: this.rlClone(this.rlRule.schedule)
      });
      this.rlRuleDrawerOpen = false;
      this.rlWindowOpen = false;
      this.queueRateLimitScheduleRefresh();
    },

    /**
     * Switch one rule on or off in the draft. Deliberately not a confirm: it is reversible, staged
     * like every other edit, named in the save bar, and the whole reason it exists is to be the fast
     * thing to reach for in an incident — a dialog in front of it would push an operator back to
     * Delete, which is the irreversible one.
     */
    setRateLimitRuleEnabled(identity, enabled) {
      const rule = this.rlFindDraftRule(identity);
      if (!rule) return;
      rule.enabled = !!enabled;
      if (this.rlRule.identity === identity) this.rlRule.enabled = !!enabled;
      this.queueRateLimitScheduleRefresh();
    },

    toggleRateLimitRuleEnabled(identity) {
      const rule = this.rlFindDraftRule(identity);
      if (!rule) return;
      this.setRateLimitRuleEnabled(identity, rule.enabled === false);
    },

    confirmDeleteRateLimitRule() {
      const identity = this.rlRule.identity;
      const info = this.rlScopeInfo(this.rlRule.scope);
      const windows = (this.rlRule.schedule || []).length;
      // Names the reversible alternative, because the two actions are one click apart and only one
      // of them can be undone after a save.
      const cost = windows
        ? ' ' + this.formatNum(windows) + ' schedule window' + (windows === 1 ? '' : 's') + ' go with it.'
        : '';
      this.openConfirm({
        title: 'Delete this rule permanently?',
        // The name an operator knows it by, never a bare key id; singletons have no target to name.
        message: (info.singleton ? info.name : info.short + ' “' + this.rlTargetDisplay(this.rlRule.scope, this.rlRule.target) + '”') + ' stops being limited by this rule once you save.' + cost
          + ' To stop enforcing it without losing the tier or the windows, switch it off instead.',
        confirmLabel: 'Delete permanently',
        danger: true,
        onConfirm: () => {
          this.rlDraft.rules = this.rlDraft.rules.filter(r => this.rlIdentity(r.scope, r.target) !== identity);
          this.rlRuleDrawerOpen = false;
          this.rlWindowOpen = false;
          this.queueRateLimitScheduleRefresh();
        }
      });
    },

    // ---- windows ----

    rlBlankWindow() {
      return {
        name: '', kind: 'weekly',
        rpm: this.rlRule.rpm || 60, burst: this.rlRule.burst || 0, maxConcurrentStreams: this.rlRule.maxConcurrentStreams || 0,
        suspend: false, priority: '',
        fromLocal: '', untilLocal: '',
        days: ['mon', 'tue', 'wed', 'thu', 'fri'], start: '19:00', end: '07:00',
        timeZone: this.rlZoneOrDefault(),
        validFromLocal: '', validUntilLocal: '',
        showAdvanced: false
      };
    },

    openRateLimitWindow(index) {
      if (!this.rlRuleDrawerOpen) return;
      const existing = index != null && index >= 0 ? this.rlRule.schedule[index] : null;
      this.rlWindowEditIndex = existing ? index : -1;
      const form = this.rlBlankWindow();
      if (existing) {
        const zone = existing.timeZone || this.rlZoneOrDefault();
        Object.assign(form, {
          name: existing.name, kind: existing.kind,
          rpm: existing.rpm, burst: existing.burst, maxConcurrentStreams: existing.maxConcurrentStreams,
          suspend: existing.suspend, priority: existing.priority == null ? '' : String(existing.priority),
          fromLocal: this.rlIsoToLocal(existing.from, zone), untilLocal: this.rlIsoToLocal(existing.until, zone),
          days: [...(existing.days || [])], start: existing.start || '19:00', end: existing.end || '07:00',
          timeZone: zone,
          validFromLocal: this.rlIsoToLocal(existing.validFrom, zone), validUntilLocal: this.rlIsoToLocal(existing.validUntil, zone),
          showAdvanced: existing.priority != null || !!existing.validFrom || !!existing.validUntil,
          // The stored instants, so an untouched field round-trips to the same instant even when
          // its wall-clock text is ambiguous (the repeated hour at the end of daylight saving).
          _orig: { from: existing.from || null, until: existing.until || null, validFrom: existing.validFrom || null, validUntil: existing.validUntil || null, zone }
        });
      }
      this.rlWindow = form;
      this._rlWindowBaseline = JSON.stringify(form);
      this.rlWindowError = '';
      this.rlWindowPreview = null;
      this.rlWindowOpen = true;
      this.queueRateLimitWindowPreview();
      // The panel swaps inside one dialog, so the modal focus handling does not run: say where we
      // are by landing on the panel's heading, and come back to what opened it.
      this._rlWindowReturn = existing ? 'rl-win-' + index : 'rl-add-window';
      this.rlFocusSoon('rl-window-title');
    },

    closeRateLimitWindow() {
      this.rlWindowOpen = false;
      this.rlFocusSoon(this._rlWindowReturn || 'rl-add-window', 'rl-rule-title');
    },

    /** Close from inside the window panel: leaves the whole drawer, asking first if either level has edits. */
    dismissRateLimitRuleFromWindow() {
      this.rlConfirmDismiss(this.rlWindowDirty || this.rlRuleDirty,
        this.rlWindowDirty ? 'This window has not been added to the rule yet.' : 'This rule has edits that have not been applied to the draft yet.',
        () => this.closeRateLimitRule());
    },

    /** Focus an element once the template swap has rendered it; `fallback` when the first is gone. */
    rlFocusSoon(id, fallback) {
      const go = () => {
        const el = document.getElementById(id) || (fallback ? document.getElementById(fallback) : null);
        if (el && el.focus) el.focus();
      };
      if (this.$nextTick) this.$nextTick(() => setTimeout(go, 0)); else go();
    },

    setRateLimitWindowKind(kind) {
      this.rlWindow.kind = kind;
      this.queueRateLimitWindowPreview();
    },

    toggleRateLimitWindowDay(day) {
      const days = new Set(this.rlWindow.days || []);
      if (days.has(day)) days.delete(day); else days.add(day);
      this.rlWindow.days = this.rlDayLabels().map(d => d[0]).filter(d => days.has(d));
      this.queueRateLimitWindowPreview();
    },

    toggleRateLimitWindowAdvanced() {
      this.rlWindow.showAdvanced = !this.rlWindow.showAdvanced;
    },

    /** The form as the API sees it: local wall-clock times resolved in the window's zone. */
    rlWindowFromForm() {
      const f = this.rlWindow;
      const zone = f.timeZone || this.rlZoneOrDefault();
      const orig = f._orig && f._orig.zone === zone ? f._orig : null;
      // An untouched datetime field keeps its stored instant rather than being re-resolved from
      // its wall-clock text, which is lossy inside the repeated hour of a DST change.
      const instant = (local, key) => {
        if (orig && orig[key] && this.rlIsoToLocal(orig[key], zone) === String(local || '')) return orig[key];
        return this.rlLocalToIso(local, zone);
      };
      return {
        name: String(f.name || '').trim(),
        kind: f.kind,
        rpm: Number(f.rpm) || 0, burst: Number(f.burst) || 0, maxConcurrentStreams: Number(f.maxConcurrentStreams) || 0,
        suspend: !!f.suspend,
        priority: f.priority === '' || f.priority == null ? null : Number(f.priority),
        from: f.kind === 'once' ? instant(f.fromLocal, 'from') : null,
        until: f.kind === 'once' ? instant(f.untilLocal, 'until') : null,
        days: f.kind === 'weekly' ? [...(f.days || [])] : [],
        start: f.kind === 'weekly' ? f.start : null,
        end: f.kind === 'weekly' ? f.end : null,
        timeZone: f.kind === 'weekly' ? zone : null,
        validFrom: instant(f.validFromLocal, 'validFrom'),
        validUntil: instant(f.validUntilLocal, 'validUntil')
      };
    },

    rlWindowLocalCheck(w) {
      if (!w.name) return 'Give the window a name.';
      if (w.kind === 'once' && !w.from) return 'Pick when the window starts.';
      if (w.kind === 'once' && w.until && w.until <= w.from) return 'Until must be after From. Leave Until empty for a change that stays in force.';
      if (w.kind === 'weekly' && !w.days.length) return 'Pick at least one day.';
      if (w.kind === 'weekly' && (!w.start || !w.end)) return 'Set a start and an end time.';
      if (!w.suspend && w.rpm <= 0 && w.maxConcurrentStreams <= 0) return 'Set rpm or streams above zero, or pause the rule instead.';
      // A suspending window ignores its numbers, so only a tier-bearing one is range-checked.
      if (!w.suspend) {
        const bounds = this.rlTierBoundsError(
          { rpm: w.rpm, burst: w.burst, maxConcurrentStreams: w.maxConcurrentStreams },
          false);
        if (bounds) return bounds;
      }
      return '';
    },

    queueRateLimitWindowPreview() {
      if (this._rlPreviewTimer) clearTimeout(this._rlPreviewTimer);
      this._rlPreviewTimer = setTimeout(() => { void this.refreshRateLimitWindowPreview(); }, 250);
    },

    /** The preview request body for the current form, and the fingerprint the answer is filed under. */
    rlWindowPreviewRequest() {
      const candidate = this.rlWindowFromForm();
      const others = this.rlRule.schedule.filter((_, i) => i !== this.rlWindowEditIndex);
      const body = {
        scope: this.rlRule.scope, target: this.rlRule.target,
        ...this.rlTierPayload(this.rlRule),
        windows: [...others, candidate].map(w => this.rlWindowPayload(w)),
        candidate: candidate.name
      };
      return { candidate, body, fingerprint: JSON.stringify(body) };
    },

    /**
     * Asks the server what the window as currently typed would do. Resolves to the preview for
     * exactly that form state, or null when the form is incomplete or the answer is already
     * stale; `rlWindowPreview` is only ever set to an answer for the form as it stands.
     */
    async refreshRateLimitWindowPreview() {
      if (!this.rlWindowOpen) return null;
      const { candidate, body, fingerprint } = this.rlWindowPreviewRequest();
      const local = this.rlWindowLocalCheck(candidate);
      this.rlWindowError = local;
      if (local) { this.rlWindowPreview = null; return null; }
      if (this.rlWindowPreview && this.rlWindowPreview._for === fingerprint) return this.rlWindowPreview;
      const seq = ++this._rlPreviewSeq;
      try {
        const preview = await this.apiJson('/admin/api/rate-limits/windows/preview', {
          method: 'POST',
          body: JSON.stringify(body)
        });
        // Answers can land out of order; only the newest request may write, and only if the
        // form still reads the way it did when the request left.
        if (seq !== this._rlPreviewSeq || !this.rlWindowOpen) return null;
        if (this.rlWindowPreviewRequest().fingerprint !== fingerprint) return null;
        preview._for = fingerprint;
        this.rlWindowPreview = preview;
        return preview;
      } catch (e) {
        if (seq !== this._rlPreviewSeq) return null;
        this.rlWindowPreview = null;
        this.rlWindowError = e.message || 'Could not check the window.';
        return null;
      }
    },

    async applyRateLimitWindow() {
      const candidate = this.rlWindowFromForm();
      const local = this.rlWindowLocalCheck(candidate);
      if (local) { this.rlWindowError = local; return; }
      const duplicate = this.rlRule.schedule.some((w, i) => i !== this.rlWindowEditIndex && w.name.toLowerCase() === candidate.name.toLowerCase());
      if (duplicate) { this.rlWindowError = 'Another window on this rule already has that name.'; return; }
      // The gate is the server's verdict on the form as it stands now, never a cached one for an
      // earlier keystroke; a click inside the debounce waits for the fresh answer.
      if (this._rlPreviewTimer) { clearTimeout(this._rlPreviewTimer); this._rlPreviewTimer = null; }
      const preview = await this.refreshRateLimitWindowPreview();
      if (!this.rlWindowOpen) return;
      if (!preview) {
        if (!this.rlWindowError) this.rlWindowError = 'Could not check the window; try again.';
        return;
      }
      if (preview.valid === false) {
        this.rlWindowError = preview.error || 'This window conflicts with another one.';
        return;
      }
      if (this.rlWindowEditIndex >= 0) this.rlRule.schedule.splice(this.rlWindowEditIndex, 1, candidate);
      else this.rlRule.schedule.push(candidate);
      this.rlWindowOpen = false;
    },

    removeRateLimitWindow() {
      if (this.rlWindowEditIndex < 0) { this.rlWindowOpen = false; return; }
      const index = this.rlWindowEditIndex;
      const name = this.rlRule.schedule[index]?.name || 'this window';
      this.openConfirm({
        title: 'Remove window?',
        message: "'" + name + "' will be removed from this rule's schedule. The change is staged; nothing is saved until you press Save.",
        confirmLabel: 'Remove',
        danger: true,
        onConfirm: () => {
          if (!this.rlRuleDrawerOpen) return;
          this.rlRule.schedule.splice(index, 1);
          this.rlWindowOpen = false;
        }
      });
    },

    // ---- new rule flow ----

    /**
     * The numbers a new rule starts from, by scope. One table for every scope rather than one
     * number for all of them, because the protective scopes mean something different: 600 rpm of
     * failed sign-ins is ten times looser than the tier this gateway ships with, so an operator
     * adding credential-guessing protection and keeping the seed would have weakened it while
     * believing they had tightened it. The gateway ceiling is deliberately blank — there is no
     * number that is right for every deployment, and a wrong one throttles everything at once.
     */
    rlDefaultTierFor(scope) {
      if (scope === 'auth_failure') return { rpm: 20, burst: 10, maxConcurrentStreams: 0 };
      if (scope === 'anonymous') return { rpm: 30, burst: 10, maxConcurrentStreams: 2 };
      if (scope === 'global') return { rpm: '', burst: 0, maxConcurrentStreams: 0 };
      return { rpm: 600, burst: 60, maxConcurrentStreams: 0 };
    },

    /** Applies the scope's seed, unless the operator has already typed over it. */
    rlSeedNewRuleTier() {
      if (this.rlNewRule.touched) return;
      Object.assign(this.rlNewRule, this.rlDefaultTierFor(this.rlNewRuleScope()));
    },

    /** Marks the tier as the operator's, so a later scope change leaves their numbers alone. */
    setRateLimitNewRuleTier(field, value) {
      this.rlNewRule[field] = value;
      this.rlNewRule.touched = true;
    },

    // Bound to a click, which hands a handler the event: the prefill has its own entry point so an
    // event can never be mistaken for one.
    openRateLimitNewRule() { this.startRateLimitNewRule(); },

    /**
     * The two protective budgets — anonymous callers, failed sign-ins — are made in the same form
     * and stored by the same path, but entered from their own button: they have no subject and no
     * model, so they are not an answer to "who is limited, on which model". Opens on whichever of
     * the two has no rule yet, since each can have only one.
     */
    openRateLimitProtectiveRule() {
      const free = ['anonymous', 'auth_failure'].find(scope => !this.rlFindDraftRule(this.rlIdentity(scope, '*')));
      this.startRateLimitNewRule({ who: this.rlIntentFor(free || 'anonymous').who });
    },

    /**
     * Opens the form on the common case — an API key on one model. A key another page already knows
     * goes in through the same pick a click on a suggestion makes, so nothing downstream can tell a
     * prefilled form from one the operator filled.
     */
    startRateLimitNewRule({ key = null, who = 'key' } = {}) {
      if (!this.rlDraft || !this.rateLimitsEditable) return;
      this.rlNewRule = { who, where: 'one', subject: '', model: '', rpm: 0, burst: 0, maxConcurrentStreams: 0, touched: false, picked: {}, tried: false, opened: '' };
      if (key) this.pickRateLimitSuggestion('subject', key.id, this.rlKeyPickText(key));
      this.rlSeedNewRuleTier();
      this.rlCombo = { field: '', index: -1, closed: {} };
      this.rlNewRule.opened = this.rlNewRuleSnapshot();
      this.rlNewRuleOpen = true;
      // Refreshed each time: a key target must be a key the list knows, so a key created since the
      // list was loaded has to be in it.
      this.loadRateLimitKeys(true);
    },

    /** "Limit this key…" on the Keys page: the same form, with the key already chosen. */
    async limitRateForKey(key) {
      this.setTab('settings');
      this.setSettingsSubTab('limits');
      if (!this.rlDraft) await this.loadRateLimits();
      if (!this.rateLimitsEditable) {
        this.toast(this.rateLimitsReadOnlyText || this.rateLimitsLoadError || 'Rate limits cannot be edited right now.', 'error');
        return;
      }
      this.startRateLimitNewRule({ key });
    },

    closeRateLimitNewRule() {
      this.rlNewRuleOpen = false;
    },

    rlNewRuleScope() {
      return this.rlScopeFor(this.rlNewRule.who, this.rlNewRule.where);
    },

    /** The choices an operator has made, as one comparable value (see `opened`). */
    rlNewRuleSnapshot() {
      const n = this.rlNewRule;
      return JSON.stringify([n.who, n.where, n.subject, n.model]);
    },

    rlClearNewRuleField(field) {
      const picked = { ...(this.rlNewRule.picked || {}) };
      delete picked[field];
      Object.assign(this.rlNewRule, { [field]: '', picked });
    },

    /**
     * A subject belongs to its kind: a key's name is not a tenant, and "everyone" has none. The
     * model is kept across key and tenant, where it means the same thing, and dropped with the
     * protective budgets, which are not on a model at all.
     */
    setRateLimitNewRuleWho(who) {
      if (this.rlNewRule.who === who) return;
      this.rlNewRule.who = who;
      this.rlClearNewRuleField('subject');
      if (!this.rlIntentFor(this.rlNewRuleScope())?.where) this.rlClearNewRuleField('model');
      this.rlSeedNewRuleTier();
    },

    setRateLimitNewRuleWhere(where) {
      if (this.rlNewRule.where === where) return;
      this.rlNewRule.where = where;
      if (where !== 'one') this.rlClearNewRuleField('model');
      this.rlSeedNewRuleTier();
    },

    /**
     * Arrow-key traversal for the form's two radiogroups. The cards carry role="radio", which
     * promises a keyboard user can move between them with the arrows and reach the group with one
     * Tab; roving tabindex (see rlNewRuleView) supplies the second half of that promise.
     */
    rateLimitChoiceKeydown(event, group, values, value) {
      const keys = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 };
      const step = keys[event?.key];
      const at = values.indexOf(value);
      if (!step || at < 0) return;
      event.preventDefault();
      const next = values[(at + step + values.length) % values.length];
      if (group === 'who') this.setRateLimitNewRuleWho(next);
      else this.setRateLimitNewRuleWhere(next);
      // Selection follows focus in a radiogroup, so focus has to follow it back.
      this.$nextTick(() => {
        const el = document.getElementById('rl-' + group + '-' + next);
        if (el && el.focus) el.focus();
      });
    },

    /**
     * The form as the rule it would create. Everything that judges or describes a new rule — the
     * error, the duplicate check, the preview, the other limits shown, Create — reads this one
     * object, so none of them can disagree about what is being created. Only fields the chosen
     * intent shows are read: text left in a hidden field has no way into the rule.
     */
    rlNewRuleBuild() {
      const n = this.rlNewRule;
      const scope = this.rlNewRuleScope();
      const intent = this.rlIntentFor(scope) || { who: '', where: '' };
      const hasSubject = intent.who === 'key' || intent.who === 'tenant';
      const hasModel = intent.where === 'one';
      const key = this.rlNewRuleKey();
      const subjectText = hasSubject ? String(n.subject || '').trim() : '';
      const canon = hasModel ? this.rlCanonicalModel(n.model) : null;
      const parts = [];
      if (hasSubject) parts.push(key ? String(key.id) : subjectText);
      if (hasModel) parts.push(canon.id);
      const tier = this.rlTierPayload(n);
      const rule = { scope, target: parts.length ? parts.join('|') : '*', ...tier, enabled: true, schedule: [] };

      let error = '';
      // Which control the message belongs under; '' for one that is about the rule as a whole.
      let errorField = '';
      if (hasSubject && parts[0].includes('|')) {
        error = 'A tenant id or slug cannot contain “|”: it is what separates the tenant from the model in a stored target.';
        errorField = 'subject';
      } else if (hasSubject && !subjectText) {
        error = intent.who === 'key' ? 'Choose the API key this limit applies to.' : 'Name the tenant: its id or its slug.';
        errorField = 'subject';
      } else if (intent.who === 'key' && this.rlNewRuleKeyError()) {
        error = this.rlNewRuleKeyError();
        errorField = 'subject';
      } else if (hasModel && !canon.id) {
        error = 'Choose a model, or switch to “All models”.';
        errorField = 'model';
      } else if (this.rlFindDraftRule(this.rlIdentity(rule.scope, rule.target))) {
        error = 'A rule for exactly this already exists; open it from the list instead.';
      } else if (tier.rpm === 0 && tier.maxConcurrentStreams === 0) {
        error = scope === 'global'
          ? 'Name the ceiling: set rpm above zero. There is no default that is right for every gateway.'
          : 'A rule must limit something: set rpm or streams above zero.';
        errorField = 'tier';
      } else if (this.rlTierBoundsError(tier, false)) {
        error = this.rlTierBoundsError(tier, false);
        errorField = 'tier';
      } else if (scope === 'tenant' && tier.rpm === 0 && tier.burst !== 0) {
        error = 'A tenant rule with rpm 0 keeps the plan rate; set burst to 0 as well.';
        errorField = 'tier';
      }
      return { rule, intent, hasSubject, hasModel, key, canon, error, errorField };
    },

    /** The target the form would store. */
    rlNewRuleTarget() {
      return this.rlNewRuleBuild().rule.target;
    },

    pickRateLimitSuggestion(field, value, fill) {
      const text = fill || value;
      this.rlNewRule[field] = text;
      this.rlNewRule.picked = { ...(this.rlNewRule.picked || {}), [field]: { value, text } };
      this.rlCombo = { field: '', index: -1, closed: {} };
    },

    // ---- pickers as comboboxes ----
    //
    // Focus never leaves the text field: arrows move an "active" option that the field points at
    // with aria-activedescendant, Enter picks it, Escape closes the list. The list is the same
    // bounded one rlSuggestList renders, so index i here is option i there.

    rlComboKind(field) {
      return field === 'model' ? 'models' : (this.rlNewRule.who === 'key' ? 'keys' : 'tenants');
    },

    rlComboOptionId(field, index) {
      return 'rl-opt-' + field + '-' + index;
    },

    /** Typing reopens a list Escape closed and forgets the active option, which no longer means the same row. */
    rateLimitComboInput(field) {
      const closed = { ...(this.rlCombo?.closed || {}) };
      delete closed[field];
      this.rlCombo = { field, index: -1, closed };
    },

    rateLimitComboKeydown(event, field) {
      const key = event.key;
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End', 'Enter', 'Escape'].includes(key)) return;
      const combo = this.rlCombo || { field: '', index: -1, closed: {} };
      const isClosed = !!combo.closed?.[field];
      const list = this.rlSuggestList(field, this.rlComboKind(field));
      const count = isClosed ? 0 : list.items.length;
      const active = combo.field === field ? combo.index : -1;

      if (key === 'Escape') {
        // Only while there is a list to close. With none, Escape is the drawer's, as everywhere else.
        if (!count) return;
        event.preventDefault();
        event.stopPropagation();
        this.rlCombo = { field, index: -1, closed: { ...(combo.closed || {}), [field]: true } };
        return;
      }
      if (key === 'Enter') {
        if (count && active >= 0 && active < count) {
          event.preventDefault();
          list.items[active].pick();
        }
        return;
      }
      // Home and End belong to the caret until an option is active.
      if ((key === 'Home' || key === 'End') && active < 0) return;
      let openCount = count;
      let closedMap = combo.closed || {};
      if (isClosed) {
        // An arrow reopens a list Escape closed and lands on its first (or last) option.
        if (key !== 'ArrowDown' && key !== 'ArrowUp') return;
        closedMap = { ...closedMap };
        delete closedMap[field];
        openCount = list.items.length;
      }
      if (!openCount) return;
      event.preventDefault();
      const next = this.rlComboNext(key, isClosed ? -1 : active, openCount);
      this.rlCombo = { field, index: next, closed: closedMap };
      const id = this.rlComboOptionId(field, next);
      const scroll = () => { const el = document.getElementById(id); if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest' }); };
      if (this.$nextTick) this.$nextTick(scroll); else scroll();
    },

    rlComboNext(key, active, count) {
      return key === 'ArrowDown' ? (active + 1) % count
        : key === 'ArrowUp' ? (active <= 0 ? count - 1 : active - 1)
        : key === 'Home' ? 0
        : count - 1;
    },

    onRateLimitSubjectInput() { this.rateLimitComboInput('subject'); },
    onRateLimitModelInput() { this.rateLimitComboInput('model'); },
    onRateLimitSubjectKeydown(event) { this.rateLimitComboKeydown(event, 'subject'); },
    onRateLimitModelKeydown(event) { this.rateLimitComboKeydown(event, 'model'); },

    /** Creates the rule in the draft; with `andSchedule` the rule drawer opens straight onto Add window. */
    createRateLimitRule(andSchedule) {
      const built = this.rlNewRuleBuild();
      this.rlNewRule.tried = true;
      if (built.error) return;
      this.rlDraft.rules.push(built.rule);
      this.rlNewRuleOpen = false;
      this.queueRateLimitScheduleRefresh();
      // Compared to true: as a click handler this receives the event, which is not a request for one.
      if (andSchedule === true) {
        this.openRateLimitRule(this.rlIdentity(built.rule.scope, built.rule.target));
        this.openRateLimitWindow(-1);
      }
    },

    createRateLimitRuleWithSchedule() {
      this.createRateLimitRule(true);
    },

    openRateLimitNewPlan() { this.openRateLimitTier('plan', ''); },
    openRateLimitNewWindow() { this.openRateLimitWindow(-1); },
    onRateLimitRangeChange() { this.setRateLimitRange(this.rlRangeDays); },
    onRateLimitZoneChange() { this.setRateLimitZone(this.rlZone); },
    toggleRateLimitReview() { this.rlReviewOpen = !this.rlReviewOpen; },

    applyCorsData(data) {
      if (!data) {
        this.corsOrigins = null;
        return;
      }
      const origins = data.allowedOrigins || data.AllowedOrigins || [];
      this.corsOrigins = Array.isArray(origins) ? origins.map((o) => String(o ?? '')) : [];
      this.corsFieldError = '';
      this.corsLoadError = '';
    },

    async fetchCors() {
      const data = await this.apiJson('/admin/api/cors');
      this.applyCorsData(data);
    },

    async loadCors() {
      this.corsLoadError = '';
      try {
        await this.fetchCors();
      } catch (e) {
        this.corsOrigins = null;
        if (String(e.title || '').startsWith('404') || e.message?.includes('404') || /not found/i.test(e.message || '')) {
          this.corsLoadError =
            'CORS API is not available on this gateway (rebuild/restart the server with the latest image).';
        } else if (e.title === 'Authentication failed' || e.message?.includes('401')) {
          this.corsLoadError = 'Connect with an Admin API key to load CORS settings.';
        } else {
          this.corsLoadError = e.message || 'Could not load CORS settings.';
        }
      }
    },

    addCorsOriginRow() {
      this.corsOrigins = [...(this.corsOrigins || []), ''];
    },

    removeCorsOriginRow(index) {
      this.corsOrigins = (this.corsOrigins || []).filter((_, i) => i !== index);
    },

    buildCorsPayload() {
      return {
        allowedOrigins: (this.corsOrigins || [])
          .map((o) => String(o || '').trim())
          .filter((o) => o.length > 0)
      };
    },

    async saveCors() {
      await this.runApi('settings', 'Saving CORS…', async () => {
        this.corsFieldError = '';
        try {
          const body = await this.apiJson('/admin/api/cors', {
            method: 'PUT',
            body: JSON.stringify(this.buildCorsPayload())
          });
          this.toast(body?.message || 'CORS origins saved.');
          await this.loadCors();
        } catch (e) {
          this.corsFieldError = e.message || 'Failed to save CORS origins.';
          throw e;
        }
      }, { localOnly: true });
    },

    async loadConfigStatus() {
      await this.runApi('settings', 'Loading config…', () => this.fetchConfigStatus());
    },

    confirmReloadConfig() {
      this.openConfirm({
        title: 'Reload config from disk?',
        message: 'This reloads models.json from the file on disk. Live registry changes made via the API are not overwritten, but file edits will be picked up.',
        confirmLabel: 'Reload',
        danger: false,
        onConfirm: () => this.reloadConfig()
      });
    },

    async reloadConfig() {
      await this.runApi('settings', 'Reloading…', async () => {
        const body = await this.apiJson('/admin/api/config/reload', { method: 'POST' });
        if (body?.status === 'error') {
          this.store.setGlobalError('Reload failed', body.message || 'Reload failed');
          return;
        }
        this.toast(body?.message || 'Config reloaded from disk.');
        await this.fetchConfigStatus();
        await this.fetchModels();
        await this.fetchBackends();
      });
    },

    /**
     * The tail's row budget. The wallboard renders ten rows, so asking for twenty-five and hiding
     * fifteen is work the gateway does for nothing. The push stream sizes its own frames, which is
     * why the ten-row trim is enforced in CSS as well and not only here.
     */
    get requestsFeedLimit() { return this.wallboard ? 12 : 25; },

    /** @param quiet true for the 2s poll tick, which must not flash the loading state or raise a banner. */
    async loadRequests(quiet) {
      // Sequenced for the same reason as loadSummary: the tail is replaced wholesale, so a reply
      // that arrives after a newer one would show the operator a feed that is a tick stale.
      const fetchRequests = () => this._sequenced('_requestsSeq',
        () => this.apiJson('/admin/api/requests?limit=' + this.requestsFeedLimit),
        rows => {
          if (this.requestsPaused) this._pausedFrame = rows ?? [];
          else this.requests = rows ?? [];
        });
      if (quiet) {
        if (!this._beginPoll('_requestsInFlight')) return;
        try { await fetchRequests(); }
        catch { /* a transient blip is already reported by the summary poll */ }
        finally { this._endPoll('_requestsInFlight'); }
        return;
      }
      await this.runApi('overview', 'Loading requests…', fetchRequests);
    },

    async applyUsageRange() {
      if (this.usageRangeInvalid) {
        this.toast(this.usageRangeError, 'error');
        return;
      }
      // Snapshot the range/filters now and tag this run; presets, filters and tab activation all
      // fire this without awaiting each other, so three in-flight reports could otherwise interleave
      // (rollups from "7d", events from "MTD", footer labelled with whatever the inputs held last).
      // Responses whose seq is no longer current are dropped on the floor.
      const snap = this.usageSnapshot();
      const seq = (this._usageSeq = (this._usageSeq || 0) + 1);
      const current = () => seq === this._usageSeq;
      await this.runApi('usage', 'Loading usage…', async () => {
        // Settled, not all-or-nothing: a failing forecast must not blank the tables that loaded.
        const results = await Promise.allSettled([
          this.apiJson('/admin/api/usage?' + this.usageParamsFrom(snap)).then(u => {
            if (!current()) return;
            this.usage = u;
            this.usageLoadedFrom = snap.from;
            this.usageLoadedTo = snap.to;
            this.usageRollupLimit = 100;
          }),
          this.apiJson('/admin/api/usage/events?' + this.usageParamsFrom(snap, { limit: 50 })).then(page => {
            if (!current()) return;
            this.usageEvents = page?.events ?? [];
            this.usageEventsHasMore = !!page?.hasMore;
            this.usageEventsCursor = page?.nextCursor || null;
            // "Load more" pages with the filters these rows were fetched with, not the live inputs.
            this._usageEventsSnapshot = snap;
          }),
          this.apiJson('/admin/api/usage/forecast?' + this.usageParamsFrom(snap, { days: 7 }, false)).then(f => {
            if (!current()) return;
            this.forecast = f;
          })
        ]);
        if (!current()) return;
        const failed = results.find(r => r.status === 'rejected');
        if (failed) throw failed.reason;
      });
    },

    async loadMoreUsageEvents() {
      if (!this.usageEventsCursor) return;
      const snap = this._usageEventsSnapshot || this.usageSnapshot();
      const seq = this._usageSeq;
      const cursor = this.usageEventsCursor;
      await this.runApi('usageEvents', 'Loading more events…', async () => {
        const page = await this.apiJson('/admin/api/usage/events?' + this.usageParamsFrom(snap, { limit: 50, cursor }));
        // A new report started (or another page landed) meanwhile: this page belongs to old rows.
        if (seq !== this._usageSeq || cursor !== this.usageEventsCursor) return;
        this.usageEvents = [...(this.usageEvents || []), ...(page?.events ?? [])];
        this.usageEventsHasMore = !!page?.hasMore;
        this.usageEventsCursor = page?.nextCursor || null;
      }).catch(() => {});
    },

    showMoreUsageRollups() { this.usageRollupLimit += 100; },

    async fetchBackends() {
      this.backends = (await this.apiJson('/admin/api/backends')) ?? [];
    },

    async loadBackends() {
      await this.runApi('routingBackends', 'Loading backends…', () => this.fetchBackends());
    },

    async fetchModels() {
      const list = (await this.apiJson('/admin/api/models')) ?? [];
      this.models = (list || []).map(item => ({
        ...(item.model || item),
        hasUpstreamCredential: item.hasUpstreamCredential === true,
        pricing: item.pricing || null
      }));
    },

    async loadModels() {
      await this.runApi('routingModels', 'Loading models…', () => this.fetchModels());
    },

    editModelFromBackend(modelId) {
      const m = (this.models || []).find(x => x.id === modelId);
      this.setRoutingSubTab('models');
      if (m) this.openModelDrawer(m);
      else {
        this.modelsFilter = modelId;
        this.openModelDrawer();
        this.editModel.id = modelId;
      }
    },

    modelWriteBody() {
      const aliases = (this.editModel.aliasesText || '').split(',').map(s => s.trim()).filter(Boolean);
      const model = {
        id: this.editModel.id.trim(),
        url: this.editModel.url.trim(),
        maxContextLength: Number(this.editModel.maxContextLength) || 8192,
        aliases,
        publicAccess: !!this.editModel.publicAccess,
        modelType: this.editModel.modelType || null,
        // Echoed back so editing a model does not wipe capabilities the UI does not expose.
        capabilities: this.editModel.capabilities || []
      };
      if (this.editModel._existing && this.editModel.upstreamAuth && !(this.editModel.apiKey || '').trim() && !this.editModel.clearApiKey) {
        model.upstreamAuth = this.editModel.upstreamAuth;
      }
      const apiKey = (this.editModel.apiKey || '').trim();
      const input = this.editModel.inputPricePerMillion;
      const output = this.editModel.outputPricePerMillion;
      const hasPricing = input !== '' && input !== null && input !== undefined &&
        output !== '' && output !== null && output !== undefined;
      return {
        model,
        apiKey: apiKey || null,
        clearApiKey: !!this.editModel.clearApiKey,
        pricing: hasPricing
          ? { inputPricePerMillionTokens: Number(input), outputPricePerMillionTokens: Number(output) }
          : null,
        // Only clear when the model previously had a price and both fields were emptied.
        clearPricing: !hasPricing && !!this.editModel._hadPricing
      };
    },

    resetModelForm() {
      this.editModel = {
        id: '', url: '', maxContextLength: 8192, aliasesText: '',
        apiKey: '', clearApiKey: false, hasUpstreamCredential: false,
        publicAccess: false, upstreamAuth: null, capabilities: [],
        modelType: 'text-generation',
        inputPricePerMillion: '', outputPricePerMillion: '',
        _hadPricing: false, _existing: false, _originalId: ''
      };
      this.showAdvancedModel = false;
    },

    startEditModel(m) {
      this.editModel = {
        id: m.id, url: m.url, maxContextLength: m.maxContextLength || 8192,
        aliasesText: (m.aliases || []).join(', '),
        apiKey: '', clearApiKey: false,
        hasUpstreamCredential: !!m.hasUpstreamCredential,
        publicAccess: !!m.publicAccess,
        upstreamAuth: m.upstreamAuth || null,
        capabilities: m.capabilities || [],
        // Show the type the gateway would resolve, so saving an older model records it explicitly.
        modelType: this.resolveModelType(m),
        inputPricePerMillion: m.pricing ? m.pricing.inputPricePerMillionTokens : '',
        outputPricePerMillion: m.pricing ? m.pricing.outputPricePerMillionTokens : '',
        _hadPricing: !!m.pricing,
        _existing: true,
        // The id the model is stored under. Editing the name is a rename, so the PATCH still has to
        // address the original id — sending it to the new one just 404s.
        _originalId: m.id
      };
    },

    modelPricingError() {
      const input = this.editModel.inputPricePerMillion;
      const output = this.editModel.outputPricePerMillion;
      const filled = v => v !== '' && v !== null && v !== undefined;
      if (filled(input) !== filled(output)) {
        return 'Set both input and output prices, or leave both blank to leave the model unpriced.';
      }
      if (!filled(input)) return '';
      for (const v of [input, output]) {
        const n = Number(v);
        if (!Number.isFinite(n) || n < 0) return 'Prices must be zero or greater.';
      }
      return '';
    },

    async saveModel() {
      if (this._saveModelInFlight) return;
      const write = this.modelWriteBody();
      if (!write.model.id || !write.model.url) {
        this.modelFieldError = 'Model name and upstream URL are required.';
        return;
      }
      if (/localhost|127\.0\.0\.1/.test(write.model.url)) {
        this.modelFieldError = 'Use http://host.docker.internal:<port> when the gateway runs in Docker (not localhost).';
        return;
      }
      const priceError = this.modelPricingError();
      if (priceError) {
        this.modelFieldError = priceError;
        return;
      }
      this._saveModelInFlight = true;
      try {
        await this.runApi('routingModels', 'Saving model…', async () => {
          const url = this.editModel._existing
            ? '/admin/api/models/' + encodeURIComponent(this.editModel._originalId || write.model.id)
            : '/admin/api/models';
          const body = await this.apiJson(url, {
            method: this.editModel._existing ? 'PATCH' : 'POST',
            body: JSON.stringify(write)
          });
          if (body && body.success === false) {
            this.modelFieldError = body.message || 'Could not save model.';
            return;
          }
          this.toast(body?.message || 'Model saved.');
          this.closeModelDrawer();
          this.resetModelForm();
          await this.fetchModels();
          await this.fetchBackends();
        }, { localOnly: true });
      } catch (e) {
        this.modelFieldError = e.message || 'Save failed.';
        if (e.global) this.handleCatch(e);
      } finally {
        this._saveModelInFlight = false;
      }
    },

    confirmStopModel(id) {
      this.openConfirm({
        title: 'Stop model?',
        message: '“' + id + '” stops serving: it disappears from /v1/models and requests for it are '
          + 'rejected. Its aliases, credential, pricing and grants are kept, so you can start it again.',
        confirmLabel: 'Stop',
        danger: true,
        onConfirm: () => this.setModelState(id, 'stop')
      });
    },

    async setModelState(id, action) {
      const failed = action === 'stop' ? 'Could not stop model.' : 'Could not start model.';
      try {
        await this.runApi('routingModels', action === 'stop' ? 'Stopping…' : 'Starting…', async () => {
          const body = await this.apiJson(
            '/admin/api/models/' + encodeURIComponent(id) + '/' + action,
            { method: 'POST' });
          if (body?.success === false) {
            this.toast(body.message || failed, 'error');
            return;
          }
          this.toast(body?.message || (action === 'stop' ? 'Model stopped.' : 'Model started.'));
          await this.fetchModels();
          await this.fetchBackends();
        }, { localOnly: true });
      } catch (e) {
        this.toast(e.message || failed, 'error');
      }
    },

    confirmRemoveModel(id) {
      this.openConfirm({
        title: 'Remove model?',
        message: 'Remove “' + id + '” from the registry. Clients using this model id or aliases may fail until reconfigured.',
        confirmLabel: 'Remove',
        danger: true,
        onConfirm: () => this.removeModel(id)
      });
    },

    async removeModel(id) {
      try {
        await this.runApi('routingModels', 'Removing…', async () => {
          const body = await this.apiJson('/admin/api/models/' + encodeURIComponent(id), { method: 'DELETE' });
          if (body?.success === false) {
            this.toast(body.message || 'Could not remove model.', 'error');
            return;
          }
          this.toast(body?.message || 'Model removed.');
          await this.fetchModels();
          await this.fetchBackends();
        }, { localOnly: true });
      } catch (e) {
        this.toast(e.message || 'Could not remove model.', 'error');
      }
    },

    async fetchKeys() {
      // Archived keys come down with the rest so the Archived filter needs no second round trip;
      // filteredKeys() keeps them out of every other view.
      // Several callers load this list (Keys, Usage, the rate-limit page); the one that started last
      // wins, so a slow early response cannot put back a list that a later one has replaced.
      const seq = this._keysSeq = (this._keysSeq || 0) + 1;
      const list = (await this.apiJson('/admin/api/keys?includeUsageSummary=true&includeArchived=true')) ?? [];
      if (seq !== this._keysSeq) return;
      this.keys = this.normalizeApiKeyList(list);
      const existingIds = new Set(this.keys.map(k => k.id));
      this.selectedKeyIds = this.selectedKeyIds.filter(id => existingIds.has(id));
    },

    async loadKeys() {
      await this.runApi('keys', 'Loading keys…', () => this.fetchKeys());
    },

    async createKey() {
      if (this._createKeyInFlight) return;
      this._createKeyInFlight = true;
      try {
        await this.runApi('keys', 'Creating key…', async () => {
          const body = await this.apiJson('/admin/api/keys', {
            method: 'POST',
            body: JSON.stringify({
              role: this.newKey.role,
              scopes: [],
              label: this.newKey.label || null,
              assignee: this.newKey.assignee || null,
              description: this.newKey.description || null,
              costCenter: this.newKey.costCenter || null
            })
          });
          this.createdKey = body?.secret || '';
          this.keysCreatedAck = false;
          this.toast('API key created — copy the secret now.');
          await this.fetchKeys();
        });
      } finally {
        this._createKeyInFlight = false;
      }
    },

    confirmRevoke(id) {
      this.revokeConfirmId = id;
    },

    confirmRevokeSelected() {
      const count = this.selectedActiveKeyCount();
      if (count === 0) return;
      this.openConfirm({
        title: 'Revoke selected API keys?',
        message: 'This will revoke ' + count + ' key' + (count === 1 ? '' : 's') + '. This cannot be undone.',
        confirmLabel: 'Revoke selected',
        danger: true,
        onConfirm: () => this.revokeSelectedKeys()
      });
    },

    cancelRevoke() {
      this.revokeConfirmId = null;
    },

    confirmArchive(key) {
      this.openConfirm({
        title: 'Archive this API key?',
        message: 'Archiving files ' + (key.label || key.keyPrefix) + ' out of the keys list. ' +
          'Its usage history and billing records are kept, and you can restore it at any time.',
        confirmLabel: 'Archive key',
        onConfirm: () => this.archiveKey(key.id)
      });
    },

    async archiveKey(id) {
      await this.runApi('keys', 'Archiving…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + encodeURIComponent(id) + '/archive', { method: 'POST' }, this.editModelUrl());
        this.toast('API key archived.');
        await this.fetchKeys();
      });
    },

    async unarchiveKey(id) {
      await this.runApi('keys', 'Restoring…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + encodeURIComponent(id) + '/unarchive', { method: 'POST' }, this.editModelUrl());
        this.toast('API key restored to the keys list.');
        await this.fetchKeys();
      });
    },

    /**
     * Permanent deletion gets a dialog of its own rather than the shared confirm: the operator has to
     * type the key's prefix back, which is the same confirmation the endpoint requires.
     */
    confirmDeleteKey(key) {
      this.deleteConfirmKey = key;
      this.deleteConfirmText = '';
    },

    cancelDeleteKey() {
      this.deleteConfirmKey = null;
      this.deleteConfirmText = '';
    },

    async deleteKeyConfirmed() {
      const key = this.deleteConfirmKey;
      if (!key || !this.deleteConfirmMatches) return;
      const prefix = key.keyPrefix;
      // The dialog stays up until the server has actually accepted. A key that picked up its first
      // request between the list load and this click comes back 409, and dismissing first would
      // leave an error toast over an empty page with the prefix to type again.
      await this.runApi('keys', 'Deleting…', async () => {
        await this.store.apiFetch(
          '/admin/api/keys/' + encodeURIComponent(key.id),
          { method: 'DELETE', body: JSON.stringify({ confirmKeyPrefix: prefix }) },
          this.editModelUrl());
        this.cancelDeleteKey();
        this.selectedKeyIds = this.selectedKeyIds.filter(existingId => existingId !== key.id);
        this.toast('API key deleted permanently. Its history is kept.');
        await this.fetchKeys();
      });
    },

    async revokeKeyConfirmed() {
      const id = this.revokeConfirmId;
      if (!id) return;
      this.revokeConfirmId = null;
      await this.runApi('keys', 'Revoking…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + encodeURIComponent(id) + '/revoke', { method: 'POST' }, this.editModelUrl());
        this.selectedKeyIds = this.selectedKeyIds.filter(existingId => existingId !== id);
        this.toast('API key revoked.');
        await this.fetchKeys();
      });
    },

    async revokeSelectedKeys() {
      const keyIds = this.selectedActiveKeyIds();
      if (keyIds.length === 0) return;
      await this.runApi('keys', 'Revoking selected…', async () => {
        const body = await this.apiJson('/admin/api/keys/revoke', {
          method: 'POST',
          body: JSON.stringify({ keyIds })
        });
        const revokedCount = Number(body?.revokedCount ?? 0);
        this.selectedKeyIds = this.selectedKeyIds.filter(id => !keyIds.includes(id));
        this.toast('Revoked ' + revokedCount + ' API key' + (revokedCount === 1 ? '' : 's') + '.');
        await this.fetchKeys();
      });
    },

    /** @param dataset 'rollups' | 'events' — the same filters the page shows apply to both. */
    async downloadExport(dataset, format) {
      if (this.usageRangeInvalid) {
        this.toast(this.usageRangeError, 'error');
        return;
      }
      await this.runApi('usageExport', 'Preparing export…', async () => {
        const ext = format === 'csv' ? 'csv' : 'json';
        const res = await this.store.downloadBlob(
          '/admin/api/usage/export?' + this.usageParams({ dataset, format }),
          'usage-' + dataset + '.' + ext,
          this.editModelUrl());
        const truncated = res?.headers?.get?.('X-Export-Truncated') === 'true';
        this.toast(truncated
          ? 'Export downloaded — capped at 5,000 events; narrow the range for the rest.'
          : 'Export downloaded.', truncated ? 'error' : undefined);
      }).catch(() => {});
    },

    // =====================================================================================
    // CSP view layer
    //
    // Everything below exists because the CSP-friendly evaluator only walks property paths.
    // Nothing here holds state of its own: each member derives from the fields above, so the
    // behaviour of the console lives in one place and the markup stays declarative.
    // =====================================================================================

    icons: (window.AdminIcons && window.AdminIcons.map) || {},

    /** Backing pair for one x-model binding; the CSP build writes through {get,set} objects. */
    bindPath(path) {
      const self = this;
      const parts = path.split('.');
      const last = parts.pop();
      const owner = () => parts.reduce((o, p) => (o == null ? o : o[p]), self);
      return {
        get() {
          const o = owner();
          return o == null ? '' : o[last];
        },
        set(v) {
          const o = owner();
          if (o != null) o[last] = v;
        }
      };
    },

    /** Every x-model target, shaped like the state it writes to: x-model="mdl.editModel.url". */
    get mdl() {
      const self = this;
      const b = p => this.bindPath(p);
      return {
        gateApiKey: b('gateApiKey'),
        headerApiKey: b('headerApiKey'),
        requestsErrorsOnly: b('requestsErrorsOnly'),
        requestsModelFilter: b('requestsModelFilter'),
        requestsTenantFilter: b('requestsTenantFilter'),
        requestsStatusClass: b('requestsStatusClass'),
        requestsSlowOnly: b('requestsSlowOnly'),
        usageFrom: b('usageFrom'),
        usageTo: b('usageTo'),
        usageFilterCostCenter: b('usageFilterCostCenter'),
        usageFilterApiKeyId: b('usageFilterApiKeyId'),
        usageIncludeAnonymous: {
          get: () => self.usageIncludeAnonymous,
          set: v => self.setUsageIncludeAnonymous(v)
        },
        modelsFilter: b('modelsFilter'),
        backendsFilter: b('backendsFilter'),
        keysFilter: b('keysFilter'),
        keysTextFilter: b('keysTextFilter'),
        deleteConfirmText: b('deleteConfirmText'),
        keysCreatedAck: b('keysCreatedAck'),
        logsSearch: b('logsSearch'),
        logsLevel: b('logsLevel'),
        logsAutoRefresh: b('logsAutoRefresh'),
        errorsSearch: b('errorsSearch'),
        errorsModel: b('errorsModel'),
        errorsStatus: b('errorsStatus'),
        errorsCode: b('errorsCode'),
        errorsLevel: b('errorsLevel'),
        errorsAutoRefresh: b('errorsAutoRefresh'),
        // Unchecking the restriction drops the selection, so saving cannot resurrect a stale list.
        tenantGrantRestricted: {
          get() { return self.tenantGrantRestricted; },
          set(v) {
            self.tenantGrantRestricted = v;
            if (!v) self.tenantGrantSelected = [];
          }
        },
        editModel: {
          id: b('editModel.id'),
          url: b('editModel.url'),
          modelType: b('editModel.modelType'),
          publicAccess: b('editModel.publicAccess'),
          apiKey: b('editModel.apiKey'),
          clearApiKey: b('editModel.clearApiKey'),
          inputPricePerMillion: b('editModel.inputPricePerMillion'),
          outputPricePerMillion: b('editModel.outputPricePerMillion'),
          maxContextLength: b('editModel.maxContextLength'),
          aliasesText: b('editModel.aliasesText')
        },
        rlDraft: {
          // Turning enforcement off asks first; see setRateLimitEnforcement.
          enabled: {
            get() { return self.rlDraft?.enabled; },
            set(v) { self.setRateLimitEnforcement(v); }
          },
          adaptiveEnabled: b('rlDraft.adaptiveEnabled')
        },
        rlFilterText: b('rlFilterText'),
        rlZone: b('rlZone'),
        rlRangeDays: b('rlRangeDays'),
        rlPreviewAt: b('rlPreviewAt'),
        rlRule: {
          rpm: b('rlRule.rpm'), burst: b('rlRule.burst'), maxConcurrentStreams: b('rlRule.maxConcurrentStreams'),
          enabled: b('rlRule.enabled')
        },
        rlTier: { slug: b('rlTier.slug'), rpm: b('rlTier.rpm'), burst: b('rlTier.burst'), maxConcurrentStreams: b('rlTier.maxConcurrentStreams') },
        rlWindow: {
          name: b('rlWindow.name'), rpm: b('rlWindow.rpm'), burst: b('rlWindow.burst'), maxConcurrentStreams: b('rlWindow.maxConcurrentStreams'),
          suspend: b('rlWindow.suspend'), priority: b('rlWindow.priority'),
          fromLocal: b('rlWindow.fromLocal'), untilLocal: b('rlWindow.untilLocal'),
          start: b('rlWindow.start'), end: b('rlWindow.end'), timeZone: b('rlWindow.timeZone'),
          validFromLocal: b('rlWindow.validFromLocal'), validUntilLocal: b('rlWindow.validUntilLocal')
        },
        // The three tier fields record that the operator typed over the scope's seed, so switching
        // scope afterwards keeps their numbers instead of re-seeding over them.
        rlNewRule: {
          subject: b('rlNewRule.subject'), model: b('rlNewRule.model'),
          rpm: {
            get() { return self.rlNewRule.rpm; },
            set(v) { self.setRateLimitNewRuleTier('rpm', v); }
          },
          burst: {
            get() { return self.rlNewRule.burst; },
            set(v) { self.setRateLimitNewRuleTier('burst', v); }
          },
          maxConcurrentStreams: {
            get() { return self.rlNewRule.maxConcurrentStreams; },
            set(v) { self.setRateLimitNewRuleTier('maxConcurrentStreams', v); }
          }
        },
        newKey: {
          role: b('newKey.role'),
          label: b('newKey.label'),
          assignee: b('newKey.assignee'),
          costCenter: b('newKey.costCenter'),
          description: b('newKey.description')
        },
        keyEdit: {
          label: b('keyEdit.label'),
          assignee: b('keyEdit.assignee'),
          costCenter: b('keyEdit.costCenter'),
          description: b('keyEdit.description')
        }
      };
    },

    // ---- session, shell and chrome ----

    get signedOut() { return !this.apiKey; },
    get signedIn() { return !!this.apiKey; },
    get keyPrefix() { return this.store.keyPrefix(); },
    get errorTitleText() { return this.errorTitle || 'Error'; },
    get apiKeyInputType() { return this.showApiKey ? 'text' : 'password'; },
    get showApiKeyLabel() { return this.showApiKey ? 'Hide key' : 'Show key'; },
    get showApiKeyIcon() { return this.icon(this.showApiKey ? 'eye-off' : 'eye'); },
    get modelApiKeyInputType() { return this.showModelApiKey ? 'text' : 'password'; },
    get modelApiKeyIcon() { return this.icon(this.showModelApiKey ? 'eye-off' : 'eye'); },
    get modelApiKeyToggleLabel() { return this.showModelApiKey ? 'Hide' : 'Show'; },
    get advancedModelIcon() { return this.icon(this.showAdvancedModel ? 'chevron-down' : 'chevron-right'); },
    get advancedModelLabel() { return this.showAdvancedModel ? 'Hide advanced' : 'Advanced options'; },

    toggleShowApiKey() { this.showApiKey = !this.showApiKey; },
    toggleShowModelApiKey() { this.showModelApiKey = !this.showModelApiKey; },
    // Clearing the draft on both edges means an abandoned half-typed key cannot be picked up by a
    // later Save, and reopening the panel never shows the previous attempt.
    toggleChangeKey() {
      this.showChangeKey = !this.showChangeKey;
      this.headerApiKey = '';
    },
    toggleAdvancedModel() { this.showAdvancedModel = !this.showAdvancedModel; },

    get loadingAuth() { return this.isLoading('auth'); },
    get loadingOverview() { return this.isLoading('overview'); },
    get loadingUsage() { return this.isLoading('usage'); },
    get loadingUsageEvents() { return this.isLoading('usageEvents'); },
    get loadingUsageExport() { return this.isLoading('usageExport'); },
    get usageBusy() { return this.isLoading('usage') || this.isLoading('usageExport'); },
    get usageApplyDisabled() { return this.usageBusy || this.usageRangeInvalid; },
    get loadingModels() { return this.isLoading('routingModels'); },
    get loadingBackends() { return this.isLoading('routingBackends'); },
    get loadingKeys() { return this.isLoading('keys'); },
    get loadingSettings() { return this.isLoading('settings'); },
    get loadingLogs() { return this.isLoading('logs'); },

    get toastRows() {
      return (this.toasts || []).map(t => ({
        key: t.id,
        type: t.type || 'success',
        message: t.message || '',
        icon: this.icon(t.type === 'success' ? 'check-circle' : 'alert-triangle')
      }));
    },

    get navTabs() {
      const defs = [
        ['dashboard', 'Overview', 'gauge'],
        ['usage', 'Usage & cost', 'bar-chart'],
        ['routing', 'Routing', 'git-branch'],
        ['keys', 'API keys', 'key'],
        ['logs', 'Logs', 'file-text'],
        ['errors', 'Errors', 'bug'],
        ['settings', 'Settings', 'settings']
      ];
      return defs.map(([id, label, iconName]) => ({
        key: id,
        tabId: 'tab-' + id,
        label,
        icon: this.icon(iconName),
        active: this.tab === id,
        cls: this.tab === id ? 'active' : '',
        select: () => this.setTab(id)
      }));
    },

    get themeButtons() {
      const defs = [
        ['light', 'sun', 'Light', 'Light theme'],
        ['dark', 'moon', 'Dark', 'Dark theme'],
        ['system', 'monitor', 'Match system', 'Match system theme']
      ];
      return defs.map(([mode, iconName, title, label]) => ({
        key: mode,
        icon: this.icon(iconName),
        title,
        label,
        active: this.isTheme(mode),
        cls: this.isTheme(mode) ? 'active' : '',
        select: () => this.setTheme(mode)
      }));
    },

    get routingTabs() {
      const defs = [['models', 'Models'], ['backends', 'Backends & health']];
      return defs.map(([id, label]) => ({
        key: id,
        label,
        cls: this.routingSubTab === id ? 'active' : '',
        select: () => this.setRoutingSubTab(id)
      }));
    },

    /**
     * How many staged rate-limit changes are waiting to be saved. The sticky save bar that reports
     * them lives inside the Rate limits sub-tab, so stepping over to CORS or Model access used to
     * hide every trace of a draft that was still there; the count rides on the sub-tab instead, which
     * is visible from all of them.
     */
    get rateLimitsUnsavedCount() {
      return this.rlDirtyView.count;
    },

    get settingsTabs() {
      const defs = [
        ['runtime', 'Runtime'],
        ['limits', 'Rate limits'],
        ['cors', 'CORS'],
        ['access', 'Model access'],
        ['observability', 'Observability']
      ];
      // The itemised diff costs roughly half again what the plain dirty check does, and the
      // pristine case is the common one on the four sub-tabs that only render the badge.
      const unsaved = this.rateLimitsDirty ? this.rateLimitsUnsavedCount : 0;
      return defs.map(([id, label]) => ({
        key: id,
        label,
        cls: this.settingsSubTab === id ? 'active' : '',
        // Precomputed strings: the CSP evaluator resolves property paths and nothing else.
        badge: id === 'limits' && unsaved > 0 ? String(unsaved) : '',
        badgeLabel: id === 'limits' && unsaved > 0
          ? unsaved + ' unsaved rate-limit change' + (unsaved === 1 ? '' : 's')
          : '',
        select: () => this.setSettingsSubTab(id)
      }));
    },

    get isDashboard() { return this.tab === 'dashboard'; },
    get isUsage() { return this.tab === 'usage'; },
    get isRouting() { return this.tab === 'routing'; },
    get isKeys() { return this.tab === 'keys'; },
    get isLogs() { return this.tab === 'logs'; },
    get isSettings() { return this.tab === 'settings'; },
    get isRoutingModels() { return this.routingSubTab === 'models'; },
    get isRoutingBackends() { return this.routingSubTab === 'backends'; },
    get isSettingsRuntime() { return this.settingsSubTab === 'runtime'; },
    get isSettingsLimits() { return this.settingsSubTab === 'limits'; },
    get isSettingsCors() { return this.settingsSubTab === 'cors'; },
    get isSettingsAccess() { return this.settingsSubTab === 'access'; },
    get isSettingsObservability() { return this.settingsSubTab === 'observability'; },

    // ---- live-vitals bar ----

    get topbarClass() { return this.connectionStatus === 'fail' ? 'auth-fail' : ''; },
    get isConnected() { return this.connectionStatus === 'ok'; },
    get connectionFailed() { return this.connectionStatus === 'fail'; },
    get sessionCheckFailed() { return this.connectionDegraded && this.connectionStatus === 'ok'; },
    get healthLiveKnown() { return this.healthLive !== null; },
    get healthLiveClass() { return this.healthLive ? 'is-ok' : 'is-fail'; },
    get healthLiveDotClass() { return this.healthLive ? 'live' : ''; },
    get healthLiveText() { return this.healthLive ? 'Live' : 'Live down'; },
    get healthReadyKnown() { return this.healthReady !== null; },
    get healthReadyClass() { return this.healthReady ? 'is-ok' : 'is-fail'; },
    get healthReadyText() { return this.healthReady ? 'ok' : 'no'; },
    get activeStreamsCount() { return Number(this.summary?.activeStreams ?? 0); },
    get hasActiveStreams() { return !!this.summary && this.activeStreamsCount > 0; },
    get noActiveStreams() { return this.activeStreamsCount === 0; },

    // In-flight: every inference being forwarded right now, streaming or not. Active streams is the
    // streaming subset, so a non-streaming completion in progress moves this and not that — which is
    // exactly the state the console used to render as an idle gateway.
    get activeRequestsCount() { return Number(this.summary?.activeRequests ?? 0); },
    get activeRequestsText() { return this.formatNum(this.activeRequestsCount); },
    get hasActiveRequests() { return !!this.summary && this.activeRequestsCount > 0; },
    get noActiveRequests() { return this.activeRequestsCount === 0; },
    get inFlightFootText() {
      const queued = this.queuedCount > 0 ? ' · ' + this.queuedCount + ' queued' : '';
      if (this.activeStreamsCount > 0) {
        return this.activeStreamsCount + ' streaming · ' +
          (this.activeRequestsCount - this.activeStreamsCount) + ' buffered' + queued;
      }
      return (this.activeRequestsCount === 1 ? 'inference running' : 'inferences running') + queued;
    },

    /** One chip per model with work in progress, so an operator can see *what* is running. */
    get activeModelChips() {
      const map = this.summary?.activeRequestsPerModel;
      if (!map || typeof map !== 'object') return [];
      return Object.entries(map)
        .map(([modelId, count]) => ({ modelId, count: Number(count) }))
        .filter(row => row.count > 0)
        .sort((a, b) => b.count - a.count || (a.modelId < b.modelId ? -1 : 1))
        .map(row => ({
          key: row.modelId,
          modelId: row.modelId,
          countText: '×' + this.formatNum(row.count)
        }));
    },

    get hasActiveModelChips() { return this.activeModelChips.length > 0; },
    get totalErrorsCount() { return Number(this.summary?.totalErrors ?? 0); },
    get clientDisconnectsCount() { return Number(this.summary?.clientDisconnects ?? 0); },
    get hasClientDisconnects() { return this.clientDisconnectsCount > 0; },
    get clientDisconnectsText() { return this.formatNum(this.clientDisconnectsCount); },
    get hasErrors() { return !!this.summary && this.totalErrorsCount > 0; },

    // ---- deep links ----

    /**
     * Follows a console link `{tab, params}` as the server's attention items and the Overview cards
     * emit them: sets the destination tab's filters first, then activates it, so it fetches once.
     */
    openLink(link) {
      if (!link || !TABS.includes(link.tab)) return;
      const params = link.params || {};
      const p = (name) => (params[name] == null ? '' : String(params[name]));
      switch (link.tab) {
        case 'errors':
          this.errorsModel = p('model');
          this.errorsStatus = p('status');
          this.errorsCode = p('code');
          if (p('range') && ERROR_RANGES.some(([key]) => key === p('range'))) this.errorsRange = p('range');
          this.errorsOffset = 0;
          break;
        case 'routing':
          this.routingSubTab = p('sub') === 'backends' ? 'backends' : 'models';
          if (this.routingSubTab === 'backends') this.backendsFilter = p('model');
          else this.modelsFilter = p('model');
          break;
        case 'usage':
          if (p('costCenter')) this.usageFilterCostCenter = p('costCenter');
          if (p('apiKeyId')) this.usageFilterApiKeyId = p('apiKeyId');
          break;
        case 'settings':
          this.setSettingsSubTab(p('sub') || 'runtime');
          break;
        case 'keys':
          this.keysTextFilter = p('q');
          if (p('filter') && KEY_FILTERS.includes(p('filter'))) this.keysFilter = p('filter');
          break;
        case 'logs':
          if (p('search')) this.logsSearch = p('search');
          break;
        case 'dashboard':
          if (p('window')) this.setOverviewWindow(p('window'));
          break;
        default:
          break;
      }
      this.applyTab(link.tab, this.routingSubTab, true);
    },

    openBackends() { this.openLink({ tab: 'routing', params: { sub: 'backends' } }); },
    openUsage() { this.openLink({ tab: 'usage' }); },
    openKeys() { this.openLink({ tab: 'keys' }); },
    openSettingsRuntime() { this.openLink({ tab: 'settings', params: { sub: 'runtime' } }); },

    /** "just now" / "42s ago" / "3m ago" / "2h ago" / "5d ago" for any ISO timestamp; '' when absent. */
    relativeTimeText(value) {
      if (!value) return '';
      const t = new Date(value).getTime();
      if (!Number.isFinite(t)) return '';
      const sec = Math.max(0, Math.floor((Date.now() - t) / 1000));
      if (sec < 5) return 'just now';
      if (sec < 60) return sec + 's ago';
      if (sec < 3600) return Math.floor(sec / 60) + 'm ago';
      if (sec < 86400) return Math.floor(sec / 3600) + 'h ago';
      return Math.floor(sec / 86400) + 'd ago';
    },

    // ---- attention ----

    attentionKey(item) {
      return [item.code, item.modelId || '', item.tenantId || ''].join('|');
    },

    restoreDismissedAttention() {
      try {
        const raw = sessionStorage.getItem('33pol-admin-attention-dismissed');
        const list = raw ? JSON.parse(raw) : [];
        this.attentionDismissed = Array.isArray(list) ? list.filter(x => typeof x === 'string') : [];
      } catch { this.attentionDismissed = []; }
    },

    dismissAttention(key) {
      if (!this.attentionDismissed.includes(key)) this.attentionDismissed = [...this.attentionDismissed, key];
      try { sessionStorage.setItem('33pol-admin-attention-dismissed', JSON.stringify(this.attentionDismissed)); } catch { /* storage unavailable */ }
    },

    undismissAttention() {
      this.attentionDismissed = [];
      try { sessionStorage.removeItem('33pol-admin-attention-dismissed'); } catch { /* storage unavailable */ }
    },

    toggleAttention() { this.attentionCollapsed = !this.attentionCollapsed; },

    /**
     * Server attention items minus the ones dismissed this session, already ranked by the gateway.
     * The wallboard ignores dismissals: they are a desk gesture made by someone who could see the
     * item, and an unattended board that quietly drops alerts because of a click made hours ago on
     * the same browser profile is worse than no board.
     */
    get attentionRows() {
      const items = Array.isArray(this.summary?.attention) ? this.summary.attention : [];
      const iconFor = { critical: 'x-circle', warning: 'alert-triangle', info: 'lightbulb' };
      return items
        .map(item => ({ item, key: this.attentionKey(item) }))
        .filter(({ key }) => this.wallboard || !this.attentionDismissed.includes(key))
        .map(({ item, key }) => ({
          key,
          cls: 'attention-item is-' + (item.severity || 'info'),
          icon: this.icon(iconFor[item.severity] || 'lightbulb'),
          severity: item.severity || 'info',
          title: item.title || item.code,
          detail: item.detail || '',
          sinceText: this.relativeTimeText(item.sinceUtc),
          sinceTitle: item.sinceUtc ? 'Since ' + new Date(item.sinceUtc).toLocaleString() : '',
          hasLink: !!item.link,
          open: () => this.openLink(item.link),
          dismiss: () => this.dismissAttention(key)
        }));
    },
    get hasAttention() { return this.attentionRows.length > 0; },
    get hasDismissedAttention() {
      if (this.wallboard) return false;
      return this.attentionDismissed.length > 0 && Array.isArray(this.summary?.attention) && this.summary.attention.length > 0;
    },
    /** Collapsing is a desk gesture; a board that came up folded shut would show nothing. */
    get attentionExpanded() { return !this.attentionCollapsed || this.wallboard; },
    get hasCriticalAttention() { return this.attentionRows.some(row => row.severity === 'critical'); },
    get attentionToggleText() { return this.attentionCollapsed ? 'Show' : 'Hide'; },
    get attentionBannerClass() {
      if (this.attentionRows.some(r => r.severity === 'critical')) return 'attention is-critical';
      if (this.attentionRows.some(r => r.severity === 'warning')) return 'attention is-warning';
      return 'attention is-info';
    },
    get attentionSummaryText() {
      const rows = this.attentionRows;
      const critical = rows.filter(r => r.severity === 'critical').length;
      const warning = rows.filter(r => r.severity === 'warning').length;
      const parts = [];
      if (critical) parts.push(critical + ' critical');
      if (warning) parts.push(warning + ' warning' + (warning === 1 ? '' : 's'));
      const info = rows.length - critical - warning;
      if (info) parts.push(info + ' info');
      return rows.length + (rows.length === 1 ? ' item needs' : ' items need') + ' attention' + (parts.length ? ' · ' + parts.join(' · ') : '');
    },

    // ---- backends card ----

    /** True when the gateway ships the routing-health section (older gateways only have /backends). */
    get hasBackendsSection() { return Array.isArray(this.summary?.backends); },

    get backendHealthRows() {
      const list = this.hasBackendsSection ? this.summary.backends : [];
      const circuitLabel = { closed: 'closed', half_open: 'half-open', open: 'OPEN', unknown: '—' };
      const circuitClass = { closed: 'tag', half_open: 'tag level-warning', open: 'tag level-critical', unknown: 'tag muted' };
      return list.map(b => {
        const max = Number(b.maxConcurrent ?? 0);
        const inFlight = Number(b.inFlight ?? 0);
        const queued = Number(b.queued ?? 0);
        const pct = max > 0 ? Math.min(100, Math.round((inFlight / max) * 100)) : 0;
        const rate = b.errorRate5m;
        const state = b.circuitState || 'unknown';
        const failures = Number(b.circuitFailures ?? 0);
        return {
          key: b.modelId + '|' + (b.url || ''),
          modelId: b.modelId,
          url: b.url || '',
          alias: b.alias || '',
          hasAlias: !!b.alias,
          dotClass: b.isHealthy ? 'dot-ok' : 'dot-fail',
          healthText: b.isHealthy ? 'Healthy' : 'Unhealthy',
          healthTitle: (b.isHealthy ? 'Healthy' : 'Unhealthy') +
            (b.lastTransitionUtc ? ' since ' + new Date(b.lastTransitionUtc).toLocaleString() : '') +
            (b.error ? ' · ' + b.error : ''),
          circuitText: circuitLabel[state] || state,
          circuitClass: circuitClass[state] || 'tag',
          circuitTitle: state === 'open'
            ? 'Circuit open since ' + (b.circuitOpenedAt ? new Date(b.circuitOpenedAt).toLocaleTimeString() : '?') + ' — requests are refused until a probe succeeds'
            : (failures > 0 ? failures + ' failure' + (failures === 1 ? '' : 's') + ' in the sampling window' : 'Circuit breaker ' + (circuitLabel[state] || state)),
          loadText: max > 0 ? inFlight + '/' + max + (queued > 0 ? ' · ' + queued + ' queued' : '') : (inFlight > 0 ? inFlight + ' in flight' : 'idle'),
          loadStyle: 'width:' + pct + '%',
          loadClass: 'load-fill' + (pct >= 100 ? ' is-over' : (pct >= 80 ? ' is-hot' : '')),
          hasLoadBar: max > 0,
          errorRateText: rate == null ? '—' : (Number(rate) * 100).toFixed(1) + '%',
          errorRateClass: rate == null ? 'num muted' : (rate > 0.05 ? 'num is-error' : (rate > 0.01 ? 'num is-warn' : 'num')),
          p95Text: b.latencyP95Ms5m == null || Number(b.requests5m ?? 0) === 0 ? '—' : this.formatMsShort(b.latencyP95Ms5m),
          checkedText: b.lastCheckedUtc ? this.relativeTimeText(b.lastCheckedUtc) : 'not probed',
          errorText: b.error || '',
          hasError: !!b.error && !b.isHealthy,
          open: () => this.openLink({ tab: 'routing', params: { sub: 'backends', model: b.modelId } })
        };
      });
    },
    get hasBackendHealthRows() { return this.backendHealthRows.length > 0; },
    get noBackendHealthRows() { return this.hasBackendsSection && this.backendHealthRows.length === 0; },
    get backendsHealthText() {
      const rows = this.hasBackendsSection ? this.summary.backends : [];
      const healthy = rows.filter(b => b.isHealthy).length;
      const open = rows.filter(b => b.circuitState === 'open').length;
      if (rows.length === 0) return 'no models';
      return healthy + ' of ' + rows.length + ' healthy' + (open ? ' · ' + open + ' circuit' + (open === 1 ? '' : 's') + ' open' : '');
    },
    get backendsHealthClass() {
      const rows = this.hasBackendsSection ? this.summary.backends : [];
      if (rows.length === 0) return 'status-chip';
      const healthy = rows.filter(b => b.isHealthy).length;
      if (healthy === 0) return 'status-chip fail';
      if (healthy < rows.length || rows.some(b => b.circuitState === 'open')) return 'status-chip warn';
      return 'status-chip ok';
    },
    get queuedCount() {
      const rows = this.hasBackendsSection ? this.summary.backends : [];
      return rows.reduce((sum, b) => sum + Number(b.queued ?? 0), 0);
    },

    // ---- tenants card ----

    get hasTenants() { return !!this.overviewTenants; },
    get tenantsError() { return this.overviewSectionErrors.tenants || ''; },
    get hasTenantsError() { return !!this.overviewSectionErrors.tenants && !this.overviewTenants; },
    get tenantsSummaryText() {
      const t = this.overviewTenants;
      if (!t) return '';
      return this.formatNum(t.tenantCount ?? 0) + (t.tenantCount === 1 ? ' tenant · ' : ' tenants · ')
        + this.formatNum(t.keyCount ?? 0) + ' keys' + (Number(t.revokedKeyCount ?? 0) > 0 ? ' (' + this.formatNum(t.revokedKeyCount) + ' revoked)' : '');
    },
    get topConsumerRows() {
      const list = this.overviewTenants?.topConsumersMonthToDate || [];
      const cur = this.overviewTenants?.currency || 'USD';
      return list.map(c => ({
        key: c.tenantId || 'anonymous',
        who: c.tenantSlug || (c.tenantId ? String(c.tenantId).slice(0, 8) : 'anonymous'),
        plan: c.planSlug || '',
        hasPlan: !!c.planSlug,
        requestsText: this.formatNum(c.requests ?? 0),
        recentText: Number(c.requests24h ?? 0) > 0 ? this.formatNum(c.requests24h) + ' in 24h' : '',
        tokensText: this.formatCompact(Number(c.promptTokens ?? 0) + Number(c.completionTokens ?? 0)),
        costText: this.formatCost(c.cost, cur),
        open: () => this.openLink({ tab: 'usage' })
      }));
    },
    get hasTopConsumers() { return this.topConsumerRows.length > 0; },
    _keyRows(list, when) {
      return (list || []).map(k => ({
        key: k.id || k.keyPrefix,
        label: k.label || k.keyPrefix,
        prefix: k.keyPrefix,
        tenant: k.tenantSlug || '',
        whenText: when(k),
        open: () => this.openLink({ tab: 'keys', params: { q: k.keyPrefix } })
      }));
    },
    get expiringKeyRows() {
      return this._keyRows(this.overviewTenants?.expiringKeys, k => k.expiresAt ? 'expires ' + new Date(k.expiresAt).toLocaleDateString() : '');
    },
    get hasExpiringKeys() { return this.expiringKeyRows.length > 0; },
    get idleKeyRows() {
      return this._keyRows(this.overviewTenants?.idleKeys, k => k.lastUsedAt ? 'last used ' + this.relativeTimeText(k.lastUsedAt) : 'never used');
    },
    get hasIdleKeys() { return this.idleKeyRows.length > 0; },
    get anonymousShareText() {
      const share = Number(this.overviewTenants?.anonymousRequestShare ?? 0);
      if (!(share > 0)) return '';
      return (share * 100).toFixed(share < 0.1 ? 1 : 0) + '% of this month\'s requests were anonymous (public models, no key)';
    },
    get hasAnonymousShare() { return !!this.anonymousShareText; },
    get tenantsQuiet() { return this.hasTenants && !this.hasTopConsumers && !this.hasExpiringKeys && !this.hasIdleKeys; },

    // ---- per-model table ----

    get modelPerfHasLatency() { return !this.isLifetimeStats; },
    get modelPerfTitleText() { return 'Models · ' + this.windowLabel; },
    get modelPerfRows() {
      const w = this.windowStats;
      let rows;
      if (w.perModel) {
        rows = w.perModel.map(m => ({
          modelId: m.modelId,
          requests: Number(m.requests ?? 0),
          errors: Number(m.errors ?? 0),
          errorRate: Number(m.errorRate ?? 0),
          p95Ms: m.latencyP95Ms,
          ttftP95Ms: m.ttftP95Ms,
          cost: m.pricedCost
        }));
      } else {
        const errs = Object.fromEntries(this.errorsByModelRows().map(r => [r.modelId, r.count]));
        rows = this.requestsByModelRows().map(r => ({
          modelId: r.modelId,
          requests: Number(r.count ?? 0),
          errors: Number(errs[r.modelId] ?? 0),
          errorRate: r.count > 0 ? Number(errs[r.modelId] ?? 0) / r.count : 0,
          p95Ms: null, ttftP95Ms: null, cost: null
        }));
      }
      rows.sort((a, b) => b.requests - a.requests);
      const max = Math.max(...rows.map(r => r.requests), 1);
      const cur = this.finopsCurrency;
      return rows.map(r => ({
        key: r.modelId,
        modelId: r.modelId,
        requestsText: this.formatNum(r.requests),
        shareStyle: 'width:' + Math.max(2, Math.round((r.requests / max) * 100)) + '%',
        errorRateText: (r.errorRate * 100).toFixed(1) + '%',
        errorRateTitle: this.formatNum(r.errors) + ' errors',
        errorRateClass: r.errorRate > 0.05 ? 'num is-error' : (r.errorRate > 0.01 ? 'num is-warn' : 'num'),
        p95Text: r.p95Ms == null ? '—' : this.formatMsShort(r.p95Ms),
        ttftText: r.ttftP95Ms == null ? '—' : this.formatMsShort(r.ttftP95Ms),
        costText: r.cost == null ? '—' : this.formatRequestCost(r.cost, cur),
        openErrors: () => this.openErrorsForModel(r.modelId),
        openRouting: () => this.openLink({ tab: 'routing', params: { sub: 'models', model: r.modelId } })
      }));
    },
    get hasModelPerfRows() { return this.modelPerfRows.length > 0; },
    get noModelPerfRows() { return !!this.summary && this.modelPerfRows.length === 0; },

    // ---- onboarding ----

    /** A gateway that has never routed a request gets a "send your first request" panel instead of empty cards. */
    get showOnboarding() {
      return !!this.summary && Number(this.summary.totalInferenceRequests ?? 0) === 0
        && (this.requests || []).length === 0 && !this.isLoading('overview');
    },
    get showOverviewBody() { return !!this.summary && !this.showOnboarding; },
    get onboardingModelId() {
      const fromBackends = this.summary?.backends?.[0]?.modelId;
      const fromModels = this.models?.[0]?.id;
      return fromBackends || fromModels || 'your-model-id';
    },
    get onboardingNoModels() { return this.hasBackendsSection && this.summary.backends.length === 0; },
    get onboardingCurlText() {
      const origin = (typeof location !== 'undefined' && location.origin) ? location.origin : 'http://localhost:8080';
      return "curl -s " + origin + "/v1/chat/completions \\\n"
        + "  -H 'Authorization: Bearer <inference-key>' \\\n"
        + "  -H 'Content-Type: application/json' \\\n"
        + "  -d '{\"model\":\"" + this.onboardingModelId + "\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}'";
    },
    copyOnboardingCurl() { this.copyText(this.onboardingCurlText, 'curl command copied.'); },

    // ---- control-plane strip ----

    formatBytes(n) {
      const x = Number(n);
      if (!Number.isFinite(x) || x < 0) return '—';
      if (x < 1024) return x + ' B';
      const units = ['KB', 'MB', 'GB', 'TB'];
      let v = x / 1024;
      let i = 0;
      while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
      return v.toFixed(v < 10 ? 1 : 0) + ' ' + units[i];
    },

    get hasControlPlane() { return !!this.overviewControlPlane || !!this.summary?.controlPlane; },

    /**
     * Uptime · Config · Secrets · Backup · DB · Process · Usage writer, each a chip that opens the
     * page that fixes it. Chips whose data the gateway does not provide are simply absent.
     */
    get controlPlaneChips() {
      const live = this.summary?.controlPlane;
      const slow = this.overviewControlPlane;
      const chips = [];
      chips.push({ key: 'uptime', label: 'Uptime', value: this.uptimeText, cls: 'mini-stat', title: 'Since the gateway process started', open: () => {} });

      const lastReload = live?.configLastReloadUtc ?? slow?.configLastReloadUtc;
      if (live || slow) {
        chips.push({
          key: 'config',
          label: 'Config',
          value: lastReload ? 'loaded ' + this.relativeTimeText(lastReload) : 'not loaded',
          cls: lastReload ? 'mini-stat is-clickable' : 'mini-stat warn is-clickable',
          title: (live?.modelCount ?? slow?.modelCount ?? 0) + ' models in the registry' + (lastReload ? ' · last reload ' + new Date(lastReload).toLocaleString() : ''),
          open: () => this.openSettingsRuntime()
        });
      }

      if (slow?.secrets) {
        const sec = slow.secrets;
        const bad = Number(sec.undecryptable ?? 0);
        chips.push({
          key: 'secrets',
          label: 'Secrets',
          value: !sec.hasRun ? 'unverified' : (bad > 0 ? bad + ' undecryptable' : (Number(sec.total ?? 0) + ' ok')),
          cls: bad > 0 ? 'mini-stat error is-clickable' : 'mini-stat is-clickable',
          title: bad > 0 ? 'Stored upstream credentials that no longer decrypt under Gateway:Security:KeyPepper' : 'Stored upstream credentials verified',
          open: () => this.openLink({ tab: 'routing', params: { sub: 'models' } })
        });
      }

      if (slow?.database) {
        const db = slow.database;
        if (db.configured) {
          const b = slow.lastBackup;
          chips.push({
            key: 'backup',
            label: 'Backup',
            value: !b ? 'never' : (b.succeeded ? this.relativeTimeText(b.attemptedAtUtc) : 'failed ' + this.relativeTimeText(b.attemptedAtUtc)),
            cls: !b ? 'mini-stat warn is-clickable' : (b.succeeded ? 'mini-stat is-clickable' : 'mini-stat error is-clickable'),
            title: !b ? 'No backup recorded — POST /admin/api/maintenance/backup' : (b.succeeded ? (b.path || '') + ' · ' + this.formatBytes(b.sizeBytes) + ' · integrity ' + (b.integrityCheck || '?') + (slow.backupCount ? ' · ' + slow.backupCount + ' kept' : '') : (b.error || 'Backup failed')),
            open: () => this.openSettingsRuntime()
          });
          chips.push({
            key: 'database',
            label: 'Database',
            value: this.formatBytes(db.sizeBytes),
            cls: 'mini-stat',
            title: (db.path || '') + (db.journalMode ? ' · journal ' + db.journalMode : ''),
            open: () => {}
          });
        } else {
          chips.push({ key: 'database', label: 'Database', value: 'none', cls: 'mini-stat', title: 'No SQLite file is configured; counters and keys live in memory only', open: () => {} });
        }
      }

      if (live) {
        const pending = Number(live.threadPoolPendingWorkItems ?? 0);
        chips.push({
          key: 'process',
          label: 'Process',
          value: this.formatBytes(live.workingSetBytes) + (pending > 0 ? ' · ' + pending + ' queued' : ''),
          cls: pending > 100 ? 'mini-stat warn' : 'mini-stat',
          title: 'GC heap ' + this.formatBytes(live.gcHeapBytes) + ' · committed ' + this.formatBytes(live.gcCommittedBytes)
            + ' · gen2 ' + this.formatNum(live.gen2Collections ?? 0) + ' · pause ' + Number(live.gcPauseTimePercent ?? 0).toFixed(1) + '%'
            + ' · ' + (live.threadPoolThreads ?? 0) + ' pool threads · ' + (live.processorCount ?? '?') + ' cores',
          open: () => {}
        });
      }

      const p = this.summary?.pipeline;
      if (p) {
        const depth = Number(p.usageWriterQueueDepth ?? -1);
        const dropped = Number(p.usageWriterDropped ?? 0);
        chips.push({
          key: 'writer',
          label: 'Usage writer',
          value: (depth < 0 ? '?' : this.formatNum(depth)) + '/' + this.formatNum(p.usageWriterCapacity ?? 0) + (dropped > 0 ? ' · ' + this.formatNum(dropped) + ' dropped' : ''),
          cls: dropped > 0 ? 'mini-stat error is-clickable' : (depth > 5000 ? 'mini-stat warn is-clickable' : 'mini-stat is-clickable'),
          title: 'Usage events waiting for the billing writer' + (Number(p.usageParseFailures ?? 0) > 0 ? ' · ' + this.formatNum(p.usageParseFailures) + ' unparsed usage frames' : ''),
          open: () => this.openLink({ tab: 'settings', params: { sub: 'observability' } })
        });
      }

      if (slow?.auditLastEntryUtc) {
        chips.push({ key: 'audit', label: 'Audit', value: 'last ' + this.relativeTimeText(slow.auditLastEntryUtc), cls: 'mini-stat', title: 'Most recent admin action in the audit trail', open: () => {} });
      }
      return chips;
    },

    // ---- activity card ----

    get hasActivitySection() { return !!this.overviewActivity && this.overviewActivity.available !== false; },
    get activityRows() {
      const list = this.overviewActivity?.entries || [];
      const dangerous = /(delete|revoke|clear|rollback|failed)/i;
      return list.map((e, i) => {
        const t = e.timestampUtc ? new Date(e.timestampUtc) : null;
        let details = e.details || '';
        if (details.length > 140) details = details.slice(0, 137) + '…';
        return {
          key: (e.timestampUtc || '') + '|' + e.action + '|' + i,
          clock: t ? t.toLocaleTimeString() : '—',
          time: t ? t.toLocaleString() : '',
          ago: this.relativeTimeText(e.timestampUtc),
          action: e.action,
          actionClass: dangerous.test(e.action) ? 'tag level-warning' : 'tag',
          actor: [e.tenantSlug || (e.tenantId ? String(e.tenantId).slice(0, 8) : ''), e.apiKeyLabel || (e.apiKeyId ? String(e.apiKeyId).slice(0, 8) : '')].filter(Boolean).join(' · ') || '—',
          details,
          detailsTitle: e.details || ''
        };
      });
    },
    get hasActivity() { return this.activityRows.length > 0; },
    get activityEmptyText() {
      if (!this.overviewActivity) return '';
      if (this.overviewActivity.available === false) return 'No audit trail yet — it starts with the first admin action.';
      const errors = Number(this.overviewActivity.parseErrors ?? 0);
      return this.activityRows.length ? (errors ? errors + ' unreadable audit lines were skipped.' : '') : 'No admin actions recorded yet.';
    },
    get hasActivityNote() { return !!this.activityEmptyText; },
    get activityError() { return this.overviewSectionErrors.activity || ''; },
    get hasActivityError() { return !!this.overviewSectionErrors.activity && !this.overviewActivity; },

    // ---- policy card ----

    /** True when the gateway ships the in-memory policy section on the summary. */
    get hasPolicyLive() { return !!this.summary?.policy; },
    get hasPolicySlow() { return !!this.overviewPolicy; },
    get hasPolicy() { return this.hasPolicyLive || this.hasPolicySlow; },
    get policyError() { return this.overviewSectionErrors.policy || ''; },
    get hasPolicyError() { return !!this.overviewSectionErrors.policy && !this.overviewPolicy; },

    /** Human labels for the server's rejection reasons, and the Errors-tab code each maps to. */
    _reasonMeta(key) {
      const meta = {
        rate_limit: ['Rate limit', 'rate_limit_exceeded'],
        quota: ['Token quota', 'quota_exceeded'],
        budget: ['Budget hard stop', 'quota_exceeded'],
        bulkhead: ['Concurrency (bulkhead)', 'concurrency_limit_exceeded'],
        stream_concurrency: ['Stream cap', 'concurrency_limit_exceeded'],
        grant_denied: ['Model not granted', 'insufficient_scope'],
        model_not_found: ['Unknown model', 'model_not_found'],
        backend_unhealthy: ['Backend unhealthy', 'backend_unhealthy'],
        circuit_open: ['Circuit open', 'circuit_open'],
        model_stopped: ['Model stopped', 'model_not_found']
      };
      return meta[key] || [key, ''];
    },

    _countBars(rows, labelOf, open) {
      const list = Array.isArray(rows) ? rows : [];
      const max = Math.max(...list.map(r => Number(r.count ?? 0)), 1);
      return list.map(r => ({
        key: r.key,
        modelId: labelOf ? labelOf(r.key) : r.key,
        countText: this.formatNum(r.count ?? 0),
        style: 'width:' + Math.max(2, Math.round((Number(r.count ?? 0) / max) * 100)) + '%',
        open: open ? () => open(r.key) : () => {}
      }));
    },

    get policyRejectionBars() {
      return this._countBars(
        this.summary?.policy?.rejectionsByReason1h,
        key => this._reasonMeta(key)[0],
        key => {
          const code = this._reasonMeta(key)[1];
          this.openLink({ tab: 'errors', params: code ? { code, range: '1h' } : { range: '1h' } });
        });
    },
    get hasPolicyRejectionBars() { return this.policyRejectionBars.length > 0; },
    get policyTenantBars() {
      return this._countBars(this.summary?.policy?.rejectionsByTenant1h, key => key.startsWith('anon:') ? 'anonymous ' + key.slice(5) : key);
    },
    get hasPolicyTenantBars() { return this.policyTenantBars.length > 0; },
    get policyModelBars() {
      return this._countBars(this.summary?.policy?.rejectionsByModel1h, null, id => this.openErrorsForModel(id));
    },
    get hasPolicyModelBars() { return this.policyModelBars.length > 0; },
    get unknownModelRows() {
      const rows = this.summary?.policy?.unknownModels1h?.length
        ? this.summary.policy.unknownModels1h
        : (this.overviewPolicy?.unknownModels || []);
      return this._countBars(rows, null, () => this.openLink({ tab: 'errors', params: { code: 'model_not_found', range: '24h' } }));
    },
    get hasUnknownModels() { return this.unknownModelRows.length > 0; },
    get grantDenialRows() {
      const rows = this.summary?.policy?.grantDenials1h?.length
        ? this.summary.policy.grantDenials1h
        : (this.overviewPolicy?.grantDenials || []);
      return this._countBars(rows, key => key.replace('|', ' → '), () => this.openLink({ tab: 'keys' }));
    },
    get hasGrantDenials() { return this.grantDenialRows.length > 0; },

    /** Monthly token quota consumption per tenant, worst first. */
    get quotaRows() {
      const list = this.overviewPolicy?.quotas || [];
      return list.map(q => {
        const ratio = Number(q.ratio ?? 0);
        const pct = Math.min(100, Math.round(ratio * 100));
        const who = q.tenantSlug || (String(q.partitionKey || '').startsWith('anon:') ? 'anonymous ' + String(q.partitionKey).slice(5) : String(q.partitionKey || '').slice(0, 8));
        return {
          key: q.partitionKey,
          who,
          plan: q.planSlug || '',
          hasPlan: !!q.planSlug,
          usedText: this.formatCompact(q.used ?? 0) + ' / ' + (Number(q.limit ?? 0) > 0 ? this.formatCompact(q.limit) : '∞'),
          pctText: Number(q.limit ?? 0) > 0 ? Math.round(ratio * 100) + '%' : '—',
          ratioStyle: 'width:' + pct + '%',
          ratioClass: 'load-fill' + (q.exceeded ? ' is-over' : (q.nearLimit ? ' is-hot' : '')),
          tagClass: q.exceeded ? 'tag level-critical' : (q.nearLimit ? 'tag level-warning' : 'tag'),
          title: this.formatNum(q.used ?? 0) + ' of ' + this.formatNum(q.limit ?? 0) + ' tokens in ' + (q.period || 'this month'),
          open: () => this.openLink({ tab: 'settings', params: { sub: 'limits' } })
        };
      });
    },
    get hasQuotaRows() { return this.quotaRows.length > 0; },
    get policyQuiet() {
      return this.hasPolicy && !this.hasPolicyRejectionBars && !this.hasQuotaRows && !this.hasUnknownModels && !this.hasGrantDenials;
    },
    get policyWindowText() { return 'last hour'; },
    get noPolicySection() { return !this.hasPolicy; },

    // ---- finops card ----

    get hasFinops() { return !!this.overviewFinops; },
    get finopsError() { return this.overviewSectionErrors.finops || ''; },
    get hasFinopsError() { return !!this.overviewSectionErrors.finops && !this.overviewFinops; },
    get finopsCurrency() { return this.overviewFinops?.currency || 'USD'; },

    /** Today / month-to-date / projected / average daily — the FinOps headline. */
    get finopsTiles() {
      const f = this.overviewFinops;
      if (!f) return [];
      const cur = this.finopsCurrency;
      const today = Number(f.todayCost ?? 0);
      const yesterday = Number(f.yesterdayCost ?? 0);
      let delta = '';
      if (yesterday > 0) {
        const pct = ((today - yesterday) / yesterday) * 100;
        delta = (pct >= 0 ? '+' : '') + pct.toFixed(0) + '% vs yesterday';
      } else if (today > 0) {
        delta = 'nothing yesterday';
      }
      return [
        { key: 'today', label: 'Today', value: this.formatCost(today, cur), foot: delta || this.formatNum(f.todayRequests ?? 0) + ' requests', cls: 'mini-stat' },
        { key: 'mtd', label: 'Month to date', value: this.formatCost(f.monthToDateCost, cur), foot: this.formatNum(f.monthToDateRequests ?? 0) + ' requests', cls: 'mini-stat' },
        { key: 'projected', label: 'Projected month', value: this.formatCost(f.projectedMonthlyCost, cur), foot: 'at ' + this.formatCost(f.averageDailyCost, cur) + '/day', cls: 'mini-stat' },
        { key: 'tokens', label: 'Tokens today', value: this.formatCompact(Number(f.todayPromptTokens ?? 0) + Number(f.todayCompletionTokens ?? 0)), foot: this.formatCompact(f.todayPromptTokens ?? 0) + ' in · ' + this.formatCompact(f.todayCompletionTokens ?? 0) + ' out', cls: 'mini-stat' }
      ];
    },

    _costBars(rows, open) {
      const list = Array.isArray(rows) ? rows : [];
      const max = Math.max(...list.map(r => Number(r.cost ?? 0)), 1e-9);
      return list.map(r => ({
        key: r.key,
        modelId: r.key,
        countText: this.formatCost(r.cost, this.finopsCurrency),
        style: 'width:' + Math.max(2, Math.round((Number(r.cost ?? 0) / max) * 100)) + '%',
        title: this.formatNum(r.requests ?? 0) + ' requests',
        open: open ? () => open(r.key) : () => {}
      }));
    },
    get finopsModelBars() { return this._costBars(this.overviewFinops?.topModelsMonthToDate); },
    get hasFinopsModelBars() { return this.finopsModelBars.length > 0; },
    get noFinopsSpend() { return this.hasFinops && this.finopsModelBars.length === 0; },

    get finopsCoverageText() {
      const f = this.overviewFinops;
      if (!f) return '';
      const total = Number(f.registeredModelCount ?? 0);
      const priced = Number(f.pricedModelCount ?? 0);
      if (total === 0) return 'no models registered';
      return priced + ' of ' + total + ' models priced';
    },
    get finopsCoverageClass() {
      const f = this.overviewFinops;
      if (!f || Number(f.registeredModelCount ?? 0) === 0) return 'hint';
      return Number(f.pricedModelCount ?? 0) < Number(f.registeredModelCount ?? 0) ? 'hint is-warn' : 'hint healthy';
    },
    get hasFinopsUnpriced() { return (this.overviewFinops?.unpricedModelIds || []).length > 0; },
    get finopsUnpricedText() {
      const list = this.overviewFinops?.unpricedModelIds || [];
      if (!list.length) return '';
      return (list.length === 1 ? '1 model has' : list.length + ' models have')
        + ' no rate card, so their spend is recorded as ' + this.formatCost(0, this.finopsCurrency)
        + ': ' + list.slice(0, 6).join(', ') + (list.length > 6 ? ', …' : '') + '.';
    },

    get finopsReconText() {
      const r = this.overviewFinops?.reconciliation;
      if (!r) return 'reconciliation not available';
      if (!r.enabled) return 'reconciliation disabled';
      if (!r.lastRunUtc) return 'reconciliation has not run yet';
      const when = this.relativeTimeText(r.lastRunUtc);
      if (Number(r.discrepancyCount ?? 0) === 0) return 'ledger balanced · checked ' + when;
      return this.formatNum(r.discrepancyCount) + (r.discrepancyCount === 1 ? ' discrepancy' : ' discrepancies')
        + ' · ' + this.formatCost(r.absoluteCostDrift, this.finopsCurrency) + ' drift · checked ' + when;
    },
    get finopsReconClass() {
      const r = this.overviewFinops?.reconciliation;
      if (!r || !r.enabled || !r.lastRunUtc) return 'finops-line muted';
      return Number(r.discrepancyCount ?? 0) === 0 ? 'finops-line healthy' : 'finops-line is-warn';
    },
    get finopsPipelineText() {
      const p = this.summary?.pipeline;
      if (!p) return '';
      const depth = Number(p.usageWriterQueueDepth ?? -1);
      const parts = [];
      parts.push(depth < 0 ? 'writer queue unknown' : 'writer queue ' + this.formatNum(depth) + '/' + this.formatNum(p.usageWriterCapacity ?? 0));
      if (Number(p.usageWriterDropped ?? 0) > 0) parts.push(this.formatNum(p.usageWriterDropped) + ' dropped');
      if (Number(p.usageParseFailures ?? 0) > 0) parts.push(this.formatNum(p.usageParseFailures) + ' unparsed');
      if (Number(p.estimatedUsage ?? 0) > 0) parts.push(this.formatNum(p.estimatedUsage) + ' estimated');
      if (Number(p.unsplitUsage ?? 0) > 0) parts.push(this.formatNum(p.unsplitUsage) + ' unsplit');
      return parts.join(' · ');
    },
    get finopsPipelineClass() {
      const p = this.summary?.pipeline;
      if (!p) return 'finops-line muted';
      if (Number(p.usageWriterDropped ?? 0) > 0) return 'finops-line is-error';
      if (Number(p.usageWriterQueueDepth ?? 0) > 5000 || Number(p.usageParseFailures ?? 0) > 0) return 'finops-line is-warn';
      return 'finops-line muted';
    },
    get hasFinopsPipeline() { return !!this.summary?.pipeline; },

    /** Budgets from the FinOps section, worst ratio first, as meter rows. */
    get finopsBudgetRows() {
      const list = this.overviewFinops?.budgets || [];
      return list.map(b => {
        const ratio = Number(b.ratio ?? 0);
        const pct = Math.min(100, Math.round(ratio * 100));
        const warn = Number(b.warningRatio ?? 0.8);
        let cls = 'load-fill';
        if (ratio >= 1) cls += ' is-over';
        else if (ratio >= warn) cls += ' is-hot';
        const who = b.tenantSlug || (b.tenantId ? String(b.tenantId).slice(0, 8) : '');
        return {
          key: b.budgetId || (b.tenantId + '|' + b.name),
          name: b.name,
          who,
          spendText: this.formatCost(b.spent, b.currency || this.finopsCurrency) + ' / ' + this.formatCost(b.limit, b.currency || this.finopsCurrency),
          pctText: Math.round(ratio * 100) + '%',
          ratioStyle: 'width:' + pct + '%',
          ratioClass: cls,
          breachText: b.projectedBreachDate ? 'runs out ~' + b.projectedBreachDate : '',
          hasBreach: !!b.projectedBreachDate,
          hardStop: !!b.hardStopEnabled,
          tagClass: ratio >= 1 ? 'tag level-critical' : (ratio >= warn ? 'tag level-warning' : 'tag'),
          open: () => this.openLink({ tab: 'usage' })
        };
      });
    },
    get hasFinopsBudgets() { return this.finopsBudgetRows.length > 0; },

    // ---- "At a glance" grid ----

    /**
     * The grouped-statistics grid under the control-plane strip: one card per category (costs by
     * cost center, tenants & keys, model fleet, policy pressure), each built only from sections
     * that have loaded — a gateway with no billing database simply shows fewer cards. Cost
     * centers render without a per-model split because the FinOps rollup
     * (CostBreakdownRow: key, cost, requests) does not carry one.
     */
    get glanceGroups() {
      const groups = [];
      // The CSP-friendly Alpine build throws on a bound property that is absent from the object,
      // so every stat and row carries the full shape even where a field is empty.
      const push = g => groups.push({
        rows: [], hint: '', ...g,
        stats: (g.stats || []).map(s => ({ cls: '', title: '', ...s })),
        hasStats: (g.stats || []).length > 0,
        hasRows: (g.rows || []).length > 0,
        hasHint: !!g.hint,
        hasOpen: !!g.open,
        open: g.open || (() => {})
      });

      const f = this.overviewFinops;
      if (f) {
        const cur = this.finopsCurrency;
        const centers = Array.isArray(f.topCostCentersMonthToDate) ? f.topCostCentersMonthToDate : [];
        const max = Math.max(...centers.map(r => Number(r.cost ?? 0)), 1e-9);
        push({
          key: 'costCenters',
          eyebrow: 'FinOps · month to date',
          title: 'Costs by cost center',
          openTitle: 'Open Usage & cost',
          open: () => this.openLink({ tab: 'usage' }),
          rows: centers.slice(0, 6).map(r => {
            // "(none)" is the server's bucket for spend with no cost center recorded. Usage
            // stores those rows with an EMPTY cost center, so filtering by the literal "(none)"
            // would match nothing — that row opens Usage unfiltered instead.
            const unassigned = r.key === '(none)';
            return {
              key: r.key,
              label: r.key,
              value: this.formatCost(r.cost, cur),
              sub: this.formatCompact(r.requests ?? 0) + ' req',
              style: 'width:' + Math.max(2, Math.round((Number(r.cost ?? 0) / max) * 100)) + '%',
              title: (unassigned ? 'No cost center recorded' : r.key) + ' · ' + this.formatNum(r.requests ?? 0) + ' requests this month',
              open: () => this.openLink({ tab: 'usage', params: unassigned ? {} : { costCenter: r.key } })
            };
          }),
          hint: centers.length ? '' : 'No cost-center spend this month — set a cost center on API keys to attribute spend.'
        });
      }

      const t = this.overviewTenants;
      if (t) {
        const anon = Number(t.anonymousRequestShare ?? 0);
        const stats = [
          { key: 'tenants', label: 'Tenants', value: this.formatNum(t.tenantCount ?? 0) },
          { key: 'keys', label: 'Active keys', value: this.formatNum(t.keyCount ?? 0) },
          { key: 'revoked', label: 'Revoked', value: this.formatNum(t.revokedKeyCount ?? 0), cls: Number(t.revokedKeyCount ?? 0) > 0 ? 'is-warn' : '' },
          { key: 'archived', label: 'Archived', value: this.formatNum(t.archivedKeyCount ?? 0) }
        ];
        if (anon > 0) {
          stats.push({
            key: 'anon', label: 'Anonymous', value: (anon * 100).toFixed(anon < 0.1 ? 1 : 0) + '%',
            title: 'Share of this month\'s requests made without an API key'
          });
        }
        push({
          key: 'tenants',
          eyebrow: 'Population',
          title: 'Tenants & keys',
          openTitle: 'Open API keys',
          open: () => this.openKeys(),
          stats
        });
      }

      const registered = Number(f?.registeredModelCount ?? this.summary?.controlPlane?.modelCount ?? 0);
      const backends = this.hasBackendsSection ? this.summary.backends : null;
      if (f || backends) {
        const stats = [];
        if (registered > 0 || f) {
          stats.push({ key: 'registered', label: 'Models', value: this.formatNum(registered) });
        }
        if (f) {
          const unpriced = (f.unpricedModelIds || []).length;
          stats.push({ key: 'priced', label: 'Priced', value: this.formatNum(f.pricedModelCount ?? 0) });
          stats.push({
            key: 'unpriced', label: 'Unpriced', value: this.formatNum(unpriced),
            cls: unpriced > 0 ? 'is-warn' : '',
            title: unpriced > 0 ? 'Models with no rate card — their spend is recorded as zero' : ''
          });
        }
        if (backends) {
          const healthy = backends.filter(b => b.isHealthy).length;
          const open = backends.filter(b => b.circuitState === 'open').length;
          stats.push({
            key: 'healthy', label: 'Healthy', value: healthy + '/' + backends.length,
            cls: backends.length > 0 && healthy < backends.length ? 'is-warn' : ''
          });
          stats.push({
            key: 'circuits', label: 'Circuits open', value: this.formatNum(open),
            cls: open > 0 ? 'is-error' : ''
          });
        }
        if (stats.length) {
          push({
            key: 'fleet',
            eyebrow: 'Registry',
            title: 'Model fleet',
            openTitle: 'Open Routing',
            open: () => this.openLink({ tab: 'routing' }),
            stats
          });
        }
      }

      if (this.hasPolicy) {
        const sum = rows => (Array.isArray(rows) ? rows : []).reduce((n, r) => n + Number(r.count ?? 0), 0);
        // Unknown models and grant denials mirror the Pressure card's fallback: the live 1h
        // counters when the summary carries them, else the slow policy section (unwindowed), so
        // this card can never say "nothing refused" while the detail card lists rows. Only the
        // live counts carry the "· 1h" qualifier; quotas and budgets are current-period state.
        const rejections = sum(this.summary?.policy?.rejectionsByReason1h);
        const liveUnknown = this.summary?.policy?.unknownModels1h?.length;
        const unknown = liveUnknown ? sum(this.summary.policy.unknownModels1h) : sum(this.overviewPolicy?.unknownModels);
        const liveDenials = this.summary?.policy?.grantDenials1h?.length;
        const denials = liveDenials ? sum(this.summary.policy.grantDenials1h) : sum(this.overviewPolicy?.grantDenials);
        const quotasHot = (this.overviewPolicy?.quotas || []).filter(q => q.nearLimit || q.exceeded).length;
        const budgetsHot = (this.overviewPolicy?.budgetsNearLimit || []).length;
        push({
          key: 'policy',
          eyebrow: 'Policy',
          title: 'Pressure at a glance',
          openTitle: 'Open Errors',
          open: () => this.openLink({ tab: 'errors', params: { range: '1h' } }),
          stats: [
            { key: 'rejections', label: 'Rejections · 1h', value: this.formatNum(rejections), cls: rejections > 0 ? 'is-warn' : '' },
            { key: 'unknown', label: 'Unknown models' + (liveUnknown ? ' · 1h' : ''), value: this.formatNum(unknown), cls: unknown > 0 ? 'is-warn' : '' },
            { key: 'denials', label: 'Grant denials' + (liveDenials ? ' · 1h' : ''), value: this.formatNum(denials), cls: denials > 0 ? 'is-warn' : '' },
            { key: 'quotas', label: 'Quotas near limit', value: this.formatNum(quotasHot), cls: quotasHot > 0 ? 'is-warn' : '' },
            { key: 'budgets', label: 'Budgets near limit', value: this.formatNum(budgetsHot), cls: budgetsHot > 0 ? 'is-warn' : '' }
          ],
          hint: rejections + unknown + denials + quotasHot + budgetsHot === 0 ? 'Nothing is being refused right now.' : ''
        });
      }

      return groups;
    },
    get hasGlanceGroups() { return this.glanceGroups.length > 0; },
    /** Combined here rather than in the template: the CSP Alpine build cannot evaluate `a && b`. */
    get showGlanceGrid() { return this.showOverviewBody && this.hasGlanceGroups; },

    // ---- overview ----

    get showStaleNotice() { return this.overviewStale && this.connectionStatus !== 'fail'; },
    get totalRequestsText() { return this.formatNum(this.summary?.totalInferenceRequests ?? 0); },
    get totalErrorsText() { return this.formatNum(this.totalErrorsCount); },
    get avgLatencyText() { return Number(this.summary?.averageLatencyMs ?? 0).toFixed(1); },
    get errorsVitalClass() { return this.windowStats.errors > 0 ? 'accent-error' : ''; },
    get errorRateText() { return (this.windowStats.errorRate * 100).toFixed(2) + '% error rate'; },

    // ---- trailing windows ----

    /** True when the gateway ships trailing windows; older gateways only have the lifetime counters. */
    get hasWindows() { return Array.isArray(this.summary?.windows) && this.summary.windows.length > 0; },
    get isLifetimeStats() { return !this.hasWindows; },
    get windowPickerDisabled() { return !this.hasWindows; },

    /**
     * The selected window's aggregates, or a synthesised lifetime object when the gateway has none,
     * so every tile reads from one shape. `lifetime: true` is what the labels key off.
     */
    get windowStats() {
      const s = this.summary;
      if (this.hasWindows) {
        const w = s.windows.find(x => x.window === this.overviewWindow) || s.windows[0];
        return {
          lifetime: false,
          window: w.window,
          seconds: Number(w.windowSeconds ?? this.windowSeconds),
          requests: Number(w.requests ?? 0),
          errors: Number(w.errors ?? 0),
          errorRate: Number(w.errorRate ?? 0),
          rps: Number(w.requestsPerSecond ?? 0),
          avgMs: Number(w.latencyAvgMs ?? 0),
          p50Ms: w.latencyP50Ms ?? null,
          p95Ms: w.latencyP95Ms ?? null,
          p99Ms: w.latencyP99Ms ?? null,
          ttftP50Ms: w.ttftP50Ms ?? null,
          ttftP95Ms: w.ttftP95Ms ?? null,
          ttftSamples: Number(w.ttftSamples ?? 0),
          promptTokens: Number(w.promptTokens ?? 0),
          completionTokens: Number(w.completionTokens ?? 0),
          cost: w.pricedCost ?? null,
          rejections: w.rejectionsByReason || {},
          perModel: Array.isArray(w.perModel) ? w.perModel : []
        };
      }
      const req = Number(s?.totalInferenceRequests ?? 0);
      const err = Number(s?.totalErrors ?? 0);
      return {
        lifetime: true,
        window: 'lifetime',
        seconds: Number(s?.uptimeSeconds ?? 0),
        requests: req,
        errors: err,
        errorRate: req > 0 ? err / req : 0,
        rps: this.currentThroughput(),
        avgMs: Number(s?.averageLatencyMs ?? 0),
        p50Ms: null, p95Ms: null, p99Ms: null,
        ttftP50Ms: null, ttftP95Ms: null, ttftSamples: 0,
        promptTokens: 0, completionTokens: 0, cost: null,
        rejections: {},
        perModel: null
      };
    },

    get windowSeconds() {
      const def = OVERVIEW_WINDOWS.find(([id]) => id === this.overviewWindow);
      return def ? def[2] : 300;
    },

    /** "last 5 min" or "lifetime" — the qualifier every windowed number carries. */
    get windowLabel() {
      if (this.isLifetimeStats) return 'lifetime';
      const def = OVERVIEW_WINDOWS.find(([id]) => id === this.overviewWindow);
      return 'last ' + (def ? def[1] : this.overviewWindow);
    },

    get windowButtons() {
      return OVERVIEW_WINDOWS.map(([id, label]) => {
        const active = id === this.overviewWindow;
        return {
          key: id,
          label,
          cls: active ? 'preset active' : 'preset',
          pressed: active ? 'true' : 'false',
          select: () => this.setOverviewWindow(id)
        };
      });
    },

    get requestsValueText() { return this.formatNum(this.windowStats.requests); },
    get requestsFootText() {
      const w = this.windowStats;
      if (w.lifetime) return w.rps > 0 ? this.throughputText + ' · total routed' : 'total routed';
      const rps = w.rps;
      return (rps.toFixed(rps < 10 ? 1 : 0)) + '/s · ' + this.windowLabel;
    },
    get errorsValueText() { return this.formatNum(this.windowStats.errors); },
    get errorsFootText() { return this.errorRateText + ' · ' + this.windowLabel + ' · view details'; },

    get latencyLabelText() { return this.isLifetimeStats ? 'Avg latency' : 'Latency p95'; },
    get latencyP95Text() {
      const w = this.windowStats;
      return this.formatMsParts(w.lifetime ? w.avgMs : (w.p95Ms ?? 0)).value;
    },
    get latencyUnitText() {
      const w = this.windowStats;
      return this.formatMsParts(w.lifetime ? w.avgMs : (w.p95Ms ?? 0)).unit;
    },
    get latencyFootText() {
      const w = this.windowStats;
      if (w.lifetime) return 'mean upstream round-trip';
      if (w.requests === 0) return 'no requests · ' + this.windowLabel;
      return 'p50 ' + this.formatMsShort(w.p50Ms) + ' · p99 ' + this.formatMsShort(w.p99Ms) + ' · ' + this.windowLabel;
    },

    get hasTtft() { return !this.isLifetimeStats && this.windowStats.ttftSamples > 0; },
    get noTtft() { return !this.hasTtft; },
    get ttftP95Text() { return this.hasTtft ? this.formatMsParts(this.windowStats.ttftP95Ms).value : '—'; },
    get ttftUnitText() { return this.hasTtft ? this.formatMsParts(this.windowStats.ttftP95Ms).unit : ''; },
    get ttftFootText() {
      if (this.isLifetimeStats) return 'needs a newer gateway';
      const w = this.windowStats;
      if (w.ttftSamples === 0) return 'no streams · ' + this.windowLabel;
      return 'p50 ' + this.formatMsShort(w.ttftP50Ms) + ' · ' + this.formatNum(w.ttftSamples) + ' streams · ' + this.windowLabel;
    },

    /** Where the sparklines come from — the tiles carry it as a title. */
    get sparkSourceText() { return this._seriesValues('throughput') ? 'Last 60 minutes, one point per minute' : 'Since this page was opened'; },
    get hasThroughput() { return this.windowStats.rps > 0; },
    get noThroughput() { return this.windowStats.rps === 0; },
    get throughputText() {
      const v = this.currentThroughput();
      return v.toFixed(v < 10 ? 1 : 0) + '/s';
    },
    get uptimeText() { return this.summary?.uptime ?? '—'; },
    get rateLimitedCount() { return Number(this.summary?.rateLimitRejections ?? 0); },
    get rateLimitedText() { return this.formatNum(this.rateLimitedCount); },
    get rateLimitedClass() { return this.rateLimitedCount > 0 ? 'warn' : ''; },
    get quotaBlockedCount() { return Number(this.summary?.quotaRejections ?? 0); },
    get quotaBlockedText() { return this.formatNum(this.quotaBlockedCount); },
    get quotaBlockedClass() { return this.quotaBlockedCount > 0 ? 'warn' : ''; },

    get spark() {
      const one = metric => ({
        has: this.hasSpark(metric),
        fill: this.sparkFill(metric),
        line: this.sparkLine(metric)
      });
      return {
        throughput: one('throughput'),
        errorRate: one('errorRate'),
        latency: one('latency'),
        ttft: one('ttft'),
        streams: one('streams'),
        inflight: one('inflight')
      };
    },

    /** @param open optional per-row action; the error bars use it to deep-link into the Errors tab. */
    modelBars(rows, open) {
      return rows.map(row => ({
        key: row.modelId,
        modelId: row.modelId,
        countText: this.formatNum(row.count),
        style: 'width:' + this.barWidth(row.count, rows),
        open: open ? () => open(row.modelId) : () => {}
      }));
    },

    get requestModelBars() { return this.modelBars(this.requestsByModelRows()); },
    get hasRequestModelBars() { return this.requestModelBars.length > 0; },
    get noRequestModelBars() { return this.requestModelBars.length === 0; },
    get errorModelBars() { return this.modelBars(this.errorsByModelRows(), id => this.openErrorsForModel(id)); },
    get hasErrorModelBars() { return this.errorModelBars.length > 0; },
    get noErrorModelBars() { return this.errorModelBars.length === 0; },

    get sortIcon() {
      return {
        requestsTime: this.sortIndicator('requests', 'timestampUtc'),
        requestsStatus: this.sortIndicator('requests', 'statusCode'),
        modelsId: this.sortIndicator('models', 'id'),
        backendsModel: this.sortIndicator('backends', 'modelId'),
        keysPrefix: this.sortIndicator('keys', 'keyPrefix'),
        keysLabel: this.sortIndicator('keys', 'label'),
        keysAssignee: this.sortIndicator('keys', 'assignee'),
        keysCostCenter: this.sortIndicator('keys', 'costCenter'),
        keysLastUsed: this.sortIndicator('keys', 'lastUsedAt'),
        keysCreated: this.sortIndicator('keys', 'createdAt'),
        rollupDate: this.sortIndicator('usageRollups', 'usageDate'),
        rollupModel: this.sortIndicator('usageRollups', 'modelId'),
        rollupCostCenter: this.sortIndicator('usageRollups', 'costCenter'),
        rollupPrompt: this.sortIndicator('usageRollups', 'promptTokens'),
        rollupCompletion: this.sortIndicator('usageRollups', 'completionTokens'),
        rollupCost: this.sortIndicator('usageRollups', 'totalCost'),
        rollupRequests: this.sortIndicator('usageRollups', 'requestCount')
      };
    },

    get sortBy() {
      return {
        requestsTime: () => this.sortToggle('requests', 'timestampUtc'),
        requestsStatus: () => this.sortToggle('requests', 'statusCode'),
        modelsId: () => this.sortToggle('models', 'id'),
        backendsModel: () => this.sortToggle('backends', 'modelId'),
        keysPrefix: () => this.sortToggle('keys', 'keyPrefix'),
        keysLabel: () => this.sortToggle('keys', 'label'),
        keysAssignee: () => this.sortToggle('keys', 'assignee'),
        keysCostCenter: () => this.sortToggle('keys', 'costCenter'),
        keysLastUsed: () => this.sortToggle('keys', 'lastUsedAt'),
        keysCreated: () => this.sortToggle('keys', 'createdAt'),
        rollupDate: () => this.sortToggle('usageRollups', 'usageDate'),
        rollupModel: () => this.sortToggle('usageRollups', 'modelId'),
        rollupCostCenter: () => this.sortToggle('usageRollups', 'costCenter'),
        rollupPrompt: () => this.sortToggle('usageRollups', 'promptTokens'),
        rollupCompletion: () => this.sortToggle('usageRollups', 'completionTokens'),
        rollupCost: () => this.sortToggle('usageRollups', 'totalCost'),
        rollupRequests: () => this.sortToggle('usageRollups', 'requestCount')
      };
    },

    /**
     * Cost of one request. Per-call amounts are routinely sub-cent, so this keeps enough digits to
     * tell $0.0004 from $0.004 instead of rounding both to $0.00.
     */
    formatRequestCost(value, currency) {
      const n = Number(value);
      if (value == null || !Number.isFinite(n)) return '—';
      const digits = n === 0 ? 2 : n >= 1 ? 2 : n >= 0.01 ? 4 : 6;
      try {
        return new Intl.NumberFormat(undefined, {
          style: 'currency', currency: currency || 'USD',
          minimumFractionDigits: 2, maximumFractionDigits: digits
        }).format(n);
      } catch { return n.toFixed(digits); }
    },

    /** How the row's cost cell should read, from the pricing state the gateway reports. */
    requestCostText(r) {
      const status = r?.pricingStatus;
      if (status === 'priced') return this.formatRequestCost(r.totalCost, r.currency);
      if (status === 'pending') return 'pricing…';
      if (status === 'unpriced') return 'unpriced';
      return '—';
    },

    requestTokensText(r) {
      if (r?.promptTokens == null && r?.completionTokens == null && r?.totalTokens == null) return '—';
      if (r.tokenSource === 'totalOnly') return this.formatCompact(r.totalTokens ?? 0) + ' total';
      return this.formatCompact(r.promptTokens ?? 0) + ' → ' + this.formatCompact(r.completionTokens ?? 0);
    },

    tokenSourceLabel(source) {
      if (source === 'estimated') return 'estimated (client disconnected before the usage frame)';
      if (source === 'totalOnly') return 'combined total only (no input/output split reported)';
      if (source === 'split') return 'reported by upstream';
      return '—';
    },

    pricingStatusLabel(r) {
      const status = r?.pricingStatus;
      if (status === 'priced') return 'Priced from the model\'s rate card';
      if (status === 'pending') return 'Queued for pricing — costs land after the next usage flush';
      if (status === 'unpriced') return 'No rate card for this model (or no billing store configured)';
      if (r?.isInFlight) return 'Not yet — usage arrives with the response';
      return 'No usage recorded for this request';
    },

    /**
     * Elapsed time for the feed. In-flight rows tick against the reactive clock so the timer runs
     * smoothly between frames; the server's own elapsed figure is the floor, so a clock skew between
     * browser and gateway can never make a running request look younger than it is.
     */
    requestElapsedMs(r) {
      const reported = Number(r?.durationMs);
      if (!r?.isInFlight) return Number.isFinite(reported) ? reported : null;
      const started = r.timestampUtc ? new Date(r.timestampUtc).getTime() : NaN;
      const local = Number.isFinite(started) ? this._nowTick - started : NaN;
      return Math.max(Number.isFinite(reported) ? reported : 0, Number.isFinite(local) ? local : 0);
    },

    formatDurationMs(ms) {
      if (ms == null || !Number.isFinite(ms)) return '—';
      if (ms < 1000) return Math.round(ms) + ' ms';
      if (ms < 60000) return (ms / 1000).toFixed(ms < 10000 ? 2 : 1) + ' s';
      const m = Math.floor(ms / 60000);
      const sec = Math.round((ms % 60000) / 1000);
      return m + 'm ' + String(sec).padStart(2, '0') + 's';
    },

    /** Marks a request id as seen; true for ~2s after its first appearance so the row can flash in. */
    isRecentArrival(id) {
      if (!id) return false;
      const seen = SEEN_REQUEST_IDS;
      let first = seen.get(id);
      if (first === undefined) {
        first = Date.now();
        seen.set(id, first);
      }
      return this._nowTick - first < 2000;
    },

    /**
     * Bookkeeping for the arrival highlight, run once per render because it needs the whole
     * visible set, not one row.
     *
     * Seeding: the first feed rendered is history, not arrivals — without this the entire table
     * flashes on load. Purging: only ids that have already scrolled out of the feed are forgotten.
     * Evicting a *visible* id would make its row read as new again on the very next render and
     * flash a request that has been sitting there for minutes.
     */
    trackFeedArrivals(rows) {
      const seen = SEEN_REQUEST_IDS;
      if (seen.size === 0 && rows.length > 0) {
        for (const r of rows) seen.set(r.requestId, 0);
        return;
      }
      if (seen.size > 400) {
        const visible = new Set(rows.map(r => r.requestId));
        for (const id of [...seen.keys()]) {
          if (!visible.has(id)) seen.delete(id);
        }
      }
    },

    get requestRows() {
      const rows = this.sortedRequests();
      this.trackFeedArrivals(rows);
      return rows.map(r => {
        const expanded = this.isRequestExpanded(r.requestId);
        // An in-flight row has no status yet and its duration is the elapsed time so far, restamped
        // by the gateway on every read and ticked locally in between — so the timer visibly runs.
        const inFlight = !!r.isInFlight;
        const elapsed = this.requestElapsedMs(r);
        const priced = r.pricingStatus === 'priced';
        const hasTokens = r.promptTokens != null || r.completionTokens != null || r.totalTokens != null;
        const completion = Number(r.completionTokens ?? 0);
        const tokensPerSec = !inFlight && completion > 0 && elapsed > 0 ? completion / (elapsed / 1000) : null;
        const arrived = this.isRecentArrival(r.requestId);
        const rowClass = this.requestRowClass(r) + (arrived ? ' row-enter' : '') + (this.pinnedRequestIds.includes(r.requestId) ? ' is-pinned' : '');
        const costText = this.requestCostText(r);
        const pinned = this.pinnedRequestIds.includes(r.requestId);
        const ttft = r.timeToFirstTokenMs ?? r.ttftMs;
        return {
          key: r.requestId,
          pinned,
          pinClass: pinned ? 'icon-btn is-on' : 'icon-btn',
          pinTitle: pinned ? 'Unpin' : 'Pin to the top of the feed',
          pin: () => this.togglePinRequest(r.requestId),
          ttftText: ttft != null ? this.formatDurationMs(ttft) : (inFlight ? '…' : '—'),
          ttftTitle: ttft != null ? 'Time to first token' : (r.isStreaming ? 'No first-token timing recorded' : 'Buffered response'),
          tokPerSecText: tokensPerSec != null ? tokensPerSec.toFixed(tokensPerSec < 10 ? 1 : 0) : (inFlight ? '…' : '—'),
          isSlow: !inFlight && elapsed >= this.requestsSlowThresholdMs(),
          requestId: r.requestId ?? '—',
          shortId: this.shortRequestId(r.requestId),
          time: this.formatTime(r.timestampUtc),
          clock: this.formatClock(r.timestampUtc),
          method: r.method ?? '—',
          path: r.path ?? '',
          modelId: r.modelId ?? '—',
          costCenter: r.costCenter || '—',
          costCenterClass: r.costCenter ? 'cc-tag' : 'cc-tag is-empty',
          costCenterTitle: r.costCenter ? 'Cost center ' + r.costCenter : 'No cost center on this key or tenant',
          inFlight,
          settled: !inFlight,
          statusCode: inFlight ? '···' : (r.statusCode ?? '—'),
          hasError: !!r.errorCode,
          errorText: inFlight ? 'running' : (r.errorCode ?? '—'),
          errorClass: inFlight ? 'live' : (r.errorCode ? 'error' : ''),
          tokensText: inFlight ? '…' : this.requestTokensText(r),
          tokensTitle: hasTokens
            ? this.formatNum(r.promptTokens ?? 0) + ' prompt · ' + this.formatNum(r.completionTokens ?? 0) +
              ' completion · ' + this.tokenSourceLabel(r.tokenSource)
            : (inFlight ? 'Usage arrives with the response' : 'No usage recorded'),
          costText: inFlight ? '…' : costText,
          costClass: 'cost-cell' + (priced ? ' is-priced' : r.pricingStatus === 'pending' ? ' is-pending' : ' is-muted'),
          costTitle: this.pricingStatusLabel(r),
          durationText: this.formatDurationMs(elapsed),
          rowClass,
          expanded,
          ariaExpanded: expanded ? 'true' : 'false',
          ariaLabel: 'Request ' + this.shortRequestId(r.requestId) +
            (inFlight ? ', in progress' : '') +
            (r.errorCode ? ', error ' + r.errorCode : '') +
            (r.costCenter ? ', cost center ' + r.costCenter : ''),
          tenant: r.tenantId ?? '—',
          streaming: r.isStreaming ? 'Yes' : 'No',
          statusDetail: inFlight ? 'In progress' : String(r.statusCode ?? '—'),
          // Detail panel
          promptTokensText: hasTokens ? this.formatNum(r.promptTokens ?? 0) : '—',
          completionTokensText: hasTokens ? this.formatNum(r.completionTokens ?? 0) : '—',
          totalTokensText: hasTokens ? this.formatNum(r.totalTokens ?? 0) : '—',
          tokenSourceText: hasTokens ? this.tokenSourceLabel(r.tokenSource) : '—',
          inputCostText: priced ? this.formatRequestCost(r.inputCost, r.currency) : costText,
          outputCostText: priced ? this.formatRequestCost(r.outputCost, r.currency) : costText,
          totalCostText: priced ? this.formatRequestCost(r.totalCost, r.currency) : costText,
          pricingText: this.pricingStatusLabel(r),
          throughputText: tokensPerSec != null ? tokensPerSec.toFixed(tokensPerSec < 10 ? 1 : 0) + ' tok/s' : '—',
          durationDetail: this.formatDurationMs(elapsed) + (inFlight ? ' so far' : ''),
          toggle: () => this.toggleRequestDetails(r.requestId),
          copyId: () => this.copyText(r.requestId, 'Request ID copied.')
        };
      });
    },

    /** One line of "what is in the feed right now", so the tail reads as a whole, not just rows. */
    get feedStats() {
      const rows = this.requests || [];
      const running = rows.filter(r => r.isInFlight);
      const settled = rows.filter(r => !r.isInFlight);
      const errors = settled.filter(r => Number(r.statusCode) >= 400 || r.errorCode);
      const priced = settled.filter(r => r.pricingStatus === 'priced' && r.totalCost != null);
      const pending = settled.filter(r => r.pricingStatus === 'pending');
      // Rate cards each carry their own currency, so a gateway pricing some models in USD and
      // others in EUR must not be handed a single meaningless total.
      const currencies = new Set(priced.map(r => r.currency).filter(Boolean));
      const mixedCurrency = currencies.size > 1;
      const spend = mixedCurrency ? null : priced.reduce((sum, r) => sum + Number(r.totalCost || 0), 0);
      const currency = currencies.values().next().value;
      const tokens = settled.reduce((sum, r) => sum + Number(r.totalTokens ?? ((r.promptTokens ?? 0) + (r.completionTokens ?? 0))), 0);
      const costCenters = new Set(rows.map(r => r.costCenter).filter(Boolean));
      return { rows: rows.length, running: running.length, settled: settled.length, errors: errors.length,
        priced: priced.length, pending: pending.length, spend, currency, mixedCurrency, tokens,
        costCenters: costCenters.size };
    },
    /**
     * The strip is built as a list rather than a row of individually x-shown spans: the separators
     * are CSS sibling rules, and a hidden-but-present span still counts as a sibling — which left a
     * dangling "·" in front of the strip whenever nothing was in flight.
     */
    get feedStrip() {
      const f = this.feedStats;
      const items = [];
      if (f.running > 0) {
        items.push({ key: 'running', text: this.formatNum(f.running) + ' in flight', cls: 'feed-stat is-live', live: true });
      }
      items.push({ key: 'shown', text: this.formatNum(f.rows) + ' shown', cls: 'feed-stat', live: false });
      items.push({
        key: 'errors',
        text: this.formatNum(f.errors) + (f.errors === 1 ? ' error' : ' errors'),
        cls: f.errors > 0 ? 'feed-stat is-error' : 'feed-stat',
        live: false
      });
      if (this.requestsPaused) {
        const n = this.pausedPendingCount;
        items.push({ key: 'paused', text: 'paused' + (n ? ' · ' + n + ' new' : ''), cls: 'feed-stat is-warn', live: false });
      }
      items.push({ key: 'spend', text: this.feedSpendText, cls: 'feed-stat is-cost', live: false });
      items.push({ key: 'tokens', text: this.formatCompact(f.tokens) + ' tokens', cls: 'feed-stat', live: false });
      items.push({
        key: 'cost-centers',
        text: f.costCenters === 0 ? 'no cost centers' : f.costCenters + (f.costCenters === 1 ? ' cost center' : ' cost centers'),
        cls: 'feed-stat',
        live: false
      });
      return items;
    },

    get feedSpendText() {
      const f = this.feedStats;
      if (f.priced === 0) return f.pending > 0 ? 'spend pricing…' : 'no priced spend';
      const pendingNote = f.pending > 0 ? ' (+' + f.pending + ' pricing)' : '';
      if (f.mixedCurrency) return 'mixed currencies' + pendingNote;
      return this.formatRequestCost(f.spend, f.currency) + ' spend' + pendingNote;
    },

    // ---- live badge ----

    get liveBadgeClass() {
      if (this.liveMode === 'stream') return 'live-badge is-stream';
      if (this.liveMode === 'reconnecting') return 'live-badge is-reconnecting';
      if (this.liveMode === 'polling') return 'live-badge is-polling';
      return 'live-badge';
    },
    get liveBadgeText() {
      if (this.liveMode === 'stream') return 'Streaming';
      if (this.liveMode === 'reconnecting') return 'Reconnecting';
      if (this.liveMode === 'polling') return 'Polling';
      return 'Connecting';
    },
    get liveBadgeTitle() {
      if (this.liveMode === 'stream') return 'Pushed by the gateway the moment activity changes' + (this.liveVersion != null ? ' · frame #' + this.liveVersion : '');
      if (this.liveMode === 'reconnecting') return 'Push stream dropped — polling every 2s until it is back';
      if (this.liveMode === 'polling') return 'Push stream unavailable here — refreshing every 2s';
      return 'Opening the push stream…';
    },
    get liveBadgeStreaming() { return this.liveMode === 'stream'; },
    get liveBadgeDotClass() { return this.liveMode === 'stream' ? 'live' : ''; },
    /**
     * What the figures above it are, and whether they are still moving.
     *
     * The second clause is the load-bearing one. While the key is rejected both the poll and the
     * stream are suspended, so "refreshing every 2s" sat over numbers that had stopped — the
     * clearest possible way to present stale data as healthy.
     */
    get updatedLineText() {
      const age = this.summaryAgeText();
      if (!age) return '';
      if (this.connectionFailed) return 'Updated ' + age + ' · not refreshing — the admin key was rejected';
      if (this.overviewStale) return 'Updated ' + age + ' · the last refresh failed, showing the previous result';
      if (this.liveMode === 'stream') return 'Updated ' + age + ' · streamed from the gateway as activity changes';
      return 'Updated ' + age + ' · refreshing every 2s';
    },

    get requestsSkeleton() { return this.isLoading('overview') && this.requestRows.length === 0; },
    get requestsTableVisible() { return this.requestRows.length > 0; },
    get requestsEmpty() { return !this.isLoading('overview') && this.requestRows.length === 0; },
    get requestsEmptyText() {
      return this.requestsErrorsOnly
        ? 'No error requests in the gateway buffer.'
        : 'No recent requests in the gateway buffer yet. Traffic will appear here as it flows.';
    },

    // ---- usage & cost ----

    get usageCurrency() { return this.usage?.currency || this.forecast?.currency || 'USD'; },

    usagePreset7() { return this.setUsagePreset(7); },
    usagePreset30() { return this.setUsagePreset(30); },
    usagePresetMtd() { return this.setUsagePreset('mtd'); },
    exportRollupsJson() { return this.downloadExport('rollups', 'json'); },
    exportRollupsCsv() { return this.downloadExport('rollups', 'csv'); },
    exportEventsJson() { return this.downloadExport('events', 'json'); },
    exportEventsCsv() { return this.downloadExport('events', 'csv'); },

    /** Which preset the current from/to equals, so the chip can show as selected. */
    get usagePresetActive() {
      const same = days => {
        const r = this.usagePresetRange(days);
        return r.from === this.usageFrom && r.to === this.usageTo;
      };
      return {
        d7: same(7) ? 'preset active' : 'preset',
        d30: same(30) ? 'preset active' : 'preset',
        mtd: same('mtd') ? 'preset active' : 'preset',
        d7Pressed: same(7) ? 'true' : 'false',
        d30Pressed: same(30) ? 'true' : 'false',
        mtdPressed: same('mtd') ? 'true' : 'false'
      };
    },

    get usageKeyOptions() {
      return (this.keys || []).map(k => ({
        key: k.id,
        id: k.id,
        label: (k.label || k.keyPrefix) + (k.assignee ? ' · ' + k.assignee : '') + (k.isRevoked ? ' (revoked)' : '')
      }));
    },

    /** Known cost centres for the datalist: from keys and from whatever the current report shows. */
    get usageCostCenterOptions() {
      const set = new Set();
      for (const k of this.keys || []) if (k.costCenter) set.add(String(k.costCenter).trim());
      for (const r of this.usage?.rollups || []) if (r.costCenter) set.add(String(r.costCenter).trim());
      const list = [...set].sort((a, b) => a.localeCompare(b)).map(v => ({ key: v, value: v }));
      list.push({ key: '(none)', value: '(none)' });
      return list;
    },

    get usageSelectedKeyLabel() {
      const id = this.usageFilterApiKeyId;
      if (!id) return '';
      const k = (this.keys || []).find(x => x.id === id);
      return k ? (k.label || k.keyPrefix) : id;
    },
    get usageScopedToKey() { return !!this.usageFilterApiKeyId; },
    get usageScopeNote() {
      const parts = [];
      if (this.usageScopedToKey) parts.push('key ' + this.usageSelectedKeyLabel);
      const cc = (this.usageFilterCostCenter || '').trim();
      if (cc) parts.push(cc === '(none)' ? 'no cost centre' : 'cost centre ' + cc);
      if (!this.usageIncludeAnonymous) parts.push('anonymous usage hidden');
      return parts.length ? 'Filtered: ' + parts.join(' · ') : '';
    },
    get hasUsageScopeNote() { return !!this.usageScopeNote; },

    get usageSummary() {
      const s = this.usage?.summary;
      const currency = this.usageCurrency;
      const anon = Number(s?.anonymousRequests ?? 0);
      return {
        has: !!s,
        promptCompact: this.formatCompact(s?.totalPromptTokens ?? 0),
        promptTotal: this.formatNum(s?.totalPromptTokens ?? 0) + ' total',
        completionCompact: this.formatCompact(s?.totalCompletionTokens ?? 0),
        completionTotal: this.formatNum(s?.totalCompletionTokens ?? 0) + ' total',
        costText: this.formatCost(s?.totalCost ?? 0, currency),
        costFoot: this.usageLoadedFrom && this.usageLoadedTo
          ? this.usageLoadedFrom + ' → ' + this.usageLoadedTo + ' UTC'
          : 'selected range',
        requestsText: this.formatNum(s?.totalRequests ?? 0),
        requestsFoot: anon > 0
          ? 'recorded inference calls · ' + this.formatNum(anon) + ' anonymous'
          : 'recorded inference calls'
      };
    },

    /** Month-end projection tile — deliberately separate from the range-scoped Cost tile. */
    get usageForecastTile() {
      const f = this.forecast;
      if (!f) return { has: false, value: '', foot: '', title: '' };
      const cur = f.currency || this.usageCurrency;
      const days = Number(f.daysRemainingInMonth ?? 0);
      return {
        has: true,
        value: this.formatCost(f.projectedMonthlyCost, cur),
        foot: this.formatCost(f.monthToDateCost, cur) + ' month to date + '
          + this.formatCost(f.averageDailyCost, cur) + '/day × ' + days + (days === 1 ? ' day' : ' days'),
        title: 'Average of the last ' + f.trailingDays + ' complete UTC days'
          + (f.windowStart ? ' (' + f.windowStart + ' → ' + f.windowEnd + ')' : '')
          + ', applied to the rest of the month. Same filters as the report.'
      };
    },
    get hasUsageForecast() { return !!this.forecast; },

    get usageUnpricedModels() { return this.usage?.unpricedModelIds || []; },
    get hasUsageUnpriced() { return this.usageUnpricedModels.length > 0; },
    get usageUnpricedText() {
      const list = this.usageUnpricedModels;
      if (!list.length) return '';
      return (list.length === 1 ? '1 model in this range has' : list.length + ' models in this range have')
        + ' no rate card, so their spend is recorded as ' + this.formatCost(0, this.usageCurrency)
        + ': ' + list.join(', ') + '.';
    },
    openRoutingModels() { this.setRoutingSubTab('models'); this.setTab('routing'); },

    get usageCols() {
      const series = this.usageDailySeries();
      const currency = this.usageCurrency;
      return series.map(d => ({
        key: d.date,
        title: d.date + ' · ' + this.formatCost(d.cost, currency) + ' · ' + this.formatNum(d.requests) + ' req',
        style: 'height:' + this.colHeight(d.cost),
        cls: d.cost > 0 ? 'col' : 'col empty'
      }));
    },

    get hasUsageCols() { return this.usageDailySeries().length > 0; },
    get usageAxisStart() { return this.shortDate(this.usageDailySeries()[0]?.date); },
    get usageAxisEnd() {
      const series = this.usageDailySeries();
      return this.shortDate(series[series.length - 1]?.date);
    },
    get usageAxisMid() {
      const series = this.usageDailySeries();
      return series.length > 2 ? this.shortDate(series[Math.floor(series.length / 2)]?.date) : '';
    },
    /** Three y-axis ticks (max, half, zero) so the bars have a scale. */
    get usageYTicks() {
      const max = this.usageMaxCost();
      const cur = this.usageCurrency;
      return { top: this.formatCost(max, cur), mid: this.formatCost(max / 2, cur), bottom: this.formatCost(0, cur) };
    },
    get usageChartAria() {
      const series = this.usageDailySeries();
      if (!series.length) return 'Cost per day chart, no data';
      let peak = series[0];
      for (const d of series) if (d.cost > peak.cost) peak = d;
      return 'Cost per day, ' + series.length + ' days from ' + series[0].date + ' to ' + series[series.length - 1].date
        + ' (UTC), highest ' + this.formatCost(peak.cost, this.usageCurrency) + ' on ' + peak.date;
    },

    usageRollupsAll() {
      const currency = this.usageCurrency;
      const rows = (this.usage?.rollups ?? []).map(row => ({
        key: [row.usageDate, row.tenantId ?? 'anon', row.modelId, row.costCenter ?? ''].join('\u0000'),
        usageDate: row.usageDate,
        modelId: row.modelId,
        anonymous: row.tenantId == null,
        named: row.tenantId != null,
        costCenter: row.costCenter ?? '',
        costCenterText: row.costCenter ?? '—',
        promptTokens: Number(row.promptTokens) || 0,
        completionTokens: Number(row.completionTokens) || 0,
        totalCost: Number(row.totalCost) || 0,
        requestCount: Number(row.requestCount) || 0
      }));
      return this.sortedList(rows, 'usageRollups').map(r => ({
        ...r,
        promptText: this.formatNum(r.promptTokens),
        completionText: this.formatNum(r.completionTokens),
        costText: this.formatCost(r.totalCost, currency),
        requestsText: this.formatNum(r.requestCount)
      }));
    },
    get usageRollupRows() { return this.usageRollupsAll().slice(0, this.usageRollupLimit); },
    get usageRollupTotal() { return (this.usage?.rollups ?? []).length; },
    get hasUsageRollups() { return this.usageRollupTotal > 0; },
    get usageRollupsEmpty() { return !this.isLoading('usage') && this.usageRollupTotal === 0; },
    get usageRollupsHasMore() { return this.usageRollupTotal > this.usageRollupLimit; },
    get usageRollupsCountText() {
      const total = this.usageRollupTotal;
      const shown = Math.min(total, this.usageRollupLimit);
      return shown < total
        ? 'Showing ' + this.formatNum(shown) + ' of ' + this.formatNum(total) + ' rows'
        : this.formatNum(total) + (total === 1 ? ' row' : ' rows');
    },

    /** Ledger timestamps render in UTC to match the UTC-day rollups; local time sits in the title. */
    formatUtcTime(iso) {
      if (!iso) return '—';
      try {
        const d = new Date(iso);
        return d.toLocaleString(undefined, { timeZone: 'UTC', hour12: false }) + ' UTC';
      } catch { return iso; }
    },

    get usageEventRows() {
      const currency = this.usageCurrency;
      return (this.usageEvents ?? []).map(ev => ({
        key: ev.id,
        time: this.formatUtcTime(ev.recordedAt),
        localTime: this.formatTime(ev.recordedAt) + ' (local)',
        anonymous: ev.apiKeyId == null,
        keyClass: ev.apiKeyId == null ? 'tag muted' : 'tag',
        costClass: ev.totalCost == null ? 'num muted' : 'num',
        keyPrefix: ev.apiKeyId == null ? 'anonymous' : (ev.keyPrefix ?? (ev.apiKeyId ? String(ev.apiKeyId).slice(0, 8) + '… (deleted)' : '—')),
        keyTitle: ev.apiKeyId == null ? 'No API key — public-model request' : (ev.apiKeyId || ''),
        assignee: ev.assignee ?? '—',
        modelId: ev.modelId ?? '—',
        promptTokens: this.formatNum(ev.promptTokens ?? '—'),
        completionTokens: this.formatNum(ev.completionTokens ?? '—'),
        totalCost: this.formatCost(ev.totalCost, currency),
        unpriced: ev.totalCost == null,
        costTitle: ev.totalCost == null ? 'Unpriced — no rate card for this model when the request was recorded' : ''
      }));
    },

    get hasUsageEvents() { return this.usageEventRows.length > 0; },
    get usageEventsEmpty() { return !this.isLoading('usage') && this.usageEventRows.length === 0; },
    get usageEventsCountText() {
      const n = this.usageEventRows.length;
      return 'Showing ' + this.formatNum(n) + (n === 1 ? ' event' : ' events') + (this.usageEventsHasMore ? ' — newest first, more available' : ' — newest first');
    },

    // ---- routing ----

    openNewModelDrawer() { this.openModelDrawer(); },

    get modelRows() {
      return this.filteredModelsList().map(m => {
        const testing = this.isLoading('modelTest') && this.modelTestDialog?.modelId === m.id;
        const stopped = this.isModelStopped(m);
        return {
          key: m.id,
          id: m.id,
          url: m.url,
          typeLabel: this.modelTypeLabel(this.resolveModelType(m)),
          aliases: (m.aliases || []).join(', ') || '—',
          context: this.formatNum(m.maxContextLength),
          price: this.formatModelPrice(m.pricing),
          accessClass: m.publicAccess ? 'warn' : 'ok',
          accessText: m.publicAccess ? 'Public' : 'Key required',
          hasCredential: !!m.hasUpstreamCredential,
          noCredential: !m.hasUpstreamCredential,
          isStopped: stopped,
          isServing: !stopped,
          stateClass: stopped ? 'fail' : 'ok',
          stateText: stopped ? 'Stopped' : 'Serving',
          stateTitle: stopped
            ? 'Stopped by an operator — hidden from /v1/models and requests for it are rejected.'
            : 'In service — listed in /v1/models and accepting requests.',
          stateChanging: this.isLoading('routingModels'),
          testing,
          copyId: () => this.copyText(m.id, 'ID copied.'),
          copyUrl: () => this.copyText(m.url, 'URL copied.'),
          test: () => this.testModel(m.id),
          stop: () => this.confirmStopModel(m.id),
          start: () => this.setModelState(m.id, 'start'),
          edit: () => this.openModelDrawer(m),
          remove: () => this.confirmRemoveModel(m.id)
        };
      });
    },

    /** A route with no state at all predates the field and is serving, same as the server reads it. */
    isModelStopped(m) { return String(m?.state ?? 'serving').toLowerCase() === 'stopped'; },

    get hasModelRows() { return this.modelRows.length > 0; },
    get modelsEmpty() { return !this.isLoading('routingModels') && this.modelRows.length === 0; },

    get backendRows() {
      return this.filteredBackends().map(b => {
        // A stopped route is never probed, so reporting it as "Unhealthy" would blame the backend
        // for a decision the operator made.
        const stopped = String(b.state ?? 'serving').toLowerCase() === 'stopped';
        return {
          key: b.modelId + (b.alias || ''),
          modelId: b.modelId,
          url: b.url,
          alias: b.alias ?? '—',
          healthClass: stopped ? 'dot-idle' : (b.isHealthy ? 'dot-ok' : 'dot-fail'),
          healthText: stopped ? 'Stopped' : (b.isHealthy ? 'Healthy' : 'Unhealthy'),
          healthTitle: stopped ? 'Stopped by an operator — not probed.' : '',
          edit: () => this.editModelFromBackend(b.modelId)
        };
      });
    },

    get hasBackendRows() { return this.backendRows.length > 0; },
    get backendsEmpty() { return !this.isLoading('routingBackends') && this.backendRows.length === 0; },

    // ---- API keys ----

    onSelectAllKeys(event) {
      this.toggleSelectAllFilteredKeys(!!event?.target?.checked);
    },

    get keysHeaderChecked() { return this.allFilteredActiveKeysSelected(); },
    get keysHeaderIndeterminate() {
      return this.someFilteredActiveKeysSelected() && !this.allFilteredActiveKeysSelected();
    },
    get revokeSelectedDisabled() { return this.isLoading('keys') || this.selectedActiveKeyCount() === 0; },
    get selectedKeyCountText() { return this.selectedActiveKeyCount(); },

    get keyRows() {
      return this.filteredKeys().map(k => {
        const cost = this.keyMtdCost(k);
        // Per-key currency when the summary carries one, else the report currency (usage, then
        // forecast) — not the forecast alone, which is unset until the Usage tab has been visited.
        const currency = k.usageSummary?.currency ?? this.usageCurrency;
        return {
          key: k.id,
          keyPrefix: k.keyPrefix,
          label: k.label || '—',
          assignee: k.assignee || '—',
          costCenter: k.costCenter || '—',
          costCenterClass: k.costCenter ? 'cc-tag' : 'cc-tag is-empty',
          costCenterTitle: k.costCenter ? 'Cost center ' + k.costCenter : 'No cost center on this key',
          role: k.role,
          roleClass: k.role === 'Admin' ? 'admin' : '',
          lastUsed: k.lastUsedAt ? this.formatTime(k.lastUsedAt) : '—',
          mtdCost: cost != null ? this.formatCost(cost, currency) : '—',
          mtdRequests: this.keyMtdRequests(k) ?? '—',
          created: this.formatTime(k.createdAt),
          statusClass: this.keyStatusClass(k),
          statusText: this.keyStatusText(k),
          active: !k.isRevoked && !k.isArchived,
          revoked: !!k.isRevoked,
          archived: !!k.isArchived,
          selected: this.isKeySelected(k.id),
          // Model access is only meaningful while the credential can still authenticate; a revoked or
          // archived key's grants can never be exercised again.
          canGrant: k.role !== 'Admin' && !k.isRevoked && !k.isArchived,
          canArchive: k.canArchive,
          canUnarchive: k.isArchived,
          // A key that cannot be deleted gets no button at all — a greyed-out one only invites
          // re-clicking, and the usage view is where "why not" belongs.
          canDelete: k.canDelete,
          selectLabel: 'Select key ' + k.keyPrefix,
          onSelect: event => this.toggleKeySelection(k.id, !!event?.target?.checked),
          edit: () => this.openKeyEditDrawer(k),
          access: () => this.openKeyAccess(k),
          usage: () => this.viewKeyUsage(k),
          limit: () => this.limitRateForKey(k),
          revoke: () => this.confirmRevoke(k.id),
          archive: () => this.confirmArchive(k),
          unarchive: () => this.unarchiveKey(k.id),
          remove: () => this.confirmDeleteKey(k)
        };
      });
    },

    get hasKeyRows() { return this.keyRows.length > 0; },
    get keysEmpty() { return !this.isLoading('keys') && this.keyRows.length === 0; },

    keyStatusClass(key) {
      if (key.isArchived) return 'muted';
      if (key.isRevoked) return 'fail';
      if (key.expiresAt && new Date(key.expiresAt) <= new Date()) return 'warn';
      return 'ok';
    },

    keyStatusText(key) {
      if (key.isArchived) return 'Archived';
      if (key.isRevoked) return 'Revoked';
      if (key.expiresAt && new Date(key.expiresAt) <= new Date()) return 'Expired';
      return 'Active';
    },

    // ---- permanent-delete dialog (CSP build: every binding is a getter or a zero-arg method) ----

    get deleteConfirmOpen() { return !!this.deleteConfirmKey; },
    get deleteConfirmPrefix() { return this.deleteConfirmKey?.keyPrefix || ''; },
    get deleteConfirmName() {
      const key = this.deleteConfirmKey;
      return key ? (key.label || key.keyPrefix) : '';
    },
    get deleteConfirmMatches() {
      return !!this.deleteConfirmKey &&
        this.deleteConfirmText.trim() === this.deleteConfirmKey.keyPrefix;
    },
    get deleteConfirmDisabled() {
      return this.isLoading('keys') || !this.deleteConfirmMatches;
    },

    get showCreateKeyForm() { return !this.createdKey; },
    get closeKeysDisabled() { return !!this.createdKey && !this.keysCreatedAck; },
    copyCreatedKey() { return this.copyText(this.createdKey, 'Secret copied.'); },
    get keyAccessPrefix() { return this.keyAccessEdit?.keyPrefix ?? ''; },

    grantRows(selected, toggle) {
      const chosen = selected || [];
      return (this.models || []).map(m => ({
        key: m.id,
        id: m.id,
        checked: chosen.includes(m.id),
        toggle: () => toggle(m.id)
      }));
    },

    get keyAccessRows() {
      return this.grantRows(this.keyAccessSelected, id => this.toggleKeyAccessModel(id));
    },

    get tenantGrantRows() {
      return this.grantRows(this.tenantGrantSelected, id => this.toggleTenantGrantModel(id));
    },

    // ---- logs ----

    /** The template's refresh triggers pass a DOM event; loadLogs' first argument means "quiet". */
    refreshLogs() { return this.loadLogs(); },

    get clearLogsDisabled() { return this.isLoading('logs') || !this.logs.length; },
    // Compares against the matched total, not the page size. The old check fired whenever a page
    // happened to be exactly full, warning about hidden entries that did not exist.
    get logsTruncated() { return this.logsTotal > this.logs.length; },
    get logsTruncatedText() {
      return `Showing ${this.formatNum(this.logs.length)} of ${this.formatNum(this.logsTotal)} matching entries.`;
    },
    get showLogsLoadError() { return !!this.logsLoadError; },
    get logsSkeleton() { return this.isLoading('logs') && !this.logs.length; },
    get hasLogs() { return this.logs.length > 0; },
    get logsEmpty() { return !this.isLoading('logs') && !this.logs.length; },
    get logsEmptyText() {
      return this.logsSearch || this.logsLevel !== 'all'
        ? 'No log entries match this filter.'
        : 'No warnings or errors recorded since the gateway started.';
    },
    /** The sink floors at Warning, so "all" and "warning and above" are the same set. Say so. */
    get logsLevelHint() {
      return 'The gateway mirrors warnings and above into this buffer; info-level logs go only to the configured log providers.';
    },

    get logRows() {
      return (this.logs || []).map(l => {
        const expanded = this.isLogExpanded(l.id);
        return {
          key: l.id,
          time: this.formatTime(l.lastTimestampUtc || l.timestampUtc),
          level: l.level,
          levelClass: this.logLevelClass(l.level),
          repeats: l.repeats,
          repeatsText: '×' + l.repeats,
          showRepeats: l.repeats > 1,
          category: l.category,
          message: l.message,
          modelId: l.modelId ?? '—',
          rowClass: this.logRowClass(l),
          expanded,
          ariaExpanded: expanded ? 'true' : 'false',
          ariaLabel: l.level + ' from ' + l.category + ': ' + l.message,
          hint: l.hint ?? '',
          hasHint: !!l.hint,
          firstSeen: this.formatTime(l.timestampUtc),
          eventCode: l.eventCode ?? '—',
          requestId: l.requestId ?? '—',
          detail: l.detail ?? '',
          hasDetail: !!l.detail,
          toggle: () => this.toggleLogDetails(l.id),
          copy: () => this.copyText(this.formatLogForCopy(l), 'Log entry copied.')
        };
      });
    },

    // ---- errors ----

    get isErrors() { return this.tab === 'errors'; },
    get loadingErrors() { return this.isLoading('errors'); },
    get errorsSkeleton() { return this.isLoading('errors') && !this.errorGroups.length; },
    get hasErrorGroups() { return this.errorGroups.length > 0; },
    get errorsEmpty() { return !this.isLoading('errors') && !this.errorGroups.length; },
    get showErrorsLoadError() { return !!this.errorsLoadError; },
    get clearErrorsDisabled() { return this.isLoading('errors') || !this.errorGroupsTotal; },

    get errorsFilterActive() {
      return !!(this.errorsModel || this.errorsStatus || this.errorsCode
        || this.errorsSearch || this.errorsLevel !== 'all');
    },

    get errorsEmptyText() {
      // Say which of the three causes it is. "Widen the range" is unhelpful advice when the store is
      // empty, and "nothing was captured" is wrong when 400 rows sit just outside the window — and an
      // operator staring at a non-zero topbar counter cannot tell those apart by looking.
      const stored = this.errorsStoredTotal;
      if (this.errorsFilterActive) {
        return stored > 0
          ? `No errors match these filters. ${this.formatNum(stored)} error `
            + `${stored === 1 ? 'record is' : 'records are'} stored in total — clear the filters to see them.`
          : 'No errors match these filters, and no error records are stored at all.';
      }
      if (this.errorsRange !== 'all' && stored > 0) {
        return `No errors in this time range, but ${this.formatNum(stored)} `
          + `${stored === 1 ? 'record is' : 'records are'} stored outside it. Search all time to see them.`;
      }
      if (this.errorsRange !== 'all') {
        return 'No errors recorded in this window, and none stored outside it either. '
          + this.errorsCounterNote;
      }
      // Nothing at all, across all time. If the Overview counter is non-zero the two disagree, and
      // the reason is almost always that the counter predates error recording: it is a cumulative
      // lifetime total restored across restarts, while records only exist from when this gateway
      // first ran a build that captured them. Saying so beats leaving the operator to guess.
      if (this.totalErrorsCount > 0) {
        return `No error records stored, though the Overview counter reads ${this.totalErrorsText}. `
          + this.errorsCounterNote;
      }
      return 'No errors recorded — the gateway is clean.';
    },

    /**
     * Why the Overview counter can exceed what this grid holds. Every reason here is a real,
     * by-design divergence rather than a fault, and none of them is guessable from the two numbers.
     */
    get errorsCounterNote() {
      return 'That counter is a cumulative lifetime total restored across restarts, while records '
        + 'are only kept from the point this gateway began capturing them and are pruned on the '
        + 'retention schedule. Failures in background jobs are stored here but not counted there. '
        + 'New failures appear here as they happen — use Clear all to rebase both to zero.';
    },

    /**
     * Records this page cannot show, as numbers. Empty when nothing was lost and nothing pruned,
     * so the line only appears when the operator needs to know the count is a floor.
     */
    get errorsCoverageNote() {
      const parts = [];
      if (this.errorsDegraded) {
        parts.push('The database is unreachable; showing the in-memory buffer, whose counts are lifetime totals rather than stored rows.');
      }
      if (this.errorsPersistFailedTotal > 0) {
        parts.push(`${this.formatNum(this.errorsPersistFailedTotal)} records failed to persist and are missing here.`);
      }
      if (this.errorsDroppedTotal > 0) {
        parts.push(`${this.formatNum(this.errorsDroppedTotal)} records were dropped before persistence because the write buffer was full.`);
      }
      if (this.errorsPrunedTotal > 0) {
        const since = this.errorsRetainedSince ? ` Nothing older than ${this.formatTime(this.errorsRetainedSince)} is kept.` : '';
        parts.push(`${this.formatNum(this.errorsPrunedTotal)} records have been pruned by retention.${since}`);
      }
      return parts.join(' ');
    },

    /** Only worth saying when the grid has rows — the empty state already explains itself in full. */
    get showErrorsCounterMismatch() {
      return this.hasErrorGroups && this.totalErrorsCount > this.errorsStoredTotal;
    },

    get errorsCounterMismatchText() {
      const stored = this.formatNum(this.errorsStoredTotal);
      return `The Overview counter reads ${this.totalErrorsText} against ${stored} stored here. `
        + this.errorsCounterNote;
    },

    /** Offered from the empty state, so "is it the filter or the data?" is one click to answer. */
    get showErrorsWidenHint() {
      return this.errorsEmpty && !this.errorsFilterActive && this.errorsRange !== 'all';
    },

    searchAllTime() { return this.setErrorsRange('all'); },

    get errorsSummaryText() {
      if (!this.errorGroupsTotal) return '';
      const groups = this.formatNum(this.errorGroupsTotal);
      const occurrences = this.formatNum(this.errorOccurrenceTotal);
      const range = (ERROR_RANGES.find(([key]) => key === this.errorsRange) || [, 'Last 24h'])[1];
      return `${occurrences} occurrences across ${groups} error groups · ${String(range).toLowerCase()}`;
    },

    /**
     * Errors are grouped and durable; the Logs tab is a volatile tail. Saying so on the page is
     * what stops an operator reading the two differing counts as a bug.
     */
    get errorsStorageNote() {
      return this.errorsPersisted
        ? 'Stored in the database and kept across restarts. Client disconnects are counted separately on the Overview and are not errors.'
        : 'No database configured, so these are held in memory only and will be lost on restart.';
    },

    get showErrorsPager() { return this.errorGroupsTotal > this.errorsPageSize; },
    get errorsPrevDisabled() { return this.errorsOffset <= 0 || this.isLoading('errors'); },
    get errorsNextDisabled() {
      return this.errorsOffset + this.errorsPageSize >= this.errorGroupsTotal || this.isLoading('errors');
    },
    get errorsPageText() {
      const first = this.errorGroupsTotal ? this.errorsOffset + 1 : 0;
      const last = Math.min(this.errorsOffset + this.errorsPageSize, this.errorGroupsTotal);
      return `${first}–${last} of ${this.formatNum(this.errorGroupsTotal)}`;
    },

    get errorsRangeChips() {
      return ERROR_RANGES.map(([key, label]) => ({
        key,
        label,
        cls: this.errorsRange === key ? 'active' : '',
        select: () => this.setErrorsRange(key)
      }));
    },

    get errorModelOptions() { return this.facetOptions(this.errorsFacets?.models); },
    get errorStatusOptions() { return this.facetOptions(this.errorsFacets?.statusCodes); },
    get errorCodeOptions() { return this.facetOptions(this.errorsFacets?.errorCodes); },

    /** Values come from the server's facets, so the UI can never offer a filter that matches nothing. */
    facetOptions(values) {
      return (values || []).map(f => ({
        key: f.value,
        value: f.value,
        label: f.value + ' (' + this.formatNum(f.count) + ')'
      }));
    },

    get errorRows() {
      return (this.errorGroups || []).map(g => {
        const expanded = this.isErrorExpanded(g.fingerprint);
        const occurrences = this.errorOccurrences[g.fingerprint];
        const endpoint = [g.endpointMethod, g.endpointPath].filter(Boolean).join(' ');
        return {
          key: g.fingerprint,
          lastSeen: this.formatTime(g.lastSeenUtc),
          firstSeen: this.formatTime(g.firstSeenUtc),
          level: g.level,
          levelClass: this.logLevelClass(g.level),
          countText: '×' + this.formatNum(g.count),
          message: g.message,
          exceptionType: g.exceptionType || '—',
          modelId: g.modelId || '—',
          hasModel: !!g.modelId,
          errorCode: g.errorCode || '—',
          statusText: g.statusCode ? String(g.statusCode) : '—',
          endpointText: endpoint || '—',
          upstreamTarget: g.upstreamTarget || '—',
          hasException: !!g.exceptionType,
          hasEndpoint: !!endpoint,
          hasUpstream: !!g.upstreamTarget,
          hasStatus: !!g.statusCode,
          hasErrorCode: !!g.errorCode,
          sourceText: this.errorSourceLabel(g.source),
          hasSource: !!g.source,
          category: g.category || '—',
          hasCategory: !!g.category,
          // A startup or background failure has no request behind it, so a detail panel of six
          // em-dashes is not "missing data" — it is the wrong panel. Say which it is instead.
          isRequestScoped: !!(g.modelId || endpoint || g.statusCode || g.lastRequestId),
          notRequestScoped: !(g.modelId || endpoint || g.statusCode || g.lastRequestId),
          hint: g.hint || '',
          hasHint: !!g.hint,
          rowClass: this.logRowClass(g),
          expanded,
          ariaExpanded: expanded ? 'true' : 'false',
          ariaLabel: g.level + ': ' + g.message + ', ' + g.count + ' occurrences',
          requestId: g.lastRequestId || '—',
          hasRequestId: !!g.lastRequestId,
          stackTrace: g.stackTrace || '',
          hasStackTrace: !!g.stackTrace,
          bodySnippet: g.upstreamBodySnippet || '',
          hasBodySnippet: !!g.upstreamBodySnippet,
          occurrencesLoading: expanded && !occurrences,
          hasOccurrences: !!(occurrences && occurrences.length),
          occurrences: (occurrences || []).map(o => ({
            key: o.id,
            time: this.formatTime(o.timestampUtc),
            requestId: o.requestId || '—',
            statusText: o.statusCode ? String(o.statusCode) : '—',
            durationText: o.durationMs == null ? '—' : this.formatNum(Math.round(o.durationMs)) + ' ms',
            // A stall with nothing sent is a time-to-first-token failure, not a mid-stream one;
            // the outcome name alone cannot tell the two apart, these two columns can.
            firstByteText: o.timeToFirstTokenMs != null
              ? this.formatNum(Math.round(o.timeToFirstTokenMs)) + ' ms'
              : (o.isStreaming ? 'never' : '—'),
            firstByteTitle: o.isStreaming == null
              ? ''
              : (o.timeToFirstTokenMs != null
                ? 'Time from forward start to the first response byte reaching the client'
                : (o.isStreaming ? 'No response byte ever reached the client' : 'Not measured for non-streaming responses')),
            bytesText: o.responseBytesForwarded == null ? '—' : this.formatNum(o.responseBytesForwarded) + ' B',
            bytesTitle: o.responseBytesForwarded == null ? '' : 'Response-body bytes delivered to the client before the request ended',
            tenant: o.tenantId || '—',
            source: o.source,
            // Each occurrence keeps its own stack and upstream body; the group's sample is only the
            // newest one, and the one an operator is chasing is often not the newest.
            stackTrace: o.stackTrace || '',
            hasStackTrace: !!o.stackTrace,
            bodySnippet: o.upstreamBodySnippet || '',
            hasBodySnippet: !!o.upstreamBodySnippet,
            hasDetail: !!(o.stackTrace || o.upstreamBodySnippet),
            expanded: this.expandedOccurrenceKey === o.id,
            ariaExpanded: this.expandedOccurrenceKey === o.id ? 'true' : 'false',
            chevronIcon: this.icon(this.expandedOccurrenceKey === o.id ? 'chevron-up' : 'chevron-down'),
            toggle: () => { this.expandedOccurrenceKey = this.expandedOccurrenceKey === o.id ? null : o.id; },
            copyId: () => this.copyText(o.requestId, 'Request ID copied.')
          })),
          toggle: () => this.toggleErrorDetails(g.fingerprint),
          copy: () => this.copyText(this.formatErrorForCopy(g), 'Error copied.'),
          copyRequestId: () => this.copyText(g.lastRequestId, 'Request ID copied.'),
          openRequest: () => this.openRequestFromError(g.lastRequestId),
          openLogs: () => this.openLogsFromError(g.lastRequestId),
          filterModel: () => this.openErrorsForModel(g.modelId)
        };
      });
    },

    // ---- settings ----

    get hasConfigStatus() { return !!this.configStatus; },
    get configHotReloadText() { return this.configStatus?.hotReloadEnabled ? 'on' : 'off'; },
    get configWatchText() { return this.configStatus?.watchEnabled ? 'on' : 'off'; },
    get configModelCountText() { return this.configStatus?.modelCount ?? 0; },

    get rateLimitsLoading() {
      return !this.rlDraft && !this.rateLimitsLoadError && this.isLoading('settings');
    },
    get rateLimitsLoaded() { return !!this.rlDraft; },
    get rateLimitsEditable() { return !!this.rlDraft && !this.rlReadOnlyReason; },
    get rateLimitsLocked() { return !this.rateLimitsEditable; },
    get rateLimitsReadOnlyText() { return this.rlReadOnlyReason || ''; },
    get rateLimitsDisabled() { return !!this.rlDraft && !this.rlDraft.enabled; },

    /** The "take me to it" affordance beside a refused save; hidden when nothing is openable. */
    get rlSaveErrorView() {
      const target = this.rlSaveErrorTarget();
      return {
        show: !!target,
        label: target ? target.label : '',
        open: target ? target.open : () => {}
      };
    },

    get rateLimitsDirty() {
      if (!this.rateLimits || !this.rlDraft) return false;
      return JSON.stringify(this.buildRateLimitsPayload(this.rlDraft)) !==
        JSON.stringify(this.buildRateLimitsPayload(this.rateLimits));
    },

    /**
     * The tier, rule, window and new-rule editors all work on a copy that reaches the draft only
     * when the operator presses Done, so "nothing staged" is not the same as "nothing to lose":
     * a reload with a half-filled editor open would take it with no prompt at all. Each getter
     * below answers for one editor, and every one of them compares state rather than watching for
     * keystrokes, so typing a value back to what it was leaves the page quiet.
     */
    get rateLimitsWorkInProgress() {
      return this.rateLimitsDirty || this.rlTierDirty || this.rlRuleDirty
        || this.rlWindowDirty || this.rlNewRuleDirty;
    },

    get rlTierDirty() {
      const t = this.rlTier;
      if (!this.rlTierDrawerOpen || !t) return false;
      // A plan being created exists nowhere but the drawer, so there is nothing to compare against.
      if (t.isNew) return true;
      const source = t.kind === 'default' ? this.rlDraft?.default : this.rlDraft?.plans?.[t.originalSlug];
      if (!source) return true;
      if (String(t.slug || '') !== String(t.originalSlug || '')) return true;
      return JSON.stringify(this.rlTierPayload(t)) !== JSON.stringify(this.rlTierPayload(source));
    },

    /** The drawer's copy against the draft rule it was opened from, in the shape Done would write. */
    rlRuleFormSnapshot(source) {
      return JSON.stringify([
        this.rlTierPayload(source),
        source.enabled !== false,
        source.schedule || []
      ]);
    },

    get rlRuleDirty() {
      if (!this.rlRuleDrawerOpen || !this.rlRule) return false;
      const rule = this.rlFindDraftRule(this.rlRule.identity);
      if (!rule) return false;
      return this.rlRuleFormSnapshot(this.rlRule) !== this.rlRuleFormSnapshot(rule);
    },

    /**
     * The window form has no counterpart in the draft until it is applied, so it is compared with
     * the snapshot taken when it was opened — blank template included, which makes a brand-new
     * window count as work only once something has been entered into it.
     */
    get rlWindowDirty() {
      if (!this.rlWindowOpen || !this.rlWindow) return false;
      return JSON.stringify(this.rlWindow) !== this._rlWindowBaseline;
    },

    /**
     * The form seeds its own numbers, so "touched" (already maintained so a scope change leaves the
     * operator's numbers alone) plus any choice or text that differs from what the form opened with
     * is the whole of what an operator can have invested in it.
     */
    get rlNewRuleDirty() {
      const n = this.rlNewRule;
      if (!this.rlNewRuleOpen || !n) return false;
      return !!n.touched || this.rlNewRuleSnapshot() !== n.opened;
    },

    /** What the sticky bar says has changed, so an operator can tell a stray edit from an intended one. */
    get rlDirtyView() {
      const before = this.rateLimits ? this.buildRateLimitsPayload(this.rateLimits) : null;
      const after = this.rlDraft ? this.buildRateLimitsPayload(this.rlDraft) : null;
      if (!before || !after) return { show: false, count: 0, countText: '', detail: '', items: [] };
      const items = [];
      if (before.enabled !== after.enabled) items.push(after.enabled ? 'enforcement on' : 'enforcement off');
      if (before.adaptiveEnabled !== after.adaptiveEnabled) items.push(after.adaptiveEnabled ? 'adaptive on' : 'adaptive off');
      if (JSON.stringify(before.default) !== JSON.stringify(after.default)) items.push('default tier');
      const slugs = new Set([...Object.keys(before.plans), ...Object.keys(after.plans)]);
      for (const slug of slugs) {
        if (JSON.stringify(before.plans[slug]) !== JSON.stringify(after.plans[slug])) {
          items.push((!before.plans[slug] ? 'new plan ' : !after.plans[slug] ? 'removed plan ' : 'plan ') + slug);
        }
      }
      const byId = (list) => Object.fromEntries(list.map(r => [this.rlIdentity(r.scope, r.target), r]));
      const b = byId(before.rules);
      const a = byId(after.rules);
      for (const id of new Set([...Object.keys(b), ...Object.keys(a)])) {
        if (JSON.stringify(b[id]) === JSON.stringify(a[id])) continue;
        const r = a[id] || b[id];
        const label = this.rlScopeInfo(r.scope).short + ' ' + (r.target === '*' ? '' : r.target);
        // Switching a rule off is the one edit whose effect is invisible in the numbers, so the
        // change list names it rather than reporting a bare "rule X".
        const prefix = !b[id] ? 'new rule '
          : !a[id] ? 'deleted rule '
          : b[id].enabled !== a[id].enabled ? (a[id].enabled ? 'switched on rule ' : 'switched off rule ')
          : '';
        items.push(prefix + label.trim());
      }
      const count = items.length;
      return {
        show: count > 0,
        count,
        countText: count + ' unsaved change' + (count === 1 ? '' : 's'),
        detail: items.slice(0, 4).join(' · ') + (count > 4 ? ' · …' : ''),
        items: items.map((text, i) => ({ key: i, text }))
      };
    },

    /** The schedule report row for a rule, keyed the same way the draft is. */
    rlStatusFor(scope, target) {
      const id = this.rlIdentity(scope, target);
      return (this.rlSchedule?.rules || []).find(r => this.rlIdentity(r.scope, r.target) === id) || null;
    },

    get rlStatusView() {
      const d = this.rlDraft || {};
      const rules = d.rules || [];
      const windows = rules.reduce((n, r) => n + (r.schedule || []).length, 0);
      const statuses = this.rlSchedule?.rules || [];
      const active = statuses.filter(r => r.activeWindow).length;
      const now = this.rlNow();
      const nexts = statuses
        .map(r => r.nextChangeAt ? new Date(r.nextChangeAt).getTime() : NaN)
        .filter(t => Number.isFinite(t) && t > now);
      const next = nexts.length ? new Date(Math.min(...nexts)).toISOString() : null;
      return {
        title: d.enabled === false ? 'Rate limits are not enforced' : 'Rate limits are enforced',
        titleClass: d.enabled === false ? 'off' : '',
        enabledAria: d.enabled === false ? 'false' : 'true',
        adaptiveAria: d.adaptiveEnabled ? 'true' : 'false',
        adaptiveText: d.adaptiveEnabled ? 'Adaptive load shedding on' : 'Adaptive load shedding off',
        // Ordinary rules only: the two protective budgets are counted in their own section.
        rules: this.formatNum(this.rlListRules().length),
        windows: this.formatNum(windows),
        active: this.formatNum(active),
        activeClass: active > 0 ? 'live' : '',
        next: next ? this.rlRelative(next) : '—',
        nextSub: next ? 'Next change · ' + this.rlFmtShort(next) : 'Next change',
        scheduleError: this.rlScheduleError || ''
      };
    },

    /** Tenants the overview knows about, as {slug, id, plan}; empty until that section has loaded. */
    rlKnownTenants() {
      const seen = new Set();
      const out = [];
      for (const c of this.overviewTenants?.topConsumersMonthToDate || []) {
        const slug = c.tenantSlug || null;
        const id = c.tenantId || null;
        const value = slug || id;
        if (!value || seen.has(value)) continue;
        seen.add(value);
        out.push({ slug, id, value, plan: c.planSlug || '' });
      }
      return out;
    },

    get rlTierCards() {
      const d = this.rlDraft;
      if (!d) return [];
      // Who is on each plan, from the overview's tenant section (the key list does not carry plans).
      const usage = new Map();
      for (const t of this.rlKnownTenants()) {
        if (t.plan) usage.set(t.plan.toLowerCase(), (usage.get(t.plan.toLowerCase()) || 0) + 1);
      }
      const card = (kind, slug, t) => ({
        key: kind + ':' + slug,
        name: kind === 'default' ? 'default' : slug,
        who: kind === 'default' ? 'tenants without a plan' : (usage.has(slug.toLowerCase()) ? usage.get(slug.toLowerCase()) + ' tenant' + (usage.get(slug.toLowerCase()) === 1 ? '' : 's') : 'plan tier'),
        rpm: this.formatNum(t.rpm), burst: this.formatNum(t.burst),
        streams: t.maxConcurrentStreams > 0 ? this.formatNum(t.maxConcurrentStreams) : '∞',
        streamsTitle: t.maxConcurrentStreams > 0 ? 'Concurrent streams' : 'Streams unlimited',
        openLabel: (this.rateLimitsEditable ? 'Edit ' : 'View ') + (kind === 'default' ? 'the default tier' : 'plan ' + slug),
        edit: () => this.openRateLimitTier(kind, slug)
      });
      const cards = [card('default', '', d.default)];
      for (const slug of Object.keys(d.plans || {}).sort((a, b) => a.localeCompare(b))) cards.push(card('plan', slug, d.plans[slug]));
      return cards;
    },

    /** The two protective budgets are stored as rules but are not answers to "who, on which model". */
    rlIsProtective(scope) {
      return scope === 'anonymous' || scope === 'auth_failure';
    },

    rlRuleMatchesFilter(rule) {
      const intent = this.rlIntentFor(rule.scope) || { who: '', where: '' };
      const who = this.rlFilterWho || 'all';
      const where = this.rlFilterWhere || 'any';
      if (who !== 'all' && intent.who !== who) return false;
      if (where !== 'any' && intent.where !== where) return false;
      if (this.rlFilterFlag === 'scheduled' && !(rule.schedule || []).length) return false;
      if (this.rlFilterFlag === 'off' && rule.enabled !== false) return false;
      const q = String(this.rlFilterText || '').trim().toLowerCase();
      if (!q) return true;
      const info = this.rlScopeInfo(rule.scope);
      return String(rule.target || '').toLowerCase().includes(q) ||
        this.rlTargetDisplay(rule.scope, rule.target).toLowerCase().includes(q) ||
        info.short.toLowerCase().includes(q) || info.name.toLowerCase().includes(q) ||
        (rule.schedule || []).some(w => String(w.name || '').toLowerCase().includes(q));
    },

    /** Ordinary rules only: what the list, its counts and its filters are about. */
    rlListRules() {
      return (this.rlDraft?.rules || []).filter(r => !this.rlIsProtective(r.scope));
    },

    get rlScopeChips() {
      const rules = this.rlListRules();
      const intents = rules.map(r => this.rlIntentFor(r.scope) || { who: '', where: '' });
      const chip = (group, id, label, count, active, select) => ({
        key: group + ':' + id, label, count: String(count),
        cls: active ? 'active' : '',
        pressed: active ? 'true' : 'false',
        select
      });
      const whoNow = this.rlFilterWho || 'all';
      const whereNow = this.rlFilterWhere || 'any';
      const who = [['all', 'All'], ['key', 'API keys'], ['tenant', 'Tenants'], ['everyone', 'Everyone']].map(([id, label]) =>
        chip('who', id, label, id === 'all' ? rules.length : intents.filter(i => i.who === id).length, whoNow === id, () => this.setRateLimitWhoFilter(id)));
      const where = [['any', 'Any'], ['one', 'One model'], ['all', 'All models']].map(([id, label]) =>
        chip('where', id, label, id === 'any' ? rules.length : intents.filter(i => i.where === id).length, whereNow === id, () => this.setRateLimitWhereFilter(id)));
      const flags = [
        ['scheduled', 'Scheduled', rules.filter(r => (r.schedule || []).length).length],
        ['off', 'Off', rules.filter(r => r.enabled === false).length]
      ].map(([id, label, count]) => chip('flag', id, label, count, this.rlFilterFlag === id, () => this.toggleRateLimitFlagFilter(id)));
      return { who, where, flags };
    },

    get rlFiltersActive() {
      return !!String(this.rlFilterText || '').trim() || (this.rlFilterWho || 'all') !== 'all' ||
        (this.rlFilterWhere || 'any') !== 'any' || !!this.rlFilterFlag;
    },

    /** Sortable headers: a real button each, with the state a screen reader announces. */
    get rlSortView() {
      const col = (key) => {
        const on = this.rlSortKey === key;
        return {
          ariaSort: on ? (this.rlSortDir > 0 ? 'ascending' : 'descending') : 'none',
          icon: on
            ? '<span class="sort-icon' + (this.rlSortDir > 0 ? '' : ' icon-flip') + '">' + (window.AdminIcons ? window.AdminIcons('chevron-up') : (this.rlSortDir > 0 ? '▲' : '▼')) + '</span>'
            : '',
          toggle: () => this.setRateLimitSort(key)
        };
      };
      return { who: col('who'), model: col('model'), rpm: col('rpm'), refused: col('refused') };
    },

    rlForceFor(rule) {
      const s = this.rlStatusFor(rule.scope, rule.target);
      if (!s) {
        return this.rlScheduleError
          ? { dot: 'err', text: 'unknown', sub: 'schedule unavailable', title: this.rlScheduleError }
          : { dot: '', text: this.rlTierText(rule), sub: 'as saved', title: '' };
      }
      const invalid = (s.windows || []).find(w => w.state === 'invalid');
      if (s.effective?.suspended) {
        return { dot: 'warn', text: 'paused', sub: (s.activeWindow || '') + (s.activeUntil ? ' · until ' + this.rlFmtShort(s.activeUntil) : ''), title: 'This rule is not enforced while the window runs' };
      }
      if (s.activeWindow) {
        return {
          dot: 'on', text: this.rlTierText(s.effective),
          sub: s.activeWindow + (s.activeUntil ? ' · until ' + this.rlFmtShort(s.activeUntil) + ' · ' + this.rlRelative(s.activeUntil) : ' · open-ended'),
          title: 'A window is in force'
        };
      }
      if (invalid) {
        return { dot: 'err', text: this.rlTierText(s.effective), sub: 'base · window “' + invalid.name + '” skipped: ' + invalid.error, title: invalid.error || '' };
      }
      return {
        dot: '', text: this.rlTierText(s.effective),
        sub: s.nextChangeAt && s.nextWindow ? 'base · ' + s.nextWindow + ' at ' + this.rlFmtShort(s.nextChangeAt) + ' · ' + this.rlRelative(s.nextChangeAt) : 'base',
        title: ''
      };
    },

    /** One rule as the list and the protective cards show it. */
    rlRuleRow(rule, index) {
      const info = this.rlScopeInfo(rule.scope);
      const intent = this.rlIntentFor(rule.scope) || { who: '', where: '' };
      const identity = this.rlIdentity(rule.scope, rule.target);
      const saved = (this.rateLimits?.rules || []).find(r => this.rlIdentity(r.scope, r.target) === identity);
      const changed = !saved || JSON.stringify(this.buildRateLimitsPayload({ rules: [saved], plans: {}, default: {} }).rules[0]) !==
        JSON.stringify(this.buildRateLimitsPayload({ rules: [rule], plans: {}, default: {} }).rules[0]);
      const windows = (rule.schedule || []).length;
      const off = rule.enabled === false;
      // A switched-off rule enforces nothing, so it reports that rather than a tier it is not
      // applying — and it says the tier is kept, which is the whole difference from deleting it.
      const force = off
        ? { dot: 'warn', text: 'off', sub: this.rlTierText(rule) + ' kept', title: 'This rule is switched off and enforces nothing' }
        : changed
          ? { dot: '', text: this.rlTierText(rule), sub: 'unsaved · in force once saved', title: '' }
          : this.rlForceFor(rule);
      const target = info.singleton ? info.name : this.rlTargetDisplay(rule.scope, rule.target);

      // Who and Model, the way the creation form asks for them. The stored target stays the title.
      const [subject, ...modelParts] = String(rule.target || '').split('|');
      const key = this.rlRuleKey(rule.scope, rule.target);
      const who = intent.who === 'everyone' ? 'Everyone'
        : intent.who === 'key' ? (key ? this.rlKeyName(key) : subject)
        : intent.who === 'tenant' ? subject
        : info.name;
      const whoKind = intent.who === 'key' ? 'API key' + (key || subject ? ' · ' + String(key?.id || subject).slice(0, 8) + '…' : '')
        : intent.who === 'tenant' ? 'Tenant'
        : intent.who === 'everyone' ? 'All callers'
        : info.desc;
      const model = intent.where === 'one' ? (intent.who === 'everyone' ? subject : modelParts.join('|')) : '';

      // Now: one cell for "is this different from the numbers beside it, and why". Always words,
      // never a dot alone.
      const plural = windows + ' window' + (windows === 1 ? '' : 's');
      let now;
      if (off) now = { tag: 'off', cls: 'tag warn', sub: 'numbers kept' };
      else if (changed) now = { tag: 'unsaved', cls: 'tag accent', sub: 'in force once saved' };
      else if (force.dot === 'on') now = { tag: 'window active', cls: 'tag live', sub: force.text + ' · ' + force.sub };
      else if (force.dot === 'warn') now = { tag: 'paused', cls: 'tag warn', sub: force.sub };
      else if (force.dot === 'err') now = { tag: force.text === 'unknown' ? 'schedule unavailable' : 'window skipped', cls: 'tag level-error', sub: force.sub };
      else if (windows) now = { tag: plural, cls: 'tag muted', sub: force.sub.startsWith('base · ') ? 'next: ' + force.sub.slice(7) : '' };
      else now = { tag: '', cls: '', sub: '' };

      const refusals = this.rlRefusalsView(rule.scope, rule.target);
      const tenantRate = rule.scope === 'tenant' && !(rule.rpm > 0);
      return {
        key: identity + ':' + index,
        identity,
        target,
        // The stored target, for when the name is not enough to tell two keys apart.
        targetTitle: info.singleton ? '' : String(rule.target || ''),
        who, whoKind,
        model: model || 'All models',
        modelCls: model ? 'rl-model' : 'rl-model muted',
        modelTitle: model,
        tier: this.rlTierText(rule),
        rpm: rule.rpm > 0 ? this.formatNum(rule.rpm) : (tenantRate ? 'plan' : '—'),
        rpmTitle: rule.rpm > 0 ? 'Sustained requests per minute' : (tenantRate ? 'RPM 0: the tenant keeps its plan’s rate' : 'RPM 0: this rule does not limit the rate'),
        burst: this.formatNum(rule.burst || 0),
        streams: rule.maxConcurrentStreams > 0 ? this.formatNum(rule.maxConcurrentStreams) : '∞',
        streamsTitle: rule.maxConcurrentStreams > 0 ? 'Concurrent streams' : 'Streams unlimited',
        nowTag: now.tag, nowCls: now.cls, nowSub: now.sub,
        hasNowTag: !!now.tag, noNow: !now.tag, hasNowSub: !!now.sub,
        nowTitle: force.title || '',
        refused: refusals.text,
        refusedTitle: refusals.title,
        refusedCls: 'rl-col-refused num' + (refusals.known && refusals.hits > 0 ? ' rl-refused-some' : '') + (refusals.known ? '' : ' muted'),
        refusedMark: refusals.qualified ? '*' : '',
        enabled: !off,
        enabledAria: off ? 'false' : 'true',
        enabledLabel: (off ? 'Switch on rule ' : 'Switch off rule ') + target,
        toggle: () => this.toggleRateLimitRuleEnabled(identity),
        openLabel: 'Open rule ' + target + ', ' + this.rlTierText(rule) + (now.tag ? ', ' + now.tag : ''),
        rowCls: 'rl-row' + (changed ? ' changed' : '') + (off ? ' off' : ''),
        open: () => this.openRateLimitRule(identity),
        _sort: {
          who: who.toLowerCase(), model: model.toLowerCase(),
          rpm: Number(rule.rpm) || 0,
          refused: refusals.known ? refusals.hits : -1
        }
      };
    },

    get rlRuleRows() {
      const rules = this.rlDraft?.rules || [];
      const key = this.rlSortKey || 'who';
      const dir = this.rlSortDir || 1;
      return rules
        .map((rule, index) => ({ rule, index }))
        .filter(({ rule }) => !this.rlIsProtective(rule.scope) && this.rlRuleMatchesFilter(rule))
        .map(({ rule, index }) => this.rlRuleRow(rule, index))
        .sort((a, b) => {
          const x = a._sort[key], y = b._sort[key];
          // Unknown refusals sort last in either direction: "—" is not a small number.
          if (key === 'refused' && (x < 0 || y < 0) && x !== y) return x < 0 ? 1 : -1;
          const primary = typeof x === 'number' ? x - y : String(x).localeCompare(String(y));
          if (primary) return primary * dir;
          return a._sort.who.localeCompare(b._sort.who) || a._sort.model.localeCompare(b._sort.model);
        });
    },

    /**
     * The two protective budgets, always both: configured ones open their rule, the others offer to
     * be configured. Same stored rules, same drawer — only where they are listed differs.
     */
    get rlProtectiveCards() {
      const rules = this.rlDraft?.rules || [];
      return ['anonymous', 'auth_failure'].map((scope) => {
        const info = this.rlScopeInfo(scope);
        const index = rules.findIndex(r => r.scope === scope);
        const row = index >= 0 ? this.rlRuleRow(rules[index], index) : null;
        const who = this.rlIntentFor(scope).who;
        return {
          key: scope,
          name: info.name,
          desc: info.desc,
          configured: !!row,
          unset: !row,
          canConfigure: !row && this.rateLimitsEditable,
          unsetText: scope === 'anonymous'
            ? 'Not set — anonymous callers are held to the default tier.'
            : 'Not set — failed sign-ins fall back to the default tier.',
          rpm: row ? row.rpm : '', burst: row ? row.burst : '', streams: row ? row.streams : '',
          isAuthFailure: scope === 'auth_failure',
          hasStreams: !!row && scope !== 'auth_failure',
          nowTag: row ? row.nowTag : '', nowCls: row ? row.nowCls : '', hasNowTag: !!row && row.hasNowTag,
          enabled: row ? row.enabled : false,
          enabledAria: row ? row.enabledAria : 'false',
          enabledLabel: row ? row.enabledLabel : '',
          toggle: row ? row.toggle : () => {},
          openLabel: row ? row.openLabel : 'Configure ' + info.name,
          open: row ? row.open : () => {},
          configure: () => this.startRateLimitNewRule({ who })
        };
      });
    },

    get rlHasRules() { return this.rlListRules().length > 0; },
    get rlNoRules() { return !!this.rlDraft && this.rlListRules().length === 0; },
    get rlNoFilteredRules() { return this.rlHasRules && this.rlRuleRows.length === 0; },

    get rlZoneOptions() {
      // ~420 entries that never change: built once, or the two zone selects would be re-diffed
      // on every keystroke in the window form.
      if (this._rlZoneCache) return this._rlZoneCache;
      const browser = this.rlBrowserZone();
      let all = [];
      try { all = Intl.supportedValuesOf('timeZone'); } catch { all = []; }
      const head = [browser, 'UTC'].filter((z, i, arr) => arr.indexOf(z) === i);
      const rest = all.filter(z => !head.includes(z));
      this._rlZoneCache = [...head, ...rest].map(z => ({ value: z, label: z === browser ? z + ' (browser)' : z }));
      return this._rlZoneCache;
    },

    /** Which heading labels the rule drawer: the window pane's while it is showing. */
    get rlRuleDrawerLabel() {
      return this.rlWindowOpen ? 'rl-window-title' : 'rl-rule-title';
    },

    get rlRangeOptions() {
      return [{ value: 7, label: 'Next 7 days' }, { value: 14, label: 'Next 14 days' }, { value: 30, label: 'Next 30 days' }];
    },

    rlBandClass(index) {
      return ['b1', 'b2', 'b3', 'b4'][index % 4];
    },

    get rlTimelineView() {
      const report = this.rlSchedule;
      const { from, to } = this.rlRangeFromTo();
      const span = to.getTime() - from.getTime();
      const now = this.rlNow();
      const pct = (t) => Math.max(0, Math.min(100, ((t - from.getTime()) / span) * 100));
      const zone = this.rlZoneOrDefault();
      const days = Number(this.rlRangeDays) || 7;

      // One tick per local day, positioned by its real instant so a 23- or 25-hour day is drawn
      // at its true width rather than forced into an equal column.
      const axis = [];
      const start = this.rlZoneParts(from, zone);
      for (let i = 0; i < days; i++) {
        const dayStart = this.rlZonedToDate(start.year, start.month, start.day + i, 0, 0, zone);
        if (days > 14 && i % 2 === 1) continue;
        axis.push({
          key: i,
          style: 'left: ' + pct(dayStart.getTime()) + '%',
          label: new Intl.DateTimeFormat(undefined, { timeZone: zone, weekday: 'short', day: 'numeric' }).format(dayStart)
        });
      }

      const rules = (this.rlDraft?.rules || []).filter(r => (r.schedule || []).length);
      const legendMap = new Map();
      const rows = rules.map((rule) => {
        const id = this.rlIdentity(rule.scope, rule.target);
        const names = (rule.schedule || []).map(w => w.name);
        const bands = (report?.occurrences || [])
          .filter(o => this.rlIdentity(o.scope, o.target) === id)
          .map((o, i) => {
            const s = new Date(o.start).getTime();
            const e = new Date(o.end).getTime();
            const idx = Math.max(0, names.indexOf(o.window));
            const state = e <= now ? 'past' : (s <= now ? 'current' : 'future');
            const cls = this.rlBandClass(idx);
            legendMap.set(id + '|' + o.window, { cls, text: o.window + ' · ' + this.rlTierText(o.tier) });
            return {
              key: i,
              style: 'left: ' + pct(s) + '%; width: ' + Math.max(0.4, pct(e) - pct(s)) + '%',
              cls: 'rl-band ' + cls + ' ' + state + (o.clippedStart ? ' clip-start' : '') + (o.clippedEnd ? ' clip-end' : ''),
              title: o.window + ': ' + this.rlFmtLong(o.start) + ' → ' + this.rlFmtLong(o.end) + ' · ' + this.rlTierText(o.tier)
            };
          });
        const info = this.rlScopeInfo(rule.scope);
        return {
          key: id,
          label: info.singleton ? info.short : String(rule.target).replace('|', ' · '),
          sub: 'base ' + this.rlTierText(rule),
          bands,
          open: () => this.openRateLimitRule(id)
        };
      });

      return {
        hasRows: rows.length > 0,
        empty: rows.length === 0,
        rows,
        axis,
        nowStyle: 'left: ' + pct(now) + '%',
        showNow: now >= from.getTime() && now <= to.getTime(),
        legend: [...legendMap.values()].map((l, i) => ({ key: i, cls: 'rl-legend-swatch ' + l.cls, text: l.text })),
        rangeText: new Intl.DateTimeFormat(undefined, { timeZone: zone, day: 'numeric', month: 'short' }).format(from) + ' – ' +
          new Intl.DateTimeFormat(undefined, { timeZone: zone, day: 'numeric', month: 'short' }).format(new Date(to.getTime() - 1)),
        // Which configuration is on screen. The panel used to say "unsaved edits are not drawn"
        // and mean it; now it draws them, so it has to say which it is showing.
        draft: this.rateLimitsDirty,
        sourceText: this.rateLimitsDirty
          ? 'Drawn from your unsaved draft, so you can check a schedule before saving it.'
          : 'From the saved configuration.'
      };
    },

    get rlTransitionRows() {
      const list = (this.rlSchedule?.transitions || []).filter(t => new Date(t.at).getTime() >= Date.now() - 60000);
      const shown = this.rlShowAllTransitions ? list : list.slice(0, 6);
      return shown.map((t, i) => {
        const info = this.rlScopeInfo(t.scope);
        const up = (t.to?.rpm || 0) > (t.from?.rpm || 0) || (!t.to?.suspended && t.from?.suspended);
        const down = (t.to?.rpm || 0) < (t.from?.rpm || 0) || (t.to?.suspended && !t.from?.suspended);
        return {
          key: i,
          when: this.rlFmtShort(t.at),
          rel: this.rlRelative(t.at),
          target: info.singleton ? info.short : String(t.target).replace('|', ' · '),
          what: t.window ? (t.to?.suspended ? 'paused by ' + t.window : t.window) : 'back to base',
          // Rate alone when both sides have one: the full tiers would say the same thing three times.
          delta: (t.from?.rpm > 0 && t.to?.rpm > 0 && !t.from?.suspended && !t.to?.suspended)
            ? this.formatNum(t.from.rpm) + ' → ' + this.formatNum(t.to.rpm) + ' rpm'
            : this.rlTierText(t.from) + ' → ' + this.rlTierText(t.to),
          deltaCls: 'rl-delta ' + (up ? 'up' : down ? 'down' : '')
        };
      });
    },

    get rlTransitionsView() {
      const all = (this.rlSchedule?.transitions || []).filter(t => new Date(t.at).getTime() >= this.rlNow() - 60000);
      const total = this.rlSchedule?.transitionsTruncated ? (this.rlSchedule.transitionsTotal || all.length) : all.length;
      return {
        empty: !!this.rlSchedule && all.length === 0,
        showToggle: all.length > 6,
        toggleText: this.rlShowAllTransitions ? 'Show fewer' : 'Show all ' + total + ' changes',
        truncatedNote: this.rlSchedule?.transitionsTruncated ? 'Only the first ' + all.length + ' of ' + total + ' changes are listed; narrow the range to see the rest.' : ''
      };
    },

    get rlPreviewView() {
      const p = this.rlPreview;
      if (!p) return { has: false, title: '', rows: [], unchanged: '', error: this.rlPreviewError || '' };
      const rows = (p.rules || []).map((r, i) => ({ r, i })).filter(({ r }) =>
        r.activeWindow || r.effective?.suspended || JSON.stringify(r.effective) !== JSON.stringify(r.base));
      const unchanged = (p.rules || []).length - rows.length;
      return {
        has: true,
        title: this.rlFmtLong(p.at) + ' (' + this.rlZoneOrDefault() + ')',
        rows: rows.map(({ r, i }) => {
          const info = this.rlScopeInfo(r.scope);
          return {
            key: i,
            target: info.singleton ? info.short : String(r.target).replace('|', ' · '),
            tag: r.activeWindow || 'base',
            tagCls: 'tag ' + (r.activeWindow ? (r.effective?.suspended ? 'warn' : 'accent') : 'muted'),
            tier: this.rlTierText(r.effective),
            next: r.nextChangeAt ? 'until ' + this.rlFmtShort(r.nextChangeAt) : ''
          };
        }),
        unchanged: unchanged > 0 ? unchanged + ' rule' + (unchanged === 1 ? '' : 's') + ' unchanged · base tiers apply' : '',
        error: this.rlPreviewError || ''
      };
    },

    // ---- rule drawer ----

    get rlRuleDrawerView() {
      const r = this.rlRule;
      const info = this.rlScopeInfo(r.scope);
      const status = this.rlStatusFor(r.scope, r.target);
      const off = r.enabled === false;
      const force = off
        ? { dot: 'warn', text: 'off', sub: this.rlTierText(r) + ' kept, enforcing nothing', title: '' }
        : status
          ? this.rlForceFor({ ...r, schedule: r.schedule })
          : { dot: '', text: this.rlTierText(r), sub: 'not saved yet', title: '' };
      const windows = (r.schedule || []).map((w, i) => {
        const ws = (status?.windows || []).find(x => String(x.name).toLowerCase() === String(w.name).toLowerCase());
        const state = ws?.state || 'unsaved';
        const stateText = state === 'active' ? 'Active' + (ws.nextEndAt ? ' · ends ' + this.rlFmtShort(ws.nextEndAt) : '')
          : state === 'upcoming' ? 'Next ' + this.rlFmtShort(ws.nextStartAt) + ' · ' + this.rlRelative(ws.nextStartAt)
          : state === 'expired' ? 'Expired · never applies again'
          : state === 'invalid' ? 'Skipped: ' + (ws.error || 'cannot be evaluated')
          : 'Not saved yet';
        return {
          key: i,
          name: w.name,
          kindTag: w.kind === 'once' ? 'once' : 'weekly',
          kindCls: 'tag ' + (w.kind === 'once' ? 'accent' : ''),
          when: this.rlWindowWhen(w),
          tier: w.suspend ? 'paused' : this.rlTierText(w),
          cls: 'rl-win ' + (state === 'active' ? 'active' : state === 'expired' || state === 'invalid' ? 'muted' : ''),
          stateCls: 'status-chip ' + (state === 'active' ? 'ok' : state === 'invalid' ? 'fail' : state === 'expired' ? 'warn' : 'muted'),
          stateText,
          domId: 'rl-win-' + i,
          edit: () => this.openRateLimitWindow(i)
        };
      });

      const id = this.rlIdentity(r.scope, r.target);
      const { from, to } = this.rlRangeFromTo();
      const span = to.getTime() - from.getTime();
      const now = this.rlNow();
      const names = (r.schedule || []).map(w => w.name);
      const pct = (t) => Math.max(0, Math.min(100, ((t - from.getTime()) / span) * 100));
      const bands = (this.rlSchedule?.occurrences || [])
        .filter(o => this.rlIdentity(o.scope, o.target) === id)
        .map((o, i) => {
          const s = new Date(o.start).getTime();
          const e = new Date(o.end).getTime();
          const state = e <= now ? 'past' : (s <= now ? 'current' : 'future');
          return {
            key: i,
            style: 'left: ' + pct(s) + '%; width: ' + Math.max(0.4, pct(e) - pct(s)) + '%',
            cls: 'rl-band ' + this.rlBandClass(Math.max(0, names.indexOf(o.window))) + ' ' + state + (o.clippedStart ? ' clip-start' : '') + (o.clippedEnd ? ' clip-end' : ''),
            title: o.window + ': ' + this.rlFmtLong(o.start) + ' → ' + this.rlFmtLong(o.end)
          };
        });

      const usage = this.rlRuleUsageView(r.scope, r.target);

      return {
        eyebrow: (this.rlIsProtective(r.scope) ? 'Protective limit' : 'Rule · ' + info.name) + (this.rateLimitsEditable ? '' : ' · read-only'),
        title: info.singleton ? info.name : this.rlTargetDisplay(r.scope, r.target),
        // The name leads; the id the rule is stored against stays one glance away.
        subtitle: info.desc + (this.rlRuleKey(r.scope, r.target) ? ' Key id ' + String(r.target).split('|')[0] + '.' : ''),
        forceCls: 'rl-forcebar ' + (force.dot === 'on' ? 'on' : force.dot === 'warn' ? 'warn' : force.dot === 'err' ? 'err' : ''),
        forceDot: 'rl-dot ' + force.dot,
        forceBig: force.text,
        forceSub: force.dot === 'on' ? 'Enforcing ' + force.sub : force.sub,
        enabled: !off,
        enabledAria: off ? 'false' : 'true',
        enabledText: off ? 'Switched off — the tier and windows below are kept' : 'Enforced',
        windows,
        noWindows: windows.length === 0,
        bands,
        hasBands: bands.length > 0,
        // The strip is drawn from whichever report the calendar holds, so it says which that is.
        bandsSource: this.rateLimitsDirty ? 'as drafted' : 'as saved',
        nowStyle: 'left: ' + pct(now) + '%',
        usage,
        readOnly: !this.rateLimitsEditable,
        editable: this.rateLimitsEditable,
        error: this.rlRuleError || ''
      };
    },

    // ---- window form ----

    get rlWindowView() {
      const f = this.rlWindow;
      const isOnce = f.kind === 'once';
      const preview = this.rlWindowPreview;
      const candidate = this.rlWindowFromForm();
      const tierText = f.suspend ? 'paused (nothing enforced by this rule)' : this.rlTierText(candidate);
      const baseText = this.rlTierText(this.rlRule);
      let summary;
      if (isOnce) {
        summary = candidate.from
          ? (candidate.until
            ? 'From ' + this.rlFmtLong(candidate.from) + ' to ' + this.rlFmtLong(candidate.until)
            : 'From ' + this.rlFmtLong(candidate.from) + ', staying in force')
          : 'Pick a start';
      } else {
        const overnight = f.start && f.end && f.end !== '24:00' && f.end <= f.start;
        summary = 'Every ' + this.rlDaysText(candidate.days) + ' from ' + (f.start || '?') + ' to ' + (f.end || '?') +
          (overnight ? ' the next morning' : '') + ' (' + candidate.timeZone + ')';
      }
      summary += ', this rule ' + (f.suspend ? 'is paused' : 'gets ' + tierText + ' instead of ' + baseText) + '.';
      const next = preview?.nextStartAt
        ? (preview.activeNow ? 'Running now' : 'Next: ' + this.rlFmtLong(preview.nextStartAt)) +
          (preview.nextEndAt ? ' → ' + this.rlFmtLong(preview.nextEndAt) : ' · open-ended')
        : '';
      const overlaps = preview?.overlaps || [];
      const outrankedBy = preview?.outrankedBy || [];
      const outranks = preview?.outranks || [];
      const precedence = [
        outrankedBy.length ? 'outranked by ' + outrankedBy.join(', ') + ' while those run' : '',
        outranks.length ? 'outranks ' + outranks.join(', ') : ''
      ].filter(Boolean).join(' · ');
      return {
        title: this.rlWindowEditIndex >= 0 ? 'Edit window' : 'Add window',
        eyebrow: (this.rlRule.target === '*' ? this.rlScopeInfo(this.rlRule.scope).name : this.rlRule.target) + ' · window',
        isOnce, isWeekly: !isOnce,
        kindRows: [
          { key: 'weekly', label: 'Every week', cls: !isOnce ? 'active' : '', pressed: !isOnce ? 'true' : 'false', select: () => this.setRateLimitWindowKind('weekly') },
          { key: 'once', label: 'One time', cls: isOnce ? 'active' : '', pressed: isOnce ? 'true' : 'false', select: () => this.setRateLimitWindowKind('once') }
        ],
        dayRows: this.rlDayLabels().map(([id, label]) => ({
          key: id,
          label,
          cls: (f.days || []).includes(id) ? 'on' : '',
          pressed: (f.days || []).includes(id) ? 'true' : 'false',
          toggle: () => this.toggleRateLimitWindowDay(id)
        })),
        summary,
        next,
        overlapCls: 'status-chip ' + (overlaps.length ? 'fail' : preview ? 'ok' : 'muted'),
        overlapText: overlaps.length ? 'Overlaps ' + overlaps.join(', ') : preview ? 'No overlap' : 'Checking…',
        precedence,
        error: this.rlWindowError || (preview && preview.valid === false ? preview.error : '') || '',
        applyDisabled: !!this.rlWindowError || !!(preview && preview.valid === false),
        applyLabel: this.rlWindowEditIndex >= 0 ? 'Apply' : 'Add window',
        showTier: !f.suspend,
        isEdit: this.rlWindowEditIndex >= 0,
        advancedLabel: f.showAdvanced ? 'Hide advanced' : 'Advanced · priority, valid from / until',
        showAdvanced: !!f.showAdvanced,
        zoneOptions: this.rlZoneOptions
      };
    },

    // ---- new rule flow ----

    /**
     * The key list for the rate-limit page. The Keys and Usage tabs load it for themselves; Settings
     * did not, so opened first it offered no keys to pick and showed every key rule as a bare id.
     * Never rejects: the rules are usable without it, and the new-rule drawer says what happened.
     */
    loadRateLimitKeys(refresh) {
      // One request at a time: entering Settings and opening the drawer can both ask.
      if (this._rlKeysLoad) return this._rlKeysLoad;
      const have = (this.keys?.length || 0) > 0;
      if (have) this.rlKeysState = 'ready';
      if (have && !refresh) return Promise.resolve();
      // A refresh of a list that is already there is silent, and a failed one keeps the list.
      if (!have) this.rlKeysState = 'loading';
      this._rlKeysLoad = this.fetchKeys()
        .then(() => { this.rlKeysState = 'ready'; }, () => { if (!have) this.rlKeysState = 'failed'; })
        .finally(() => { this._rlKeysLoad = null; });
      return this._rlKeysLoad;
    },

    retryRateLimitKeys() { this.loadRateLimitKeys(); },

    /** The key with this id, in any casing — ids are compared the way the gateway compares them. */
    rlFindKey(id) {
      const q = String(id || '').trim().toLowerCase();
      if (!q) return null;
      return (this.keys || []).find(k => String(k.id || '').toLowerCase() === q) || null;
    },

    /** What an operator calls a key: its label, else its assignee, else its prefix. */
    rlKeyName(k) {
      return k.label || k.assignee || (k.keyPrefix ? k.keyPrefix + '…' : String(k.id || ''));
    },

    /** The text a picked key leaves in the field: the name, with the prefix to tell namesakes apart. */
    rlKeyPickText(k) {
      const name = k.label || k.assignee || '';
      const prefix = k.keyPrefix ? k.keyPrefix + '…' : '';
      return name && prefix ? name + ' (' + prefix + ')' : (name || prefix || String(k.id || ''));
    },

    /** The key a stored rule is about, when it is a key rule and the key list knows the id. */
    rlRuleKey(scope, target) {
      const isKeyRule = scope === 'api_key' || scope === 'api_key_model';
      return isKeyRule ? this.rlFindKey(String(target || '').split('|')[0]) : null;
    },

    /**
     * A rule target for people: a key id becomes the key's name when the list knows it. The stored
     * target is untouched — this is only ever what is shown and searched.
     */
    rlTargetDisplay(scope, target) {
      const [first, ...rest] = String(target || '').split('|');
      const key = this.rlRuleKey(scope, target);
      return [key ? this.rlKeyName(key) : first, ...rest].join(' · ');
    },

    /** The new-rule field that holds an API key under the current choice, or '' when none does. */
    rlNewRuleKeyField() {
      return this.rlNewRule.who === 'key' ? 'subject' : '';
    },

    /** The suggestion picked for a field, for as long as the field still holds the text it wrote. */
    rlNewRulePick(field) {
      const picked = this.rlNewRule.picked?.[field];
      return picked && String(picked.text).trim() === String(this.rlNewRule[field] || '').trim() ? picked : null;
    },

    /** Active keys called exactly this. More than one is an ambiguity to refuse, never to guess. */
    rlKeysNamed(text) {
      const q = String(text || '').trim().toLowerCase();
      return (this.keys || []).filter(k => !k.isRevoked && !k.isArchived &&
        (this.rlKeyName(k).toLowerCase() === q || this.rlKeyPickText(k).toLowerCase() === q));
    },

    /**
     * The one place a new rule's key is resolved: the picked key, else the key whose id was typed,
     * else the only active key of that name. The id is the authority; a name is a way to find it.
     */
    rlNewRuleKey() {
      const field = this.rlNewRuleKeyField();
      const text = field ? String(this.rlNewRule[field] || '').trim() : '';
      if (!text) return null;
      const picked = this.rlNewRulePick(field);
      if (picked) return this.rlFindKey(picked.value);
      const named = this.rlKeysNamed(text);
      return this.rlFindKey(text) || (named.length === 1 ? named[0] : null);
    },

    /**
     * Why the key field cannot be saved yet, or ''. A key rule is matched on the key's id — the GUID
     * the gateway issued — and on nothing else, so text that is not the id of a key can never limit
     * anything; it would only look configured. With the list loaded the text must resolve to a key
     * in it. Without the list a pasted id cannot be checked, so it is accepted on its shape alone.
     */
    rlNewRuleKeyError() {
      const field = this.rlNewRuleKeyField();
      const text = field ? String(this.rlNewRule[field] || '').trim() : '';
      if (!text || this.rlNewRuleKey()) return '';
      if (this.rlKeysState !== 'ready') {
        return /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(text) ? ''
          : 'The API keys are not loaded, so a key cannot be found by name. Paste the key’s id (it looks like 6f1c0a52-…), or load the keys and pick one.';
      }
      const namesakes = this.rlKeysNamed(text).length;
      return namesakes > 1
        ? namesakes + ' keys are named ‘' + text + '’. Pick the one you mean from the list.'
        : 'No API key has this name or id. Pick a key from the list; a rule is matched on the key’s id, so anything else would never apply.';
    },

    /**
     * A model as enforcement will look it up. Rules are matched on the canonical id only, so an alias
     * is resolved here; stored as typed it would save cleanly and never limit anything.
     */
    rlCanonicalModel(text) {
      const q = String(text || '').trim().toLowerCase();
      if (!q) return { id: '', known: false, viaAlias: false };
      const models = this.models || [];
      const byId = models.find(m => String(m.id || '').toLowerCase() === q);
      if (byId) return { id: byId.id, known: true, viaAlias: false };
      const byAlias = models.find(m => (m.aliases || []).some(a => String(a).toLowerCase() === q));
      if (byAlias) return { id: byAlias.id, known: true, viaAlias: true };
      return { id: String(text).trim(), known: false, viaAlias: false };
    },

    rlSuggestionsFor(kind, query) {
      const q = String(query || '').trim().toLowerCase();
      const out = [];
      if (kind === 'models') {
        for (const m of this.models || []) {
          const id = m.id || '';
          const aliases = (m.aliases || []).join(', ');
          if (!q || id.toLowerCase().includes(q) || aliases.toLowerCase().includes(q)) {
            out.push({ value: id, text: id, sub: 'registered' + (aliases ? ' · alias ' + aliases : '') + (m.publicAccess ? ' · public' : '') });
          }
        }
      } else if (kind === 'keys') {
        for (const k of this.keys || []) {
          if (k.isRevoked || k.isArchived) continue;
          const id = k.id || '';
          const label = [k.label, k.assignee].filter(Boolean).join(' · ');
          if (!q || id.toLowerCase().includes(q) || label.toLowerCase().includes(q) || (k.keyPrefix || '').toLowerCase().includes(q)) {
            // `fill` is what a pick leaves in the field; `value` (the id) is what the rule stores.
            const sub = [k.keyPrefix ? k.keyPrefix + '…' : '', k.label ? k.assignee : ''].filter(Boolean).join(' · ');
            out.push({ value: id, fill: this.rlKeyPickText(k), text: this.rlKeyName(k), sub: sub || id });
          }
        }
      } else if (kind === 'tenants') {
        // Tenants the overview has seen this month, then any tenant an existing rule already
        // names, so a target can be picked rather than typed even before the overview loads.
        const seen = new Set();
        for (const t of this.rlKnownTenants()) {
          seen.add(String(t.value).toLowerCase());
          if (!q || String(t.value).toLowerCase().includes(q) || String(t.id || '').toLowerCase().includes(q)) {
            out.push({ value: t.value, text: t.value, sub: t.plan ? 'plan ' + t.plan : (t.id && t.slug ? t.id : 'tenant') });
          }
        }
        for (const r of this.rlDraft?.rules || []) {
          const value = r.scope === 'tenant' ? r.target : r.scope === 'tenant_model' ? String(r.target || '').split('|')[0] : '';
          if (!value || seen.has(value.toLowerCase())) continue;
          seen.add(value.toLowerCase());
          if (!q || value.toLowerCase().includes(q)) out.push({ value, text: value, sub: 'has a rule' });
        }
      }
      // Every match: the list scrolls. The view bounds what it renders (rlSuggestList).
      return out;
    },

    /**
     * One field's suggestion list. Closed once the field holds a pick, and bounded in what it
     * renders — with the remainder counted, never silently dropped — so a gateway with thousands of
     * keys does not put thousands of buttons in the drawer.
     */
    rlSuggestList(field, kind) {
      if (!kind || this.rlNewRulePick(field)) return { items: [], has: false, more: '' };
      const all = this.rlSuggestionsFor(kind, this.rlNewRule[field]);
      const max = 200;
      const active = this.rlCombo?.field === field ? this.rlCombo.index : -1;
      const items = all.slice(0, max).map((s, i) => ({
        key: field + ':' + i, text: s.text, sub: s.sub,
        id: this.rlComboOptionId(field, i),
        selected: i === active ? 'true' : 'false',
        cls: i === active ? 'active' : '',
        pick: () => this.pickRateLimitSuggestion(field, s.value, s.fill)
      }));
      return {
        items,
        has: items.length > 0,
        more: all.length > max ? 'Showing ' + max + ' of ' + this.formatNum(all.length) + '. Type to narrow the list.' : ''
      };
    },

    /**
     * What the two protective scopes fall back to while they have no rule, so their numbers are
     * typed against a reference instead of into a blank. Reaching here means no rule exists for the
     * scope — creation refuses a duplicate — so the honest answer is the default tier.
     */
    rlProtectiveBaselineFor(scope) {
      const fallback = Number(this.rlDraft?.default?.rpm) || 0;
      if (scope === 'auth_failure') {
        return { rpm: fallback, text: 'Failed sign-ins currently fall back to the default tier, ' + this.formatNum(fallback) + ' rpm per client address.' };
      }
      if (scope === 'anonymous') {
        return { rpm: fallback, text: 'Anonymous callers currently fall back to the default tier, ' + this.formatNum(fallback) + ' rpm per client address.' };
      }
      return { rpm: 0, text: '' };
    },

    /**
     * What a limit's rate can be, as a band: exact for a steady rule, a range where it cannot be
     * known here. `tiers` are the {rpm, burst} pairs it may run at. `open` marks a limit that is at
     * times not there at all (a suspended or rate-less window), so nothing can be said about its
     * loosest state; `scaled` marks one that adaptive shedding shrinks under load.
     */
    rlLimitBand(tiers, { open = false, scaled = false } = {}) {
      const rated = tiers.filter(t => Number(t.rpm) > 0)
        .map(t => ({ rpm: Number(t.rpm), burst: Math.max(0, Number(t.burst) || 0) }));
      if (!rated.length) return { rated: false };
      const caps = rated.map(t => t.rpm + t.burst);
      return {
        rated: true, scaled,
        rpmMin: Math.min(...rated.map(t => t.rpm)), rpmMax: open ? Infinity : Math.max(...rated.map(t => t.rpm)),
        capMin: Math.min(...caps), capMax: open ? Infinity : Math.max(...caps),
        burstMin: Math.min(...rated.map(t => t.burst)), burstMax: open ? Infinity : Math.max(...rated.map(t => t.burst))
      };
    },

    /**
     * Whether limit `a` is certainly never looser than limit `b`: at its loosest, a's rate and its
     * capacity (rpm + burst) are both within b's at its tightest. Rate alone is not enough — the
     * same rpm with a smaller burst is the tighter limit, on the burst. With adaptive shedding on,
     * limits that name a model shrink under load and the others do not: a shrinking `b` can drop
     * below any fixed `a`, and two that shrink together (the same model, so the same factor) keep
     * their order only when the bursts are ordered too, since rate and burst are rounded apart.
     */
    rlAtMost(a, b, adaptive) {
      if (!a.rated || !b.rated) return false;
      if (adaptive && b.scaled && (!a.scaled || a.burstMax > b.burstMin)) return false;
      return a.rpmMax <= b.rpmMin && a.capMax <= b.capMin;
    },

    /**
     * The limits already in the draft that every request counted by `rule` must also pass, and what
     * can honestly be concluded from them. One calculation feeds the list under the form, the
     * "tightest" mark and the redundancy warning, so the three cannot tell different stories.
     *
     * Each row counts a superset of the rule's traffic and sits earlier in the gateway's scope order
     * (global, tenant, key, model, tenant-on-model, key-on-model), so the rule is never charged for
     * a request a row refused. That is what lets a row vouch for redundancy: if it is never looser
     * than the rule (rlAtMost) the rule's bucket always holds at least the row's tokens and cannot
     * be the one that refuses. A tier change keeps a bucket's tokens, so a band over every tier a
     * limit may run at is a sound bound; a limit that is at times absent is not (`open`), and
     * neither is any rule with schedule windows taken at its base rate alone.
     *
     *  - Tenant tier. Which tenant a key belongs to is unknown here, and a tenant may be written by
     *    id in one rule and by slug in another, so the tier is never looked up: it is the band over
     *    the default and every plan (floored at 1 rpm, as the gateway floors them), every enforced
     *    tenant rule and every window on one. rpm 0 there keeps the plan rate, already counted.
     *  - "Dominant" is the only ordering the gateway has, and it is partial. A limit decides the
     *    outcome alone only if it counts every request the others count (`covers`) and is never
     *    looser than any of them; the others then can never be the one that refuses. The lowest
     *    numbers are not enough: a wider limit is shared with other traffic and can refuse first
     *    however generous it is, and a tenant tier and a model rule do not contain each other at
     *    all. Where no limit qualifies none is marked, and the form says so.
     *  - A false "this rule is redundant" talks an operator out of a limit that works, and a missing
     *    one costs nothing, so the warning needs a steady row. A tenant rule is never warned about:
     *    it replaces the plan rate, so being looser is its job.
     */
    rlApplicableLimits(rule) {
      const d = this.rlDraft || {};
      const adaptive = d.adaptiveEnabled === true;
      const intent = this.rlIntentFor(rule.scope);
      const none = { rows: [], mine: { rated: false }, mineLowest: false, redundantUnder: null };
      if (!intent || !intent.where) return none;
      const parts = String(rule.target || '').split('|');
      const subject = intent.who === 'everyone' ? '' : parts[0];
      const model = intent.where === 'one' ? parts[parts.length - 1] : '';
      const enforced = (d.rules || []).filter(r => r.enabled !== false);
      const rows = [];
      // `source` is how the row is named when it vouches for the warning; `covers` is which kinds of
      // the other rows' traffic it counts as well (every row covers the new rule's).
      const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
      const ruleRow = (r, { id = r.scope, label, source = '', shared = false, covers = [], conditional = false }) => {
        const windows = r.schedule || [];
        rows.push({
          id, kind: r.scope, label, source, shared, covers, conditional, scheduled: windows.length > 0, streams: Number(r.maxConcurrentStreams) || 0,
          band: this.rlLimitBand([r, ...windows.filter(w => !w.suspend)], { open: windows.length > 0, scaled: r.scope === 'model' || r.scope === 'tenant_model' })
        });
      };
      const find = (scope, target) => enforced.find(x => x.scope === scope && same(x.target, target));

      const gateway = rule.scope === 'global' ? null : find('global', '*');
      if (gateway) ruleRow(gateway, { label: 'Whole gateway', source: 'whole-gateway limit', shared: true, covers: ['tenant', 'api_key', 'model', 'tenant_model'] });
      if (intent.who !== 'everyone' && rule.scope !== 'tenant') {
        const floor = t => ({ rpm: Math.max(1, Number(t.rpm) || 0), burst: t.burst });
        const tiers = [d.default || {}, ...Object.values(d.plans || {})].map(floor);
        for (const r of enforced.filter(x => x.scope === 'tenant')) {
          tiers.push(r, ...(r.schedule || []).filter(w => !w.suspend));
        }
        rows.push({
          id: 'tenant', kind: 'tenant', source: 'loosest tenant tier configured', covers: ['api_key', 'tenant_model'], shared: false, conditional: false,
          scheduled: false, streams: 0, band: this.rlLimitBand(tiers),
          label: intent.who === 'tenant' ? 'Tenant tier of ' + subject : 'Tenant tier of the key’s tenant'
        });
      }
      const ownKey = rule.scope === 'api_key_model' ? find('api_key', subject) : null;
      if (ownKey) ruleRow(ownKey, { label: this.rlTargetDisplay('api_key', subject) + ' on all models', source: 'limit on this key across all models' });
      const shared = model && intent.who !== 'everyone' ? find('model', model) : null;
      if (shared) ruleRow(shared, { label: 'Everyone on ' + model, source: 'limit on ' + model + ' for every caller', shared: true, covers: ['tenant_model'] });
      if (rule.scope === 'api_key_model') {
        // A tenant's rule on this model counts the key's requests too — if the key is that tenant's,
        // which cannot be known here. Listed so that nothing is called dominant over a limit that
        // may refuse first; never a reason to warn, and never dominant itself.
        for (const r of enforced.filter(x => x.scope === 'tenant_model' && same(String(x.target || '').split('|')[1], model))) {
          const tenant = String(r.target).split('|')[0];
          ruleRow(r, { id: 'tenant_model:' + String(r.target).toLowerCase(), label: 'Tenant ' + tenant + ' on ' + model, conditional: true });
        }
      }

      const mine = this.rlLimitBand([rule], { scaled: model !== '' });
      const others = rows.filter(r => r.band.rated);
      for (const row of others) {
        row.dominant = mine.rated && !row.scheduled && !row.conditional && this.rlAtMost(row.band, mine, adaptive) &&
          others.every(o => o === row || (row.covers.includes(o.kind) && this.rlAtMost(row.band, o.band, adaptive)));
      }
      const redundantUnder = rule.scope === 'tenant' ? null
        : others.filter(r => !r.scheduled && !r.conditional && this.rlAtMost(r.band, mine, adaptive)).sort((x, y) => x.band.rpmMax - y.band.rpmMax)[0] || null;
      const mineLowest = mine.rated && others.length > 0 && others.every(o => this.rlAtMost(mine, o.band, adaptive));
      return { rows, mine, mineLowest, redundantUnder };
    },

    /** A rule as one sentence an operator can check: who, how much, where. Reads only the rule. */
    rlRuleSentence(rule) {
      const intent = this.rlIntentFor(rule.scope) || { who: '', where: '' };
      const parts = String(rule.target || '').split('|');
      const model = intent.where === 'one' ? parts[parts.length - 1] : '';
      const who = intent.who === 'key' ? 'API key ' + (this.rlTargetDisplay('api_key', parts[0]) || '?')
        : intent.who === 'tenant' ? 'tenant ' + (parts[0] || '?')
        : intent.who === 'everyone' ? 'everyone together'
        : intent.who === 'anonymous' ? 'each anonymous client address'
        : 'each client address';
      const where = intent.where === 'one' ? ' on ' + (model || '?') : intent.where === 'all' ? ' across all models' : '';
      const unit = intent.who === 'auth_failure' ? ' failed sign-ins/minute' : ' requests/minute';
      const rpm = Number(rule.rpm) || 0;
      const burst = Number(rule.burst) || 0;
      const streams = Number(rule.maxConcurrentStreams) || 0;
      const streamsText = streams > 0 ? ' At most ' + this.formatNum(streams) + ' streams open at once.' : '';
      if (rpm <= 0) {
        return 'No request-rate limit for ' + who + where + (rule.scope === 'tenant' ? ': the plan’s rate stays.' : ' from this rule.') + streamsText;
      }
      return 'Limit ' + who + ' to ' + this.formatNum(rpm) + unit + where + '.' +
        (burst > 0 ? ' Up to ' + this.formatNum(rpm + burst) + ' at once after a quiet spell (' + this.formatNum(rpm) + ' + ' + this.formatNum(burst) + ' burst).' : '') +
        streamsText + (rule.scope === 'tenant' ? ' This replaces the tenant’s plan rate.' : '');
    },

    rlBandText(band) {
      if (!band.rated) return 'no rate limit';
      const span = (lo, hi) => (hi === Infinity || lo === hi ? this.formatNum(lo) : this.formatNum(lo) + '–' + this.formatNum(hi));
      return span(band.rpmMin, band.rpmMax) + ' rpm · up to ' + span(band.capMin, band.capMax) + ' at once';
    },

    get rlNewRuleView() {
      const n = this.rlNewRule;
      const built = this.rlNewRuleBuild();
      const { rule, intent, hasSubject, hasModel, key, canon } = built;
      const isKeys = intent.who === 'key';
      const choice = (group, values, value, name, desc) => ({
        key: value, id: 'rl-' + group + '-' + value, name, desc,
        cls: 'rl-scope-card' + (n[group] === value ? ' sel' : ''),
        ariaChecked: n[group] === value ? 'true' : 'false',
        // Roving tabindex: one stop for the whole group, arrows move within it.
        tabIndex: n[group] === value ? '0' : '-1',
        select: () => (group === 'who' ? this.setRateLimitNewRuleWho(value) : this.setRateLimitNewRuleWhere(value)),
        onKey: (e) => this.rateLimitChoiceKeydown(e, group, values, value)
      });
      // Two separate groups: the everyday question has exactly three answers, and the protective
      // budgets, which answer neither question, are a form of their own with its own entry point.
      const isProtective = intent.where === '';
      const whos = ['key', 'tenant', 'everyone'];
      const budgets = ['anonymous', 'auth_failure'];
      const wheres = ['one', 'all'];

      // One list per field, each holding only what that field takes.
      const none = { items: [], has: false, more: '' };
      const subjectList = hasSubject ? this.rlSuggestList('subject', isKeys ? 'keys' : 'tenants') : none;
      const modelList = hasModel ? this.rlSuggestList('model', 'models') : none;
      const closed = this.rlCombo?.closed || {};
      const subjectOpen = subjectList.has && !closed.subject;
      const modelOpen = modelList.has && !closed.model;
      const combo = (field, open) => ({
        expanded: open ? 'true' : 'false',
        active: open && this.rlCombo?.field === field && this.rlCombo.index >= 0 ? this.rlComboOptionId(field, this.rlCombo.index) : ''
      });
      const liveError = !!n.tried && !!built.error;
      const modelText = hasModel ? String(n.model || '').trim() : '';
      const modelNote = !modelText ? ''
        : !canon.known ? 'Not a registered model. The rule is stored and applies as soon as a model with this id exists.'
        : canon.viaAlias ? '‘' + modelText + '’ is an alias of ' + canon.id + '. Limits are matched on the model’s own id, so the rule is saved against ' + canon.id + '.'
        : '';
      // The key: named once resolved, and said plainly when the text names no key at all.
      const keysReady = this.rlKeysState === 'ready';
      const keyNote = !isKeys || !String(n.subject || '').trim() ? ''
        : key ? 'Key: ' + this.rlKeyName(key) + (key.keyPrefix ? ' · ' + key.keyPrefix + '…' : '') + ' · id ' + key.id + (key.isRevoked || key.isArchived ? ' · revoked — it can no longer send requests, so this rule would limit nothing' : '')
        : this.rlKeysState === 'loading' ? ''
        : this.rlNewRuleKeyError();

      const limits = this.rlApplicableLimits(rule);
      const protective = this.rlProtectiveBaselineFor(rule.scope);
      const under = limits.redundantUnder;
      // A warning, never a block: an operator may have a reason to set a limit that binds no
      // tighter than what is already there, but they should not do it by accident.
      const looserWarning = under
        ? 'At ' + this.formatNum(rule.rpm) + ' rpm this rule’s rate is no tighter than the ' + under.source
          + ' (' + this.formatNum(under.band.rpmMax) + ' rpm), which these requests must pass as well — so this rate would never be the one that refuses a request.'
        : protective.rpm > 0 && rule.rpm > 0 && rule.rpm >= protective.rpm
          ? 'At ' + this.formatNum(rule.rpm) + ' rpm this rule is no tighter than the '
            + this.formatNum(protective.rpm) + ' rpm already in force, so it would not change what this scope allows.'
          : '';
      const limitRows = limits.rows.map(r => ({
        key: r.id, label: r.label,
        numbers: this.rlBandText(r.band) + (r.streams > 0 ? ' · ' + this.formatNum(r.streams) + ' streams' : ''),
        note: [r.conditional ? 'only if this key belongs to that tenant' : '', r.shared ? 'shared with every caller' : '', r.scheduled ? 'scheduled: its windows change these numbers' : '',
          r.id === 'tenant' && r.band.rpmMin !== r.band.rpmMax ? 'depends on the tenant’s plan or its own rule' : ''].filter(Boolean).join(' · '),
        cls: 'rl-limit', chip: r.dominant ? 'dominant' : '', hasChip: !!r.dominant
      }));
      const dominant = limits.rows.find(r => r.dominant);
      if (limitRows.length) {
        limitRows.unshift({ key: 'mine', label: 'This rule', numbers: this.rlBandText(limits.mine), note: '', cls: 'rl-limit mine', chip: '', hasChip: false });
      }
      return {
        eyebrow: isProtective ? 'New protective limit' : 'New rule',
        title: isProtective ? 'What should the gateway be protected from?' : 'What should be limited?',
        isProtective,
        notProtective: !isProtective,
        whoCards: [
          choice('who', whos, 'key', 'An API key', 'One credential.'),
          choice('who', whos, 'tenant', 'A tenant', 'All of one customer’s keys together.'),
          choice('who', whos, 'everyone', 'Everyone', 'Every caller together, sharing one budget.')
        ],
        protectiveCards: [
          choice('who', budgets, 'anonymous', 'Anonymous callers', 'Requests with no key, on public models. Counted per client address.'),
          choice('who', budgets, 'auth_failure', 'Failed sign-ins', 'Credential guessing. Counted per client address; rate only.')
        ],
        whereCards: [
          choice('where', wheres, 'one', 'One model', 'Only requests to that model are counted.'),
          choice('where', wheres, 'all', 'All models', intent.who === 'tenant' ? 'Replaces the tenant’s plan rate.' : 'One count across every model.')
        ],
        showSubject: hasSubject,
        showWhere: intent.where !== '',
        showModel: hasModel,
        subjectLabel: isKeys ? 'Which API key?' : 'Which tenant?',
        subjectPlaceholder: isKeys ? 'Key name, prefix or id' : 'Tenant id or slug, e.g. acme',
        subjectSuggestions: subjectList.items,
        hasSubjectSuggestions: subjectOpen,
        subjectMore: subjectList.more,
        modelSuggestions: modelList.items,
        hasModelSuggestions: modelOpen,
        modelMore: modelList.more,
        // Combobox state for the two pickers: what the field announces about its list.
        subjectCombo: combo('subject', subjectOpen),
        modelCombo: combo('model', modelOpen),
        // A message sits under the control it is about; only rule-wide ones use the shared alert.
        subjectError: liveError && built.errorField === 'subject' ? built.error : '',
        modelError: liveError && built.errorField === 'model' ? built.error : '',
        tierError: liveError && built.errorField === 'tier' ? built.error : '',
        generalError: liveError && !built.errorField ? built.error : '',
        subjectInvalid: liveError && built.errorField === 'subject' ? 'true' : 'false',
        modelInvalid: liveError && built.errorField === 'model' ? 'true' : 'false',
        tierInvalid: liveError && built.errorField === 'tier' ? 'true' : 'false',
        keysLoading: isKeys && this.rlKeysState === 'loading',
        keysFailed: isKeys && this.rlKeysState === 'failed',
        keysEmpty: isKeys && keysReady && !(this.keys || []).some(k => !k.isRevoked && !k.isArchived),
        keyNote,
        unknownNote: modelNote,
        preview: this.rlRuleSentence(rule),
        baselineText: protective.text,
        looserWarning,
        limitRows,
        hasLimitRows: limitRows.length > 0,
        limitsNote: dominant
          ? 'Every limit listed must admit a request. “' + dominant.label + '” counts all of these requests and is never looser than the others, so none of the others can be the one that refuses.'
          : limits.mineLowest
            ? 'Every limit listed must admit a request. This rule has the lowest rate and capacity of them, but the wider limits are shared with other traffic and can still refuse a request first.'
            : 'Every limit listed must admit a request, and no one of them decides the outcome alone: they count different traffic, their rates and bursts point different ways, or one of them varies.',
        // Once Create has been pressed the error is live, so it goes as soon as it is fixed.
        error: n.tried ? built.error : '',
        isTenant: rule.scope === 'tenant'
      };
    },

    get rlWindowClosed() { return !this.rlWindowOpen; },

    /**
     * Everything the help markup binds, in the selected language. Built flat and complete on purpose:
     * the CSP Alpine build throws on a bound key that is missing, so every field, scope and section
     * row carries every key the template reads, and a language missing an entry falls back to
     * English rather than to a blank element. `f.<key>` are the inline explainers, `open.<topic>`
     * the closures the "?" buttons call, `sections` the guide itself.
     */
    get rlHelpView() {
      const all = window.RateLimitHelp || null;
      const base = all?.en || { ui: {}, fields: {}, scopes: {}, sections: [] };
      const lang = all && all[this.rlHelpLang] ? this.rlHelpLang : 'en';
      const tree = all ? all[lang] : base;
      const meta = (all?.langs || []).find(l => l.id === lang) || { id: 'en', dir: 'ltr' };
      const other = (all?.langs || []).find(l => l.id !== lang) || { id: 'fa', dir: 'rtl' };
      const ui = Object.assign({}, base.ui, tree.ui);
      const exampleLabel = ui.example || 'Example:';

      const open = {};
      for (const id of this.rlHelpTopics()) open[id] = () => this.openRateLimitHelp(id);

      const f = {};
      for (const key of Object.keys(base.fields || {})) {
        const src = (tree.fields && tree.fields[key]) || base.fields[key];
        const topic = src.topic || 'overview';
        f[key] = {
          title: src.title || '',
          text: src.text || '',
          example: src.example || '',
          hasExample: !!src.example,
          exampleLabel,
          more: ui.more || '',
          open: open[topic] || open.overview
        };
      }

      const scopeId = this.rlNewRuleScope() || 'model';
      const sc = (tree.scopes && tree.scopes[scopeId]) || (base.scopes && base.scopes[scopeId]) || {};
      const scope = {
        title: ui.scopeHelpTitle || '',
        name: sc.name || '',
        what: sc.what || '',
        when: sc.when || '',
        example: sc.example || '',
        hasExample: !!sc.example,
        exampleLabel,
        open: open.scopes
      };

      const current = this.rlHelpTopic;
      const sections = (tree.sections || []).map((s, i) => ({
        key: s.id,
        domId: 'rl-help-' + s.id,
        title: s.title || '',
        intro: s.intro || '',
        hasIntro: !!s.intro,
        cls: 'rl-help-section' + (s.id === current ? ' current' : ''),
        tocCls: 'preset' + (s.id === current ? ' active' : ''),
        jump: () => this.rlHelpJump(s.id),
        items: (s.items || []).map((it, j) => ({
          key: s.id + ':' + j,
          term: it.term || '',
          hasTerm: !!it.term,
          text: it.text || '',
          example: it.example || '',
          hasExample: !!it.example,
          exampleLabel
        })),
        tip: s.tip || '',
        hasTip: !!s.tip,
        tipLabel: ui.tip || 'Tip:',
        index: i
      }));

      return {
        lang,
        dir: meta.dir || 'ltr',
        isFa: lang === 'fa',
        otherLangCode: other.id,
        ui,
        f,
        scope,
        open,
        sections,
        langRows: (all?.langs || []).map(l => ({
          key: l.id,
          label: l.label,
          name: l.name,
          cls: l.id === lang ? 'active' : '',
          pressed: l.id === lang ? 'true' : 'false',
          select: () => this.setRateLimitHelpLang(l.id)
        }))
      };
    },

    get rlTierView() {
      const t = this.rlTier;
      const isPlan = t.kind === 'plan';
      const tier = this.rlTierPayload(t);
      const capacity = tier.rpm + tier.burst;
      const who = isPlan ? 'Tenants on plan “' + (String(t.slug || '').trim() || '…') + '”' : 'Tenants without a matching plan';
      return {
        title: isPlan ? (t.isNew ? 'New plan tier' : 'Plan · ' + t.originalSlug) : 'Default tier',
        hint: isPlan
          ? 'Tenants whose plan slug matches get this tier instead of the default. A tenant rule still overrides it.'
          : 'The tier every tenant gets unless a plan or a tenant rule matches them.',
        isPlan,
        eyebrow: this.rateLimitsEditable ? 'Tier' : 'Tier · read-only',
        canRemove: isPlan && !t.isNew && this.rateLimitsEditable,
        applyLabel: t.isNew ? 'Add plan' : 'Done',
        summary: who + ' may take ' + this.formatNum(tier.rpm) + ' requests a minute' +
          (tier.burst > 0 ? ', up to ' + this.formatNum(capacity) + ' at once after a quiet spell (' + this.formatNum(tier.rpm) + ' + ' + this.formatNum(tier.burst) + ' burst)' : '') +
          (tier.maxConcurrentStreams > 0 ? ', with at most ' + this.formatNum(tier.maxConcurrentStreams) + ' streams open.' : ', with no cap on open streams.')
      };
    },

    // The admin page runs under a CSP-friendly Alpine build that evaluates property paths only, so
    // every formatted cell below is precomputed here rather than in the template.

    // ---- Activity: what the usage report can and cannot say ----
    //
    // Three time bases live in one payload, and mixing them up is how this card used to mislead:
    // totals and the by-subject sections are sums over the SELECTED WINDOW; `violations` are plain
    // counters, cumulative SINCE THE GATEWAY STARTED; the adaptive and store blocks are CURRENT.
    // Every label below names its basis. The by-subject rows are also subject-level — all traffic
    // from a tenant, key or model, whichever limit decided it — so they are never called a rule's.

    get rlActivityView() {
      const u = this.rateLimitUsage;
      const has = !!(u && u.totals);
      const failed = !!this.rateLimitUsageError;
      const unavailable = this.rateLimitUsageUnavailable && !has;
      const t = (has && u.totals) || {};
      const requests = t.requests ?? 0;
      const rejected = t.rejected ?? 0;
      const rate = t.rateRejected ?? 0;
      const concurrency = t.concurrencyRejected ?? 0;
      const share = requests > 0 ? rejected / requests : 0;
      const minutes = (has && u.windowMinutes) || Number(this.rateLimitUsageMinutes) || 60;
      const asOf = has && u.generatedUtc ? this.rlFmtShort(u.generatedUtc) : '';
      return {
        has,
        loading: this.rateLimitUsageLoading && !has,
        refreshing: this.rateLimitUsageLoading,
        // Stale: a report is on screen, and the refresh after it failed.
        stale: has && failed,
        staleText: has && failed
          ? 'Activity could not be refreshed, so these figures are from ' + (asOf || 'an earlier load') + '. ' + this.rateLimitUsageError
          : '',
        failedEmpty: !has && failed && !unavailable,
        failedText: !has && failed && !unavailable ? 'Activity could not be loaded. ' + this.rateLimitUsageError + ' Rules and tiers above are unaffected.' : '',
        unavailable,
        asOfText: asOf ? 'As of ' + asOf : '',
        windowText: 'last ' + minutes + ' min',
        windowHeading: 'In the selected window · last ' + minutes + ' min',
        decisions: this.formatNum(requests),
        admitted: this.formatNum(t.admitted ?? 0),
        // The split says which control is biting, and they call for opposite responses: a rate
        // refusal means the tier is too small for the traffic, a concurrency refusal means too many
        // streams are held open at once.
        refusedText: rejected === 0 ? '0' : this.formatNum(rejected) + ' (' + this.formatNum(rate) + ' rate · ' + this.formatNum(concurrency) + ' streams)',
        refusedCls: rejected > 0 ? 'mini-stat warn' : 'mini-stat',
        refusalRateText: requests > 0 ? (share * 100).toFixed(share > 0 && share < 0.1 ? 1 : 0) + '%' : '—',
        bucketsText: has ? this.formatNum(u.store?.requestPartitions ?? 0) + ' of ' + this.formatNum(u.store?.maxPartitions ?? 0) : '—',
        backedOffText: has ? this.formatNum(u.adaptive?.backedOffPartitions ?? 0) : '—',
        windowRows: [15, 60, 180].map((m) => ({
          key: m,
          label: m === 15 ? '15 min' : m === 60 ? '1 hour' : '3 hours',
          cls: Number(this.rateLimitUsageMinutes) === m ? 'active' : '',
          pressed: Number(this.rateLimitUsageMinutes) === m ? 'true' : 'false',
          select: () => this.setRateLimitUsageMinutes(m)
        }))
      };
    },

    /// "600" when nothing is adapting it, "420 of 600" when something is, "—" when no limit governs
    /// the row — presenting a reduced figure as the limit would be a lie an operator cannot see.
    rateLimitLimitText(row) {
      if (!row || !(row.effectiveRpm > 0)) return '—';
      return row.effectiveRpm === row.configuredRpm
        ? String(row.effectiveRpm)
        : row.effectiveRpm + ' of ' + row.configuredRpm;
    },

    /** What an operator calls a tenant the tracker only knows by id. Anonymous partitions say so. */
    rlTenantLabel(id) {
      const raw = String(id || '');
      if (raw.toLowerCase().startsWith('anon:')) return 'Anonymous · ' + raw.slice(5);
      const known = this.rlKnownTenants().find(t => String(t.id || '').toLowerCase() === raw.toLowerCase());
      return known?.slug || raw || '—';
    },

    /**
     * The tenant id a rule target means, or null when the console cannot tell. A tenant rule may be
     * written by slug, but the tracker keys everything by id — and the console only knows the
     * tenants the overview lists. Traffic seen under the exact text proves it is an id.
     */
    rlResolveTenantId(target) {
      const q = String(target || '').trim().toLowerCase();
      if (!q) return null;
      const known = this.rlKnownTenants();
      const byId = known.find(t => String(t.id || '').toLowerCase() === q);
      if (byId) return byId.id;
      const bySlug = known.find(t => String(t.slug || '').toLowerCase() === q);
      if (bySlug) return bySlug.id || null;
      const seen = (this.rateLimitUsage?.byTenant || []).some(r => String(r.key || '').toLowerCase() === q);
      return seen ? String(target).trim() : null;
    },

    /** The tracker's key for a rule's bucket, or null when it cannot be derived. */
    rlUsageKeyFor(scope, target) {
      if (scope === 'global') return '*';
      if (scope === 'tenant') return this.rlResolveTenantId(target);
      if (scope === 'tenant_model') {
        const [tenant, ...model] = String(target || '').split('|');
        const id = this.rlResolveTenantId(tenant);
        return id ? id + '|' + model.join('|') : null;
      }
      return String(target || '');
    },

    /**
     * Requests refused by one rule's bucket since the gateway started — the only per-limit figure
     * the report carries. The tracker counts a refusal against the scope and partition that
     * refused, which for model, key and pair rules IS the rule's bucket. For a tenant it is the
     * tenant's whole allowance (plan tier composed with the rule), hence `qualified`. States other
     * than 'ok' render as "—": an unknown is not a zero.
     */
    rlRefusalsFor(scope, target) {
      const violations = this.rateLimitUsage?.violations;
      if (!Array.isArray(violations)) return { state: 'unavailable', hits: 0, qualified: false };
      // Metered by their own middleware, which reports to the metrics endpoint only.
      if (scope === 'anonymous' || scope === 'auth_failure') return { state: 'untracked', hits: 0, qualified: false };
      const key = this.rlUsageKeyFor(scope, target);
      if (!key) return { state: 'unresolved', hits: 0, qualified: false };
      const q = key.toLowerCase();
      const rows = violations.filter(v => v.scope === scope && String(v.key || '').toLowerCase() === q);
      // A full page of rows may have cut this one off; a short page is the whole list.
      if (!rows.length && violations.length >= this.rlUsageTake()) return { state: 'truncated', hits: 0, qualified: false };
      return {
        state: 'ok',
        hits: rows.reduce((n, v) => n + (Number(v.hits) || 0), 0),
        qualified: scope === 'tenant'
      };
    },

    /** The cell and tooltip for rlRefusalsFor — one wording for the list and the drawer. */
    rlRefusalsView(scope, target) {
      const r = this.rlRefusalsFor(scope, target);
      const title = {
        ok: r.qualified
          ? 'Refused by this tenant’s allowance — its plan tier combined with this rule — since the gateway last started.'
          : 'Refused by this limit since the gateway last started. When several limits apply, only the first to refuse is counted.',
        unavailable: 'Activity is unavailable, so refusals are unknown.',
        untracked: 'Protective limits are not part of the activity report; they are counted in the gateway metrics only.',
        unresolved: 'Refusals are counted by tenant id. This rule is written by slug and the console cannot match it to an id.',
        truncated: 'Not among the top ' + this.rlUsageTake() + ' limits by refusals.'
      }[r.state] || '';
      return {
        state: r.state,
        known: r.state === 'ok',
        hits: r.hits,
        text: r.state === 'ok' ? this.formatNum(r.hits) : '—',
        qualified: r.qualified,
        title
      };
    },

    /**
     * Subject-level traffic for a rule: every decision about that tenant, key or model in the
     * window, whichever limit made it. Null row with a `state` that says exactly why there is none.
     */
    rlSubjectTrafficFor(scope, target) {
      const u = this.rateLimitUsage;
      if (!u || !u.totals) return { state: 'unavailable', row: null };
      if (scope === 'global') {
        const t = u.totals;
        const minutes = u.windowMinutes || 60;
        return { state: 'ok', row: { requests: t.requests ?? 0, rejected: t.rejected ?? 0, requestsPerMinute: (t.requests ?? 0) / minutes } };
      }
      const section = scope === 'model' ? u.byModel
        : scope === 'api_key' ? u.byApiKey
        : scope === 'tenant' ? u.byTenant
        : scope === 'tenant_model' ? u.byTenantModel
        : null;
      // Key-on-model and the protective scopes have no section of their own in the report.
      if (!section) return { state: 'nosection', row: null };
      const key = this.rlUsageKeyFor(scope, target);
      if (!key) return { state: 'unresolved', row: null };
      const row = section.find(x => String(x.key || '').toLowerCase() === key.toLowerCase());
      if (row) return { state: 'ok', row };
      if (section.length >= this.rlUsageTake()) return { state: 'truncated', row: null };
      return { state: (u.totals.requests ?? 0) === 0 ? 'quiet' : 'absent', row: null };
    },

    get rlUsageTabRows() {
      const tabs = [
        { id: 'tenant', label: 'Tenant' },
        { id: 'key', label: 'API key' },
        { id: 'model', label: 'Model' },
        { id: 'tenantModel', label: 'Tenant × model' }
      ];
      return tabs.map(t => ({
        key: t.id, label: t.label,
        cls: this.rlUsageTab === t.id ? 'active' : '',
        pressed: this.rlUsageTab === t.id ? 'true' : 'false',
        select: () => this.setRateLimitUsageTab(t.id)
      }));
    },

    get rlUsageSubjectView() {
      const u = this.rateLimitUsage || {};
      const tab = this.rlUsageTab;
      const section = tab === 'key' ? u.byApiKey : tab === 'model' ? u.byModel : tab === 'tenantModel' ? u.byTenantModel : u.byTenant;
      const list = Array.isArray(section) ? section : [];
      const rows = list.map((row) => {
        let name;
        let sub = '';
        if (tab === 'key') {
          const k = this.rlFindKey(row.apiKeyId || row.key);
          name = k ? this.rlKeyName(k) : String(row.apiKeyId || row.key || '—');
          sub = k ? String(k.id || '') : '';
        } else if (tab === 'model') {
          name = row.modelId || row.key || '—';
        } else {
          name = this.rlTenantLabel(row.tenantId || String(row.key || '').split('|')[0]);
          if (tab === 'tenantModel') name += ' · ' + (row.modelId || '—');
          sub = name.startsWith(String(row.tenantId || '')) ? '' : String(row.tenantId || '');
        }
        return {
          key: String(row.key),
          name, sub, hasSub: !!sub,
          title: String(row.key || ''),
          decisions: this.formatNum(row.requests ?? 0),
          refused: this.formatNum(row.rejected ?? 0),
          refusedCls: (row.rejected ?? 0) > 0 ? 'num rl-refused-some' : 'num',
          rpmText: (row.requestsPerMinute ?? 0).toFixed(1),
          limitText: this.rateLimitLimitText(row)
        };
      });
      const take = this.rlUsageTake();
      return {
        rows,
        has: rows.length > 0,
        empty: !!this.rateLimitUsage?.totals && rows.length === 0,
        emptyText: (u.totals?.requests ?? 0) === 0
          ? 'No inference requests were decided in this window.'
          : 'Nothing recorded under this heading in this window.',
        subjectHeading: tab === 'key' ? 'API key' : tab === 'model' ? 'Model' : tab === 'tenantModel' ? 'Tenant · model' : 'Tenant',
        // Refusals made before the body is read carry no model, so the model-bearing sections
        // cannot show them. Said here rather than left for an operator to reconcile by hand.
        note: tab === 'tenantModel' || tab === 'model'
          ? 'Requests refused before the model was read — by the gateway, tenant or key limits — are counted under Tenant and API key, not here.'
          : '',
        hasNote: tab === 'tenantModel' || tab === 'model',
        truncated: rows.length >= take ? 'Showing the ' + take + ' busiest.' : '',
        caption: 'Traffic by ' + (tab === 'key' ? 'API key' : tab === 'model' ? 'model' : tab === 'tenantModel' ? 'tenant and model' : 'tenant') + ', selected window'
      };
    },

    /**
     * The rule drawer's two activity readings, kept apart because they answer different questions:
     * how busy the rule's subject is (windowed, subject-level), and how often this limit itself
     * refused (cumulative). Neither is ever described as "traffic for this rule".
     */
    rlRuleUsageView(scope, target) {
      const info = this.rlScopeInfo(scope);
      const traffic = this.rlSubjectTrafficFor(scope, target);
      const refusals = this.rlRefusalsView(scope, target);
      const minutes = this.rateLimitUsage?.windowMinutes || Number(this.rateLimitUsageMinutes) || 60;
      const display = info.singleton ? info.name : this.rlTargetDisplay(scope, target);
      const who = scope === 'global' ? 'the whole gateway' : display;
      const kind = scope === 'model' ? 'model' : scope === 'api_key' ? 'key' : scope === 'global' ? 'gateway' : 'tenant';
      const row = traffic.row;
      const trafficText = {
        ok: row ? this.formatNum(row.requests ?? 0) + ' decisions · ' + this.formatNum(row.rejected ?? 0) + ' refused by any limit · ' + (row.requestsPerMinute ?? 0).toFixed(1) + ' avg req/min' : '',
        quiet: 'No decisions were recorded in this window.',
        absent: 'No decisions recorded from ' + who + ' in this window.',
        truncated: who + ' is not among the ' + this.rlUsageTake() + ' busiest subjects in this window, so its traffic is not in the report.',
        unresolved: 'Traffic is recorded by tenant id. This rule is written by slug and the console cannot match it to an id.',
        unavailable: 'Activity is unavailable.',
        nosection: ''
      }[traffic.state] || '';
      return {
        // No section in the report for this scope: say nothing rather than claim silence.
        showTraffic: traffic.state !== 'nosection',
        noSection: traffic.state === 'nosection',
        noSectionText: 'The activity report has no per-subject figures for this kind of limit.',
        trafficTitle: 'Traffic from ' + who,
        trafficWindow: '· last ' + minutes + ' min · subject-level',
        trafficText,
        trafficNote: scope === 'global'
          ? 'Every inference decision in the window, whichever limit made it.'
          : 'Every request from this ' + kind + ', whichever limit decided it — not only the ones this rule refused.',
        refusedText: refusals.text,
        refusedKnown: refusals.known,
        refusedNote: refusals.title,
        stale: !!this.rateLimitUsageError && !!this.rateLimitUsage?.totals,
        staleText: 'Activity could not be refreshed; these figures may be out of date.'
      };
    },

    /** A limit the tracker names by scope and partition, in the words the rest of the page uses. */
    rlLimitLabel(scope, key) {
      const raw = String(key || '');
      const [first, ...rest] = raw.split('|');
      const model = rest.join('|');
      const keyName = (id) => { const k = this.rlFindKey(id); return k ? this.rlKeyName(k) : id; };
      switch (scope) {
        case 'global': return { name: 'Whole gateway', sub: 'every inference request' };
        case 'tenant': return raw.toLowerCase().startsWith('anon:')
          ? { name: 'Anonymous caller ' + raw.slice(5), sub: 'anonymous allowance' }
          : { name: this.rlTenantLabel(raw), sub: 'tenant allowance · plan tier or tenant rule' };
        case 'api_key': return { name: keyName(raw), sub: 'API key · all models' };
        case 'model': return { name: raw, sub: 'everyone on this model' };
        case 'tenant_model': return { name: this.rlTenantLabel(first) + ' · ' + model, sub: 'tenant on one model' };
        case 'api_key_model': return { name: keyName(first) + ' · ' + model, sub: 'API key on one model' };
        default: return { name: raw || scope, sub: scope };
      }
    },

    /** The draft rule whose bucket a violation row names, if there is one. */
    rlRuleForUsageKey(scope, key) {
      const q = String(key || '').toLowerCase();
      return (this.rlDraft?.rules || []).find(r =>
        r.scope === scope && String(this.rlUsageKeyFor(r.scope, r.target) || '').toLowerCase() === q) || null;
    },

    get rateLimitViolationRows() {
      const list = this.rateLimitUsage?.violations;
      return (Array.isArray(list) ? list : []).map((v) => {
        const label = this.rlLimitLabel(v.scope, v.key);
        const rule = this.rlRuleForUsageKey(v.scope, v.key);
        const identity = rule ? this.rlIdentity(rule.scope, rule.target) : '';
        return {
          key: v.scope + '|' + v.key + '|' + v.control,
          name: label.name,
          sub: label.sub,
          title: String(v.key || ''),
          control: v.control === 'concurrency' ? 'streams' : 'rate',
          hits: this.formatNum(v.hits ?? 0),
          hasRule: !!rule,
          noRule: !rule,
          openLabel: 'Open rule ' + label.name,
          open: () => { if (identity) this.openRateLimitRule(identity); }
        };
      });
    },

    get rateLimitHasViolations() { return this.rateLimitViolationRows.length > 0; },
    get rateLimitNoViolations() { return !!this.rateLimitUsage?.totals && this.rateLimitViolationRows.length === 0; },
    get rateLimitViolationsTruncated() {
      const n = this.rateLimitViolationRows.length;
      return n >= this.rlUsageTake() ? 'Showing the top ' + n + ' limits by refusals.' : '';
    },

    get rateLimitAdaptiveRows() {
      return (this.rateLimitUsage?.adaptive?.models || []).map((m) => ({
        key: m.modelId,
        modelId: m.modelId,
        factorText: Math.round((m.factor ?? 1) * 100) + '% of configured',
        saturationText: Math.round((m.saturation ?? 0) * 100) + '%',
        reason: m.reason
      }));
    },

    /// Only worth showing while something is actually adapted; a table of 100% rows is noise.
    get rateLimitAdaptiveActive() {
      return !!this.rateLimitUsage?.adaptive?.enabled && this.rateLimitAdaptiveRows.length > 0;
    },

    get corsLoading() {
      return this.corsOrigins === null && !this.corsLoadError && this.isLoading('settings');
    },
    get corsLoaded() { return this.corsOrigins !== null; },

    get corsRows() {
      return (this.corsOrigins || []).map((_, index) => ({
        key: index,
        value: this.bindPath('corsOrigins.' + index),
        remove: () => this.removeCorsOriginRow(index)
      }));
    },

    // ---- model drawer ----

    applyModelTemplateFromEvent(event) {
      const select = event?.target;
      if (!select) return;
      this.applyModelTemplate(select.value);
      select.value = '';
    },

    get editingModel() { return !!this.editModel._existing; },
    get modelDrawerTitle() { return this.editModel._existing ? 'Edit model' : 'Add model'; },
    get modelSaveLabel() { return this.editModel._existing ? 'Save changes' : 'Add model'; },
    get modelApiKeyLabel() {
      return this.editModel._existing
        ? 'New API key (leave blank to keep current)'
        : 'API key (optional)';
    },
    get showStoredCredentialHint() {
      return !!this.editModel._existing && !!this.editModel.hasUpstreamCredential &&
        !(this.editModel.apiKey || '').trim();
    },
    get showClearCredential() {
      return !!this.editModel._existing && !!this.editModel.hasUpstreamCredential;
    },
    get editModelTypeUnknown() { return this.isUnknownModelType(this.editModel.modelType); },
    get editModelTypeUnknownLabel() { return this.editModel.modelType + ' (unrecognised)'; },
    get modelTypeOptions() { return this.modelTypes(); },
    get modelTestEndpointHint() {
      const entry = this.modelTypes().find(t => t.value === this.editModel.modelType);
      return entry?.testEndpoint || 'no automated test available';
    },

    // ---- model test dialog ----

    rerunModelTest() { return this.testModel(this.modelTestDialog?.modelId); },

    get modelTest() {
      const dialog = this.modelTestDialog;
      const result = dialog?.result;
      const unsupported = result?.supported === false;
      const failed = !!result && !result.ok;
      const model = (this.models || []).find(m => m.id === dialog?.modelId);
      return {
        open: !!dialog,
        modelId: dialog?.modelId ?? '',
        typeLabel: this.modelTypeLabel(this.resolveModelType(model)),
        hint: this.modelTestHint(dialog?.modelId),
        loading: !!dialog?.loading,
        showResult: !!result && !dialog?.loading,
        resultClass: result?.ok ? 'ok' : (unsupported ? 'warn' : 'bad'),
        resultIcon: this.icon(result?.ok ? 'check-circle' : (unsupported ? 'alert-triangle' : 'x-circle')),
        resultText: result?.ok ? 'Success' : (unsupported ? 'Not available' : 'Failed'),
        endpoint: result?.endpoint ?? '',
        hasEndpoint: !!result?.endpoint,
        latencyMs: result?.latencyMs ?? '',
        hasLatency: result?.latencyMs != null && !unsupported,
        statusCode: result?.statusCode ?? '',
        hasStatusCode: !!result?.statusCode,
        content: result?.content ?? '',
        hasContent: !!result?.content,
        detail: result?.detail ?? '',
        showDetail: !!result?.detail && failed,
        resultHint: result?.hint ?? '',
        showHint: !!result?.hint && failed,
        showLogNote: failed && !unsupported,
        error: dialog?.error ?? '',
        showError: !!dialog?.error && !dialog?.loading,
        rerunDisabled: !!dialog?.loading || !dialog?.modelId
      };
    },

    // ---- confirm dialogs ----

    get confirmView() {
      const d = this.confirmDialog;
      return {
        open: !!d,
        title: d?.title ?? '',
        message: d?.message ?? '',
        confirmLabel: d?.confirmLabel || 'Confirm',
        confirmClass: d?.danger ? 'danger' : '',
        labelledBy: d ? 'confirm-title' : null
      };
    }
  };
}

// Registered as an Alpine component rather than left as a global: the CSP-friendly build resolves
// x-data="adminApp" through Alpine's data registry, and it cannot evaluate the call x-data="adminApp()".
document.addEventListener('alpine:init', () => {
  Alpine.data('adminApp', adminApp);

  // `indeterminate` is a DOM property with no HTML attribute behind it, and x-bind in Alpine 3.14.9
  // implements only the `.camel` modifier — `:indeterminate.prop` writes a dead attribute, leaving
  // the select-all checkbox stuck between "all" and "none". This writes the property itself.
  Alpine.directive('indeterminate', (el, { expression }, { effect, evaluateLater }) => {
    const read = evaluateLater(expression);
    effect(() => read(value => { el.indeterminate = !!value; }));
  });
});
