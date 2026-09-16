#!/usr/bin/env node
// Asserts that the English and Persian trees in
// src/33pol.App/wwwroot/admin/admin-rate-limit-help.js carry the same keys, section ids and item
// counts, that every rule scope the console knows has an entry, and that no string is empty where
// the templates bind it. The console looks help up by key in whichever language is selected; under
// the Alpine CSP build a missing key is a silently empty element, not an error, so this is the check.
//
// Usage: node scripts/check-rate-limit-help.mjs   (exit code 1 on any mismatch)

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const file = path.join(here, '..', 'src', '33pol.App', 'wwwroot', 'admin', 'admin-rate-limit-help.js');

export function loadHelp() {
  const src = readFileSync(file, 'utf8');
  const sandbox = {};
  new Function('window', src)(sandbox);
  if (!sandbox.RateLimitHelp) throw new Error('admin-rate-limit-help.js did not set window.RateLimitHelp');
  return sandbox.RateLimitHelp;
}

// The scope ids `rlScopeCatalog()` in admin-app.js hands the new-rule wizard.
export const SCOPE_IDS = ['model', 'tenant', 'api_key', 'global', 'tenant_model', 'api_key_model', 'anonymous', 'auth_failure'];

// Inline explainer keys the markup binds through `rlHelpView.f.<key>`.
export const FIELD_KEYS = [
  'master', 'adaptive', 'numbers', 'ruleNumbers', 'tiers', 'planSlug', 'rules', 'scope', 'target',
  'limit', 'baseTier', 'windows', 'windowKind', 'suspend', 'timeZone', 'priority', 'calendar', 'combine', 'save'
];

function keysDeep(value, prefix = '') {
  if (Array.isArray(value)) {
    return value.flatMap((v, i) => keysDeep(v, prefix + '[' + i + ']'));
  }
  if (value && typeof value === 'object') {
    return Object.keys(value).sort().flatMap(k => keysDeep(value[k], prefix ? prefix + '.' + k : k));
  }
  return [prefix];
}

export function findProblems(help) {
  const problems = [];
  const langs = help.langs.map(l => l.id);
  if (langs.join(',') !== 'en,fa') problems.push('expected langs en,fa; got ' + langs.join(','));

  const shapes = Object.fromEntries(langs.map(l => [l, keysDeep(help[l]).join('\n')]));
  for (const l of langs.slice(1)) {
    if (shapes[l] !== shapes[langs[0]]) {
      const a = new Set(shapes[langs[0]].split('\n'));
      const b = new Set(shapes[l].split('\n'));
      for (const k of a) if (!b.has(k)) problems.push(`${l} is missing ${k}`);
      for (const k of b) if (!a.has(k)) problems.push(`${l} has extra ${k}`);
    }
  }

  for (const l of langs) {
    const tree = help[l];
    for (const id of SCOPE_IDS) if (!tree.scopes?.[id]) problems.push(`${l}.scopes.${id} missing`);
    for (const k of FIELD_KEYS) if (!tree.fields?.[k]) problems.push(`${l}.fields.${k} missing`);
    const sectionIds = (tree.sections || []).map(s => s.id);
    for (const [k, f] of Object.entries(tree.fields || {})) {
      if (!sectionIds.includes(f.topic)) problems.push(`${l}.fields.${k}.topic "${f.topic}" is not a section id`);
      for (const p of ['title', 'text', 'example']) if (!String(f[p] || '').trim()) problems.push(`${l}.fields.${k}.${p} is empty`);
    }
    for (const [k, s] of Object.entries(tree.scopes || {})) {
      for (const p of ['name', 'what', 'when', 'example']) if (!String(s[p] || '').trim()) problems.push(`${l}.scopes.${k}.${p} is empty`);
    }
    for (const s of tree.sections || []) {
      if (!String(s.title || '').trim()) problems.push(`${l}.sections.${s.id}.title is empty`);
      if (!String(s.intro || '').trim()) problems.push(`${l}.sections.${s.id}.intro is empty`);
      if (!Array.isArray(s.items) || s.items.length === 0) problems.push(`${l}.sections.${s.id} has no items`);
      for (const [i, it] of (s.items || []).entries()) {
        if (!String(it.term || '').trim()) problems.push(`${l}.sections.${s.id}.items[${i}].term is empty`);
        if (!String(it.text || '').trim()) problems.push(`${l}.sections.${s.id}.items[${i}].text is empty`);
        if (typeof it.example !== 'string') problems.push(`${l}.sections.${s.id}.items[${i}].example must be a string (empty allowed)`);
      }
    }
    for (const [k, v] of Object.entries(tree.ui || {})) if (!String(v).trim()) problems.push(`${l}.ui.${k} is empty`);
    const dup = sectionIds.filter((id, i) => sectionIds.indexOf(id) !== i);
    if (dup.length) problems.push(`${l} has duplicate section ids: ${dup.join(', ')}`);
  }
  return problems;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const help = loadHelp();
  const problems = findProblems(help);
  if (problems.length) {
    console.error('rate-limit help content problems:\n  ' + problems.join('\n  '));
    process.exit(1);
  }
  const en = help.en;
  console.log(`ok: ${help.langs.length} languages, ${Object.keys(en.fields).length} inline explainers, ${Object.keys(en.scopes).length} scopes, ${en.sections.length} guide sections, ${en.sections.reduce((n, s) => n + s.items.length, 0)} guide items`);
}
