import { createSignal } from 'solid-js';

export interface VitalSample {
  t: number;
  requests: number;
  errors: number;
  latency: number;
  streams: number;
  inflight: number;
}

export type SparkMetric = 'throughput' | 'errorRate' | 'latency' | 'ttft' | 'inflight';

const CAP = 60;
const DEDUPE_MS = 500;

const [history, setHistory] = createSignal<VitalSample[]>([]);

export function useVitalsHistory() {
  return history;
}

export function recordVitals(summary: Record<string, unknown> | null): void {
  if (!summary) return;
  const sample: VitalSample = {
    t: Date.now(),
    requests: Number(summary.totalInferenceRequests ?? 0),
    errors: Number(summary.totalErrors ?? 0),
    latency: Number(summary.averageLatencyMs ?? 0),
    streams: Number(summary.activeStreams ?? 0),
    inflight: Number(summary.activeRequests ?? 0),
  };
  setHistory((h) => {
    const last = h[h.length - 1];
    if (last && sample.t - last.t < DEDUPE_MS) return h;
    const next = [...h, sample];
    while (next.length > CAP) next.shift();
    return next;
  });
}

export function resetVitalsErrorCounters(): void {
  setHistory((h) => h.map((sample) => ({ ...sample, errors: 0 })));
}

export function clearVitalsHistory(): void {
  setHistory([]);
}

function seriesValues(summary: Record<string, unknown> | null, metric: SparkMetric): number[] | null {
  const series = summary?.series as { points?: unknown[]; stepSeconds?: number } | undefined;
  const points = series?.points;
  if (!Array.isArray(points) || points.length < 2) return null;
  const step = Math.max(1, Number(series?.stepSeconds ?? 60));
  return points.map((raw) => {
    const p = raw as Record<string, unknown>;
    const requests = Number(p.requests ?? 0);
    switch (metric) {
      case 'throughput':
        return requests / step;
      case 'errorRate':
        return requests > 0 ? Number(p.errors ?? 0) / requests : 0;
      case 'latency':
        return Number(p.latencyP95Ms ?? 0);
      case 'ttft':
        return Number(p.ttftP95Ms ?? 0);
      case 'inflight':
        return Number(p.inFlight ?? 0);
      default:
        return 0;
    }
  });
}

export function sparkValues(
  summary: Record<string, unknown> | null,
  metric: SparkMetric,
  samples: readonly VitalSample[] = history(),
): number[] {
  const fromServer = seriesValues(summary, metric);
  if (fromServer) return fromServer;
  if (samples.length < 2) return [];
  if (metric === 'ttft') return [];
  if (metric === 'throughput' || metric === 'errorRate') {
    const key = metric === 'throughput' ? 'requests' : 'errors';
    const out: number[] = [];
    for (let i = 1; i < samples.length; i++) {
      const dt = Math.max(1, (samples[i].t - samples[i - 1].t) / 1000);
      out.push(Math.max(0, (samples[i][key] - samples[i - 1][key]) / dt));
    }
    return out;
  }
  const key = metric === 'latency' ? 'latency' : metric === 'inflight' ? 'inflight' : 'streams';
  return samples.map((s) => Number(s[key] ?? 0));
}

export function sparkPaths(values: readonly number[]): { line: string; fill: string; has: boolean } {
  if (values.length < 2) return { line: '', fill: '', has: false };
  const max = Math.max(...values, 1e-9);
  const n = values.length;
  const points = values.map((val, i) => {
    const x = (i / (n - 1)) * 100;
    const y = 94 - Math.min(88, (val / max) * 88);
    return { x, y };
  });
  const line = points.map((p) => `${p.x.toFixed(2)},${p.y.toFixed(2)}`).join(' ');
  let fill = 'M0,100';
  for (const p of points) fill += ` L${p.x.toFixed(2)},${p.y.toFixed(2)}`;
  fill += ' L100,100 Z';
  return { line, fill, has: true };
}

export function sparkSourceText(summary: Record<string, unknown> | null): string {
  return seriesValues(summary, 'throughput')
    ? 'Last 60 minutes, one point per minute'
    : 'Since this page was opened';
}

export interface WindowStats {
  lifetime: boolean;
  window: string;
  seconds: number;
  requests: number;
  errors: number;
  errorRate: number;
  rps: number;
  avgMs: number;
  p50Ms: number | null;
  p95Ms: number | null;
  p99Ms: number | null;
  ttftP50Ms: number | null;
  ttftP95Ms: number | null;
  ttftSamples: number;
}

export function windowStats(
  summary: Record<string, unknown> | null,
  windowId: string,
  samples: readonly VitalSample[] = history(),
): WindowStats {
  const windows = summary?.windows;
  if (Array.isArray(windows) && windows.length > 0) {
    const w =
      (windows as Record<string, unknown>[]).find((x) => x.window === windowId) ??
      (windows as Record<string, unknown>[])[0];
    return {
      lifetime: false,
      window: String(w.window ?? windowId),
      seconds: Number(w.windowSeconds ?? 300),
      requests: Number(w.requests ?? 0),
      errors: Number(w.errors ?? 0),
      errorRate: Number(w.errorRate ?? 0),
      rps: Number(w.requestsPerSecond ?? 0),
      avgMs: Number(w.latencyAvgMs ?? 0),
      p50Ms: (w.latencyP50Ms as number | null) ?? null,
      p95Ms: (w.latencyP95Ms as number | null) ?? null,
      p99Ms: (w.latencyP99Ms as number | null) ?? null,
      ttftP50Ms: (w.ttftP50Ms as number | null) ?? null,
      ttftP95Ms: (w.ttftP95Ms as number | null) ?? null,
      ttftSamples: Number(w.ttftSamples ?? 0),
    };
  }
  const req = Number(summary?.totalInferenceRequests ?? 0);
  const err = Number(summary?.totalErrors ?? 0);
  let rps = 0;
  if (samples.length >= 2) {
    const last = samples[samples.length - 1];
    const prev = samples[samples.length - 2];
    const dt = Math.max(1, (last.t - prev.t) / 1000);
    rps = Math.max(0, (last.requests - prev.requests) / dt);
  }
  return {
    lifetime: true,
    window: 'lifetime',
    seconds: Number(summary?.uptimeSeconds ?? 0),
    requests: req,
    errors: err,
    errorRate: req > 0 ? err / req : 0,
    rps,
    avgMs: Number(summary?.averageLatencyMs ?? 0),
    p50Ms: null,
    p95Ms: null,
    p99Ms: null,
    ttftP50Ms: null,
    ttftP95Ms: null,
    ttftSamples: 0,
  };
}

export function hasTrailingWindows(summary: Record<string, unknown> | null): boolean {
  return Array.isArray(summary?.windows) && (summary!.windows as unknown[]).length > 0;
}
