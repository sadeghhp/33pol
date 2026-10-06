import type { JSX } from 'solid-js';
import { splitProps } from 'solid-js';

export interface ButtonProps extends JSX.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost' | 'danger';
  size?: 'sm' | 'md';
}

export function Button(props: ButtonProps) {
  const [local, rest] = splitProps(props, ['variant', 'size', 'class', 'type', 'children']);
  const variant = () => local.variant ?? 'primary';
  const size = () => local.size ?? 'md';
  return (
    <button
      type={local.type ?? 'button'}
      class={`action ${variant() === 'ghost' ? 'ghost' : ''} ${variant() === 'danger' ? 'danger' : ''} ${size() === 'sm' ? 'sm' : ''} ${local.class ?? ''}`.trim()}
      {...rest}
    >
      {local.children}
    </button>
  );
}
