/**
 * Attributes idle CPU on the Overview to its individual causes.
 *
 * Differential measurement: sample the page as it ships, then disable one mechanism at a time and
 * re-sample the same window. The deltas say which mechanism owns the cost, which is the difference
 * between "the dashboard feels slow" and "the 500 ms tick is 70% of it".
 *
 * This script reaches into Alpine internals deliberately (it is a diagnostic, not a test). After the
 * Overview migrates it must be rewritten against whatever owns the timers then — and the expected
 * result changes: a fine-grained implementation should show almost nothing left to attribute.
 *
 * Usage:  node perf/frontend/attribute.mjs
 */
import { BASE_URL, IDLE_WINDOW_MS, launch, sampleWindow, signIn, writeResult, run } from './lib/harness.mjs';

run(async () => {
  const { browser, page, cdp } = await launch();
  const out = { baseUrl: BASE_URL, at: new Date().toISOString(), idleWindowMs: IDLE_WINDOW_MS, stages: [] };

  const stage = async (label, note) => {
    const sample = await sampleWindow(page, cdp);
    console.log(`${label.padEnd(42)} task=${String(sample.taskS).padStart(6)}s  cpu=${String(sample.cpuPct).padStart(5)}%  ` +
                `script=${String(sample.scriptS).padStart(6)}s  layouts=${sample.layoutCount}`);
    out.stages.push({ label, note, ...sample });
    return sample;
  };

  await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'load' });
  await signIn(page);
  await page.waitForTimeout(4000);

  out.feedRows = await page.evaluate(
    () => document.querySelectorAll('#panel-dashboard tbody tr.request-row').length);
  out.liveMode = await page.evaluate(() => window.Alpine?.$data(document.body)?.liveMode ?? 'unknown');
  console.log(`\nfeed rows on screen: ${out.feedRows}   liveMode: ${out.liveMode}\n`);

  const a = await stage('A. everything running (as shipped)', 'baseline');

  const killedTick = await page.evaluate(() => {
    const c = window.Alpine?.$data(document.body);
    if (!c?._tickTimer) return false;
    clearInterval(c._tickTimer);
    c._tickTimer = null;
    return true;
  });
  if (!killedTick) {
    // Not a failure: after the Overview migrates there is no _tickTimer to clear, and that is the
    // point of the milestone. Say so rather than reporting a misleading zero delta.
    console.log('\n_tickTimer not found — the global tick is gone (or renamed). Nothing to attribute.');
    out.tickTimerPresent = false;
    writeResult('attribute', out);
    await browser.close();
    return;
  }
  out.tickTimerPresent = true;
  const b = await stage('B. 500 ms tick cleared', 'isolates the global clock');

  await page.evaluate(() => {
    const c = window.Alpine?.$data(document.body);
    if (c?.poll) { clearInterval(c.poll); c.poll = null; }
    c?.stopLive?.();
  });
  const c = await stage('C. tick + 2 s poll + SSE all stopped', 'irreducible floor');

  out.attribution = {
    tickShareOfIdlePct: +(((a.taskS - b.taskS) / a.taskS) * 100).toFixed(1),
    tickCostS: +(a.taskS - b.taskS).toFixed(2),
    pollAndSseCostS: +(b.taskS - c.taskS).toFixed(2),
    irreducibleS: c.taskS,
  };

  console.log('\n=== ATTRIBUTION ===');
  console.log(`500 ms tick        ${out.attribution.tickCostS}s / ${IDLE_WINDOW_MS / 1000}s  ` +
              `= ${out.attribution.tickShareOfIdlePct}% of idle CPU`);
  console.log(`poll + SSE         ${out.attribution.pollAndSseCostS}s / ${IDLE_WINDOW_MS / 1000}s`);
  console.log(`irreducible        ${out.attribution.irreducibleS}s / ${IDLE_WINDOW_MS / 1000}s`);

  writeResult('attribute', out);
  await browser.close();
});
