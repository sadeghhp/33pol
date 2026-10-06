#!/usr/bin/env node
/**
 * CI perf gates for the Solid admin console (M17).
 * Compares the latest measure baseline against migration plan targets.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const baselineDir = path.join(root, 'perf/frontend/baseline');
const file = path.join(baselineDir, '2026-10-05-m17-solid-measure.json');

if (!fs.existsSync(file)) {
  console.error(`::error::Missing baseline ${file} — run perf/frontend/measure.mjs and commit baseline`);
  process.exit(1);
}

const data = JSON.parse(fs.readFileSync(file, 'utf8'));
const idleCpu = data.idleOverview?.cpuPct ?? 999;
const maxLong = data.idleOverview?.longTasks?.maxMs ?? 999;
const authNodes = data.authGate?.domNodes ?? 9999;
const bound = data.overview?.boundAttrs ?? 9999;
const overviewNodes = data.overview?.domNodes ?? 0;
const detailRows = data.overview?.detailRowsMounted ?? 0;

const failures = [];
if (idleCpu > 3) failures.push(`idle Overview CPU ${idleCpu}% exceeds 3% gate`);
if (maxLong > 50) failures.push(`max long task ${maxLong}ms exceeds 50ms gate`);
if (authNodes > 200) failures.push(`auth gate DOM ${authNodes} nodes exceeds 200 (defer dashboard until sign-in)`);
if (bound > 0) failures.push(`Overview bound attrs ${bound} — Alpine directives must be gone`);
if (overviewNodes < 350) {
  failures.push(`signed-in Overview DOM ${overviewNodes} nodes is below 350 — dashboard may be stripped`);
}
if (detailRows < 0) failures.push('detailRowsMounted missing from baseline');

if (failures.length) {
  for (const f of failures) console.error(`::error::${f}`);
  process.exit(1);
}

console.log(`Admin perf gates OK: idle CPU ${idleCpu}%, max long task ${maxLong}ms, auth gate ${authNodes} nodes`);
