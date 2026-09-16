document.addEventListener('alpine:init', () => {
  /**
   * Retry pacing. Only statuses that mean "not now, try again" are retried, and only on requests
   * that already carry a retry budget (GET). Everything else still surfaces on the first response,
   * exactly as before.
   */
  const RETRYABLE_STATUS = new Set([429, 503]);
  const RETRY_BASE_MS = 250;
  const RETRY_JITTER = 0.25;
  /** Never park a caller longer than the 2s poll's own cadence. */
  const RETRY_MAX_WAIT_MS = 2000;

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
    /** Single-flight guard for the recheck a bare 401 triggers. See requestConnectionRecheck. */
    _recheckInFlight: false,
    /** Bound by startConnectionWatch so a recheck can reach verifyConnection with its arguments. */
    _verifyConnection: null,
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

    /**
     * The credential header, omitted entirely when there is none.
     *
     * An empty `X-API-Key` is not the same as no header: it presents a credential and gets it
     * refused, which is a rejected sign-in as far as the gateway's auth-failure budget and its
     * audit trail are concerned. A signed-out console should be anonymous, not wrong.
     */
    headers() {
      return this.apiKey ? { 'X-API-Key': this.apiKey } : {};
    },

    jsonHeaders() {
      return { ...this.headers(), 'Content-Type': 'application/json' };
    },

    /**
     * Turns a failed response into the console's normalised error, and decides what it means for
     * the session as a whole.
     *
     * Only a definite credential rejection may declare the session dead. Any 401 used to do that,
     * which meant one endpoint refusing a request stopped the 2s poll, tore down the live stream,
     * froze the Overview on figures it kept presenting as current, and told the operator to sign in
     * again with a key that was never the problem. An unproven 401 now asks the connection watchdog
     * to check the key — one authoritative request — and leaves everything running meanwhile.
     */
    classifyAndThrow(status, statusText, text, editModelUrl, gatewayErrorCode) {
      const err = window.AdminErrors.classifyError(
        status, statusText, text, { editModelUrl, gatewayErrorCode });
      const e = new Error(err.message);
      // The HTTP status, so a caller that needs to branch on one can test it instead of matching the
      // number inside the rendered message — which is how a wording change becomes a silent bug.
      e.status = status;
      e.title = err.title;
      e.detail = err.detail;
      e.global = err.global;
      e.section = err.section;
      // True only when the gateway said the credential itself was refused. Gates the one transition
      // that stops polling and drops the live stream, so it must never be inferred.
      e.credentialRejected = err.credentialRejected === true;

      if (e.credentialRejected) {
        this.connectionStatus = 'fail';
        this.connectionDegraded = true;
      } else if (status === 401) {
        // Degraded, not dead: the badge says the session could not be confirmed while the recheck
        // settles it, and polling carries on in the meantime.
        this.connectionDegraded = true;
        this.requestConnectionRecheck();
      }
      throw e;
    },

    /**
     * Asks the watchdog to settle the question a bare 401 could not.
     *
     * Single-flight and deterministic: concurrent 401s produce one probe, and the probe's own
     * answer — not the request that raised the doubt — is what may set the session to failed.
     */
    requestConnectionRecheck() {
      if (this._recheckInFlight || !this.apiKey || !this._verifyConnection) return;
      this._recheckInFlight = true;
      Promise.resolve()
        .then(() => this._verifyConnection())
        .catch(() => { /* verifyConnection records its own outcome */ })
        .finally(() => { this._recheckInFlight = false; });
    },

    _sleep(ms) {
      return ms > 0 ? new Promise(resolve => setTimeout(resolve, ms)) : Promise.resolve();
    },

    /**
     * How long to wait before the next attempt.
     *
     * A server that states Retry-After is answered on its own terms, capped: the 2s poll calls
     * through here, and parking a tick for the 60s a rate limiter might ask for would freeze the
     * live vitals for half a minute and stack up timers behind it. Without that header the wait is
     * exponential from 250ms with +/-25% jitter — the jitter matters because several panels refresh
     * on the same tick, and a fixed backoff would have them all retry in lockstep.
     */
    retryDelayMs(attempt, response) {
      const header = response?.headers?.get?.('Retry-After');
      if (header) {
        const seconds = Number(header);
        const ms = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(header) - Date.now();
        if (Number.isFinite(ms) && ms >= 0) return Math.min(ms, RETRY_MAX_WAIT_MS);
      }
      const base = RETRY_BASE_MS * Math.pow(2, attempt);
      const jitter = base * RETRY_JITTER * (Math.random() * 2 - 1);
      return Math.max(0, Math.round(Math.min(base + jitter, RETRY_MAX_WAIT_MS)));
    },

    async fetchWithRetry(url, options, editModelUrl, retries, readBodyAsText) {
      const max = retries ?? 1;
      const asText = readBodyAsText !== false;
      let lastErr;
      for (let i = 0; i <= max; i++) {
        try {
          const res = await fetch(url, options);
          if (!res.ok) {
            // Retried only while a budget remains, which apiFetch grants to GET and never to a
            // mutation — so a POST that may already have been applied is still never replayed.
            // Checked ahead of classifyAndThrow so a transient 503 cannot touch connection state or
            // raise a banner for a request that is about to succeed.
            if (i < max && RETRYABLE_STATUS.has(res.status)) {
              const wait = this.retryDelayMs(i, res);
              // Drain before waiting. An abandoned body keeps its connection checked out of the
              // browser's per-origin pool until GC gets to it, which on the 2s poll is exactly the
              // wrong moment to be one connection short.
              try { await res.text(); } catch { /* already consumed or torn down */ }
              await this._sleep(wait);
              continue;
            }
            const text = asText ? await res.text() : '';
            this.classifyAndThrow(
              res.status, res.statusText, text, editModelUrl,
              res.headers?.get?.('X-33pol-Error-Code'));
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
          // The retry used to be immediate, which meant a gateway still coming up was hit twice
          // inside a millisecond and reported as down.
          await this._sleep(this.retryDelayMs(i));
        }
      }
      throw lastErr;
    },

    async apiFetch(url, options = {}, editModelUrl) {
      const method = (options.method || 'GET').toUpperCase();
      const retry = method === 'GET' ? 1 : 0;
      // Content-Type describes a body. A GET, or a DELETE that carries none, has nothing to
      // describe, and declaring a type there is how a same-origin request picks up a preflight and
      // a content negotiation it never needed.
      const hasBody = options.body !== undefined && options.body !== null;
      return this.fetchWithRetry(url, {
        ...options,
        headers: { ...(hasBody ? this.jsonHeaders() : this.headers()), ...(options.headers || {}) }
      }, editModelUrl, retry);
    },

    /**
     * A JSON read that cannot throw an unclassified error.
     *
     * The parse used to be bare, so a 200 carrying something other than JSON — an SSO or captive
     * portal interception page, a proxy error page, a truncated body — produced a raw SyntaxError.
     * That error carries none of the console's own fields, so it slipped past both the classifier
     * and the unhandledrejection net in init(), and surfaced (if at all) as
     * "Unexpected token < in JSON at position 0". Routed through classifyAndThrow it becomes the
     * same normalised error as any other failure, and admin-errors.js already recognises an HTML
     * body and says what it means.
     */
    async apiJson(url, options = {}, editModelUrl) {
      const res = await this.apiFetch(url, options, editModelUrl);
      const text = res._bodyText ?? '';
      if (!text) return null;
      try {
        return JSON.parse(text);
      } catch {
        // Reported against the response that actually arrived: a 200 whose body is not JSON is a
        // gateway that did not answer, whoever ended up answering instead.
        this.classifyAndThrow(
          res.status,
          res.status === 200 ? 'Unexpected response' : res.statusText,
          text,
          editModelUrl,
          res.headers?.get?.('X-33pol-Error-Code'));
      }
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
        // This probe is the authority on the key: it asks the one endpoint every admin credential
        // can reach, so a 401 here is about the credential and nothing else. Keyed on the status
        // rather than the rendered title, and deliberately not on `credentialRejected` — a gateway
        // or proxy that omits the error-code header must still be able to say the key is dead.
        if (e && (e.status === 401 || e.credentialRejected === true)) {
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
      // Captured so an unproven 401 anywhere in the console can ask for the same authoritative
      // check the timer and the focus listener run, rather than deciding on its own.
      this._verifyConnection = () => this.verifyConnection(editModelUrl);
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
      this._verifyConnection = null;
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
