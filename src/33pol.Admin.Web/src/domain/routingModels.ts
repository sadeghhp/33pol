export interface ModelPricing {
  inputPricePerMillionTokens: number;
  outputPricePerMillionTokens: number;
}

export interface ModelRow {
  id: string;
  url: string;
  aliases: string[];
  state: string;
  modelType: string | null;
  publicAccess: boolean;
  hasUpstreamCredential: boolean;
  pricing: ModelPricing | null;
  upstreamAuth: unknown;
  capabilities: unknown[];
  maxContextLength: number;
}

export interface ModelTypeDescriptor {
  value: string;
  label: string;
  testEndpoint: string | null;
  aliases: string[];
}

export interface ModelEditDraft {
  id: string;
  url: string;
  maxContextLength: number;
  aliasesText: string;
  apiKey: string;
  clearApiKey: boolean;
  hasUpstreamCredential: boolean;
  publicAccess: boolean;
  upstreamAuth: unknown;
  capabilities: unknown[];
  modelType: string;
  inputPricePerMillion: string | number;
  outputPricePerMillion: string | number;
  _hadPricing: boolean;
  _existing: boolean;
  _originalId: string;
}

export interface ModelWriteBody {
  model: {
    id: string;
    url: string;
    maxContextLength: number;
    aliases: string[];
    publicAccess: boolean;
    modelType: string | null;
    capabilities: unknown[];
    upstreamAuth?: unknown;
  };
  apiKey: string | null;
  clearApiKey: boolean;
  pricing: ModelPricing | null;
  clearPricing: boolean;
}

export interface ModelTestResult {
  ok: boolean;
  modelId?: string;
  modelType?: string;
  endpoint?: string;
  supported?: boolean;
  latencyMs?: number;
  statusCode?: number;
  detail?: string;
  content?: string;
  hint?: string;
}

export const DEFAULT_MODEL_DRAFT: ModelEditDraft = {
  id: '',
  url: '',
  maxContextLength: 8192,
  aliasesText: '',
  apiKey: '',
  clearApiKey: false,
  hasUpstreamCredential: false,
  publicAccess: false,
  upstreamAuth: null,
  capabilities: [],
  modelType: 'text-generation',
  inputPricePerMillion: '',
  outputPricePerMillion: '',
  _hadPricing: false,
  _existing: false,
  _originalId: '',
};

export function normalizeModelItem(raw: Record<string, unknown>): ModelRow {
  const nested = (raw.model as Record<string, unknown> | undefined) ?? raw;
  const pricingRaw = (raw.pricing ?? nested.pricing) as ModelPricing | null | undefined;
  return {
    id: String(nested.id ?? ''),
    url: String(nested.url ?? ''),
    aliases: Array.isArray(nested.aliases) ? nested.aliases.map(String) : [],
    state: String(nested.state ?? raw.state ?? 'serving'),
    modelType: nested.modelType != null ? String(nested.modelType) : null,
    publicAccess: !!(nested.publicAccess ?? raw.publicAccess),
    hasUpstreamCredential: !!(raw.hasUpstreamCredential ?? nested.hasUpstreamCredential),
    pricing: pricingRaw
      ? {
          inputPricePerMillionTokens: Number(pricingRaw.inputPricePerMillionTokens ?? 0),
          outputPricePerMillionTokens: Number(pricingRaw.outputPricePerMillionTokens ?? 0),
        }
      : null,
    upstreamAuth: nested.upstreamAuth ?? null,
    capabilities: Array.isArray(nested.capabilities) ? nested.capabilities : [],
    maxContextLength: Number(nested.maxContextLength ?? 8192),
  };
}

export function resolveModelType(model: ModelRow, types: ModelTypeDescriptor[]): string {
  if (model.modelType) return model.modelType;
  const id = model.id.toLowerCase();
  for (const t of types) {
    if (t.value.toLowerCase() === id) return t.value;
    if (t.aliases.some((a) => a.toLowerCase() === id)) return t.value;
  }
  return 'text-generation';
}

export function draftFromModel(model: ModelRow, types: ModelTypeDescriptor[]): ModelEditDraft {
  return {
    id: model.id,
    url: model.url,
    maxContextLength: model.maxContextLength,
    aliasesText: model.aliases.join(', '),
    apiKey: '',
    clearApiKey: false,
    hasUpstreamCredential: model.hasUpstreamCredential,
    publicAccess: model.publicAccess,
    upstreamAuth: model.upstreamAuth,
    capabilities: model.capabilities,
    modelType: resolveModelType(model, types),
    inputPricePerMillion: model.pricing?.inputPricePerMillionTokens ?? '',
    outputPricePerMillion: model.pricing?.outputPricePerMillionTokens ?? '',
    _hadPricing: !!model.pricing,
    _existing: true,
    _originalId: model.id,
  };
}

function pricingFilled(v: string | number | null | undefined): boolean {
  return v !== '' && v !== null && v !== undefined;
}

export function modelPricingError(draft: ModelEditDraft): string {
  const input = draft.inputPricePerMillion;
  const output = draft.outputPricePerMillion;
  const inputFilled = pricingFilled(input);
  const outputFilled = pricingFilled(output);
  if (inputFilled !== outputFilled) {
    return 'Set both input and output prices, or leave both blank to leave the model unpriced.';
  }
  if (!inputFilled) return '';
  for (const v of [input, output]) {
    const n = Number(v);
    if (!Number.isFinite(n) || n < 0) return 'Prices must be zero or greater.';
  }
  return '';
}

export function buildModelWriteBody(draft: ModelEditDraft): ModelWriteBody {
  const aliases = (draft.aliasesText || '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
  const model: ModelWriteBody['model'] = {
    id: draft.id.trim(),
    url: draft.url.trim(),
    maxContextLength: Number(draft.maxContextLength) || 8192,
    aliases,
    publicAccess: !!draft.publicAccess,
    modelType: draft.modelType || null,
    capabilities: draft.capabilities || [],
  };
  if (
    draft._existing &&
    draft.upstreamAuth &&
    !(draft.apiKey || '').trim() &&
    !draft.clearApiKey
  ) {
    model.upstreamAuth = draft.upstreamAuth;
  }
  const apiKey = (draft.apiKey || '').trim();
  const hasPricing =
    pricingFilled(draft.inputPricePerMillion) && pricingFilled(draft.outputPricePerMillion);
  return {
    model,
    apiKey: apiKey || null,
    clearApiKey: !!draft.clearApiKey,
    pricing: hasPricing
      ? {
          inputPricePerMillionTokens: Number(draft.inputPricePerMillion),
          outputPricePerMillionTokens: Number(draft.outputPricePerMillion),
        }
      : null,
    clearPricing: !hasPricing && !!draft._hadPricing,
  };
}

export function validateModelDraft(draft: ModelEditDraft): string {
  const write = buildModelWriteBody(draft);
  if (!write.model.id || !write.model.url) return 'Model name and upstream URL are required.';
  if (/localhost|127\.0\.0\.1/i.test(write.model.url)) {
    return 'Use http://host.docker.internal:<port> when the gateway runs in Docker (not localhost).';
  }
  return modelPricingError(draft);
}

export function formatModelPrice(pricing: ModelPricing | null): string {
  if (!pricing) return '—';
  return `$${pricing.inputPricePerMillionTokens}/$${pricing.outputPricePerMillionTokens}`;
}
