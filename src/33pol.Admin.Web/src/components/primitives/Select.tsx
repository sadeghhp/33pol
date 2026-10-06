import type { JSX } from 'solid-js';
import { For, splitProps } from 'solid-js';

export interface SelectOption {
  value: string;
  label: string;
}

export interface SelectProps extends Omit<JSX.SelectHTMLAttributes<HTMLSelectElement>, 'onChange'> {
  options: SelectOption[];
  onChange?: (value: string) => void;
}

export function Select(props: SelectProps) {
  const [local, rest] = splitProps(props, ['options', 'onChange', 'class', 'value']);
  return (
    <select
      class={`select ${local.class ?? ''}`.trim()}
      value={local.value}
      onChange={(e) => local.onChange?.(e.currentTarget.value)}
      {...rest}
    >
      <For each={local.options}>
        {(opt) => <option value={opt.value}>{opt.label}</option>}
      </For>
    </select>
  );
}
