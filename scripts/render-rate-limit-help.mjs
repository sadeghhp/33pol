#!/usr/bin/env node
// Renders the in-console rate-limit guide (src/33pol.App/wwwroot/admin/admin-rate-limit-help.js)
// to Markdown, one file per language, so the same words the operator reads in the admin console
// can be linked from the docs and reviewed in a pull request.
//
//   node scripts/render-rate-limit-help.mjs        writes docs/rate-limit-guide.en.md and .fa.md
//   node scripts/render-rate-limit-help.mjs --check exits 1 if the files on disk are stale
//
// Edit the .js, never the .md: the generated files carry a header saying so.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { loadHelp, findProblems } from './check-rate-limit-help.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const docs = path.join(here, '..', 'docs');

const chrome = {
  en: {
    title: 'Rate limits, explained',
    generated: 'Generated from `src/33pol.App/wwwroot/admin/admin-rate-limit-help.js` by `scripts/render-rate-limit-help.mjs`. Edit the source, not this file. The same text opens in the admin console under Settings → Rate limits → Help.',
    otherFile: 'Persian version: [rate-limit-guide.fa.md](./rate-limit-guide.fa.md)',
    contents: 'Contents',
    scopesTitle: 'The eight scopes at a glance',
    what: 'What it counts',
    when: 'When to use it',
    example: 'Example',
    tip: 'Tip',
    fieldsTitle: 'Quick answers shown next to each control',
    reference: 'Operator reference: [runbooks/rate-limit-admin.md](./runbooks/rate-limit-admin.md).'
  },
  fa: {
    title: 'محدودیت نرخ به زبان ساده',
    generated: 'این فایل از `src/33pol.App/wwwroot/admin/admin-rate-limit-help.js` با `scripts/render-rate-limit-help.mjs` تولید شده است. منبع را ویرایش کنید، نه این فایل را. همین متن در کنسول مدیریت زیر تنظیمات ← محدودیت نرخ ← راهنما باز می‌شود.',
    otherFile: 'نسخهٴ انگلیسی: [rate-limit-guide.en.md](./rate-limit-guide.en.md)',
    contents: 'فهرست',
    scopesTitle: 'هشت دامنه در یک نگاه',
    what: 'چه چیزی را می‌شمارد',
    when: 'کِی به کار می‌آید',
    example: 'مثال',
    tip: 'نکته',
    fieldsTitle: 'پاسخ‌های کوتاهی که کنار هر کنترل نشان داده می‌شوند',
    reference: 'مرجع اپراتور: [runbooks/rate-limit-admin.md](./runbooks/rate-limit-admin.md).'
  }
};

function slug(id) { return id; }

export function render(help, lang) {
  const t = help[lang];
  const c = chrome[lang];
  const rtl = (help.langs.find(l => l.id === lang) || {}).dir === 'rtl';
  const out = [];
  out.push(`# ${c.title}`);
  out.push('');
  if (rtl) out.push('<div dir="rtl" lang="fa">');
  if (rtl) out.push('');
  out.push(`> ${c.generated}`);
  out.push('>');
  out.push(`> ${c.otherFile}`);
  out.push('');
  out.push(`## ${c.contents}`);
  out.push('');
  for (const s of t.sections) out.push(`- [${s.title}](#${slug(s.id)})`);
  out.push(`- [${c.scopesTitle}](#scopes-table)`);
  out.push(`- [${c.fieldsTitle}](#fields)`);
  out.push('');

  for (const s of t.sections) {
    out.push(`<a id="${slug(s.id)}"></a>`);
    out.push('');
    out.push(`## ${s.title}`);
    out.push('');
    if (s.intro) { out.push(s.intro); out.push(''); }
    for (const it of s.items) {
      out.push(`**${it.term}** — ${it.text}`);
      if (it.example) { out.push(''); out.push(`> *${c.example}:* ${it.example}`); }
      out.push('');
    }
    if (s.tip) { out.push(`> **${c.tip}** ${s.tip}`); out.push(''); }
  }

  out.push('<a id="scopes-table"></a>');
  out.push('');
  out.push(`## ${c.scopesTitle}`);
  out.push('');
  out.push(`| | ${c.what} | ${c.when} | ${c.example} |`);
  out.push('|---|---|---|---|');
  for (const [id, sc] of Object.entries(t.scopes)) {
    const cell = v => String(v).replace(/\|/g, '\\|');
    out.push(`| **${cell(sc.name)}** (\`${id}\`) | ${cell(sc.what)} | ${cell(sc.when)} | ${cell(sc.example)} |`);
  }
  out.push('');

  out.push('<a id="fields"></a>');
  out.push('');
  out.push(`## ${c.fieldsTitle}`);
  out.push('');
  for (const f of Object.values(t.fields)) {
    out.push(`### ${f.title}`);
    out.push('');
    out.push(f.text);
    out.push('');
    out.push(`> *${c.example}:* ${f.example}`);
    out.push('');
  }
  out.push(c.reference);
  out.push('');
  if (rtl) out.push('</div>');
  if (rtl) out.push('');
  return out.join('\n');
}

const help = loadHelp();
const problems = findProblems(help);
if (problems.length) {
  console.error('rate-limit help content problems:\n  ' + problems.join('\n  '));
  process.exit(1);
}
const check = process.argv.includes('--check');
let stale = 0;
for (const l of help.langs) {
  const file = path.join(docs, `rate-limit-guide.${l.id}.md`);
  const text = render(help, l.id);
  if (check) {
    if (!existsSync(file) || readFileSync(file, 'utf8') !== text) { console.error(`stale: ${path.relative(process.cwd(), file)}`); stale++; }
  } else {
    writeFileSync(file, text);
    console.log(`wrote ${path.relative(process.cwd(), file)} (${text.length} chars)`);
  }
}
if (check) {
  if (stale) process.exit(1);
  console.log('docs are up to date');
}
