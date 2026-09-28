/**
 * Usage & cost: the model filter and the per-key share of the filtered load.
 *
 *   - the model travels with every usage call, is named in the scope note and cleared with the rest;
 *   - the picker keeps its options once a model is selected;
 *   - the share table labels keys, anonymous traffic and deleted keys, and formats percentages.
 *
 *     node --test tests/admin-console/
 */
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const ADMIN = path.join(__dirname, '../../src/33pol.App/wwwroot/admin');
const SOURCE = path.join(ADMIN, 'admin-app.js');
const HTML = fs.readFileSync(path.join(ADMIN, 'index.html'), 'utf8');

function createApp() {
  const context = {
    document: { addEventListener() {}, hidden: false, getElementById: () => ({ scrollIntoView() {}, focus() {} }) },
    window: { addEventListener() {}, AdminIcons: null },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    Alpine: { data() {}, directive() {}, store: () => ({}) },
    setInterval: () => 0, clearInterval() {}, setTimeout: () => 0, clearTimeout() {},
    console, Intl, URLSearchParams,
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(SOURCE, 'utf8'), context);
  const app = context.adminApp();
  app.isLoading = () => false;
  return app;
}

const KEY_A = '0b9f6c1e-1111-4222-8333-444455556666';
const KEY_GONE = '9a9a9a9a-2222-4333-8444-555566667777';

test('the model filter is sent on every usage call and named in the scope note', () => {
  const app = createApp();
  app.usageFrom = '2026-09-01';
  app.usageTo = '2026-09-28';
  app.usageFilterModelId = ' llama-3 ';
  const q = new URLSearchParams(app.usageParams());
  assert.strictEqual(q.get('modelId'), 'llama-3');
  const forecast = new URLSearchParams(app.usageParams({ days: 7 }, false));
  assert.strictEqual(forecast.get('modelId'), 'llama-3');
  assert.match(app.usageScopeNote, /model llama-3/);

  app.clearUsageFilters();
  assert.strictEqual(app.usageFilterModelId, '');
  assert.strictEqual(new URLSearchParams(app.usageParams()).get('modelId'), null);
});

test('the model picker keeps models seen before one was selected', () => {
  const app = createApp();
  app.models = [{ id: 'gpt-4o' }];
  app.rememberUsageModels([{ modelId: 'llama-3' }, { modelId: 'gpt-4o' }]);
  // A filtered report names only the chosen model; the other options must survive.
  app.usage = { rollups: [{ modelId: 'llama-3' }] };
  app.usageFilterModelId = 'llama-3';
  assert.deepStrictEqual(Array.from(app.usageModelOptions, o => o.value), ['gpt-4o', 'llama-3']);
});

test('the share table labels keys, anonymous traffic and deleted keys', () => {
  const app = createApp();
  app.usageKeyShares = {
    modelId: 'llama-3', currency: 'USD', totalRequests: 1000, totalTokens: 50000, totalCost: 2,
    keys: [
      { apiKeyId: KEY_A, keyPrefix: 'sk-33pol-ab', label: 'Batch', assignee: 'ana', requests: 750, promptTokens: 30000, completionTokens: 10000, totalCost: 1.5, requestShare: 0.75, tokenShare: 0.8, costShare: 0.75 },
      { apiKeyId: null, requests: 249, promptTokens: 5000, completionTokens: 4999, totalCost: 0.5, requestShare: 0.249, tokenShare: 0.19998, costShare: 0.25 },
      { apiKeyId: KEY_GONE, requests: 1, promptTokens: 1, completionTokens: 0, totalCost: 0, requestShare: 0.0004, tokenShare: 0.00002, costShare: null },
    ],
  };
  const rows = app.usageKeyShareRows;
  assert.deepStrictEqual(Array.from(rows, r => r.name), ['Batch', 'anonymous', '9a9a9a9a… (deleted)']);
  assert.strictEqual(rows[0].requestShareText, '75%');
  assert.strictEqual(rows[0].barStyle, 'width:75.0%');
  assert.strictEqual(rows[0].assignee, 'ana');
  assert.strictEqual(rows[1].nameClass, 'tag muted');
  assert.strictEqual(rows[1].requestShareText, '25%');
  assert.strictEqual(rows[2].requestShareText, '<0.1%');
  assert.strictEqual(rows[2].costShareText, '—');
  assert.strictEqual(app.usageKeySharesTitle, 'Load on llama-3 by API key');
  assert.match(app.usageKeySharesHint, /^1,000 requests across 3 keys$/);
});

test('without a model the table covers every model and prompts for one', () => {
  const app = createApp();
  app.usageKeyShares = { modelId: null, totalRequests: 0, keys: [] };
  assert.strictEqual(app.usageKeySharesTitle, 'Load by API key (all models)');
  assert.match(app.usageKeySharesHint, /pick a model/);
  assert.strictEqual(app.usageKeySharesEmpty, true);
});

test('the markup binds the new filter and table', () => {
  assert.match(HTML, /x-model="mdl\.usageFilterModelId"/);
  assert.match(HTML, /x-for="m in usageModelOptions"/);
  assert.match(HTML, /x-for="k in usageKeyShareRows"/);
});
