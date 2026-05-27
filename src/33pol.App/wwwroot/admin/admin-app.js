function adminApp() {
  const TABS = ['dashboard', 'usage', 'routing', 'keys', 'settings'];
  const LEGACY = {
    backends: { tab: 'routing', routingSubTab: 'backends' },
    models: { tab: 'routing', routingSubTab: 'models' }
  };

  return {
    tab: 'dashboard',
    routingSubTab: 'models',
    showApiKey: false,
    showModelApiKey: false,
    showChangeKey: false,
    /** Draft key on the sign-in gate only; do not bind the gate to store.apiKey or the shell appears on first keystroke. */
    gateApiKey: '',
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
    keysFilter: 'active',
    requests: [],
    configStatus: null,
    healthLive: null,
    healthReady: null,
    confirmDialog: null,
    _confirmReturnFocus: null,
    revokeConfirmId: null,
    modelTestDialog: null,
    editModel: {
      id: '', url: '', maxContextLength: 8192, aliasesText: '',
      apiKey: '', clearApiKey: false, hasUpstreamCredential: false,
      upstreamAuth: null, _existing: false
    },
    modelFieldError: '',
    newKey: { role: 'Inference' },
    createdKey: '',
    sort: {
      models: { key: 'id', dir: 1 },
      backends: { key: 'modelId', dir: 1 },
      keys: { key: 'createdAt', dir: -1 },
      requests: { key: 'timestampUtc', dir: -1 }
    },
    _saveModelInFlight: false,
    _createKeyInFlight: false,
    darkMode: localStorage.getItem('33pol-admin-dark') === 'true' ||
      (!localStorage.getItem('33pol-admin-dark') && window.matchMedia('(prefers-color-scheme: dark)').matches),

    get store() { return Alpine.store('admin'); },
    get apiKey() { return this.store.apiKey; },
    set apiKey(v) { this.store.apiKey = v; },
    get connectionStatus() { return this.store.connectionStatus; },
    get connectionDegraded() { return this.store.connectionDegraded; },
    get error() { return this.store.error; },
    get errorTitle() { return this.store.errorTitle; },
    get errorDetail() { return this.store.errorDetail; },
    get toasts() { return this.store.toasts; },

    init() {
      this.initUsageDates();
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

    toggleDarkMode() {
      this.darkMode = !this.darkMode;
      localStorage.setItem('33pol-admin-dark', this.darkMode);
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

    summaryAgeText() {
      if (!this.summaryUpdatedAt) return '';
      const sec = Math.floor((Date.now() - this.summaryUpdatedAt) / 1000);
      return sec < 5 ? 'just now' : sec + 's ago';
    },

    usageQuery() {
      const q = new URLSearchParams();
      if (this.usageFrom) q.set('from', this.usageFrom);
      if (this.usageTo) q.set('to', this.usageTo);
      const s = q.toString();
      return s ? '?' + s : '';
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
        if (key === 'createdAt' || key === 'timestampUtc') {
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

    syncPoll() {
      if (this.poll) clearInterval(this.poll);
      this.poll = null;
      if (!this.apiKey) return;
      this.poll = setInterval(() => {
        if (this.tab !== 'dashboard' || document.hidden) return;
        this.loadSummary(true);
      }, 2000);
    },

    async saveKey() {
      const key = ((this.apiKey ? this.apiKey : this.gateApiKey) || '').trim();
      if (!key) {
        this.store.error = 'Enter an admin API key.';
        return;
      }
      await this.runApi('auth', 'Connecting…', async () => {
        this.store.persistApiKey(key);
        this.gateApiKey = '';
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
      this.store.connectionStatus = '';
      this.store.connectionDegraded = false;
      this.summary = null;
      this.usage = null;
      this.usageEvents = null;
      this.backends = [];
      this.models = [];
      this.keys = [];
      this.requests = [];
      this.createdKey = '';
      this.modelDrawerOpen = false;
      this.keysDrawerOpen = false;
      this.clearMessages();
      this.toast('Signed out — API key cleared from this browser.');
    },

    onTabActivated(name) {
      if (!this.apiKey) return;
      if (name === 'dashboard') this.loadOverviewData();
      if (name === 'usage') this.applyUsageRange();
      if (name === 'routing') {
        if (this.routingSubTab === 'backends') this.loadBackends();
        else this.loadModels();
      }
      if (name === 'keys') this.loadKeys();
      if (name === 'settings') this.loadSettings();
    },

    async loadSettings() {
      await this.runApi('settings', 'Loading settings…', async () => {
        const tasks = [this.fetchTenantGrants(), this.fetchConfigStatus()];
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
      return this.sortedList(filtered, 'keys');
    },

    sortedRequests() {
      return this.sortedList(this.requests || [], 'requests');
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
        else if (this.keysDrawerOpen) this.closeKeysDrawer();
      }
    },

    closeModelTestDialog() {
      this.modelTestDialog = null;
    },

    async testModel(modelId) {
      if (!modelId) return;
      this.modelTestDialog = { modelId, loading: true, result: null, error: '' };
      try {
        const result = await this.runApi('modelTest', 'Testing model…', async () =>
          this.apiJson('/admin/api/models/' + encodeURIComponent(modelId) + '/test', {
            method: 'POST',
            body: JSON.stringify({ prompt: 'Hello world', maxTokens: 16 })
          }), { localOnly: true });
        if (this.modelTestDialog?.modelId === modelId) {
          this.modelTestDialog = { modelId, loading: false, result, error: '' };
        }
        if (result?.ok) this.toast('Model test succeeded.');
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
      this.keysDrawerOpen = true;
      this.createdKey = '';
      this.keysCreatedAck = false;
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
        groq: 'https://api.groq.com/openai'
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
          (async () => {
            const params = new URLSearchParams();
            if (this.usageFrom) params.set('from', this.usageFrom);
            if (this.usageTo) params.set('to', this.usageTo);
            params.set('limit', '50');
            const page = await this.apiJson('/admin/api/usage/events?' + params.toString());
            this.usageEvents = page?.events ?? page?.Events ?? [];
          })(),
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
        hasUpstreamCredential: item.hasUpstreamCredential === true
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
        aliases
      };
      if (this.editModel._existing && this.editModel.upstreamAuth && !(this.editModel.apiKey || '').trim() && !this.editModel.clearApiKey) {
        model.upstreamAuth = this.editModel.upstreamAuth;
      }
      const apiKey = (this.editModel.apiKey || '').trim();
      return {
        model,
        apiKey: apiKey || null,
        clearApiKey: !!this.editModel.clearApiKey
      };
    },

    resetModelForm() {
      this.editModel = {
        id: '', url: '', maxContextLength: 8192, aliasesText: '',
        apiKey: '', clearApiKey: false, hasUpstreamCredential: false,
        upstreamAuth: null, _existing: false
      };
      this.showAdvancedModel = false;
    },

    startEditModel(m) {
      this.editModel = {
        id: m.id, url: m.url, maxContextLength: m.maxContextLength || 8192,
        aliasesText: (m.aliases || []).join(', '),
        apiKey: '', clearApiKey: false,
        hasUpstreamCredential: !!m.hasUpstreamCredential,
        upstreamAuth: m.upstreamAuth || null,
        _existing: true
      };
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
      this._saveModelInFlight = true;
      try {
        await this.runApi('routingModels', 'Saving model…', async () => {
          const url = this.editModel._existing
            ? '/admin/api/models/' + encodeURIComponent(write.model.id)
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
      const list = (await this.apiJson('/admin/api/keys')) ?? [];
      this.keys = this.normalizeApiKeyList(list);
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
            body: JSON.stringify({ role: this.newKey.role, scopes: [] })
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

    cancelRevoke() {
      this.revokeConfirmId = null;
    },

    async revokeKeyConfirmed() {
      const id = this.revokeConfirmId;
      if (!id) return;
      this.revokeConfirmId = null;
      await this.runApi('keys', 'Revoking…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + id + '/revoke', { method: 'POST' }, this.editModelUrl());
        this.toast('API key revoked.');
        await this.fetchKeys();
      });
    },

    async downloadExport(format) {
      await this.runApi('usage', 'Preparing export…', async () => {
        const params = new URLSearchParams();
        if (this.usageFrom) params.set('from', this.usageFrom);
        if (this.usageTo) params.set('to', this.usageTo);
        params.set('format', format);
        const ext = format === 'csv' ? 'csv' : 'json';
        await this.store.downloadBlob(
          '/admin/api/usage/export?' + params.toString(),
          'usage-export.' + ext,
          this.editModelUrl());
        this.toast('Export downloaded.');
      });
    }
  };
}
