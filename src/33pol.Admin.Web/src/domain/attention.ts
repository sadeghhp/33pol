export interface AttentionItem {
  id?: string;
  severity?: string;
  title?: string;
  message?: string;
  detail?: string;
}

export function attentionKey(item: AttentionItem): string {
  return String(item.id ?? `${item.severity ?? 'info'}:${item.title ?? ''}:${item.message ?? ''}`);
}

export function attentionRows(
  items: unknown,
  dismissed: readonly string[],
  wallboard: boolean,
): Array<AttentionItem & { key: string; cls: string }> {
  if (!Array.isArray(items)) return [];
  return items
    .map((raw) => raw as AttentionItem)
    .map((item) => ({ item, key: attentionKey(item) }))
    .filter(({ key }) => wallboard || !dismissed.includes(key))
    .map(({ item, key }) => ({
      ...item,
      key,
      cls: `attention-item is-${item.severity ?? 'info'}`,
    }));
}

export function hasCriticalAttention(rows: readonly { severity?: string }[]): boolean {
  return rows.some((r) => r.severity === 'critical');
}
