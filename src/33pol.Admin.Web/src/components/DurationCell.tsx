import { createEffect, createMemo, createSignal } from 'solid-js';
import { formatDurationMs } from '../domain/format';
import { createClock } from '../app/clock';

const sharedClock = createClock();

export interface DurationCellProps {
  startedAt?: string;
  durationMs?: number;
  inFlight?: boolean;
}

export function DurationCell(props: DurationCellProps) {
  const [now, setNow] = createSignal(sharedClock.nowMs());

  createEffect(() => {
    if (!props.inFlight) return;
    return sharedClock.subscribe(setNow);
  });

  const value = createMemo(() => {
    if (props.inFlight && props.startedAt) {
      const start = new Date(props.startedAt).getTime();
      if (Number.isFinite(start)) return formatDurationMs(now() - start);
    }
    return formatDurationMs(props.durationMs);
  });

  return <span class="mono">{value()}</span>;
}
