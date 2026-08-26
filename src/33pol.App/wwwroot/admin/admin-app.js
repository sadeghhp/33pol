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
    logsPageSize: 200,
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
    rateLimitRuleRows: [],
    rateLimitUsage: null,
    rateLimitUsageError: '',
    rateLimitUsageMinutes: 60,
    rateLimitPlanRows: [],
    rateLimitFieldError: '',
    rateLimitsLoadError: '',
    corsOrigins: null,
    corsFieldError: '',
    corsLoadError: '',
    healthLive: null,
    healthReady: null,
    confirmDialog: null,
    _confirmReturnFocus: null,
    revokeConfirmId: null,
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
      window.addEventListener('beforeunload', () => this.stopLive());
      this._tickTimer = setInterval(() => {
        if (document.hidden) return;
        // Only the Overview reads the clock; ticking it elsewhere would re-render for nothing.
        if (this.tab === 'dashboard' && this.apiKey) {
          this._nowTick = Date.now();
          this.checkLiveStale();
          // The wallboard's staleness and severity switches live on <html>, out of reach of any
          // binding inside the panel, so the clock is what keeps them honest.
          if (this.wallboard) this.applyWallboard();
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
        this.applyErrorHashParams(resolved.params);
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
        this.applyErrorHashParams(resolved.params);
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
      if (sub === 'limits' && !this.rateLimitUsage) {
        void this.loadRateLimitUsage();
      }
    },

    icon(name) {
      return window.AdminIcons ? window.AdminIcons(name) : '';
    },

    handleCatch(e, options) {
      if (options?.localOnly || (e.section && !e.global)) return;
      if (e.global !== false) {
        const isAuth =
          e.title === 'Authentication failed' ||
          /admin API key/i.test(e.message || '');
        if (isAuth && this.connectionStatus === 'fail') return;
        this.store.setGlobalError(e.title || 'Error', e.message || String(e), e.detail);
      }
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
        if (this.connectionStatus === 'fail') return;
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
          this.loadCors()
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
        isRevoked: !!(k.isRevoked ?? k.revokedAt)
      }));
    },

    filteredKeys() {
      const list = this.keys || [];
      let filtered = list;
      if (this.keysFilter === 'active') filtered = list.filter(k => !k.isRevoked);
      else if (this.keysFilter === 'revoked') filtered = list.filter(k => k.isRevoked);
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
      return this.filteredKeys().filter(k => !k.isRevoked);
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
      const activeIds = new Set((this.keys || []).filter(k => !k.isRevoked).map(k => k.id));
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
    _optionsFrom(values, emptyLabel) {
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
      this._confirmReturnFocus = returnFocusEl || document.activeElement;
      this.confirmDialog = dialog;
      this.$nextTick(() => {
        const btn = this.$refs.confirmPrimary;
        if (btn) btn.focus();
      });
    },

    cancelConfirm() {
      this.confirmDialog = null;
      const el = this._confirmReturnFocus;
      this._confirmReturnFocus = null;
      if (el && el.focus) el.focus();
    },

    async confirmOk() {
      const d = this.confirmDialog;
      this.confirmDialog = null;
      if (d?.onConfirm) await d.onConfirm();
      const el = this._confirmReturnFocus;
      this._confirmReturnFocus = null;
      if (el && el.focus) el.focus();
    },

    onModalKeydown(e) {
      if (e.key === 'Escape') {
        if (this.confirmDialog) this.cancelConfirm();
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
        await this.apiJson('/admin/api/keys/' + key.id, {
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
        const body = await this.apiJson('/admin/api/keys/' + key.id + '/model-grants');
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
        await this.apiJson('/admin/api/keys/' + key.id + '/model-grants', {
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

    async loadSummary(silent) {
      if (silent) {
        try {
          this.summary = await this.apiJson('/admin/api/summary');
          this.summaryUpdatedAt = Date.now();
          this.recordVitals();
          this.pollFailCount = 0;
          this.overviewStale = false;
        } catch {
          this.pollFailCount++;
          if (this.pollFailCount >= 2) this.overviewStale = true;
        }
        return;
      }
      await this.runApi('overview', 'Loading summary…', async () => {
        this.summary = await this.apiJson('/admin/api/summary');
        this.summaryUpdatedAt = Date.now();
        this.recordVitals();
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
          ...tier(r)
        }))
      };
    },

    applyRateLimitsData(data) {
      const normalized = this.normalizeRateLimitsPayload(data);
      if (!normalized) {
        this.rateLimits = null;
        return;
      }
      this.rateLimits = normalized;
      this.rateLimitPlanRows = Object.entries(normalized.plans).map(([slug, t]) => ({
        slug,
        rpm: t.rpm,
        burst: t.burst,
        maxConcurrentStreams: t.maxConcurrentStreams
      }));
      this.rateLimitRuleRows = normalized.rules.map((r) => ({ ...r }));
      this.rateLimitFieldError = '';
      this.rateLimitsLoadError = '';
    },

    async fetchRateLimits() {
      const data = await this.apiJson('/admin/api/rate-limits');
      this.applyRateLimitsData(data);
    },

    async loadRateLimits() {
      this.rateLimitsLoadError = '';
      try {
        await this.fetchRateLimits();
      } catch (e) {
        this.rateLimits = null;
        this.rateLimitPlanRows = [];
        this.rateLimitRuleRows = [];
        if (String(e.title || '').startsWith('404') || e.message?.includes('404') || /not found/i.test(e.message || '')) {
          this.rateLimitsLoadError =
            'Rate limit API is not available on this gateway (rebuild/restart the server with the latest image).';
        } else if (e.title === 'Authentication failed' || e.message?.includes('401')) {
          this.rateLimitsLoadError = 'Connect with an Admin API key to load rate limits.';
        } else {
          this.rateLimitsLoadError = e.message || 'Could not load rate limits.';
        }
      }
    },

    addRateLimitPlanRow() {
      this.rateLimitPlanRows = [
        ...this.rateLimitPlanRows,
        { slug: '', rpm: 60, burst: 10, maxConcurrentStreams: 5 }
      ];
    },

    removeRateLimitPlanRow(index) {
      this.rateLimitPlanRows = this.rateLimitPlanRows.filter((_, i) => i !== index);
    },

    addRateLimitRuleRow() {
      this.rateLimitRuleRows = [
        ...this.rateLimitRuleRows,
        { scope: 'model', target: '', rpm: 60, burst: 10, maxConcurrentStreams: 0 }
      ];
    },

    removeRateLimitRuleRow(index) {
      this.rateLimitRuleRows = this.rateLimitRuleRows.filter((_, i) => i !== index);
    },

    buildRateLimitsPayload() {
      const plans = {};
      for (const row of this.rateLimitPlanRows) {
        const slug = (row.slug || '').trim();
        if (!slug) continue;
        plans[slug] = {
          rpm: Number(row.rpm),
          burst: Number(row.burst),
          maxConcurrentStreams: Number(row.maxConcurrentStreams)
        };
      }
      const d = this.rateLimits?.default || {};

      // Rows with no target are dropped rather than sent: an empty target is the half-typed state of
      // a row the operator has not finished, and the server would reject the whole save for it.
      const rules = this.rateLimitRuleRows
        .filter((row) => String(row.target ?? '').trim() !== '')
        .map((row) => ({
          scope: String(row.scope ?? '').trim(),
          target: String(row.target ?? '').trim(),
          rpm: Number(row.rpm) || 0,
          burst: Number(row.burst) || 0,
          maxConcurrentStreams: Number(row.maxConcurrentStreams) || 0
        }));

      return {
        enabled: this.rateLimits?.enabled !== false,
        adaptiveEnabled: this.rateLimits?.adaptiveEnabled === true,
        default: {
          rpm: Number(d.rpm),
          burst: Number(d.burst),
          maxConcurrentStreams: Number(d.maxConcurrentStreams)
        },
        plans,
        rules
      };
    },

    async loadRateLimitUsage() {
      this.rateLimitUsageError = '';
      try {
        const minutes = Number(this.rateLimitUsageMinutes) || 60;
        this.rateLimitUsage = await this.apiJson(
          '/admin/api/rate-limits/usage?minutes=' + minutes + '&take=25'
        );
      } catch (e) {
        this.rateLimitUsage = null;
        this.rateLimitUsageError = e.message || 'Could not load the rate-limit usage report.';
      }
    },

    async saveRateLimits() {
      await this.runApi('settings', 'Saving rate limits…', async () => {
        this.rateLimitFieldError = '';
        try {
          const body = await this.apiJson('/admin/api/rate-limits', {
            method: 'PUT',
            body: JSON.stringify(this.buildRateLimitsPayload())
          });
          this.toast(body?.message || 'Rate limits saved.');
          await this.loadRateLimits();
        } catch (e) {
          this.rateLimitFieldError = e.message || 'Failed to save rate limits.';
          throw e;
        }
      }, { localOnly: true });
    },

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
      const fetchRequests = async () => {
        const rows = (await this.apiJson('/admin/api/requests?limit=' + this.requestsFeedLimit)) ?? [];
        if (this.requestsPaused) this._pausedFrame = rows;
        else this.requests = rows;
      };
      if (quiet) {
        try { await fetchRequests(); } catch { /* a transient blip is already reported by the summary poll */ }
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
      const list = (await this.apiJson('/admin/api/keys?includeUsageSummary=true')) ?? [];
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

    async revokeKeyConfirmed() {
      const id = this.revokeConfirmId;
      if (!id) return;
      this.revokeConfirmId = null;
      await this.runApi('keys', 'Revoking…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + id + '/revoke', { method: 'POST' }, this.editModelUrl());
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
        rateLimits: {
          enabled: b('rateLimits.enabled'),
          adaptiveEnabled: b('rateLimits.adaptiveEnabled'),
          usageMinutes: b('rateLimitUsageMinutes'),
          default: {
            rpm: b('rateLimits.default.rpm'),
            burst: b('rateLimits.default.burst'),
            maxConcurrentStreams: b('rateLimits.default.maxConcurrentStreams')
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

    get settingsTabs() {
      const defs = [
        ['runtime', 'Runtime'],
        ['limits', 'Rate limits'],
        ['cors', 'CORS'],
        ['access', 'Model access'],
        ['observability', 'Observability']
      ];
      return defs.map(([id, label]) => ({
        key: id,
        label,
        cls: this.settingsSubTab === id ? 'active' : '',
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
          if (p('filter')) this.keysFilter = p('filter');
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
    get finopsCostCenterBars() {
      return this._costBars(this.overviewFinops?.topCostCentersMonthToDate, key => this.openLink({ tab: 'usage', params: { costCenter: key } }));
    },
    get hasFinopsCostCenterBars() { return this.finopsCostCenterBars.length > 0; },
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
    get updatedLineText() {
      const age = this.summaryAgeText();
      if (!age) return '';
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
          statusClass: k.isRevoked ? 'fail' : 'ok',
          statusText: k.isRevoked ? 'Revoked' : 'Active',
          active: !k.isRevoked,
          revoked: !!k.isRevoked,
          selected: this.isKeySelected(k.id),
          canGrant: k.role !== 'Admin',
          selectLabel: 'Select key ' + k.keyPrefix,
          onSelect: event => this.toggleKeySelection(k.id, !!event?.target?.checked),
          edit: () => this.openKeyEditDrawer(k),
          access: () => this.openKeyAccess(k),
          usage: () => this.viewKeyUsage(k),
          revoke: () => this.confirmRevoke(k.id)
        };
      });
    },

    get hasKeyRows() { return this.keyRows.length > 0; },
    get keysEmpty() { return !this.isLoading('keys') && this.keyRows.length === 0; },

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
      return !this.rateLimits && !this.rateLimitsLoadError && this.isLoading('settings');
    },
    get rateLimitsDisabled() { return !!this.rateLimits && !this.rateLimits.enabled; },

    get rateLimitPlanViewRows() {
      return this.rateLimitPlanRows.map((_, index) => ({
        key: index,
        slug: this.bindPath('rateLimitPlanRows.' + index + '.slug'),
        rpm: this.bindPath('rateLimitPlanRows.' + index + '.rpm'),
        burst: this.bindPath('rateLimitPlanRows.' + index + '.burst'),
        maxConcurrentStreams: this.bindPath('rateLimitPlanRows.' + index + '.maxConcurrentStreams'),
        remove: () => this.removeRateLimitPlanRow(index)
      }));
    },

    get rateLimitRuleViewRows() {
      return this.rateLimitRuleRows.map((_, index) => ({
        key: index,
        scope: this.bindPath('rateLimitRuleRows.' + index + '.scope'),
        target: this.bindPath('rateLimitRuleRows.' + index + '.target'),
        rpm: this.bindPath('rateLimitRuleRows.' + index + '.rpm'),
        burst: this.bindPath('rateLimitRuleRows.' + index + '.burst'),
        maxConcurrentStreams: this.bindPath('rateLimitRuleRows.' + index + '.maxConcurrentStreams'),
        remove: () => this.removeRateLimitRuleRow(index)
      }));
    },

    // The admin page runs under a CSP-friendly Alpine build that evaluates property paths only, so
    // every formatted cell below is precomputed here rather than in the template.

    get rateLimitUsageTotals() {
      const u = this.rateLimitUsage;
      if (!u) return { requests: 0, admitted: 0, rejected: 0, refusedText: '0', partitionsText: '—' };
      const rejected = u.totals?.rejected ?? 0;
      const rate = u.totals?.rateRejected ?? 0;
      const concurrency = u.totals?.concurrencyRejected ?? 0;
      return {
        requests: u.totals?.requests ?? 0,
        admitted: u.totals?.admitted ?? 0,
        rejected: rejected,
        // The split says which control is biting, and they call for opposite responses: a rate
        // refusal means the tier is too small for the traffic, a concurrency refusal means too many
        // streams are held open at once.
        refusedText: rejected === 0 ? '0' : rejected + ' (' + rate + ' rate / ' + concurrency + ' streams)',
        partitionsText: (u.store?.requestPartitions ?? 0) + ' / ' + (u.store?.maxPartitions ?? 0)
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

    get rateLimitUsageTenantModelRows() {
      return (this.rateLimitUsage?.byTenantModel || []).map((row) => ({
        key: row.key,
        tenant: row.tenantId || '—',
        model: row.modelId || '—',
        requests: row.requests,
        rejected: row.rejected,
        rpmText: (row.requestsPerMinute ?? 0).toFixed(1),
        limitText: this.rateLimitLimitText(row)
      }));
    },

    get rateLimitViolationRows() {
      return (this.rateLimitUsage?.violations || []).map((v) => ({
        key: v.scope + '|' + v.key + '|' + v.control,
        scope: v.scope,
        target: v.key,
        control: v.control,
        hits: v.hits
      }));
    },

    get rateLimitHasViolations() { return this.rateLimitViolationRows.length > 0; },
    get rateLimitNoViolations() { return !!this.rateLimitUsage && this.rateLimitViolationRows.length === 0; },

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
