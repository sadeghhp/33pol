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

  /** Time-range presets for the Errors tab, in hours. `all` drops the lower bound entirely. */
  const ERROR_RANGES = [
    ['1h', 'Last hour', 1],
    ['24h', 'Last 24h', 24],
    ['7d', 'Last 7 days', 168],
    ['30d', 'Last 30 days', 720],
    ['all', 'All time', 0]
  ];
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
    usage: null,
    usageEvents: null,
    usageFrom: '',
    usageTo: '',
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
      requests: { key: 'timestampUtc', dir: -1 }
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
      window.addEventListener('hashchange', () => this.applyHashTab());
      document.addEventListener('visibilitychange', () => { this.syncPoll(); this.syncLive(); });
      window.addEventListener('beforeunload', () => this.stopLive());
      this._tickTimer = setInterval(() => {
        if (document.hidden) return;
        // Only the Overview reads the clock; ticking it elsewhere would re-render for nothing.
        if (this.tab === 'dashboard' && this.apiKey) this._nowTick = Date.now();
      }, 500);
      if (this.apiKey) {
        this.store.startConnectionWatch(() => this.editModelUrl());
        this.saveKey();
      }
    },

    initUsageDates() {
      const to = new Date();
      const from = new Date();
      from.setDate(from.getDate() - 30);
      this.usageTo = to.toISOString().slice(0, 10);
      this.usageFrom = from.toISOString().slice(0, 10);
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
      const resolved = this.resolveHash(location.hash);
      if (resolved) {
        this.applyErrorHashParams(resolved.params);
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

    applyTab(name, routingSubTab, updateHash) {
      if (!TABS.includes(name)) return;
      this.tab = name;
      if (name === 'routing' && routingSubTab) {
        this.routingSubTab = routingSubTab === 'backends' ? 'backends' : 'models';
      }
      sessionStorage.setItem('33pol-admin-tab', name);
      sessionStorage.setItem('33pol-admin-routing-sub', this.routingSubTab);
      if (updateHash) {
        const next = '#' + name;
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

    formatCost(value, currency) {
      const n = Number(value);
      if (Number.isNaN(n)) return value ?? '—';
      try {
        return new Intl.NumberFormat(undefined, {
          style: 'currency', currency: currency || 'USD', maximumFractionDigits: 4
        }).format(n);
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

    usageQuery() {
      const q = new URLSearchParams();
      if (this.usageFrom) q.set('from', this.usageFrom);
      if (this.usageTo) q.set('to', this.usageTo);
      const costCenter = (this.usageFilterCostCenter || '').trim();
      if (costCenter) q.set('costCenter', costCenter);
      const s = q.toString();
      return s ? '?' + s : '';
    },

    usageEventsQuery() {
      const params = new URLSearchParams();
      if (this.usageFrom) params.set('from', this.usageFrom);
      if (this.usageTo) params.set('to', this.usageTo);
      const costCenter = (this.usageFilterCostCenter || '').trim();
      if (costCenter) params.set('costCenter', costCenter);
      if (this.usageFilterApiKeyId) params.set('apiKeyId', this.usageFilterApiKeyId);
      params.set('limit', '50');
      return params.toString();
    },

    async setUsagePreset(days) {
      const to = new Date();
      const from = new Date();
      if (days === 'mtd') {
        from.setDate(1);
      } else {
        from.setDate(from.getDate() - Number(days));
      }
      this.usageTo = to.toISOString().slice(0, 10);
      this.usageFrom = from.toISOString().slice(0, 10);
      if (this.apiKey) await this.applyUsageRange();
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

    _sparkValues(metric) {
      const h = this.vitalsHistory;
      if (h.length < 2) return [];
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

    usageDailySeries() {
      const rollups = this.usage?.rollups || [];
      if (!rollups.length) return [];
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
      return [...byDate.values()].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
    },

    usageMaxCost() {
      return Math.max(1e-9, ...this.usageDailySeries().map(d => d.cost));
    },

    colHeight(value) {
      return Math.max(2, Math.round((Number(value) / this.usageMaxCost()) * 100)) + '%';
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
        this.store.persistApiKey(key);
        this.gateApiKey = '';
        this.headerApiKey = '';
        this.clearMessages();
        await this.store.verifyConnection(this.editModelUrl());
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
        if (!this.keys?.length) this.fetchKeys();
        this.applyUsageRange();
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
    async loadLogs(quiet) {
      const fetchLogs = () => this._sequenced('_logsSeq', async () => {
        const body = await this.apiJson('/admin/api/logs' + this.logsQuery());
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
    _sequenced(key, run) {
      const seq = (this[key] = (this[key] || 0) + 1);
      return run().then(result => (seq === this[key] ? result : undefined));
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
      const fetchErrors = () => this._sequenced('_errorsSeq', async () => {
        const body = await this.apiJson('/admin/api/errors/groups' + this.errorsQuery());
        this.errorGroups = body?.groups ?? [];
        this.errorGroupsTotal = Number(body?.total ?? 0);
        this.errorOccurrenceTotal = Number(body?.occurrenceTotal ?? 0);
        this.errorsStoredTotal = Number(body?.storedTotal ?? 0);
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
      } catch {
        // Facets are a convenience; the free-text search still works without them.
        this.errorsFacets = null;
      }
    },

    async loadErrorOccurrences(fingerprint) {
      if (!fingerprint) return;
      try {
        await this._sequenced('_errorsOccSeq', async () => {
          const body = await this.apiJson(
            '/admin/api/errors' + this.errorsQuery({ fingerprint, limit: '20', offset: '0' })
          );
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
        await this.apiJson('/admin/api/errors?confirm=true', { method: 'DELETE' });
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
        this.toast('All recorded errors cleared.');
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
        const requestsP = this.apiJson('/admin/api/requests?limit=25');
        await healthP;
        this.summary = await summaryP;
        this.requests = (await requestsP) ?? [];
        this.summaryUpdatedAt = Date.now();
        this.recordVitals();
        this.pollFailCount = 0;
        this.overviewStale = false;
      });
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
     * Server-sent events over fetch rather than EventSource: the admin key travels in a header,
     * which EventSource cannot set. Frames are `event: update` + one JSON line; comment lines are
     * heartbeats. A drop schedules a reconnect with backoff and hands the page back to polling in
     * the meantime, so a proxy that cannot stream simply leaves the console on its 2s cadence.
     */
    async openLiveStream() {
      const controller = new AbortController();
      this._liveAbort = controller;
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

    sortedRequests() {
      let list = this.requests || [];
      if (this.requestsErrorsOnly) {
        list = list.filter(r => Number(r.statusCode) >= 400 || r.errorCode);
      }
      // Whatever the sort, work in progress stays on top: it is the part of the feed that is
      // changing, and a request that started 40s ago should not sink under ones that finished since.
      const sorted = this.sortedList(list, 'requests');
      const running = sorted.filter(r => r.isInFlight);
      return running.length ? running.concat(sorted.filter(r => !r.isInFlight)) : sorted;
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
      this.usageFilterApiKeyId = key.id;
      this.usageFilterCostCenter = key.costCenter || '';
      this.setUsagePreset('mtd');
      this.setTab('usage');
    },

    clearUsageFilters() {
      this.usageFilterApiKeyId = '';
      this.usageFilterCostCenter = '';
      if (this.apiKey) this.applyUsageRange();
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
      return {
        // ?? not || so an explicit false is preserved; absent means enforcing, matching the server default.
        enabled: data.enabled ?? data.Enabled ?? true,
        default: tier(d),
        plans: Object.fromEntries(
          Object.entries(plans).map(([slug, t]) => [slug, tier(t)])
        )
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
      return {
        enabled: this.rateLimits?.enabled !== false,
        default: {
          rpm: Number(d.rpm),
          burst: Number(d.burst),
          maxConcurrentStreams: Number(d.maxConcurrentStreams)
        },
        plans
      };
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

    /** @param quiet true for the 2s poll tick, which must not flash the loading state or raise a banner. */
    async loadRequests(quiet) {
      const fetchRequests = async () => {
        this.requests = (await this.apiJson('/admin/api/requests?limit=25')) ?? [];
      };
      if (quiet) {
        try { await fetchRequests(); } catch { /* a transient blip is already reported by the summary poll */ }
        return;
      }
      await this.runApi('overview', 'Loading requests…', fetchRequests);
    },

    async applyUsageRange() {
      await this.runApi('usage', 'Loading usage…', async () => {
        await Promise.all([
          this.apiJson('/admin/api/usage' + this.usageQuery()).then(u => { this.usage = u; }),
          this.apiJson('/admin/api/usage/events?' + this.usageEventsQuery()).then(page => {
            this.usageEvents = page?.events ?? page?.Events ?? [];
          }),
          this.apiJson('/admin/api/usage/forecast?days=7').then(f => { this.forecast = f; })
        ]);
      });
    },

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

    async downloadExport(format) {
      await this.runApi('usage', 'Preparing export…', async () => {
        const params = new URLSearchParams();
        if (this.usageFrom) params.set('from', this.usageFrom);
        if (this.usageTo) params.set('to', this.usageTo);
        const costCenter = (this.usageFilterCostCenter || '').trim();
        if (costCenter) params.set('costCenter', costCenter);
        params.set('format', format);
        const ext = format === 'csv' ? 'csv' : 'json';
        await this.store.downloadBlob(
          '/admin/api/usage/export?' + params.toString(),
          'usage-export.' + ext,
          this.editModelUrl());
        this.toast('Export downloaded.');
      });
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
        usageFrom: b('usageFrom'),
        usageTo: b('usageTo'),
        usageFilterCostCenter: b('usageFilterCostCenter'),
        usageFilterApiKeyId: b('usageFilterApiKeyId'),
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
      if (this.activeStreamsCount > 0) {
        return this.activeStreamsCount + ' streaming · ' +
          (this.activeRequestsCount - this.activeStreamsCount) + ' buffered';
      }
      return this.activeRequestsCount === 1 ? 'inference running' : 'inferences running';
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
    get hasErrors() { return !!this.summary && this.totalErrorsCount > 0; },

    // ---- overview ----

    get showStaleNotice() { return this.overviewStale && this.connectionStatus !== 'fail'; },
    get totalRequestsText() { return this.formatNum(this.summary?.totalInferenceRequests ?? 0); },
    get totalErrorsText() { return this.formatNum(this.totalErrorsCount); },
    get avgLatencyText() { return Number(this.summary?.averageLatencyMs ?? 0).toFixed(1); },
    get errorsVitalClass() { return this.totalErrorsCount > 0 ? 'accent-error' : ''; },
    get errorRateText() { return this.errorRatePct().toFixed(2) + '% error rate'; },
    get hasThroughput() { return this.currentThroughput() > 0; },
    get noThroughput() { return this.currentThroughput() === 0; },
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
        keysCreated: this.sortIndicator('keys', 'createdAt')
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
        keysCreated: () => this.sortToggle('keys', 'createdAt')
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
        const rowClass = this.requestRowClass(r) + (arrived ? ' row-enter' : '');
        const costText = this.requestCostText(r);
        return {
          key: r.requestId,
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

    get usageCurrency() { return this.forecast?.currency || this.usage?.currency; },

    usagePreset7() { return this.setUsagePreset(7); },
    usagePreset30() { return this.setUsagePreset(30); },
    usagePresetMtd() { return this.setUsagePreset('mtd'); },
    downloadExportJson() { return this.downloadExport('json'); },
    downloadExportCsv() { return this.downloadExport('csv'); },

    get usageKeyOptions() {
      return (this.keys || []).map(k => ({
        key: k.id,
        id: k.id,
        label: (k.label || k.keyPrefix) + (k.assignee ? ' · ' + k.assignee : '')
      }));
    },

    get usageSummary() {
      const s = this.usage?.summary;
      const currency = this.usageCurrency;
      return {
        has: !!s,
        promptCompact: this.formatCompact(s?.totalPromptTokens ?? 0),
        promptTotal: this.formatNum(s?.totalPromptTokens ?? 0) + ' total',
        completionCompact: this.formatCompact(s?.totalCompletionTokens ?? 0),
        completionTotal: this.formatNum(s?.totalCompletionTokens ?? 0) + ' total',
        costText: this.formatCost(s?.totalCost, currency),
        requestsText: this.formatNum(s?.totalRequests ?? 0),
        hasForecast: !!this.forecast,
        forecastText: this.formatCost(this.forecast?.projectedMonthlyCost, this.forecast?.currency) + '/mo projected'
      };
    },

    get usageCols() {
      const series = this.usageDailySeries();
      return series.map(d => ({
        key: d.date,
        title: d.date + ' · ' + this.formatCost(d.cost, this.usageCurrency) + ' · ' +
          this.formatNum(d.requests) + ' req',
        style: 'height:' + this.colHeight(d.cost)
      }));
    },

    get hasUsageCols() { return this.usageDailySeries().length > 0; },
    get usageAxisStart() { return this.shortDate(this.usageDailySeries()[0]?.date); },
    get usageAxisEnd() {
      const series = this.usageDailySeries();
      return this.shortDate(series[series.length - 1]?.date);
    },

    get usageRollupRows() {
      const currency = this.usageCurrency;
      return (this.usage?.rollups ?? []).map(row => ({
        key: row.usageDate + row.modelId + (row.costCenter || ''),
        usageDate: row.usageDate,
        modelId: row.modelId,
        costCenter: row.costCenter ?? '—',
        promptTokens: this.formatNum(row.promptTokens),
        completionTokens: this.formatNum(row.completionTokens),
        totalCost: this.formatCost(row.totalCost, currency),
        requestCount: this.formatNum(row.requestCount)
      }));
    },

    get hasUsageRollups() { return this.usageRollupRows.length > 0; },
    get usageRollupsEmpty() { return !this.isLoading('usage') && this.usageRollupRows.length === 0; },

    get usageEventRows() {
      const currency = this.usageCurrency;
      return (this.usageEvents ?? []).map(ev => ({
        key: ev.id,
        time: this.formatTime(ev.recordedAt),
        keyPrefix: ev.keyPrefix ?? '—',
        assignee: ev.assignee ?? '—',
        modelId: ev.modelId ?? '—',
        promptTokens: this.formatNum(ev.promptTokens ?? '—'),
        completionTokens: this.formatNum(ev.completionTokens ?? '—'),
        totalCost: this.formatCost(ev.totalCost, currency)
      }));
    },

    get hasUsageEvents() { return this.usageEventRows.length > 0; },
    get usageEventsEmpty() { return !this.isLoading('usage') && this.usageEventRows.length === 0; },

    // ---- routing ----

    openNewModelDrawer() { this.openModelDrawer(); },

    get modelRows() {
      return this.filteredModelsList().map(m => {
        const testing = this.isLoading('modelTest') && this.modelTestDialog?.modelId === m.id;
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
          testing,
          copyId: () => this.copyText(m.id, 'ID copied.'),
          copyUrl: () => this.copyText(m.url, 'URL copied.'),
          test: () => this.testModel(m.id),
          edit: () => this.openModelDrawer(m),
          remove: () => this.confirmRemoveModel(m.id)
        };
      });
    },

    get hasModelRows() { return this.modelRows.length > 0; },
    get modelsEmpty() { return !this.isLoading('routingModels') && this.modelRows.length === 0; },

    get backendRows() {
      return this.filteredBackends().map(b => ({
        key: b.modelId + (b.alias || ''),
        modelId: b.modelId,
        url: b.url,
        alias: b.alias ?? '—',
        healthClass: b.isHealthy ? 'dot-ok' : 'dot-fail',
        healthText: b.isHealthy ? 'Healthy' : 'Unhealthy',
        edit: () => this.editModelFromBackend(b.modelId)
      }));
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
      const currency = this.forecast?.currency;
      return this.filteredKeys().map(k => {
        const cost = this.keyMtdCost(k);
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
      return 'That counter is a cumulative lifetime total restored across restarts, and it also '
        + 'counts client disconnects, which are deliberately not stored here. Records are only kept '
        + 'from the point this gateway began capturing them, and are pruned on the retention '
        + 'schedule. New failures appear here as they happen — use Clear all to rebase both to zero.';
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
        ? 'Stored in the database and kept across restarts. Client disconnects are excluded, so this can read lower than the Overview error counter.'
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
            copyId: () => this.copyText(o.requestId, 'Request ID copied.')
          })),
          toggle: () => this.toggleErrorDetails(g.fingerprint),
          copy: () => this.copyText(this.formatErrorForCopy(g), 'Error copied.'),
          copyRequestId: () => this.copyText(g.lastRequestId, 'Request ID copied.'),
          openRequest: () => this.openRequestFromError(g.lastRequestId),
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
