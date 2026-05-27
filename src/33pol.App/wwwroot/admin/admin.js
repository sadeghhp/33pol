function adminApp() {
  return {
    apiKey: localStorage.getItem('33pol-admin-key') || '',
    tab: 'dashboard',
    poll: null,
    summary: null,
    usage: null,
    forecast: null,
    backends: [],
    models: [],
    keys: [],
    requests: [],
    configStatus: null,
    editModel: { id: '', url: '', maxContextLength: 8192, aliasesText: '', _existing: false },
    newKey: { role: 'Inference' },
    createdKey: '',
    error: '',
    errorTitle: '',
    errorDetail: '',
    successMessage: '',

    init() {
      if (this.apiKey) {
        this.saveKey();
      }
    },

    setTab(name) {
      this.tab = name;
      if (name === 'dashboard') this.loadSummary();
      if (name === 'usage' && !this.usage) this.loadUsage();
      if (name === 'backends') this.loadBackends();
      if (name === 'models') this.loadModels();
      if (name === 'keys') this.loadKeys();
    },

    clearMessages() {
      this.error = '';
      this.errorTitle = '';
      this.errorDetail = '';
      this.successMessage = '';
    },

    setError(title, message, detail) {
      this.successMessage = '';
      this.errorTitle = title || 'Error';
      this.error = message || 'Something went wrong.';
      this.errorDetail = detail || '';
    },

    setSuccess(message) {
      this.clearMessages();
      this.successMessage = message;
    },

    parseJsonBody(text) {
      if (!text || !text.trim().startsWith('{')) return null;
      try { return JSON.parse(text); } catch { return null; }
    },

    friendlyApiError(status, statusText, text) {
      const json = this.parseJsonBody(text);
      if (json?.message) {
        return { title: status + ' ' + (json.success === false ? 'Failed' : statusText), message: json.message, detail: json.success === false ? null : text };
      }
      if (json?.title && json?.detail) {
        return { title: json.title, message: json.detail, detail: text };
      }
      const raw = (text || '').trim();
      const isHtml = raw.startsWith('<') || raw.includes('<!DOCTYPE');
      const isStack = raw.includes(' at ') && raw.includes(' in ');
      if (isHtml || isStack) {
        let hint = 'The gateway returned an unexpected error.';
        if (raw.includes('Device or resource busy') || raw.includes('models.json')) {
          hint = 'Cannot write models.json — registry file may be read-only (Docker :ro mount). Use a writable volume or edit deploy/docker/config/models.json and Reload config.';
        } else if (raw.includes('Unauthorized') || status === 401) {
          hint = 'Invalid or missing admin API key.';
        }
        const firstLine = raw.split('\n').find(l => l.trim() && !l.startsWith('<')) || '';
        return { title: status + ' ' + statusText, message: hint, detail: firstLine || raw.slice(0, 2000) };
      }
      if (this.editModel?.url && /localhost|127\.0\.0\.1/.test(this.editModel.url)) {
        return {
          title: status + ' ' + statusText,
          message: (raw || statusText) + ' — From Docker, use http://host.docker.internal:<port> instead of localhost.',
          detail: null
        };
      }
      return { title: status + ' ' + statusText, message: raw || statusText, detail: null };
    },

    saveKey() {
      localStorage.setItem('33pol-admin-key', this.apiKey);
      this.clearMessages();
      if (this.poll) clearInterval(this.poll);
      this.poll = setInterval(() => {
        if (this.tab === 'dashboard' && this.apiKey) this.loadSummary();
      }, 2000);
      this.loadSummary();
    },

    headers() {
      return { 'X-API-Key': this.apiKey, 'Content-Type': 'application/json' };
    },

    formatTime(iso) {
      if (!iso) return '—';
      try { return new Date(iso).toLocaleString(); } catch { return iso; }
    },

    async apiFetch(url, options = {}) {
      const res = await fetch(url, { ...options, headers: { ...this.headers(), ...(options.headers || {}) } });
      const text = await res.text();
      if (!res.ok) {
        const err = this.friendlyApiError(res.status, res.statusText, text);
        const e = new Error(err.message);
        e.title = err.title;
        e.detail = err.detail;
        throw e;
      }
      res._bodyText = text;
      return res;
    },

    async apiJson(url, options = {}) {
      const res = await this.apiFetch(url, options);
      const text = res._bodyText ?? '';
      if (!text) return null;
      return JSON.parse(text);
    },

    handleCatch(e) {
      this.setError(e.title || 'Error', e.message || String(e), e.detail);
    },

    async loadSummary() {
      try {
        this.summary = await this.apiJson('/admin/api/summary');
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async loadConfigStatus() {
      try {
        this.configStatus = await this.apiJson('/admin/api/config/status');
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async reloadConfig() {
      try {
        const body = await this.apiJson('/admin/api/config/reload', { method: 'POST' });
        if (body?.status === 'error') {
          this.setError('Reload failed', body.message || 'Reload failed');
          return;
        }
        this.setSuccess(body?.message || 'Config reloaded from disk.');
        await this.loadConfigStatus();
        await this.loadModels();
        await this.loadBackends();
      } catch (e) { this.handleCatch(e); }
    },

    async loadRequests() {
      try {
        this.requests = (await this.apiJson('/admin/api/requests?limit=25')) ?? [];
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async loadUsage() {
      try {
        this.usage = await this.apiJson('/admin/api/usage');
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async loadForecast() {
      try {
        this.forecast = await this.apiJson('/admin/api/usage/forecast?days=7');
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async loadBackends() {
      try {
        this.backends = (await this.apiJson('/admin/api/backends')) ?? [];
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async loadModels() {
      try {
        this.models = (await this.apiJson('/admin/api/models')) ?? [];
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    modelPayload() {
      const aliases = (this.editModel.aliasesText || '')
        .split(',')
        .map(s => s.trim())
        .filter(Boolean);
      return {
        id: this.editModel.id.trim(),
        url: this.editModel.url.trim(),
        maxContextLength: Number(this.editModel.maxContextLength) || 8192,
        aliases
      };
    },

    resetModelForm() {
      this.editModel = { id: '', url: '', maxContextLength: 8192, aliasesText: '', _existing: false };
    },

    startEditModel(m) {
      this.editModel = {
        id: m.id,
        url: m.url,
        maxContextLength: m.maxContextLength || 8192,
        aliasesText: (m.aliases || []).join(', '),
        _existing: true
      };
    },

    async saveModel() {
      try {
        const payload = this.modelPayload();
        if (!payload.id || !payload.url) {
          this.setError('Validation', 'Model id and url are required.');
          return;
        }
        if (/localhost|127\.0\.0\.1/.test(payload.url)) {
          this.setError(
            'Upstream URL',
            'Use http://host.docker.internal:<port> when the gateway runs in Docker (not localhost).');
          return;
        }
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
      } catch (e) { this.handleCatch(e); }
    },

    async removeModel(id) {
      if (!confirm('Remove model ' + id + '?')) return;
      try {
        const body = await this.apiJson('/admin/api/models/' + encodeURIComponent(id), { method: 'DELETE' });
        if (body?.success === false) {
          this.setError('Remove failed', body.message || 'Could not remove model.');
          return;
        }
        this.setSuccess(body?.message || 'Model removed.');
        await this.loadModels();
        await this.loadBackends();
      } catch (e) { this.handleCatch(e); }
    },

    async loadKeys() {
      try {
        this.keys = (await this.apiJson('/admin/api/keys')) ?? [];
        this.clearMessages();
      } catch (e) { this.handleCatch(e); }
    },

    async createKey() {
      try {
        const body = await this.apiJson('/admin/api/keys', {
          method: 'POST',
          body: JSON.stringify({ role: this.newKey.role, scopes: [] })
        });
        this.createdKey = body?.secret || '';
        this.setSuccess('API key created — copy the secret now.');
        await this.loadKeys();
      } catch (e) { this.handleCatch(e); }
    },

    async revokeKey(id) {
      if (!confirm('Revoke this API key?')) return;
      try {
        await this.apiFetch('/admin/api/keys/' + id + '/revoke', { method: 'POST' });
        await this.loadKeys();
      } catch (e) { this.handleCatch(e); }
    },

    async downloadExport(format) {
      try {
        const res = await fetch('/admin/api/usage/export?format=' + format, { headers: this.headers() });
        if (!res.ok) throw new Error(res.status + ' ' + res.statusText);
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'usage-export.' + (format === 'csv' ? 'csv' : 'json');
        a.click();
        URL.revokeObjectURL(url);
      } catch (e) { this.handleCatch(e); }
    }
  };
}
