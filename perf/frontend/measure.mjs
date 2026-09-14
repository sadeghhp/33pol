/**
 * Full baseline capture for the admin console.
 *
 * Answers the questions the migration is graded on: what does delivery cost cold and warm, how much
 * DOM is standing, and how busy is the main thread when nobody is touching the page.
 *
 * Usage:  node perf/frontend/measure.mjs        (see ./README.md for seeding)
 */
import {
  BASE_URL, IDLE_WINDOW_MS, launch, sampleWindow, census, signIn, goToTab,
  trackAssets, watchLongTasks, readLongTasks, writeResult, run, pad,
} from './lib/harness.mjs';

run(async () => {
  const { browser, page, cdp } = await launch();
  const out = { baseUrl: BASE_URL, at: new Date().toISOString(), idleWindowMs: IDLE_WINDOW_MS };

  // ---- cold load, at the auth gate --------------------------------------------------------
  let assets = trackAssets(page);
  const coldStarted = Date.now();
  await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'networkidle' });
  const cold = await assets.stop();
  out.cold = {
    loadMs: Date.now() - coldStarted,
    transferredBytes: cold.bytes,
    requests: cold.resources.length,
    fromCache: cold.fromCache,
    uncompressed: cold.resources.filter(r => r.encoding === 'none').length,
    resources: cold.resources,
  };
  out.navTiming = await page.evaluate(() => {
    const n = performance.getEntriesByType('navigation')[0];
    return {
      domInteractive: Math.round(n.domInteractive),
      domContentLoaded: Math.round(n.domContentLoadedEventEnd),
      load: Math.round(n.loadEventEnd),
    };
  });
  out.authGate = await page.evaluate(census);

  // ---- signed in, Overview ----------------------------------------------------------------
  await watchLongTasks(page);
  out.signInToFirstRowMs = await signIn(page);
  await page.waitForTimeout(3000);
  out.overview = await page.evaluate(census);
  out.liveMode = await page.evaluate(() => window.Alpine?.$data(document.body)?.liveMode ?? 'unknown');

  out.idleOverview = await sampleWindow(page, cdp);
  const long = await readLongTasks(page);
  out.idleOverview.longTasks = { count: long.length, maxMs: long.length ? Math.max(...long) : 0, all: long };

  // ---- navigation ------------------------------------------------------------------------
  out.tabSwitchMs = {};
  out.tabSwitchMs.toLogs = await goToTab(page, '#/logs', '#panel-logs table tbody tr.request-row');
  await page.waitForTimeout(1500);
  out.logs = await page.evaluate(census);
  out.logs.rowCount = await page.evaluate(
    () => document.querySelectorAll('#panel-logs tbody tr.request-row').length);
  out.idleLogs = await sampleWindow(page, cdp);

  out.tabSwitchMs.toKeys = await goToTab(page, '#/keys', '#panel-keys');
  await page.waitForTimeout(800);
  out.tabSwitchMs.backToOverview = await goToTab(page, '#/dashboard', '#panel-dashboard tbody tr.request-row');
  await page.waitForTimeout(800);
  out.tabSwitchMs.toLogsAgain = await goToTab(page, '#/logs', '#panel-logs tbody tr.request-row');

  // ---- warm reload ------------------------------------------------------------------------
  // 'load' rather than 'networkidle': once signed in, the 2 s poll and the SSE stream mean the
  // network is never idle, and waiting for that would simply time out.
  assets = trackAssets(page);
  const warmStarted = Date.now();
  await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'load' });
  await page.waitForTimeout(2500);
  const warm = await assets.stop();
  out.warm = {
    loadMs: Date.now() - warmStarted,
    transferredBytes: warm.bytes,
    requests: warm.resources.length,
    fromCache: warm.fromCache,
    uncompressed: warm.resources.filter(r => r.encoding === 'none').length,
    resources: warm.resources,
  };

  // ---- report -----------------------------------------------------------------------------
  const kb = b => `${(b / 1024).toFixed(1)} KB`;
  console.log(`\n=== ADMIN CONSOLE BASELINE · ${BASE_URL} ===\n`);
  console.log(`cold load          ${pad(kb(out.cold.transferredBytes), 10)}  ${out.cold.requests} requests, ` +
              `${out.cold.uncompressed} uncompressed, ${out.cold.fromCache} from cache`);
  console.log(`warm reload        ${pad(kb(out.warm.transferredBytes), 10)}  ${out.warm.requests} requests, ` +
              `${out.warm.fromCache} from cache`);
  console.log(`auth gate          ${pad(out.authGate.domNodes, 10)} nodes  ${out.authGate.boundAttrs} bound attrs`);
  console.log(`overview           ${pad(out.overview.domNodes, 10)} nodes  ${out.overview.boundAttrs} bound attrs`);
  console.log(`logs (${pad(out.logs.rowCount, 3)} rows)   ${pad(out.logs.domNodes, 10)} nodes  ${out.logs.boundAttrs} bound attrs, ` +
              `${out.logs.detailRowsMounted} detail rows mounted`);
  console.log(`idle Overview      ${pad(out.idleOverview.cpuPct + '%', 10)}  ${out.idleOverview.taskS}s task / ${IDLE_WINDOW_MS / 1000}s, ` +
              `${out.idleOverview.longTasks.count} long tasks (max ${out.idleOverview.longTasks.maxMs}ms)`);
  console.log(`idle Logs          ${pad(out.idleLogs.cpuPct + '%', 10)}  ${out.idleLogs.taskS}s task / ${IDLE_WINDOW_MS / 1000}s`);
  console.log(`heap / listeners   ${pad(out.idleLogs.jsHeapUsedMB + ' MB', 10)}  ${out.idleLogs.jsEventListeners} listeners`);
  console.log(`tab switch (ms)    ${JSON.stringify(out.tabSwitchMs)}`);
  console.log('\nasset delivery:');
  for (const r of out.cold.resources) {
    console.log(`  ${pad(r.status, 3)} ${pad(kb(r.bytes), 10)}  enc=${r.encoding.padEnd(5)} ` +
                `cc=${(r.cacheControl || '-').slice(0, 42).padEnd(42)} ${r.url}`);
  }

  writeResult('measure', out);
  await browser.close();
});
