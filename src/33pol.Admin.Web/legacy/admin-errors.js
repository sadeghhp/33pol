/* global window */
(function () {
  function parseJsonBody(text) {
    if (!text || !text.trim().startsWith('{')) return null;
    try { return JSON.parse(text); } catch { return null; }
  }

  /**
   * Error codes the gateway sends on an actual credential rejection, in the X-33pol-Error-Code
   * header (ApiKeyAuthenticationHandler.HandleChallengeAsync). Their presence is the only positive
   * proof the console has that the key itself is the problem, rather than this one request.
   */
  const CREDENTIAL_REJECTION_CODES = new Set(['invalid_api_key', 'expired_api_key']);

  function classifyError(status, statusText, text, context) {
    const editModelUrl = context?.editModelUrl || '';
    const gatewayErrorCode = (context?.gatewayErrorCode || '').trim().toLowerCase();
    const json = parseJsonBody(text);

    if (status === 401) {
      // `credentialRejected` gates a global teardown of the session, so it asserts only what the
      // gateway actually said. Without that proof the caller re-verifies the key instead of
      // assuming the worst — a 401 from one endpoint used to stop polling and drop the live stream
      // for the whole console, including the panels that were working.
      const rejected = CREDENTIAL_REJECTION_CODES.has(gatewayErrorCode)
        || json?.error?.type === 'authentication_error';
      return {
        title: 'Authentication failed',
        message: 'Invalid or missing admin API key.',
        detail: json?.detail || null,
        credentialRejected: rejected,
        global: true
      };
    }

    // Authenticated, but not for this. Never a credential problem, so it must not touch the
    // session: the request the operator made failed, and nothing else has.
    if (status === 403) {
      const message = json?.message
        || json?.error?.message
        || json?.detail
        || 'This admin key is not permitted to perform that action.';
      return {
        title: json?.code === 'tenant_context_required' ? 'No tenant for this key' : 'Not permitted',
        message,
        detail: null,
        credentialRejected: false,
        // Section-level: shown where the operator was working rather than as a page-wide banner,
        // because the rest of the console is unaffected and still live.
        global: false,
        section: json?.code || 'forbidden'
      };
    }

    // A 2xx whose body is not JSON. Only reachable from apiJson's parse guard, and almost always
    // something between the browser and the gateway answering instead of it — an SSO or captive
    // portal page, a proxy error page — so say that rather than blame the gateway for a body it
    // never sent.
    if (status >= 200 && status < 300) {
      const body = (text || '').trim();
      const looksLikeAPage = body.startsWith('<') || /<!DOCTYPE/i.test(body);
      return {
        title: 'Unexpected response',
        message: looksLikeAPage
          ? 'The gateway returned a web page instead of data. Something between this browser and '
            + 'the gateway answered the request — a proxy, or a sign-in page. Check the connection, '
            + 'and sign in again if a portal is involved.'
          : 'The gateway returned a response this console could not read.',
        detail: body.slice(0, 2000) || null,
        credentialRejected: false,
        global: true
      };
    }

    // API key lifecycle conflicts are expected, informative outcomes rather than faults: the server
    // has already written the sentence the operator needs, so show that instead of "409 Conflict".
    if (status === 409 && json?.code && json?.message) {
      const titles = {
        key_has_usage: 'This key has been used',
        key_not_revoked: 'Revoke the key first',
        already_archived: 'Already archived',
        not_archived: 'Not archived',
        self_action: 'Not allowed on your own key',
        last_admin_key: 'Last admin key'
      };
      return {
        title: titles[json.code] || 'Action not allowed',
        message: json.message,
        detail: null,
        global: false
      };
    }

    if (json?.message) {
      const isEnvToken = status === 400 && /environment variable|Missing API token|envVar/i.test(json.message);
      return {
        title: isEnvToken ? 'Provider key not configured' : (status + ' ' + (json.success === false ? 'Failed' : statusText)),
        message: json.message,
        detail: json.success === false ? null : text,
        global: status >= 500,
        section: isEnvToken ? 'provider' : null
      };
    }

    if (json?.title && json?.detail) {
      const detailText = json.detail === json.title ? null : text;
      const is405 = status === 405;
      return {
        title: is405 ? 'Action not supported' : json.title,
        message: is405
          ? 'Provider discovery requires POST. Hard-refresh the page (Ctrl+Shift+R) if this persists after an upgrade.'
          : json.detail,
        detail: detailText,
        global: status >= 500,
        section: is405 ? 'provider' : null
      };
    }

    const raw = (text || '').trim();
    const isHtml = raw.startsWith('<') || raw.includes('<!DOCTYPE');
    const isStack = raw.includes(' at ') && raw.includes(' in ');

    if (isHtml || isStack) {
      let hint = 'The gateway returned an unexpected error.';
      if (raw.includes('Device or resource busy') || raw.includes('models.json')) {
        hint = 'Cannot write models.json — registry file may be read-only (Docker read-only mount). Use a writable volume or edit models.json on the host, then reload config from Settings.';
      } else if (raw.includes('Unauthorized') || status === 401) {
        hint = 'Invalid or missing admin API key.';
      }
      const firstLine = raw.split('\n').find(l => l.trim() && !l.startsWith('<')) || '';
      return {
        title: status === 503 ? 'Could not save registry' : (status + ' ' + statusText),
        message: status === 503 ? hint : hint,
        detail: firstLine || raw.slice(0, 2000),
        global: true
      };
    }

    if (editModelUrl && /localhost|127\.0\.0\.1/.test(editModelUrl)) {
      return {
        title: status + ' ' + statusText,
        message: (raw || statusText) + ' — From Docker, use http://host.docker.internal:<port> instead of localhost.',
        detail: null,
        global: false
      };
    }

    if (status === 0 || statusText === 'Failed to fetch') {
      return {
        title: 'Cannot reach gateway',
        message: 'Check that the gateway is running, the URL is correct, and your network or VPN allows access.',
        detail: raw || null,
        global: true
      };
    }

    return {
      title: status + ' ' + statusText,
      message: raw || statusText,
      detail: null,
      global: status >= 500
    };
  }

  window.AdminErrors = { parseJsonBody, classifyError };
})();
