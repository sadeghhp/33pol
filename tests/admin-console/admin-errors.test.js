/**
 * Regression tests for the admin console's error classifier.
 *
 * `admin-errors.js` is a plain browser IIFE with no imports, so it runs under node's built-in test
 * runner with nothing installed:
 *
 *     node --test tests/admin-console/
 *
 * What is pinned here is the classifier's contract with the rest of the console, because three
 * separate defects all came from it being wrong:
 *
 *   - `credentialRejected` gates the one transition that stops polling and drops the live stream.
 *     Every 401 used to assert it, so a single endpoint refusing a request took down the whole
 *     session and told the operator their key had been rejected when it had not been.
 *   - `global: false` used to mean "show nothing at all", so a 400 or 409 whose body said exactly
 *     what was wrong vanished without trace.
 *   - a 2xx that was not JSON never reached the classifier at all; it threw a bare SyntaxError out
 *     of JSON.parse, past both the classifier and the unhandledrejection net.
 */
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

global.window = {};
require(path.join(__dirname, '../../src/33pol.App/wwwroot/admin/admin-errors.js'));
const { classifyError } = global.window.AdminErrors;

const json = value => JSON.stringify(value);

test('401 is only a credential rejection when the gateway says so', async t => {
  await t.test('the X-33pol-Error-Code header is proof', () => {
    for (const code of ['invalid_api_key', 'expired_api_key']) {
      const result = classifyError(401, 'Unauthorized', '', { gatewayErrorCode: code });
      assert.equal(result.credentialRejected, true, code);
    }
  });

  await t.test('the OpenAI-shaped body is proof', () => {
    const body = json({ error: { type: 'authentication_error', message: 'Invalid or missing API key.' } });
    assert.equal(classifyError(401, 'Unauthorized', body, {}).credentialRejected, true);
  });

  await t.test('a bare 401 is not proof', () => {
    // The regression: this used to stop the 2s poll, tear down the live stream and freeze the
    // Overview on figures it kept presenting as current.
    assert.equal(classifyError(401, 'Unauthorized', '', {}).credentialRejected, false);
    assert.equal(classifyError(401, 'Unauthorized', '{}', {}).credentialRejected, false);
  });

  await t.test('another endpoint\'s error code is not proof', () => {
    const result = classifyError(401, 'Unauthorized', '', { gatewayErrorCode: 'model_not_found' });
    assert.equal(result.credentialRejected, false);
  });
});

test('403 is never a credential problem and never page-wide', async t => {
  await t.test('the tenant-scope refusal keeps the server\'s explanation', () => {
    const body = json({ code: 'tenant_context_required', message: 'Configure ConnectionStrings:GatewayDb.' });
    const result = classifyError(403, 'Forbidden', body, {});

    assert.equal(result.credentialRejected, false);
    assert.equal(result.global, false);
    assert.equal(result.message, 'Configure ConnectionStrings:GatewayDb.');
    assert.equal(result.section, 'tenant_context_required');
  });

  await t.test('an OpenAI-shaped 403 is handled too', () => {
    const body = json({ error: { type: 'permission_error', message: 'No permission for this operation.' } });
    const result = classifyError(403, 'Forbidden', body, {});

    assert.equal(result.credentialRejected, false);
    assert.equal(result.global, false);
    assert.equal(result.message, 'No permission for this operation.');
  });

  await t.test('a 403 with no body still says something useful', () => {
    const result = classifyError(403, 'Forbidden', '', {});
    assert.match(result.message, /not permitted/i);
    assert.equal(result.global, false);
  });
});

test('a successful status carrying something other than JSON is normalised', async t => {
  await t.test('an intercepting sign-in page', () => {
    const result = classifyError(
      200, 'Unexpected response', '<!DOCTYPE html><html><body>Sign in</body></html>', {});

    assert.equal(result.title, 'Unexpected response');
    assert.match(result.message, /web page instead of data/i);
    assert.equal(result.global, true);
    assert.equal(result.credentialRejected, false);
  });

  await t.test('a bare HTML body without a doctype', () => {
    const result = classifyError(200, 'Unexpected response', '<html><body>nope</body></html>', {});
    assert.match(result.message, /web page instead of data/i);
  });

  await t.test('a truncated JSON body', () => {
    const result = classifyError(200, 'Unexpected response', '{"summary": ', {});
    assert.equal(result.title, 'Unexpected response');
    assert.match(result.message, /could not read/i);
    assert.equal(result.global, true);
  });

  await t.test('plain text', () => {
    const result = classifyError(200, 'Unexpected response', 'Gateway Timeout', {});
    assert.equal(result.title, 'Unexpected response');
    assert.equal(result.global, true);
  });

  await t.test('the body is carried as detail so it can be inspected', () => {
    const result = classifyError(200, 'Unexpected response', '<html>portal</html>', {});
    assert.match(result.detail, /portal/);
  });
});

test('4xx bodies that explain themselves keep their explanation', async t => {
  await t.test('a 400 with a message', () => {
    const body = json({ message: 'Label must be 64 characters or fewer.' });
    const result = classifyError(400, 'Bad Request', body, {});

    assert.equal(result.message, 'Label must be 64 characters or fewer.');
    // Not page-wide — but handleCatch must still surface it; that half is pinned in the browser
    // probe, since it needs the store.
    assert.equal(result.global, false);
  });

  await t.test('a 409 key-lifecycle conflict', () => {
    const body = json({ code: 'key_has_usage', message: 'This key has been used.' });
    const result = classifyError(409, 'Conflict', body, {});

    assert.equal(result.title, 'This key has been used');
    assert.equal(result.message, 'This key has been used.');
    assert.equal(result.global, false);
  });
});

test('a 5xx is still page-wide', () => {
  const result = classifyError(500, 'Internal Server Error', json({ message: 'boom' }), {});
  assert.equal(result.global, true);
});
