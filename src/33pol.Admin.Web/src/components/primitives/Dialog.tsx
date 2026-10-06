import type { JSX } from 'solid-js';
import { Show, createEffect, onCleanup, onMount, splitProps } from 'solid-js';
import { Portal } from 'solid-js/web';
import { IconX } from '../icons';
import { getFocusable, setInertBackground, trapFocus } from './focusTrap';

export interface DialogProps {
  open: boolean;
  title?: string;
  onClose: () => void;
  children: JSX.Element;
  initialFocus?: HTMLElement;
}

export function Dialog(props: DialogProps) {
  const [local] = splitProps(props, ['open', 'title', 'onClose', 'children', 'initialFocus']);
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
    requestAnimationFrame(() => {
      const target = local.initialFocus ?? getFocusable(panelRef!)[0];
      target?.focus();
    });
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
        <div class="modal-overlay" role="presentation" onClick={(e) => e.target === e.currentTarget && local.onClose()}>
          <div class="modal dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title" ref={panelRef}>
            <header class="modal-header">
              <Show when={local.title}>
                <h2 id="dialog-title">{local.title}</h2>
              </Show>
              <button type="button" class="icon-btn" aria-label="Close" onClick={() => local.onClose()}>
                <span class="icon"><IconX /></span>
              </button>
            </header>
            <div class="modal-body">{local.children}</div>
          </div>
        </div>
      </Portal>
    </Show>
  );
}
