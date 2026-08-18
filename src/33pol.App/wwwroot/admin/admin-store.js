document.addEventListener('alpine:init', () => {
  const emptyLoading = () => ({
    overview: false,
    usage: false,
    routingModels: false,
    routingBackends: false,
    keys: false,
    settings: false,
    logs: false,
    errors: false,
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
    _focusHandler: null,
    _loadingDepth: {},

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
      const depth = (this._loadingDepth[scope] || 0) + 1;
      this._loadingDepth[scope] = depth;
      const first = depth === 1;
      if (first) {
        this.loading = { ...this.loading, [scope]: true };
        this.loadingMessage = message || '';
      }
      try {
        return await fn();
      } finally {
        const next = (this._loadingDepth[scope] || 1) - 1;
        if (next <= 0) {
          delete this._loadingDepth[scope];
          this.loading = { ...this.loading, [scope]: false };
          if (!this.anyLoading()) this.loadingMessage = '';
        } else {
          this._loadingDepth[scope] = next;
        }
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

    /** Saves the response body; the server's Content-Disposition filename wins over the fallback. */
    async downloadBlob(url, fallbackFilename, editModelUrl) {
      const res = await this.fetchWithRetry(
        url, { headers: this.headers() }, editModelUrl, 0, false);
      const blob = await res.blob();
      const objectUrl = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = objectUrl;
      a.download = this.filenameFromDisposition(res.headers.get('Content-Disposition')) || fallbackFilename;
      a.click();
      // Revoke on the next tick: revoking synchronously after a programmatic click can abort the
      // download in Safari / older Firefox before the navigation has started.
      setTimeout(() => URL.revokeObjectURL(objectUrl), 0);
      return res;
    },

    filenameFromDisposition(header) {
      if (!header) return '';
      const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
      if (star) { try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, '')); } catch { /* fall through */ } }
      const plain = /filename="?([^";]+)"?/i.exec(header);
      return plain ? plain[1].trim() : '';
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

    /**
     * Probes the control plane with the current key, or with `candidateKey` when given.
     *
     * Current key: only a 401 marks the connection `fail` (which halts polling and the live stream
     * until the key is changed or the watchdog re-checks); any other failure — a network blip, a
     * 5xx, a proxy hiccup — is `degraded` and polling keeps running so the page recovers on its own.
     *
     * Candidate key: the probe runs with the candidate in the header override, and the key is
     * persisted ONLY when the probe succeeds, so a mistyped key never replaces a working one in
     * localStorage. On failure the previous connection state is restored and the error is rethrown
     * for the caller (saveKey) to report.
     */
    async verifyConnection(editModelUrl, candidateKey) {
      const candidate = (candidateKey || '').trim();
      if (!candidate && !this.apiKey) {
        this.connectionStatus = '';
        this.connectionDegraded = false;
        return false;
      }
      const prevStatus = this.connectionStatus;
      const prevDegraded = this.connectionDegraded;
      try {
        const options = candidate ? { headers: { 'X-API-Key': candidate } } : {};
        await this.apiJson('/admin/api/config/status', options, editModelUrl);
        if (candidate) this.persistApiKey(candidate);
        this.connectionStatus = 'ok';
        this.connectionDegraded = false;
        return true;
      } catch (e) {
        if (candidate) {
          // classifyAndThrow may have flagged a 401 for the candidate; that says nothing about the
          // key that is still in use, so put the previous state back and let the caller report.
          this.connectionStatus = prevStatus;
          this.connectionDegraded = prevDegraded;
          throw e;
        }
        if (e && e.title === 'Authentication failed') {
          this.connectionStatus = 'fail';
          this.connectionDegraded = true;
          // The dedicated "key rejected" banner takes over from the generic error alert.
          if (this.errorTitle === 'Authentication failed') this.clearMessages();
        } else {
          // Transient: keep the last known status, flag degraded, keep polling.
          this.connectionDegraded = true;
        }
        return false;
      }
    },

    startConnectionWatch(editModelUrl) {
      this.stopConnectionWatch();
      this._connectionTimer = setInterval(() => {
        if (document.hidden || !this.apiKey) return;
        this.verifyConnection(editModelUrl).catch(() => {});
      }, 5 * 60 * 1000);
      // Exactly one focus listener, tracked so stopConnectionWatch can remove it (each Connect /
      // Change-key used to add another one that fired for the rest of the session).
      this._focusHandler = () => {
        if (this.apiKey) this.verifyConnection(editModelUrl).catch(() => {});
      };
      window.addEventListener('focus', this._focusHandler);
    },

    stopConnectionWatch() {
      if (this._connectionTimer) {
        clearInterval(this._connectionTimer);
        this._connectionTimer = null;
      }
      if (this._focusHandler) {
        window.removeEventListener('focus', this._focusHandler);
        this._focusHandler = null;
      }
    }
  });
});
