import { For } from 'solid-js';
import { IconAlertTriangle, IconCheckCircle } from '../icons';

export interface ToastItem {
  id: number;
  message: string;
  type?: 'success' | 'error' | 'warn';
}

export interface ToastContainerProps {
  toasts: ToastItem[];
}

export function ToastContainer(props: ToastContainerProps) {
  return (
    <div class="toast-container" aria-live="polite">
      <For each={props.toasts}>
        {(t) => (
          <div class={`toast ${t.type ?? 'success'}`}>
            <span class="toast-icon icon">
              {t.type === 'error' || t.type === 'warn' ? <IconAlertTriangle /> : <IconCheckCircle />}
            </span>
            <span>{t.message}</span>
          </div>
        )}
      </For>
    </div>
  );
}
