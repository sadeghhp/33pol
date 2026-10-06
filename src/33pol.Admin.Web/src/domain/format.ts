export function formatNum(n: unknown): string {
  const x = Number(n);
  if (!Number.isFinite(x)) return n == null || n === '' ? '—' : String(n);
  return x.toLocaleString();
}

export function formatTime(iso: unknown): string {
  if (!iso) return '—';
  try {
    return new Date(String(iso)).toLocaleString();
  } catch {
    return String(iso);
  }
}

/**
 * Money on the Usage page. null/undefined is "not priced" and renders as a dash — it must not
 * collapse into $0.00, which is what a free request costs. Sub-cent amounts keep three
 * significant digits so a $0.0000014 event is distinguishable from zero.
 */
export function formatCost(value: unknown, currency?: string): string {
  if (value === null || value === undefined || value === '') return '—';
  const n = Number(value);
  if (!Number.isFinite(n)) return String(value);
  const opts: Intl.NumberFormatOptions = { style: 'currency', currency: currency || 'USD' };
  if (n !== 0 && Math.abs(n) < 0.01) {
    opts.maximumSignificantDigits = 3;
  } else {
    opts.minimumFractionDigits = 2;
    opts.maximumFractionDigits = 4;
  }
  try {
    return new Intl.NumberFormat(undefined, opts).format(n);
  } catch {
    return n.toFixed(4);
  }
}

export function formatDurationMs(ms: unknown): string {
  if (ms == null || !Number.isFinite(Number(ms))) return '—';
  const value = Number(ms);
  if (value < 1000) return Math.round(value) + ' ms';
  if (value < 60000) return (value / 1000).toFixed(value < 10000 ? 2 : 1) + ' s';
  const m = Math.floor(value / 60000);
  const sec = Math.round((value % 60000) / 1000);
  return m + 'm ' + String(sec).padStart(2, '0') + 's';
}
