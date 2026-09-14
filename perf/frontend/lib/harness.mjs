/**
 * Shared plumbing for the admin-console performance harness.
 *
 * Everything here talks to a *running* gateway over CDP; nothing imports application code, so the
 * harness keeps working unchanged across the Alpine → Solid migration. That is the point: the same
 * script produces the before and after numbers, which is what makes a performance claim checkable
 * rather than asserted. See ../README.md for the seeding procedure.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

export const HERE = path.dirname(fileURLToPath(import.meta.url));
export const ROOT = path.resolve(HERE, '..');

export const BASE_URL = process.env.ADMIN_BASE_URL ?? 'http://127.0.0.1:5080';
export const API_KEY = process.env.GATEWAY_ADMIN_API_KEY ?? 'sk-33pol-dev-local-unsafe';

/** Length of every idle-CPU sampling window. Long enough to average out a 2 s poll and a 15 s heartbeat. */
export const IDLE_WINDOW_MS = Number(process.env.IDLE_WINDOW_MS ?? 20000);

/** Hashes in the DOM-count helpers below are attribute *prefixes*, not exact names. */
const BOUND_PREFIXES = ['x-', ':', '@'];

export async function launch() {
  const browser = await chromium.launch({ args: ['--no-sandbox'] });
  const context = await browser.newContext();
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Performance.enable');
  return { browser, context, page, cdp };
}

/** One CDP metric by name, 0 when absent. */
export const metric = (sample, name) => sample.metrics.find(m => m.name === name)?.value ?? 0;

/**
 * Samples CDP performance counters across a fixed window of wall-clock time.
 *
 * TaskDuration is the honest number for "is this page busy": it counts every task the main thread
 * ran, whether it was script, layout or style. cpuPct is that over the window, so 30 means the page
 * burned roughly a third of one core doing nothing.
 */
export async function sampleWindow(page, cdp, ms = IDLE_WINDOW_MS) {
  const before = await cdp.send('Performance.getMetrics');
  await page.waitForTimeout(ms);
  const after = await cdp.send('Performance.getMetrics');
  const delta = name => +(metric(after, name) - metric(before, name)).toFixed(3);
  const taskS = delta('TaskDuration');
  return {
    windowMs: ms,
    taskS,
    cpuPct: +((taskS / (ms / 1000)) * 100).toFixed(1),
    scriptS: delta('ScriptDuration'),
    layoutS: delta('LayoutDuration'),
    recalcStyleS: delta('RecalcStyleDuration'),
    layoutCount: delta('LayoutCount'),
    recalcStyleCount: delta('RecalcStyleCount'),
    nodes: metric(after, 'Nodes'),
    jsEventListeners: metric(after, 'JSEventListeners'),
    jsHeapUsedMB: +(metric(after, 'JSHeapUsedSize') / 1048576).toFixed(1),
  };
}

/**
 * DOM and reactive-binding census.
 *
 * Binding counts are attribute-based rather than framework-internal on purpose: Alpine directives
 * and Solid's absence of them are both visible this way, so the same metric stays meaningful after
 * a panel migrates. A migrated panel should report ~0 bound attributes.
 */
export function census() {
  const prefixes = ['x-', ':', '@'];
  const byKind = {};
  let bound = 0;
  for (const el of document.querySelectorAll('*')) {
    for (const attr of el.attributes) {
      const n = attr.name;
      if (!prefixes.some(p => n.startsWith(p))) continue;
      bound++;
      const kind = n.startsWith(':') ? ':bind' : n.startsWith('@') ? '@on' : n.split('.')[0];
      byKind[kind] = (byKind[kind] ?? 0) + 1;
    }
  }
  return {
    domNodes: document.querySelectorAll('*').length,
    boundAttrs: bound,
    byKind,
    detailRowsMounted: document.querySelectorAll('tr.request-detail-row').length,
    solidRoots: document.querySelectorAll('[data-solid-root]').length,
  };
}

/** Installs a long-task observer. Read it back with `readLongTasks(page)`. */
export async function watchLongTasks(page) {
  await page.evaluate(() => {
    if (window.__longTasks) return;
    window.__longTasks = [];
    new PerformanceObserver(list => {
      for (const entry of list.getEntries()) window.__longTasks.push(Math.round(entry.duration));
    }).observe({ entryTypes: ['longtask'] });
  });
}

export const readLongTasks = page => page.evaluate(() => (window.__longTasks ?? []).slice());
export const clearLongTasks = page => page.evaluate(() => { window.__longTasks = []; });

/** Signs in through the real auth gate, then waits for the live feed to paint a row. */
export async function signIn(page, { waitForFeed = true } = {}) {
  await page.fill('#gate-apiKey', API_KEY);
  const started = Date.now();
  await page.click('button.action:has-text("Connect")');
  if (waitForFeed) {
    await page.waitForSelector('#panel-dashboard tbody tr.request-row', { timeout: 20000 });
  } else {
    await page.waitForSelector('.app-shell', { state: 'visible', timeout: 20000 });
  }
  return Date.now() - started;
}

/** Navigates by hash the way an operator does, and waits for the panel to be on screen. */
export async function goToTab(page, hash, selector) {
  const started = Date.now();
  await page.evaluate(h => { window.location.hash = h; }, hash);
  await page.waitForSelector(selector, { timeout: 20000 });
  return Date.now() - started;
}

/**
 * Records every non-API response so byte accounting reflects what the *asset pipeline* costs.
 * Admin API and health traffic is excluded: it is payload, not delivery, and it would make cold and
 * warm loads incomparable.
 */
export function trackAssets(page) {
  const pending = [];
  const onResponse = response => {
    const url = response.url().replace(BASE_URL, '');
    if (url.startsWith('/admin/api') || url.startsWith('/health')) return;
    const headers = response.headers();
    pending.push((async () => {
      // Content-Length is absent on a compressed (chunked) response, so reading it would report
      // every Brotli asset as 0 bytes — which looks like a spectacular win and measures nothing.
      // request().sizes() reports what actually crossed the wire, encoded, and is 0 for a response
      // the browser served from its own cache. That makes transferredBytes the honest headline for
      // both compression and caching.
      let bytes = 0;
      try {
        bytes = (await response.request().sizes())?.responseBodySize ?? 0;
      } catch { /* torn down mid-navigation */ }
      if (bytes <= 0) bytes = Number(headers['content-length'] ?? 0);
      return {
        url,
        status: response.status(),
        bytes,
        encoding: headers['content-encoding'] ?? 'none',
        cacheControl: headers['cache-control'] ?? '',
      };
    })());
  };
  page.on('response', onResponse);
  /**
   * Detaching matters: without it a second navigation keeps appending to the first recording, and
   * the cold-load figures silently absorb the warm reload's requests.
   */
  const stop = async () => {
    page.off('response', onResponse);
    const resources = await Promise.all(pending);
    return {
      resources,
      bytes: resources.reduce((sum, r) => sum + r.bytes, 0),
      // A 200 that transferred nothing came out of the browser's cache; a 304 was revalidated.
      fromCache: resources.filter(r => r.status === 304 || r.bytes === 0).length,
    };
  };
  return { stop };
}

/** Writes machine-readable output and echoes where it went. */
export function writeResult(name, payload) {
  const dir = process.env.RESULT_DIR ?? path.join(ROOT, 'results');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${name}-${new Date().toISOString().replace(/[:.]/g, '-')}.json`);
  fs.writeFileSync(file, JSON.stringify(payload, null, 2));
  console.log(`\n→ ${file}`);
  return file;
}

/** Fails loudly and with a non-zero exit code, so this is usable as a gate later. */
export function run(main) {
  main().catch(err => {
    console.error(err?.stack ?? String(err));
    process.exitCode = 1;
  });
}

export const pad = (s, n) => String(s).padStart(n);
