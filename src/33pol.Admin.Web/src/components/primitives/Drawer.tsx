import type { JSX } from 'solid-js';
import { Show, createEffect, onCleanup, onMount, splitProps } from 'solid-js';
import { Portal } from 'solid-js/web';
import { IconX } from '../icons';
import { getFocusable, setInertBackground, trapFocus } from './focusTrap';

export interface DrawerProps {
  open: boolean;
  title?: string;
  onClose: () => void;
  children: JSX.Element;
}

export function Drawer(props: DrawerProps) {
  const [local] = splitProps(props, ['open', 'title', 'onClose', 'children']);
  let panelRef: HTMLDivElement | undefined;
  let previousFocus: HTMLElement | null = null;

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      local.onClose();
      return;
    }
    if (panelRef) trapFocus(panelRef, e);
  };

  onMount(() => {
    document.addEventListener('keydown', onKeyDown);
    onCleanup(() => document.removeEventListener('keydown', onKeyDown));
  });

  const activate = () => {
    previousFocus = document.activeElement as HTMLElement | null;
    setInertBackground(true, panelRef);
    requestAnimationFrame(() => getFocusable(panelRef!)[0]?.focus());
  };

  const deactivate = () => {
    setInertBackground(false);
    previousFocus?.focus?.();
    previousFocus = null;
  };

  createEffect(() => {
    if (local.open) activate();
    else deactivate();
  });

  return (
    <Show when={local.open}>
      <Portal>
        <div class="drawer-overlay" role="presentation" onClick={(e) => e.target === e.currentTarget && local.onClose()}>
          <aside class="drawer" role="dialog" aria-modal="true" ref={panelRef}>
            <header class="drawer-header">
              <Show when={local.title}>
                <h2>{local.title}</h2>
              </Show>
              <button type="button" class="icon-btn" aria-label="Close" onClick={() => local.onClose()}>
                <span class="icon"><IconX /></span>
              </button>
            </header>
            <div class="drawer-body">{local.children}</div>
          </aside>
        </div>
      </Portal>
    </Show>
  );
}
