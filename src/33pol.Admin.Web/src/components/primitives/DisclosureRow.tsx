import type { JSX } from 'solid-js';
import { Show, splitProps } from 'solid-js';
import { IconChevronDown, IconChevronUp } from '../icons';

export interface DisclosureRowProps {
  expanded: boolean;
  onToggle: () => void;
  summary: JSX.Element;
  detail?: JSX.Element;
  class?: string;
}

export function DisclosureRow(props: DisclosureRowProps) {
  const [local] = splitProps(props, ['expanded', 'onToggle', 'summary', 'detail', 'class']);
  return (
    <div class={`disclosure-row ${local.class ?? ''}`.trim()}>
      <button type="button" class="disclosure-summary" aria-expanded={local.expanded} onClick={() => local.onToggle()}>
        <span class="icon disclosure-chevron">{local.expanded ? <IconChevronUp /> : <IconChevronDown />}</span>
        {local.summary}
      </button>
      <Show when={local.expanded && local.detail}>
        <div class="disclosure-detail">{local.detail}</div>
      </Show>
    </div>
  );
}
