/**
 * Cache effectiveness across real browser visits.
 *
 * Separate from measure.mjs for a reason that cost an afternoon to find: Playwright's default
 * context is incognito-style with no persistent disk cache, so every "warm" navigation re-downloads
 * fonts and reports the cache policy as broken when it is working. This script drives a persistent
 * on-disk profile — what an operator's browser actually is — and closes the browser between visits,
 * so a hit here is a real hit.
 *
 * Usage:  node perf/frontend/cache.mjs
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';
import { BASE_URL, writeResult, run } from './lib/harness.mjs';

const PROFILE = process.env.CACHE_PROFILE_DIR
  ?? path.join(os.tmpdir(), '33pol-admin-cache-profile');

const kb = b => `${(b / 1024).toFixed(1)} KB`;

async function visit(label) {
  const context = await chromium.launchPersistentContext(PROFILE, { args: ['--no-sandbox'] });
  try {
    const page = await context.newPage();
    const pending = [];
    page.on('response', response => {
      const url = response.url().replace(BASE_URL, '');
      if (url.startsWith('/admin/api') || url.startsWith('/health')) return;
      pending.push((async () => {
        let bytes = 0;
        try {
          bytes = Math.max(0, (await response.request().sizes())?.responseBodySize ?? 0);
        } catch { /* torn down */ }
        return { url, bytes, cacheControl: response.headers()['cache-control'] ?? '' };
      })());
    });

    await page.goto(`${BASE_URL}/admin/index.html`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(2500);
    const resources = await Promise.all(pending);

    const total = resources.reduce((sum, r) => sum + r.bytes, 0);
    const fonts = resources.filter(r => r.url.includes('.woff2'));
    const served = resources.filter(r => r.bytes === 0);
    const result = {
      label,
      transferredBytes: total,
      requests: resources.length,
      fontBytes: fonts.reduce((sum, f) => sum + f.bytes, 0),
      servedFromCache: served.length,
      resources,
    };
    console.log(`${label.padEnd(28)} ${kb(total).padStart(10)}  ${resources.length} requests, ` +
                `fonts ${kb(result.fontBytes)}, ${served.length} served from cache`);
    return result;
  } finally {
    await context.close();
  }
}

run(async () => {
  // A cold profile is the whole point of the first visit; leaving a previous run's cache in place
  // would report the first visit as already warm.
  fs.rmSync(PROFILE, { recursive: true, force: true });

  const visits = [];
  visits.push(await visit('1. first visit (cold)'));
  visits.push(await visit('2. return visit'));
  visits.push(await visit('3. return visit'));

  const cold = visits[0].transferredBytes;
  const warm = visits[2].transferredBytes;
  const out = {
    baseUrl: BASE_URL, at: new Date().toISOString(), visits,
    summary: {
      coldBytes: cold,
      warmBytes: warm,
      savedBytes: cold - warm,
      savedPct: +(((cold - warm) / cold) * 100).toFixed(1),
      fontsCachedOnReturn: visits[2].fontBytes === 0,
      returnVisitsStable: visits[1].transferredBytes === visits[2].transferredBytes,
    },
  };

  console.log(`\ncold ${kb(cold)} → warm ${kb(warm)} (${out.summary.savedPct}% saved)`);
  console.log(`fonts fully cached on return: ${out.summary.fontsCachedOnReturn}`);
  if (!out.summary.fontsCachedOnReturn) {
    console.log('  ↑ vendor assets are meant to be immutable; investigate the cache policy.');
  }
  // What is left on a return visit is the still-uncacheable set: index.html plus the hand-versioned
  // ?v=N assets. Content-hashed filenames move those onto the immutable branch too.
  const remaining = visits[2].resources.filter(r => r.bytes > 0)
    .sort((a, b) => b.bytes - a.bytes);
  console.log('\nstill transferred on a return visit:');
  for (const r of remaining) console.log(`  ${kb(r.bytes).padStart(10)}  ${r.url}`);

  writeResult('cache', out);
});
