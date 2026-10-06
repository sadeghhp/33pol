#!/usr/bin/env node
/** Fails fast when integration tests would 404 hashed admin bundles. */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const adminDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '../src/33pol.App/wwwroot/admin');
const index = path.join(adminDir, 'index.html');

if (!fs.existsSync(index)) {
  console.error(
    '::error::Missing built admin assets. Run: cd src/33pol.Admin.Web && npm ci && npm run build',
  );
  process.exit(1);
}

const html = fs.readFileSync(index, 'utf8');
if (!html.includes('/admin/assets/index-') && !html.includes('assets/index-')) {
  console.error('::error::index.html does not reference a hashed main bundle');
  process.exit(1);
}

console.log('Admin assets present at', adminDir);
