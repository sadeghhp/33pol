document.addEventListener('alpine:init', () => {
  Alpine.store('admin', {
    apiKey: localStorage.getItem('33pol-admin-key') || '',
    loading: false,
    loadingMessage: '',
    connectionStatus: '',
    error: '',
    errorTitle: '',
    errorDetail: '',
    successMessage: '',

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
      this.scrollToAlert();
    },

    dismissError() {
      this.clearMessages();
    },

    setSuccess(message) {
      this.clearMessages();
      this.successMessage = message;
    },

    scrollToAlert() {
      requestAnimationFrame(() => {
        const el = document.getElementById('global-alert');
        if (el) {
          el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
      });
    },

    parseJsonBody(text) {
      if (!text || !text.trim().startsWith('{')) return null;
      try { return JSON.parse(text); } catch { return null; }
    },

    friendlyApiError(status, statusText, text, editModelUrl) {
      const json = this.parseJsonBody(text);
      if (json?.message) {
        return {
          title: status + ' ' + (json.success === false ? 'Failed' : statusText),
          message: json.message,
          detail: json.success === false ? null : text
        };
      }
      if (json?.title && json?.detail) {
        const detailText = json.detail === json.title ? null : text;
        return { title: json.title, message: json.detail, detail: detailText };
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
      if (editModelUrl && /localhost|127\.0\.0\.1/.test(editModelUrl)) {
        return {
          title: status + ' ' + statusText,
          message: (raw || statusText) + ' — From Docker, use http://host.docker.internal:<port> instead of localhost.',
          detail: null
        };
      }
      return { title: status + ' ' + statusText, message: raw || statusText, detail: null };
    },

    headers() {
      return { 'X-API-Key': this.apiKey, 'Content-Type': 'application/json' };
    },

    async apiFetch(url, options = {}, editModelUrl) {
      const res = await fetch(url, {
        ...options,
        headers: { ...this.headers(), ...(options.headers || {}) }
      });
      const text = await res.text();
      if (!res.ok) {
        const err = this.friendlyApiError(res.status, res.statusText, text, editModelUrl);
        const e = new Error(err.message);
        e.title = err.title;
        e.detail = err.detail;
        throw e;
      }
      res._bodyText = text;
      return res;
    },

    async apiJson(url, options = {}, editModelUrl) {
      const res = await this.apiFetch(url, options, editModelUrl);
      const text = res._bodyText ?? '';
      if (!text) return null;
      return JSON.parse(text);
    },

    async withLoading(message, fn) {
      this.loading = true;
      this.loadingMessage = message || '';
      try {
        return await fn();
      } finally {
        this.loading = false;
        this.loadingMessage = '';
      }
    },

    persistApiKey(key) {
      this.apiKey = key || '';
      if (key) {
        localStorage.setItem('33pol-admin-key', key);
      } else {
        localStorage.removeItem('33pol-admin-key');
      }
    },

    async verifyConnection(editModelUrl) {
      if (!this.apiKey) {
        this.connectionStatus = '';
        return;
      }
      try {
        await this.apiJson('/admin/api/config/status', {}, editModelUrl);
        this.connectionStatus = 'ok';
      } catch {
        this.connectionStatus = 'fail';
      }
    }
  });
});
