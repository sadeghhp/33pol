export interface AttentionLink {
  tab: string;
  params?: Record<string, string | undefined>;
}

export interface AttentionItem {
  id?: string;
  code?: string;
  modelId?: string;
  tenantId?: string;
  severity?: string;
  title?: string;
  message?: string;
  detail?: string;
  link?: AttentionLink;
}

export function attentionKey(item: AttentionItem): string {
  if (item.id) return item.id;
  return [item.code ?? item.severity ?? 'info', item.modelId ?? '', item.tenantId ?? ''].join('|');
}

export function attentionLinkPath(link: AttentionLink | undefined): string | null {
  if (!link?.tab) return null;
  const tabPaths: Record<string, string> = {
    dashboard: '/dashboard',
    usage: '/usage',
    routing: '/routing',
    keys: '/keys',
    logs: '/logs',
    errors: '/errors',
    settings: '/settings',
  };
  const base = tabPaths[link.tab];
  if (!base) return null;
  const params = link.params ?? {};
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v != null && v !== '') qs.set(k, v);
  }
  const q = qs.toString();
  return q ? `${base}?${q}` : base;
}

export function attentionRows(
  items: unknown,
  dismissed: readonly string[],
  wallboard: boolean,
): Array<AttentionItem & { key: string; cls: string; hasLink: boolean; linkPath: string | null }> {
  if (!Array.isArray(items)) return [];
  return items
    .map((raw) => raw as AttentionItem)
    .map((item) => ({ item, key: attentionKey(item) }))
    .filter(({ key }) => wallboard || !dismissed.includes(key))
    .map(({ item, key }) => ({
      ...item,
      key,
      cls: `attention-item is-${item.severity ?? 'info'}`,
      hasLink: !!item.link?.tab,
      linkPath: attentionLinkPath(item.link),
    }));
}

export function hasCriticalAttention(rows: readonly { severity?: string }[]): boolean {
  return rows.some((r) => r.severity === 'critical');
}
