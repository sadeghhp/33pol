import { Show } from 'solid-js';
import { sparkPaths } from '../stores/vitalsHistory';

export interface SparklineProps {
  values: readonly number[];
  class?: string;
}

export function Sparkline(props: SparklineProps) {
  const paths = () => sparkPaths(props.values);
  return (
    <Show when={paths().has}>
      <div class={`vital-spark ${props.class ?? ''}`.trim()} aria-hidden="true">
        <svg viewBox="0 0 100 100" preserveAspectRatio="none">
          <path class="spark-fill" d={paths().fill} />
          <polyline class="spark-line" points={paths().line} />
        </svg>
      </div>
    </Show>
  );
}
