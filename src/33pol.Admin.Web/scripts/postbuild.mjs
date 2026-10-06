#!/usr/bin/env node
/**
 * After Vite emits the Solid bundle, copy vendored static assets that are not part of the JS build:
 * fonts, rate-limit help (source in src/content), and the manifest for CI checks.
 */
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const vendor = path.join(root, 'vendor');
const content = path.join(root, 'src', 'content');
const out = path.join(root, '..', '33pol.App', 'wwwroot', 'admin');

function copyDir(src, dest) {
  if (!fs.existsSync(src)) return;
  fs.mkdirSync(dest, { recursive: true });
  for (const name of fs.readdirSync(src)) {
    const s = path.join(src, name);
    const d = path.join(dest, name);
    if (fs.statSync(s).isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

// Self-hosted fonts and standalone help source (check scripts use this path).
copyDir(vendor, path.join(out, 'vendor'));
const helpSrc = path.join(content, 'admin-rate-limit-help.js');
if (fs.existsSync(helpSrc)) {
  fs.copyFileSync(helpSrc, path.join(out, 'admin-rate-limit-help.js'));
  const helpText = fs.readFileSync(helpSrc, 'utf8');
  const sandbox = { window: {} };
  vm.runInNewContext(helpText, sandbox);
  if (sandbox.window.RateLimitHelp) {
    fs.writeFileSync(
      path.join(out, 'admin-rate-limit-help.json'),
      JSON.stringify(sandbox.window.RateLimitHelp),
    );
  }
}

const manifest = {};
for (const name of fs.readdirSync(out)) {
  const p = path.join(out, name);
  if (fs.statSync(p).isFile()) {
    manifest[name] = fs.statSync(p).size;
  }
}
fs.writeFileSync(path.join(out, 'asset-manifest.json'), JSON.stringify(manifest, null, 2));

console.log('postbuild: copied vendor assets to', out);
