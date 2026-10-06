#!/usr/bin/env node
/**
 * CI perf gates for the Solid admin console (M17).
 * Compares the latest measure baseline against migration plan targets.
 *
 * Set PERF_LIVE=1 to run perf/frontend/measure.mjs against a running gateway and gate on that
 * output instead of the committed baseline JSON (requires ADMIN_BASE_URL / GATEWAY_ADMIN_API_KEY).
 */
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const baselineDir = path.join(root, 'perf/frontend/baseline');
const baselineFile = path.join(baselineDir, '2026-10-05-m17-solid-measure.json');

function loadLiveMeasure() {
  const measureScript = path.join(root, 'perf/frontend/measure.mjs');
  if (!fs.existsSync(measureScript)) {
    console.error(`::error::Missing ${measureScript}`);
    process.exit(1);
  }

  const result = spawnSync(process.execPath, [measureScript], {
    cwd: root,
    env: { ...process.env },
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);

  if (result.status !== 0) {
    console.error('::error::perf/frontend/measure.mjs failed under PERF_LIVE=1');
    process.exit(result.status || 1);
  }

  const files = fs.readdirSync(baselineDir)
    .filter((name) => name.endsWith('.json') && name.includes('measure'))
    .map((name) => ({ name, mtime: fs.statSync(path.join(baselineDir, name)).mtimeMs }))
    .sort((a, b) => b.mtime - a.mtime);

  if (!files.length) {
    console.error('::error::PERF_LIVE=1 produced no measure JSON in perf/frontend/baseline/');
    process.exit(1);
  }

  return JSON.parse(fs.readFileSync(path.join(baselineDir, files[0].name), 'utf8'));
}

function gate(data, label) {
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
    for (const f of failures) console.error(`::error::[${label}] ${f}`);
    process.exit(1);
  }

  console.log(
    `Admin perf gates OK (${label}): idle CPU ${idleCpu}%, max long task ${maxLong}ms, auth gate ${authNodes} nodes`,
  );
}

if (process.env.PERF_LIVE === '1') {
  console.log('PERF_LIVE=1 — measuring running gateway before gating');
  gate(loadLiveMeasure(), 'live');
  process.exit(0);
}

if (!fs.existsSync(baselineFile)) {
  console.error(`::error::Missing baseline ${baselineFile} — run perf/frontend/measure.mjs and commit baseline`);
  process.exit(1);
}

gate(JSON.parse(fs.readFileSync(baselineFile, 'utf8')), 'baseline');
