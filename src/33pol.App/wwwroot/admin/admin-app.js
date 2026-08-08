/**
 * The console runs on Alpine's CSP-friendly build so the admin surface can keep `script-src 'self'`
 * (see AdminSecurityHeaders.cs). That build's evaluator resolves a directive's value as a property
 * path and nothing else — no operators, no ternaries, no calls with arguments — so everything the
 * markup needs is exposed from here as a getter, a zero-argument method, or a {get,set} pair for
 * x-model. Row-level actions ride on the row objects as bound closures, which is how a template
 * reaches copyText(id) without writing an argument. See the "CSP view layer" section below.
 */
function adminApp() {
  const TABS = ['dashboard', 'usage', 'routing', 'keys', 'logs', 'settings'];
  const LEGACY = {
    backends: { tab: 'routing', routingSubTab: 'backends' },
    models: { tab: 'routing', routingSubTab: 'models' }
  };

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
    logs: [],
    logsLevel: 'all',
    logsSearch: '',
    logsCapacity: 0,
    logsPageSize: 200,
    logsAutoRefresh: false,
    expandedLogId: null,
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
      document.addEventListener('visibilitychange', () => this.syncPoll());
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
      const h = (hash || '').replace(/^#\/?/, '');
      if (LEGACY[h]) return LEGACY[h];
      if (TABS.includes(h)) return { tab: h };
      return null;
    },

    restoreTab() {
      const resolved = this.resolveHash(location.hash);
      if (resolved) {
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
        this.applyTab(resolved.tab, resolved.routingSubTab, false);
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
      if (updateHash) {
        const next = '#' + name;
        if (location.hash !== next) location.hash = next;
      }
      this.syncPoll();
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
      const sec = Math.floor((Date.now() - this.summaryUpdatedAt) / 1000);
      return sec < 5 ? 'just now' : sec + 's ago';
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
        this.loadSummary(true);
        if (this._pollTick % 5 === 0) this.loadHealth();
        // Every 5th tick (10s) — the log buffer does not move fast enough to justify 2s polling,
        // and only while the tab is actually on screen.
        if (this.logsAutoRefresh && this.tab === 'logs' && this._pollTick % 5 === 0) this.loadLogs(true);
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
        streams: Number(s.activeStreams ?? 0)
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
      const key = metric === 'latency' ? 'latency' : 'streams';
      return h.map(s => s[key]);
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
        this.onTabActivated(this.tab);
      });
    },

    clearSession() {
      if (this.poll) clearInterval(this.poll);
      this.poll = null;
      this.store.stopConnectionWatch();
      this.store.persistApiKey('');
      this.gateApiKey = '';
      this.headerApiKey = '';
      this.showChangeKey = false;
      this.store.connectionStatus = '';
      this.store.connectionDegraded = false;
      this.summary = null;
      this.vitalsHistory = [];
      this.usage = null;
      this.usageEvents = null;
      this.backends = [];
      this.models = [];
      this.keys = [];
      this.selectedKeyIds = [];
      this.requests = [];
      this.logs = [];
      this.expandedLogId = null;
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
      if (name === 'settings') this.loadSettings();
    },

    logsQuery() {
      const params = new URLSearchParams({ limit: String(this.logsPageSize) });
      if (this.logsLevel && this.logsLevel !== 'all') params.set('level', this.logsLevel);
      const search = (this.logsSearch || '').trim();
      if (search) params.set('search', search);
      return '?' + params.toString();
    },

    /** @param quiet true for the auto-refresh tick, which must not flash the loading state. */
    async loadLogs(quiet) {
      const fetchLogs = async () => {
        const body = await this.apiJson('/admin/api/logs' + this.logsQuery());
        this.logs = body?.entries ?? [];
        this.logsCapacity = Number(body?.capacity ?? 0);
      };
      if (quiet) {
        try { await fetchLogs(); } catch { /* the poll must not raise a banner on a transient blip */ }
        return;
      }
      await this.runApi('logs', 'Loading logs…', fetchLogs);
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
      return this.sortedList(list, 'requests');
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

    async loadRequests() {
      await this.runApi('overview', 'Loading requests…', async () => {
        this.requests = (await this.apiJson('/admin/api/requests?limit=25')) ?? [];
      });
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
        streams: one('streams')
      };
    },

    modelBars(rows) {
      return rows.map(row => ({
        key: row.modelId,
        modelId: row.modelId,
        countText: this.formatNum(row.count),
        style: 'width:' + this.barWidth(row.count, rows)
      }));
    },

    get requestModelBars() { return this.modelBars(this.requestsByModelRows()); },
    get hasRequestModelBars() { return this.requestModelBars.length > 0; },
    get noRequestModelBars() { return this.requestModelBars.length === 0; },
    get errorModelBars() { return this.modelBars(this.errorsByModelRows()); },
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
        keysLastUsed: () => this.sortToggle('keys', 'lastUsedAt'),
        keysCreated: () => this.sortToggle('keys', 'createdAt')
      };
    },

    get requestRows() {
      return this.sortedRequests().map(r => {
        const expanded = this.isRequestExpanded(r.requestId);
        const duration = r.durationMs;
        return {
          key: r.requestId,
          requestId: r.requestId ?? '—',
          shortId: this.shortRequestId(r.requestId),
          time: this.formatTime(r.timestampUtc),
          method: r.method ?? '—',
          path: r.path ?? '',
          modelId: r.modelId ?? '—',
          statusCode: r.statusCode ?? '—',
          errorText: r.errorCode ?? '—',
          errorClass: r.errorCode ? 'error' : '',
          durationText: typeof duration?.toFixed === 'function' ? duration.toFixed(0) : (duration ?? '—'),
          rowClass: this.requestRowClass(r),
          expanded,
          ariaExpanded: expanded ? 'true' : 'false',
          ariaLabel: 'Request ' + this.shortRequestId(r.requestId) +
            (r.errorCode ? ', error ' + r.errorCode : ''),
          tenant: r.tenantId ?? '—',
          streaming: r.isStreaming ? 'Yes' : 'No',
          toggle: () => this.toggleRequestDetails(r.requestId),
          copyId: () => this.copyText(r.requestId, 'Request ID copied.')
        };
      });
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
    get logsTruncated() { return this.logs.length >= this.logsPageSize; },
    get logsSkeleton() { return this.isLoading('logs') && !this.logs.length; },
    get hasLogs() { return this.logs.length > 0; },
    get logsEmpty() { return !this.isLoading('logs') && !this.logs.length; },
    get logsEmptyText() {
      return this.logsSearch || this.logsLevel !== 'all'
        ? 'No log entries match this filter.'
        : 'No warnings or errors recorded since the gateway started.';
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
