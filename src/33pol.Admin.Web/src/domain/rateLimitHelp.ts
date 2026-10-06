export interface HelpSection {
  id: string;
  title: string;
  intro?: string;
  items?: Array<{ term?: string; text?: string; example?: string }>;
}

export interface RateLimitHelpLang {
  ui?: Record<string, string>;
  sections?: HelpSection[];
  scopes?: Record<string, unknown>;
  fields?: Record<string, unknown>;
}

export interface RateLimitHelpRoot {
  langs?: Array<{ id: string; label: string; name: string; dir: string }>;
  en?: RateLimitHelpLang;
  fa?: RateLimitHelpLang;
}

let cached: RateLimitHelpRoot | null = null;

export async function fetchRateLimitHelpRoot(): Promise<RateLimitHelpRoot | null> {
  if (cached) return cached;
  try {
    const res = await fetch('/admin/admin-rate-limit-help.json');
    if (!res.ok) return null;
    cached = (await res.json()) as RateLimitHelpRoot;
    return cached;
  } catch {
    return null;
  }
}

export async function fetchRateLimitHelp(lang: 'en' | 'fa' = 'en'): Promise<RateLimitHelpLang | null> {
  const root = await fetchRateLimitHelpRoot();
  return root?.[lang] ?? null;
}

/** Test helper — reset module cache between tests. */
export function resetRateLimitHelpCache(): void {
  cached = null;
}
