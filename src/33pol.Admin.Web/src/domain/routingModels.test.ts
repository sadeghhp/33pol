import { describe, expect, it } from 'vitest';
import {
  buildModelWriteBody,
  draftFromModel,
  modelPricingError,
  normalizeModelItem,
  validateModelDraft,
  type ModelEditDraft,
} from './routingModels';

const baseDraft = (): ModelEditDraft => ({
  id: 'gpt-4',
  url: 'http://host.docker.internal:8080',
  maxContextLength: 8192,
  aliasesText: 'gpt4, gpt-4o',
  apiKey: '',
  clearApiKey: false,
  hasUpstreamCredential: false,
  publicAccess: true,
  upstreamAuth: null,
  capabilities: ['chat'],
  modelType: 'text-generation',
  inputPricePerMillion: '3',
  outputPricePerMillion: '6',
  _hadPricing: false,
  _existing: false,
  _originalId: '',
});

describe('normalizeModelItem', () => {
  it('flattensAdminListItem', () => {
    const row = normalizeModelItem({
      model: { id: 'm1', url: 'http://x', aliases: ['a'], state: 'serving' },
      hasUpstreamCredential: true,
      pricing: { inputPricePerMillionTokens: 1, outputPricePerMillionTokens: 2 },
    });
    expect(row.id).toBe('m1');
    expect(row.hasUpstreamCredential).toBe(true);
    expect(row.pricing?.inputPricePerMillionTokens).toBe(1);
  });
});

describe('buildModelWriteBody', () => {
  it('buildsAliasesAndPricing', () => {
    const body = buildModelWriteBody(baseDraft());
    expect(body.model.aliases).toEqual(['gpt4', 'gpt-4o']);
    expect(body.pricing).toEqual({ inputPricePerMillionTokens: 3, outputPricePerMillionTokens: 6 });
  });

  it('setsClearPricingWhenEmptied', () => {
    const draft = { ...baseDraft(), inputPricePerMillion: '', outputPricePerMillion: '', _hadPricing: true };
    const body = buildModelWriteBody(draft);
    expect(body.clearPricing).toBe(true);
    expect(body.pricing).toBeNull();
  });
});

describe('modelPricingError', () => {
  it('requiresBothPrices', () => {
    expect(modelPricingError({ ...baseDraft(), outputPricePerMillion: '' })).toContain('both');
  });
});

describe('validateModelDraft', () => {
  it('rejectsLocalhostUrl', () => {
    expect(validateModelDraft({ ...baseDraft(), url: 'http://localhost:8080' })).toContain('Docker');
  });
});

describe('draftFromModel', () => {
  it('preservesOriginalId', () => {
    const model = normalizeModelItem({ id: 'old', url: 'http://x' });
    const draft = draftFromModel(model, []);
    expect(draft._originalId).toBe('old');
    expect(draft._existing).toBe(true);
  });
});
