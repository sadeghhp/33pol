#!/usr/bin/env node
/**
 * CI bundle-size gates for the Solid admin console (migration plan §12 subset).
 */
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assetsDir = path.join(root, 'src/33pol.App/wwwroot/admin/assets');

const LIMITS = {
  indexJs: 60 * 1024,
  indexCss: 20 * 1024,
  lazyChunk: 40 * 1024,
};

function gzipSize(filePath) {
  const raw = fs.readFileSync(filePath);
  return zlib.gzipSync(raw).length;
}

function fail(msg) {
  console.error(`::error::${msg}`);
  process.exit(1);
}

if (!fs.existsSync(assetsDir)) {
  fail(`Missing admin assets at ${assetsDir}. Run: cd src/33pol.Admin.Web && npm run build`);
}

const files = fs.readdirSync(assetsDir);
const indexJs = files.find((f) => /^index-.*\.js$/.test(f));
const indexCss = files.find((f) => /^index-.*\.css$/.test(f));
const lazyJs = files
  .filter((f) => f.endsWith('.js') && f !== indexJs)
  .map((f) => ({ name: f, gzip: gzipSize(path.join(assetsDir, f)) }))
  .sort((a, b) => b.gzip - a.gzip)[0];

if (!indexJs) fail('No index-*.js bundle found');
if (!indexCss) fail('No index-*.css bundle found');

const indexJsGzip = gzipSize(path.join(assetsDir, indexJs));
const indexCssGzip = gzipSize(path.join(assetsDir, indexCss));

if (indexJsGzip > LIMITS.indexJs) {
  fail(`index JS gzip ${indexJsGzip} exceeds ${LIMITS.indexJs} (${indexJs})`);
}
if (indexCssGzip > LIMITS.indexCss) {
  fail(`index CSS gzip ${indexCssGzip} exceeds ${LIMITS.indexCss} (${indexCss})`);
}
if (lazyJs && lazyJs.gzip > LIMITS.lazyChunk) {
  fail(`largest lazy chunk gzip ${lazyJs.gzip} exceeds ${LIMITS.lazyChunk} (${lazyJs.name})`);
}

console.log(
  `Admin bundle gates OK: index.js ${(indexJsGzip / 1024).toFixed(1)} KB, ` +
    `index.css ${(indexCssGzip / 1024).toFixed(1)} KB, ` +
    `largest lazy ${lazyJs ? `${(lazyJs.gzip / 1024).toFixed(1)} KB (${lazyJs.name})` : 'n/a'}`,
);
