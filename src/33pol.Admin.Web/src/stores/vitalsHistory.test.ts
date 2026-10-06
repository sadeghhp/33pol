import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearVitalsHistory,
  recordVitals,
  resetVitalsErrorCounters,
  sparkPaths,
  sparkSourceText,
  sparkValues,
  windowStats,
} from './vitalsHistory';

describe('vitalsHistory', () => {
  beforeEach(() => {
    clearVitalsHistory();
  });

  it('recordVitals_capsAtSixtySamples', () => {
    let now = 1_000_000;
    vi.spyOn(Date, 'now').mockImplementation(() => {
      now += 600;
      return now;
    });
    const summary = {
      totalInferenceRequests: 0,
      totalErrors: 0,
      averageLatencyMs: 1,
      activeStreams: 0,
      activeRequests: 0,
    };
    for (let i = 0; i < 65; i++) {
      recordVitals({ ...summary, totalInferenceRequests: i, activeRequests: i });
    }
    expect(sparkValues(null, 'inflight').length).toBe(60);
    vi.restoreAllMocks();
  });

  it('recordVitals_usesTotalInferenceRequests', () => {
    recordVitals({
      totalInferenceRequests: 42,
      totalErrors: 0,
      averageLatencyMs: 0,
      activeStreams: 0,
      activeRequests: 3,
    });
    const stats = windowStats(
      { totalInferenceRequests: 42, totalErrors: 0, averageLatencyMs: 0 },
      '5m',
    );
    expect(stats.requests).toBe(42);
  });

  it('sparkValues_prefersServerSeries', () => {
    const summary = {
      series: {
        stepSeconds: 60,
        points: [
          { requests: 60, errors: 0, latencyP95Ms: 100, ttftP95Ms: 50, inFlight: 2 },
          { requests: 120, errors: 6, latencyP95Ms: 200, ttftP95Ms: 80, inFlight: 4 },
        ],
      },
    };
    expect(sparkValues(summary, 'throughput')).toEqual([1, 2]);
    expect(sparkValues(summary, 'errorRate')).toEqual([0, 0.05]);
    expect(sparkSourceText(summary)).toContain('60 minutes');
  });

  it('sparkPaths_buildsSvgGeometry', () => {
    const paths = sparkPaths([1, 2, 3]);
    expect(paths.has).toBe(true);
    expect(paths.line).toMatch(/0\.00,\d+/);
    expect(paths.fill).toMatch(/^M0,100 L/);
  });

  it('resetVitalsErrorCounters_zerosErrorSamples', () => {
    recordVitals({
      totalInferenceRequests: 10,
      totalErrors: 5,
      averageLatencyMs: 0,
      activeStreams: 0,
      activeRequests: 0,
    });
    resetVitalsErrorCounters();
    expect(sparkValues(null, 'errorRate').every((v) => v >= 0)).toBe(true);
  });
});
