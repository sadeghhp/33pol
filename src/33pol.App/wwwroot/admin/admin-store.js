document.addEventListener('alpine:init', () => {
  const emptyLoading = () => ({
    overview: false,
    usage: false,
    routingModels: false,
    routingBackends: false,
    keys: false,
    settings: false,
    providerFetch: false,
    auth: false
  });

  Alpine.store('admin', {
    apiKey: localStorage.getItem('33pol-admin-key') || '',
    loading: emptyLoading(),
    loadingMessage: '',
    connectionStatus: '',
    connectionDegraded: false,
    error: '',
    errorTitle: '',
    errorDetail: '',
    toasts: [],
    _toastId: 0,
    _connectionTimer: null,

    clearMessages() {
      this.error = '';
      this.errorTitle = '';
      this.errorDetail = '';
    },

    setError(title, message, detail) {
      this.errorTitle = title || 'Error';
      this.error = message || 'Something went wrong.';
      this.errorDetail = detail || '';
      this.scrollToAlert();
    },

    setGlobalError(title, message, detail) {
      this.setError(title, message, detail);
    },

    dismissError() {
      this.clearMessages();
    },

    pushToast(message, type) {
      if (!message) return;
      const id = ++this._toastId;
      this.toasts = [...this.toasts.slice(-2), { id, message, type: type || 'success' }];
      setTimeout(() => {
        this.toasts = this.toasts.filter(t => t.id !== id);
      }, 3000);
    },

    scrollToAlert() {
      requestAnimationFrame(() => {
        const el = document.getElementById('global-alert');
        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      });
    },

    isLoading(scope) {
      return !!this.loading[scope];
    },

    anyLoading() {
      return Object.values(this.loading).some(Boolean);
    },

    async withLoading(scope, message, fn) {
      if (this.loading[scope]) return;
      this.loading = { ...this.loading, [scope]: true };
      this.loadingMessage = message || '';
      try {
        return await fn();
      } finally {
        this.loading = { ...this.loading, [scope]: false };
        if (!this.anyLoading()) this.loadingMessage = '';
      }
    },

    headers() {
      const h = { 'X-API-Key': this.apiKey };
      return h;
    },

    jsonHeaders() {
      return { ...this.headers(), 'Content-Type': 'application/json' };
    },

    classifyAndThrow(status, statusText, text, editModelUrl) {
      const err = window.AdminErrors.classifyError(status, statusText, text, { editModelUrl });
      const e = new Error(err.message);
      e.title = err.title;
      e.detail = err.detail;
      e.global = err.global;
      e.section = err.section;
      if (status === 401) {
        this.connectionStatus = 'fail';
        this.connectionDegraded = true;
      }
      throw e;
    },

    async fetchWithRetry(url, options, editModelUrl, retries, readBodyAsText) {
      const max = retries ?? 1;
      const asText = readBodyAsText !== false;
      let lastErr;
      for (let i = 0; i <= max; i++) {
        try {
          const res = await fetch(url, options);
          if (!res.ok) {
            const text = asText ? await res.text() : '';
            this.classifyAndThrow(res.status, res.statusText, text, editModelUrl);
          }
          if (asText) {
            const text = await res.text();
            res._bodyText = text;
          }
          return res;
        } catch (e) {
          lastErr = e;
          if (e.title || e.global !== undefined) throw e;
          if (i === max) {
            this.classifyAndThrow(0, 'Failed to fetch', String(e), editModelUrl);
          }
        }
      }
      throw lastErr;
    },

    async apiFetch(url, options = {}, editModelUrl) {
      const method = (options.method || 'GET').toUpperCase();
      const retry = method === 'GET' ? 1 : 0;
      return this.fetchWithRetry(url, {
        ...options,
        headers: { ...this.jsonHeaders(), ...(options.headers || {}) }
      }, editModelUrl, retry);
    },

    async apiJson(url, options = {}, editModelUrl) {
      const res = await this.apiFetch(url, options, editModelUrl);
      const text = res._bodyText ?? '';
      if (!text) return null;
      return JSON.parse(text);
    },

    async downloadBlob(url, filename, editModelUrl) {
      const res = await this.fetchWithRetry(
        url, { headers: this.headers() }, editModelUrl, 0, false);
      const blob = await res.blob();
      const objectUrl = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = objectUrl;
      a.download = filename;
      a.click();
      URL.revokeObjectURL(objectUrl);
    },

    persistApiKey(key) {
      this.apiKey = key || '';
      if (key) localStorage.setItem('33pol-admin-key', key);
      else localStorage.removeItem('33pol-admin-key');
    },

    keyPrefix() {
      const k = this.apiKey || '';
      if (k.length <= 8) return k ? '••••' : '';
      return k.slice(0, 4) + '…' + k.slice(-4);
    },

    async verifyConnection(editModelUrl) {
      if (!this.apiKey) {
        this.connectionStatus = '';
        this.connectionDegraded = false;
        return false;
      }
      try {
        await this.apiJson('/admin/api/config/status', {}, editModelUrl);
        this.connectionStatus = 'ok';
        this.connectionDegraded = false;
        return true;
      } catch {
        this.connectionStatus = 'fail';
        this.connectionDegraded = true;
        return false;
      }
    },

    startConnectionWatch(editModelUrl) {
      if (this._connectionTimer) clearInterval(this._connectionTimer);
      this._connectionTimer = setInterval(() => {
        if (document.hidden || !this.apiKey) return;
        this.verifyConnection(editModelUrl);
      }, 5 * 60 * 1000);
      window.addEventListener('focus', () => {
        if (this.apiKey) this.verifyConnection(editModelUrl);
      });
    },

    stopConnectionWatch() {
      if (this._connectionTimer) {
        clearInterval(this._connectionTimer);
        this._connectionTimer = null;
      }
    }
  });
});
