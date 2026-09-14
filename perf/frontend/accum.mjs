/**
 * DOM accumulation across a tab tour.
 *
 * The question this answers: does anything ever get unmounted? Walk every tab in order and watch the
 * node, binding and listener counts. Under Alpine's `x-show` panels the numbers only ever go up,
 * which is why a long-lived operator session (or a wallboard left up for weeks) keeps growing.
 *
 * This is the regression guard for the migration's lifecycle promise: after a panel migrates, leaving
 * it must give nodes back. The acceptance criterion is *non-monotonic* DOM, not a smaller peak.
 *
 * Usage:  node perf/frontend/accum.mjs
 */
import { BASE_URL, IDLE_WINDOW_MS, launch, sampleWindow, census, signIn, writeResult, run, pad } from './lib/harness.mjs';

const TOUR = [
  ['#/logs', 'Logs'],
  ['#/errors', 'Errors'],
  ['#/keys', 'Keys'],
  ['#/usage', 'Usage'],
  ['#/routing', 'Routing'],
  ['#/settings', 'Settings'],
  ['#/dashboard', 'back to Overview'],
];

run(async () => {
  const { browser, page, cdp } = await launch();
  const out = { baseUrl: BASE_URL, at: new Date().toISOString(), steps: [] };

  const snap = async label => {
    const c = await page.evaluate(census);
    const perf = await cdp.send('Performance.getMetrics');
    const listeners = perf.metrics.find(m => m.name === 'JSEventListeners')?.value ?? 0;
    const heapMB = +((perf.metrics.find(m => m.name === 'JSHeapUsedSize')?.value ?? 0) / 1048576).toFixed(1);
    const rows = await page.evaluate(() => ({
      feed: document.querySelectorAll('#panel-dashboard tbody tr.request-row').length,
      logs: document.querySelectorAll('#panel-logs tbody tr.request-row').length,
      errors: document.querySelectorAll('#panel-errors tbody tr.request-row').length,
    }));
    const step = { label, ...c, listeners, heapMB, rows };
    out.steps.push(step);
    console.log(`${label.padEnd(26)} nodes=${pad(c.domNodes, 6)} bound=${pad(c.boundAttrs, 6)} ` +
                `feed=${pad(rows.feed, 3)} logs=${pad(rows.logs, 4)} errs=${pad(rows.errors, 3)} ` +
                `detailRows=${pad(c.detailRowsMounted, 4)} listeners=${pad(listeners, 5)} heap=${heapMB}MB`);
    return step;
  };

  await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'load' });
  const gate = await snap('1. auth gate');
  await signIn(page);
  await page.waitForTimeout(2500);
  await snap('2. Overview only');

  let i = 3;
  for (const [hash, name] of TOUR) {
    await page.evaluate(h => { window.location.hash = h; }, hash);
    await page.waitForTimeout(2000);
    await snap(`${i++}. ${name}`);
  }

  const peak = Math.max(...out.steps.map(s => s.domNodes));
  const final = out.steps.at(-1).domNodes;
  out.summary = {
    authGateNodes: gate.domNodes,
    authGateBoundAttrs: gate.boundAttrs,
    peakNodes: peak,
    finalNodes: final,
    growthFromGate: final - gate.domNodes,
    // True while nothing is ever unmounted. Migrated panels must flip this to false.
    monotonic: out.steps.every((s, idx) => idx === 0 || s.domNodes >= out.steps[idx - 1].domNodes),
  };

  out.idleAfterTour = await sampleWindow(page, cdp);
  console.log(`\nOverview idle after the full tour: ${out.idleAfterTour.taskS}s / ${IDLE_WINDOW_MS / 1000}s ` +
              `= ${out.idleAfterTour.cpuPct}% CPU`);
  console.log(`DOM ${out.summary.authGateNodes} → ${out.summary.finalNodes} (peak ${peak}), ` +
              `monotonic=${out.summary.monotonic}`);
  if (out.summary.monotonic) {
    console.log('monotonic=true means nothing was ever unmounted — expected before the migration.');
  }

  writeResult('accum', out);
  await browser.close();
});
