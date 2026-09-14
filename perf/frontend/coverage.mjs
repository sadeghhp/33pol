/**
 * Per-panel weight and shipped-asset coverage.
 *
 * Two questions: how much of what we ship is ever used, and which panel owns the DOM. Coverage is
 * collected after visiting every tab, so the "used" figure is a generous upper bound — whatever is
 * still unused after a full tour really is unused.
 *
 * Caveat worth keeping: Chrome's CSS coverage does not report `@font-face` rules as used, so
 * fonts.css always reads 0%. That is a tooling artifact, not evidence that a font is unused. Never
 * delete a face on this number. See the plan's "CSS and icons" section.
 *
 * Usage:  node perf/frontend/coverage.mjs
 */
import { BASE_URL, launch, signIn, writeResult, run, pad } from './lib/harness.mjs';

const TABS = ['#/usage', '#/routing', '#/keys', '#/logs', '#/errors', '#/settings', '#/dashboard'];

run(async () => {
  const { browser, page } = await launch();
  const out = { baseUrl: BASE_URL, at: new Date().toISOString() };

  await page.coverage.startCSSCoverage();
  await page.coverage.startJSCoverage();
  await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'load' });
  await signIn(page);
  await page.waitForTimeout(3000);

  // Panel weight is measured with the Overview active, so the other panels show what they cost
  // *before* their tables have loaded any rows — which is the distinction that matters: inactive
  // panels are cheap while empty, and only become expensive once visited.
  out.panels = await page.evaluate(() => {
    const result = {};
    const sel = 'section[role="tabpanel"], .drawer-backdrop, .modal-backdrop';
    for (const section of document.querySelectorAll(sel)) {
      const id = section.id || section.className.split(' ')[0];
      let bound = 0;
      for (const el of section.querySelectorAll('*')) {
        for (const attr of el.attributes) {
          if (attr.name.startsWith('x-') || attr.name.startsWith(':') || attr.name.startsWith('@')) bound++;
        }
      }
      result[id] = {
        nodes: section.querySelectorAll('*').length,
        boundAttrs: bound,
        htmlBytes: section.outerHTML.length,
      };
    }
    return result;
  });

  console.log('\n=== PER-PANEL WEIGHT (Overview active; others mounted but empty) ===');
  for (const [id, v] of Object.entries(out.panels).sort((a, b) => b[1].nodes - a[1].nodes)) {
    console.log(`${id.padEnd(24)} nodes=${pad(v.nodes, 5)} boundAttrs=${pad(v.boundAttrs, 5)} ` +
                `html=${(v.htmlBytes / 1024).toFixed(1)}KB`);
  }

  for (const hash of TABS) {
    await page.evaluate(h => { window.location.hash = h; }, hash);
    await page.waitForTimeout(1200);
  }

  const css = await page.coverage.stopCSSCoverage();
  const js = await page.coverage.stopJSCoverage();

  out.css = css.map(entry => {
    const used = entry.ranges.reduce((sum, r) => sum + r.end - r.start, 0);
    return {
      file: entry.url.split('/').pop(),
      totalBytes: entry.text.length,
      usedBytes: used,
      usedPct: +((used / entry.text.length) * 100).toFixed(1),
    };
  });
  out.js = js
    .filter(entry => entry.url.includes('/admin/'))
    .map(entry => {
      const executed = entry.functions.filter(f => f.ranges.some(r => r.count > 0)).length;
      return {
        file: entry.url.split('/').pop().split('?')[0],
        functions: entry.functions.length,
        executed,
        executedPct: entry.functions.length ? +((executed / entry.functions.length) * 100).toFixed(1) : 0,
        bytes: entry.source?.length ?? 0,
      };
    });

  console.log('\n=== CSS COVERAGE (after visiting every tab) ===');
  for (const c of out.css) {
    console.log(`${c.file.padEnd(28)} total=${(c.totalBytes / 1024).toFixed(1)}KB ` +
                `used=${(c.usedBytes / 1024).toFixed(1)}KB (${c.usedPct}%)`);
  }
  console.log('  note: fonts.css always reads 0% — @font-face is not reported as used. Not evidence.');

  console.log('\n=== JS COVERAGE ===');
  for (const j of out.js) {
    console.log(`${j.file.padEnd(28)} functions=${pad(j.functions, 5)} executed=${pad(j.executed, 5)} ` +
                `(${j.executedPct}%) size=${(j.bytes / 1024).toFixed(1)}KB`);
  }

  writeResult('coverage', out);
  await browser.close();
});
