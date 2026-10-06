import type { Page, Route } from '@playwright/test';

export const TEST_API_KEY = 'sk-e2e-test-admin-key';

const json = (body: unknown, status = 200) => ({
  status,
  contentType: 'application/json',
  body: JSON.stringify(body),
});

async function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill(json(body, status));
}

const MODELS_PAYLOAD = [
  {
    id: 'local-mock',
    modelType: 'mock',
    url: 'http://127.0.0.1/mock',
    state: 'serving',
    publicAccess: true,
    hasUpstreamCredential: false,
    pricing: null,
    aliases: [],
  },
];

export async function installAdminApiMocks(page: Page): Promise<void> {
  await page.route(/\/admin\/api\//, async (route) => {
    const url = new URL(route.request().url());
    const { pathname } = url;
    const method = route.request().method();

    if (pathname === '/admin/api/config/status') {
      await fulfillJson(route, {
        modelCount: 1,
        hotReloadEnabled: true,
        watchEnabled: false,
        lastReload: '2026-10-05T12:00:00Z',
      });
      return;
    }

    if (pathname === '/admin/api/summary') {
      await fulfillJson(route, { activeRequests: 0, totalErrors: 0, requestsPerMinute: 0 });
      return;
    }

    if (pathname === '/admin/api/requests') {
      await fulfillJson(route, { items: [] });
      return;
    }

    if (pathname.startsWith('/admin/api/live')) {
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (pathname.startsWith('/admin/api/overview/')) {
      await fulfillJson(route, {});
      return;
    }

    if (pathname === '/admin/api/usage' || pathname.startsWith('/admin/api/usage/')) {
      await fulfillJson(route, { items: [], total: 0 });
      return;
    }

    if (pathname === '/admin/api/logs') {
      if (method === 'DELETE') {
        await fulfillJson(route, { deleted: 0 });
        return;
      }
      await fulfillJson(route, { items: [], total: 0 });
      return;
    }

    if (pathname === '/admin/api/models' && method === 'GET') {
      await fulfillJson(route, MODELS_PAYLOAD);
      return;
    }

    if (pathname.match(/^\/admin\/api\/models\/[^/]+\/(stop|start)$/) && method === 'POST') {
      const action = pathname.endsWith('/stop') ? 'stop' : 'start';
      const id = decodeURIComponent(pathname.split('/')[4] ?? '');
      await fulfillJson(route, {
        success: true,
        message: action === 'stop' ? `Model "${id}" stopped.` : `Model "${id}" started.`,
      });
      return;
    }

    if (pathname === '/admin/api/backends') {
      await fulfillJson(route, [
        {
          modelId: 'local-mock',
          url: 'http://127.0.0.1/mock',
          isHealthy: true,
          state: 'serving',
          lastProbeAt: '2026-10-05T12:00:00Z',
        },
      ]);
      return;
    }

    if (pathname === '/admin/api/model-types') {
      await fulfillJson(route, [{ id: 'mock', label: 'Mock' }]);
      return;
    }

    if (pathname === '/admin/api/keys' && method === 'GET') {
      await fulfillJson(route, {
        items: [
          {
            id: 'key-1',
            keyPrefix: 'sk-live',
            role: 'Inference',
            label: 'Existing',
            active: true,
            isRevoked: false,
            isArchived: false,
            lastUsedAt: '2026-10-05T10:00:00Z',
            mtdRequests: 12,
            mtdCost: 1.25,
            assignee: 'ops',
            createdAt: '2026-09-01T08:00:00Z',
          },
        ],
      });
      return;
    }

    if (pathname === '/admin/api/keys' && method === 'POST') {
      await fulfillJson(route, {
        id: 'key-new',
        keyPrefix: 'sk-new',
        secret: 'sk-new-secret-shown-once',
      });
      return;
    }

    if (pathname === '/admin/api/cors') {
      if (method === 'PUT') {
        const payload = route.request().postDataJSON() as { allowedOrigins?: string[] };
        await fulfillJson(route, { message: 'CORS origins saved.', allowedOrigins: payload?.allowedOrigins ?? [] });
        return;
      }
      await fulfillJson(route, { allowedOrigins: ['https://example.com'] });
      return;
    }

    if (pathname.startsWith('/admin/api/errors/export')) {
      const format = url.searchParams.get('format') ?? 'json';
      const body = format === 'csv' ? 'fingerprint,code,count\nfp1,server_error,3\n' : '{"items":[]}';
      const contentType = format === 'csv' ? 'text/csv' : 'application/json';
      await route.fulfill({
        status: 200,
        contentType,
        headers: {
          'Content-Disposition': `attachment; filename="errors-export.${format}"`,
        },
        body,
      });
      return;
    }

    if (pathname === '/admin/api/errors/facets') {
      await fulfillJson(route, { models: [], statusCodes: [], errorCodes: [] });
      return;
    }

    if (pathname.startsWith('/admin/api/errors/groups')) {
      await fulfillJson(route, {
        groups: [
          {
            fingerprint: 'fp-server-error',
            code: 'server_error',
            message: 'Upstream refused connection',
            count: 3,
            lastSeenUtc: '2026-10-05T11:00:00Z',
            level: 'error',
          },
        ],
        total: 1,
      });
      return;
    }

    if (pathname.startsWith('/admin/api/errors')) {
      if (method === 'DELETE') {
        await fulfillJson(route, { deleted: 1 });
        return;
      }
      await fulfillJson(route, { occurrences: [] });
      return;
    }

    if (pathname === '/admin/api/rate-limits') {
      await fulfillJson(route, {
        version: 1,
        enabled: true,
        adaptiveEnabled: false,
        default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
        plans: {},
        rules: [],
      });
      return;
    }

    if (pathname === '/admin/api/tenant/model-grants') {
      await fulfillJson(route, { restricted: false, modelIds: [] });
      return;
    }

    await fulfillJson(route, {}, 404);
  });
}

export async function signInThroughGate(page: Page, apiKey = TEST_API_KEY): Promise<void> {
  await page.goto('./');
  await page.getByLabel('Admin API key').fill(apiKey);
  await page.getByRole('button', { name: 'Connect' }).click();
  await page.getByRole('navigation', { name: 'Main' }).waitFor();
}

export async function openPrimaryTab(page: Page, label: string, panel: string): Promise<void> {
  await page.getByRole('tab', { name: label }).click();
  await page.locator(panel).waitFor({ state: 'visible' });
}
