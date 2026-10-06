import type { JSX } from 'solid-js';
import { Show, splitProps } from 'solid-js';

export interface FieldProps {
  label?: string;
  for?: string;
  hint?: string;
  error?: string;
  children: JSX.Element;
  class?: string;
}

export function Field(props: FieldProps) {
  const [local] = splitProps(props, ['label', 'for', 'hint', 'error', 'children', 'class']);
  return (
    <div class={`field ${local.class ?? ''}`.trim()}>
      <Show when={local.label}>
        <label for={local.for}>{local.label}</label>
      </Show>
      {local.children}
      <Show when={local.hint}>
        <p class="field-hint">{local.hint}</p>
      </Show>
      <Show when={local.error}>
        <p class="field-error">{local.error}</p>
      </Show>
    </div>
  );
}
