export interface ClassifiedError {
  title: string;
  message: string;
  detail: string | null;
  credentialRejected?: boolean;
  global: boolean;
  section?: string | null;
}

export interface ClassifyErrorContext {
  editModelUrl?: string;
  gatewayErrorCode?: string;
}

/**
 * Error codes the gateway sends on an actual credential rejection, in the X-33pol-Error-Code
 * header (ApiKeyAuthenticationHandler.HandleChallengeAsync). Their presence is the only positive
 * proof the console has that the key itself is the problem, rather than this one request.
 */
export const CREDENTIAL_REJECTION_CODES = new Set(['invalid_api_key', 'expired_api_key']);

export function parseJsonBody(text: string): Record<string, unknown> | null {
  if (!text || !text.trim().startsWith('{')) return null;
  try {
    return JSON.parse(text) as Record<string, unknown>;
  } catch {
    return null;
  }
}

export function classifyError(
  status: number,
  statusText: string,
  text: string,
  context?: ClassifyErrorContext,
): ClassifiedError {
  const editModelUrl = context?.editModelUrl || '';
  const gatewayErrorCode = (context?.gatewayErrorCode || '').trim().toLowerCase();
  const json = parseJsonBody(text);
  const errorObj = json?.error as Record<string, unknown> | undefined;

  if (status === 401) {
    const rejected =
      CREDENTIAL_REJECTION_CODES.has(gatewayErrorCode) ||
      errorObj?.type === 'authentication_error';
    return {
      title: 'Authentication failed',
      message: 'Invalid or missing admin API key.',
      detail: (json?.detail as string) || null,
      credentialRejected: rejected,
      global: true,
    };
  }

  if (status === 403) {
    const message =
      (json?.message as string) ||
      (errorObj?.message as string) ||
      (json?.detail as string) ||
      'This admin key is not permitted to perform that action.';
    return {
      title: json?.code === 'tenant_context_required' ? 'No tenant for this key' : 'Not permitted',
      message,
      detail: null,
      credentialRejected: false,
      global: false,
      section: (json?.code as string) || 'forbidden',
    };
  }

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
      global: true,
    };
  }

  if (status === 409 && json?.code && json?.message) {
    const titles: Record<string, string> = {
      key_has_usage: 'This key has been used',
      key_not_revoked: 'Revoke the key first',
      already_archived: 'Already archived',
      not_archived: 'Not archived',
      self_action: 'Not allowed on your own key',
      last_admin_key: 'Last admin key',
    };
    return {
      title: titles[json.code as string] || 'Action not allowed',
      message: json.message as string,
      detail: null,
      global: false,
    };
  }

  if (json?.message) {
    const isEnvToken =
      status === 400 && /environment variable|Missing API token|envVar/i.test(json.message as string);
    return {
      title: isEnvToken
        ? 'Provider key not configured'
        : status + ' ' + (json.success === false ? 'Failed' : statusText),
      message: json.message as string,
      detail: json.success === false ? null : text,
      global: status >= 500,
      section: isEnvToken ? 'provider' : null,
    };
  }

  if (json?.title && json?.detail) {
    const detailText = json.detail === json.title ? null : text;
    const is405 = status === 405;
    return {
      title: is405 ? 'Action not supported' : (json.title as string),
      message: is405
        ? 'Provider discovery requires POST. Hard-refresh the page (Ctrl+Shift+R) if this persists after an upgrade.'
        : (json.detail as string),
      detail: detailText,
      global: status >= 500,
      section: is405 ? 'provider' : null,
    };
  }

  const raw = (text || '').trim();
  const isHtml = raw.startsWith('<') || raw.includes('<!DOCTYPE');
  const isStack = raw.includes(' at ') && raw.includes(' in ');

  if (isHtml || isStack) {
    let hint = 'The gateway returned an unexpected error.';
    if (raw.includes('Device or resource busy') || raw.includes('models.json')) {
      hint =
        'Cannot write models.json — registry file may be read-only (Docker read-only mount). Use a writable volume or edit models.json on the host, then reload config from Settings.';
    } else if (raw.includes('Unauthorized') || status === 401) {
      hint = 'Invalid or missing admin API key.';
    }
    const firstLine = raw.split('\n').find((l) => l.trim() && !l.startsWith('<')) || '';
    return {
      title: status === 503 ? 'Could not save registry' : status + ' ' + statusText,
      message: status === 503 ? hint : hint,
      detail: firstLine || raw.slice(0, 2000),
      global: true,
    };
  }

  if (editModelUrl && /localhost|127\.0\.0\.1/.test(editModelUrl)) {
    return {
      title: status + ' ' + statusText,
      message: (raw || statusText) + ' — From Docker, use http://host.docker.internal:<port> instead of localhost.',
      detail: null,
      global: false,
    };
  }

  if (status === 0 || statusText === 'Failed to fetch') {
    return {
      title: 'Cannot reach gateway',
      message: 'Check that the gateway is running, the URL is correct, and your network or VPN allows access.',
      detail: raw || null,
      global: true,
    };
  }

  return {
    title: status + ' ' + statusText,
    message: raw || statusText,
    detail: null,
    global: status >= 500,
  };
}
