import { describe, expect, it } from 'vitest';
import { classifyError, CREDENTIAL_REJECTION_CODES } from './errors';

describe('classifyError', () => {
  it('401_withInvalidApiKeyHeader_setsCredentialRejected', () => {
    const err = classifyError(401, 'Unauthorized', '', { gatewayErrorCode: 'invalid_api_key' });
    expect(err.credentialRejected).toBe(true);
    expect(err.title).toBe('Authentication failed');
    expect(err.global).toBe(true);
  });

  it('401_withAuthenticationErrorBody_setsCredentialRejected', () => {
    const body = JSON.stringify({ error: { type: 'authentication_error' } });
    const err = classifyError(401, 'Unauthorized', body);
    expect(err.credentialRejected).toBe(true);
  });

  it('401_withoutProof_doesNotSetCredentialRejected', () => {
    const err = classifyError(401, 'Unauthorized', '');
    expect(err.credentialRejected).toBe(false);
    expect(err.message).toContain('admin API key');
  });

  it('409_keyHasUsage_isInformativeNotFault', () => {
    const body = JSON.stringify({
      code: 'key_has_usage',
      message: 'This key recorded usage in the last 30 days.',
    });
    const err = classifyError(409, 'Conflict', body);
    expect(err.title).toBe('This key has been used');
    expect(err.message).toContain('30 days');
    expect(err.global).toBe(false);
  });

  it('409_lastAdminKey_usesLifecycleTitle', () => {
    const body = JSON.stringify({ code: 'last_admin_key', message: 'Cannot revoke the last admin key.' });
    const err = classifyError(409, 'Conflict', body);
    expect(err.title).toBe('Last admin key');
  });

  it('200_htmlBody_detectsInterceptionPage', () => {
    const body = '<!DOCTYPE html><html><body>Sign in</body></html>';
    const err = classifyError(200, 'OK', body);
    expect(err.title).toBe('Unexpected response');
    expect(err.message).toContain('web page');
    expect(err.detail).toContain('<!DOCTYPE');
  });

  it('exposesAllCredentialRejectionCodes', () => {
    expect(CREDENTIAL_REJECTION_CODES.has('invalid_api_key')).toBe(true);
    expect(CREDENTIAL_REJECTION_CODES.has('expired_api_key')).toBe(true);
  });
});
