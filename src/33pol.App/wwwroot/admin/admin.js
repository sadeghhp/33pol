function adminApp() {
  const TABS = ['dashboard', 'usage', 'backends', 'models', 'keys'];

  return {
    tab: 'dashboard',
    showApiKey: false,
    poll: null,
    summary: null,
    usage: null,
    usageEvents: null,
    usageFrom: '',
    usageTo: '',
    forecast: null,
    backends: [],
    backendsFilter: '',
    models: [],
    providers: [],
    selectedProviderId: 'openrouter',
    providerEnvVar: 'OPENROUTER_API_KEY',
    providerModels: [],
    providerFilter: '',
    providerLoading: false,
    providerError: '',
    providerErrorTitle: '',
    providerErrorDetail: '',
    providerDiscoveryUpstreamUrl: '',
    providerDiscoveryRequiresAuth: true,
    customModelsUrl: '',
    customUpstreamBaseUrl: '',
    keys: [],
    keysFilter: 'active',
    requests: [],
    configStatus: null,
    healthLive: null,
    healthReady: null,
    revokeConfirmId: null,
    editModel: {
      id: '',
      url: '',
      maxContextLength: 8192,
      aliasesText: '',
      useUpstreamAuth: false,
      upstreamAuthEnvVar: 'OPENROUTER_API_KEY',
      _existing: false
    },
    newKey: { role: 'Inference' },
    createdKey: '',

    get store() {
      return Alpine.store('admin');
    },
    get apiKey() { return this.store.apiKey; },
    set apiKey(v) { this.store.apiKey = v; },
    get loading() { return this.store.loading; },
    get loadingMessage() { return this.store.loadingMessage; },
    get connectionStatus() { return this.store.connectionStatus; },
    get error() { return this.store.error; },
    get errorTitle() { return this.store.errorTitle; },
    get errorDetail() { return this.store.errorDetail; },
    get successMessage() { return this.store.successMessage; },

    init() {
      this.initUsageDates();
      this.restoreTab();
      window.addEventListener('hashchange', () => this.applyHashTab());
      document.addEventListener('visibilitychange', () => this.syncPoll());

      if (this.apiKey) {
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

    restoreTab() {
      const hash = (location.hash || '').replace(/^#\/?/, '');
      if (TABS.includes(hash)) {
        this.setTab(hash, false);
        return;
      }
      const saved = sessionStorage.getItem('33pol-admin-tab');
      if (saved && TABS.includes(saved)) {
        this.setTab(saved, false);
      }
    },

    applyHashTab() {
      const hash = (location.hash || '').replace(/^#\/?/, '');
      if (TABS.includes(hash) && hash !== this.tab) {
        this.setTab(hash, false);
      }
    },

    editModelUrl() {
      return this.editModel?.url || '';
    },

    clearMessages() {
      this.store.clearMessages();
    },

    setError(title, message, detail) {
      this.store.setError(title, message, detail);
    },

    setSuccess(message) {
      this.store.setSuccess(message);
    },

    handleCatch(e) {
      this.setError(e.title || 'Error', e.message || String(e), e.detail);
    },

    clearProviderError() {
      this.providerError = '';
      this.providerErrorTitle = '';
      this.providerErrorDetail = '';
    },

    setProviderError(title, message, detail) {
      this.providerErrorTitle = title || 'Could not fetch models';
      this.providerError = message || 'Something went wrong.';
      this.providerErrorDetail = detail || '';
      requestAnimationFrame(() => {
        const el = document.getElementById('provider-fetch-alert');
        if (el) {
          el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
      });
    },

    looksLikeEnvVarSecret(value) {
      const v = (value || '').trim();
      if (!v) return false;
      if (/^sk-/i.test(v) || /^Bearer\s+/i.test(v)) return true;
      return v.length >= 32 && !v.includes('_');
    },

    validateProviderEnvVar() {
      if (!this.selectedProviderRequiresAuth()) {
        return true;
      }
      const envVar = (this.providerEnvVar || '').trim();
      if (!envVar) {
        const msg = 'Enter the environment variable name (e.g. OPENROUTER_API_KEY).';
        this.setProviderError('Validation', msg);
        this.setError('Validation', msg);
        return false;
      }
      if (this.looksLikeEnvVarSecret(envVar)) {
        const msg =
          'This looks like an API key secret. Enter the variable name configured on the gateway (e.g. OPENROUTER_API_KEY), then set the secret in Docker or host environment.';
        this.setProviderError('Invalid env var', msg);
        this.setError('Invalid env var', msg);
        return false;
      }
      return true;
    },

    async runApi(label, fn) {
      try {
        return await this.store.withLoading(label, fn);
      } catch (e) {
        this.handleCatch(e);
        throw e;
      }
    },

    async apiJson(url, options = {}) {
      return this.store.apiJson(url, options, this.editModelUrl());
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
          style: 'currency',
          currency: currency || 'USD',
          maximumFractionDigits: 4
        }).format(n);
      } catch {
        return n.toFixed(4);
      }
    },

    usageQuery() {
      const q = new URLSearchParams();
      if (this.usageFrom) q.set('from', this.usageFrom);
      if (this.usageTo) q.set('to', this.usageTo);
      const s = q.toString();
      return s ? '?' + s : '';
    },

    async copyText(text, successMsg) {
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        this.setSuccess(successMsg || 'Copied to clipboard.');
      } catch {
        this.setError('Copy failed', 'Could not access clipboard.');
      }
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
      this.store.persistApiKey(this.apiKey);
      this.clearMessages();
      await this.store.verifyConnection(this.editModelUrl());
      this.syncPoll();
      await this.loadSummary();
      this.onTabActivated(this.tab);
    },

    clearSession() {
      if (this.poll) clearInterval(this.poll);
      this.poll = null;
      this.store.persistApiKey('');
      this.store.connectionStatus = '';
      this.summary = null;
      this.usage = null;
      this.usageEvents = null;
      this.backends = [];
      this.models = [];
      this.keys = [];
      this.requests = [];
      this.createdKey = '';
      this.clearProviderError();
      this.clearMessages();
      this.setSuccess('Signed out — API key cleared from this browser.');
    },

    setTab(name, updateHash = true) {
      if (!TABS.includes(name)) return;
      this.tab = name;
      sessionStorage.setItem('33pol-admin-tab', name);
      if (updateHash) {
        const next = '#' + name;
        if (location.hash !== next) location.hash = next;
      }
      this.syncPoll();
      this.onTabActivated(name);
    },

    onTabActivated(name) {
      if (!this.apiKey) return;
      if (name === 'dashboard') {
        this.loadSummary();
        this.loadHealth();
        if (!this.requests?.length) this.loadRequests();
      }
      if (name === 'usage' && !this.usage) {
        this.loadUsage();
        this.loadUsageEvents();
      }
      if (name === 'backends') this.loadBackends();
      if (name === 'models') {
        this.loadModels();
        if (!this.providers?.length) this.loadProviders();
      }
      if (name === 'keys') this.loadKeys();
    },

    filteredBackends() {
      const q = (this.backendsFilter || '').trim().toLowerCase();
      let list = [...(this.backends || [])];
      list.sort((a, b) => Number(a.isHealthy) - Number(b.isHealthy));
      if (!q) return list;
      return list.filter(b =>
        (b.modelId || '').toLowerCase().includes(q) ||
        (b.url || '').toLowerCase().includes(q) ||
        (b.alias || '').toLowerCase().includes(q));
    },

    filteredKeys() {
      const list = this.keys || [];
      if (this.keysFilter === 'active') return list.filter(k => !k.isRevoked);
      if (this.keysFilter === 'revoked') return list.filter(k => k.isRevoked);
      return list;
    },

    urlLooksLocalhost() {
      return /localhost|127\.0\.0\.1/.test(this.editModel?.url || '');
    },

    applyModelTemplate(kind) {
      if (kind === 'lmstudio-docker') {
        this.editModel = {
          id: '', url: 'http://host.docker.internal:1234', maxContextLength: 8192,
          aliasesText: '', useUpstreamAuth: false, upstreamAuthEnvVar: 'OPENROUTER_API_KEY', _existing: false
        };
      } else if (kind === 'lmstudio-native') {
        this.editModel = {
          id: '', url: 'http://127.0.0.1:1234', maxContextLength: 8192,
          aliasesText: '', useUpstreamAuth: false, upstreamAuthEnvVar: 'OPENROUTER_API_KEY', _existing: false
        };
      } else if (kind === 'openrouter' || kind === 'together' || kind === 'groq') {
        const p = this.providers.find(x => x.id === kind) || {
          upstreamBaseUrl: kind === 'together' ? 'https://api.together.xyz' : kind === 'groq' ? 'https://api.groq.com/openai' : 'https://openrouter.ai/api',
          defaultEnvVar: kind === 'together' ? 'TOGETHER_API_KEY' : kind === 'groq' ? 'GROQ_API_KEY' : 'OPENROUTER_API_KEY',
          requiresUpstreamAuth: true
        };
        const envVar = (p.defaultEnvVar || 'OPENROUTER_API_KEY').trim();
        this.editModel = {
          id: '', url: p.upstreamBaseUrl, maxContextLength: 8192,
          aliasesText: '', useUpstreamAuth: !!p.requiresUpstreamAuth, upstreamAuthEnvVar: envVar, _existing: false
        };
        this.selectedProviderId = kind;
        this.providerEnvVar = envVar;
      } else if (kind === 'vllm-docker') {
        this.editModel = {
          id: '', url: 'http://host.docker.internal:8000', maxContextLength: 8192,
          aliasesText: '', useUpstreamAuth: false, upstreamAuthEnvVar: 'OPENROUTER_API_KEY', _existing: false
        };
      }
      this.setSuccess('Template applied — set model id and save.');
    },

    async loadSummary(silent) {
      if (silent) {
        try {
          this.summary = await this.apiJson('/admin/api/summary');
        } catch { /* background poll — keep last summary */ }
        return;
      }
      await this.runApi('Loading summary…', async () => {
        this.summary = await this.apiJson('/admin/api/summary');
        this.clearMessages();
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

    async loadConfigStatus() {
      await this.runApi('Loading config status…', async () => {
        this.configStatus = await this.apiJson('/admin/api/config/status');
        this.clearMessages();
      });
    },

    async reloadConfig() {
      await this.runApi('Reloading config…', async () => {
        const body = await this.apiJson('/admin/api/config/reload', { method: 'POST' });
        if (body?.status === 'error') {
          this.setError('Reload failed', body.message || 'Reload failed');
          return;
        }
        this.setSuccess(body?.message || 'Config reloaded from disk.');
        await this.loadConfigStatus();
        await this.loadModels();
        await this.loadBackends();
      });
    },

    async loadRequests() {
      await this.runApi('Loading requests…', async () => {
        this.requests = (await this.apiJson('/admin/api/requests?limit=25')) ?? [];
        this.clearMessages();
      });
    },

    async loadUsage() {
      await this.runApi('Loading usage…', async () => {
        this.usage = await this.apiJson('/admin/api/usage' + this.usageQuery());
        this.clearMessages();
      });
    },

    async loadUsageEvents() {
      await this.runApi('Loading usage events…', async () => {
        const params = new URLSearchParams();
        if (this.usageFrom) params.set('from', this.usageFrom);
        if (this.usageTo) params.set('to', this.usageTo);
        params.set('limit', '50');
        const page = await this.apiJson('/admin/api/usage/events?' + params.toString());
        this.usageEvents = page?.events ?? page?.Events ?? [];
        this.clearMessages();
      });
    },

    async loadForecast() {
      await this.runApi('Loading forecast…', async () => {
        this.forecast = await this.apiJson('/admin/api/usage/forecast?days=7');
        this.clearMessages();
      });
    },

    async loadBackends() {
      await this.runApi('Loading backends…', async () => {
        this.backends = (await this.apiJson('/admin/api/backends')) ?? [];
        this.clearMessages();
      });
    },

    async loadModels() {
      await this.runApi('Loading models…', async () => {
        this.models = (await this.apiJson('/admin/api/models')) ?? [];
        this.clearMessages();
      });
    },

    getSelectedProvider() {
      return (this.providers || []).find(p => p.id === this.selectedProviderId);
    },

    selectedProviderRequiresAuth() {
      const p = this.getSelectedProvider();
      if (!p) return true;
      if (p.id === 'custom') return true;
      return p.requiresUpstreamAuth !== false;
    },

    onProviderChanged() {
      this.clearProviderError();
      const p = this.getSelectedProvider();
      if (p?.defaultEnvVar) {
        this.providerEnvVar = p.defaultEnvVar;
      }
      this.providerModels = [];
      this.providerDiscoveryUpstreamUrl = p?.upstreamBaseUrl || '';
      this.providerDiscoveryRequiresAuth = this.selectedProviderRequiresAuth();
      if (p?.id === 'custom' && !this.customModelsUrl && p.modelsListUrl) {
        this.customModelsUrl = p.modelsListUrl;
      }
    },

    async loadProviders() {
      await this.runApi('Loading providers…', async () => {
        const body = await this.apiJson('/admin/api/providers/catalog');
        this.providers = body?.data || [];
        if (!this.providers.some(p => p.id === this.selectedProviderId)) {
          this.selectedProviderId = this.providers[0]?.id || 'openrouter';
        }
        this.onProviderChanged();
        this.clearMessages();
      });
    },

    filteredProviderModels() {
      const q = (this.providerFilter || '').trim().toLowerCase();
      if (!q) return this.providerModels || [];
      return (this.providerModels || []).filter(m =>
        (m.id || '').toLowerCase().includes(q) || (m.name || '').toLowerCase().includes(q));
    },

    useProviderModel(m) {
      const id = (m?.id || '').trim();
      if (!id) return;
      const upstreamUrl = (this.providerDiscoveryUpstreamUrl || this.getSelectedProvider()?.upstreamBaseUrl || '').trim();
      const requiresAuth = this.providerDiscoveryRequiresAuth;
      const envVar = (this.providerEnvVar || '').trim();
      this.editModel = {
        id,
        url: upstreamUrl,
        maxContextLength: m?.contextLength || 8192,
        aliasesText: '',
        useUpstreamAuth: requiresAuth && !!envVar,
        upstreamAuthEnvVar: envVar || 'OPENROUTER_API_KEY',
        _existing: false
      };
      this.setSuccess('Prefilled model form from provider catalog.');
    },

    async fetchProviderModels() {
      if (!this.validateProviderEnvVar()) {
        return;
      }
      this.clearProviderError();
      try {
        this.providerLoading = true;
        const envVar = (this.providerEnvVar || '').trim();
        let body;
        if (this.selectedProviderId === 'custom') {
          const modelsUrl = (this.customModelsUrl || '').trim();
          body = await this.apiJson('/admin/api/providers/models', {
            method: 'POST',
            body: JSON.stringify({ modelsUrl, envVar })
          });
          this.providerDiscoveryUpstreamUrl = (this.customUpstreamBaseUrl || body?.upstreamBaseUrl || '').trim();
        } else {
          body = await this.apiJson(
            '/admin/api/providers/' + encodeURIComponent(this.selectedProviderId) + '/models',
            { method: 'POST', body: JSON.stringify({ envVar }) });
          this.providerDiscoveryUpstreamUrl = body?.upstreamBaseUrl || this.getSelectedProvider()?.upstreamBaseUrl || '';
        }
        this.providerModels = body?.data || [];
        this.providerDiscoveryRequiresAuth = this.selectedProviderRequiresAuth();
        const name = this.getSelectedProvider()?.displayName || this.selectedProviderId;
        this.setSuccess('Fetched models from ' + name + '.');
      } catch (e) {
        this.setProviderError(e.title || 'Error', e.message || String(e), e.detail);
        this.handleCatch(e);
      } finally {
        this.providerLoading = false;
      }
    },

    modelPayload() {
      const aliases = (this.editModel.aliasesText || '')
        .split(',')
        .map(s => s.trim())
        .filter(Boolean);
      const payload = {
        id: this.editModel.id.trim(),
        url: this.editModel.url.trim(),
        maxContextLength: Number(this.editModel.maxContextLength) || 8192,
        aliases
      };
      if (this.editModel.useUpstreamAuth) {
        const envVar = (this.editModel.upstreamAuthEnvVar || 'OPENROUTER_API_KEY').trim();
        if (envVar) payload.upstreamAuth = { type: 'bearer', envVar };
      }
      return payload;
    },

    resetModelForm() {
      this.editModel = {
        id: '', url: '', maxContextLength: 8192, aliasesText: '',
        useUpstreamAuth: false, upstreamAuthEnvVar: 'OPENROUTER_API_KEY', _existing: false
      };
    },

    startEditModel(m) {
      this.editModel = {
        id: m.id,
        url: m.url,
        maxContextLength: m.maxContextLength || 8192,
        aliasesText: (m.aliases || []).join(', '),
        useUpstreamAuth: !!m.upstreamAuth,
        upstreamAuthEnvVar: m.upstreamAuth?.envVar || 'OPENROUTER_API_KEY',
        _existing: true
      };
    },

    validateModelUpstreamEnvVar() {
      if (!this.editModel.useUpstreamAuth) {
        return true;
      }
      const envVar = (this.editModel.upstreamAuthEnvVar || '').trim();
      if (!envVar) {
        this.setError('Validation', 'Upstream auth env var name is required (e.g. OPENROUTER_API_KEY).');
        return false;
      }
      if (this.looksLikeEnvVarSecret(envVar)) {
        this.setError(
          'Invalid env var',
          'Auth env var must be the variable name on the gateway, not the API key secret.');
        return false;
      }
      if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(envVar)) {
        this.setError(
          'Invalid env var',
          'Use a valid environment variable name (letters, digits, underscore).');
        return false;
      }
      return true;
    },

    async saveModel() {
      const payload = this.modelPayload();
      if (!payload.id || !payload.url) {
        this.setError('Validation', 'Model id and url are required.');
        return;
      }
      if (!this.validateModelUpstreamEnvVar()) {
        return;
      }
      if (/localhost|127\.0\.0\.1/.test(payload.url)) {
        this.setError(
          'Upstream URL',
          'Use http://host.docker.internal:<port> when the gateway runs in Docker (not localhost).');
        return;
      }
      await this.runApi('Saving model…', async () => {
        const url = this.editModel._existing
          ? '/admin/api/models/' + encodeURIComponent(payload.id)
          : '/admin/api/models';
        const body = await this.apiJson(url, {
          method: this.editModel._existing ? 'PATCH' : 'POST',
          body: JSON.stringify(payload)
        });
        if (body && body.success === false) {
          this.setError('Save failed', body.message || 'Could not save model.');
          return;
        }
        this.setSuccess(body?.message || 'Model saved.');
        this.resetModelForm();
        await this.loadModels();
        await this.loadBackends();
      });
    },

    async removeModel(id) {
      if (!confirm('Remove model ' + id + '?')) return;
      await this.runApi('Removing model…', async () => {
        const body = await this.apiJson('/admin/api/models/' + encodeURIComponent(id), { method: 'DELETE' });
        if (body?.success === false) {
          this.setError('Remove failed', body.message || 'Could not remove model.');
          return;
        }
        this.setSuccess(body?.message || 'Model removed.');
        await this.loadModels();
        await this.loadBackends();
      });
    },

    async loadKeys() {
      await this.runApi('Loading API keys…', async () => {
        this.keys = (await this.apiJson('/admin/api/keys')) ?? [];
        this.clearMessages();
      });
    },

    async createKey() {
      await this.runApi('Creating API key…', async () => {
        const body = await this.apiJson('/admin/api/keys', {
          method: 'POST',
          body: JSON.stringify({ role: this.newKey.role, scopes: [] })
        });
        this.createdKey = body?.secret || '';
        this.setSuccess('API key created — copy the secret now.');
        await this.loadKeys();
      });
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
      await this.runApi('Revoking key…', async () => {
        await this.store.apiFetch('/admin/api/keys/' + id + '/revoke', { method: 'POST' }, this.editModelUrl());
        this.setSuccess('API key revoked.');
        await this.loadKeys();
      });
    },

    async downloadExport(format) {
      await this.runApi('Preparing export…', async () => {
        const params = new URLSearchParams();
        if (this.usageFrom) params.set('from', this.usageFrom);
        if (this.usageTo) params.set('to', this.usageTo);
        params.set('format', format);
        const res = await fetch(
          '/admin/api/usage/export?' + params.toString(),
          { headers: this.store.headers() });
        if (!res.ok) throw new Error(res.status + ' ' + res.statusText);
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'usage-export.' + (format === 'csv' ? 'csv' : 'json');
        a.click();
        URL.revokeObjectURL(url);
        this.setSuccess('Export downloaded.');
      });
    }
  };
}
