import { Show } from 'solid-js';
import { Button } from './Button';

export interface AlertProps {
  title?: string;
  message: string;
  detail?: string;
  onDismiss?: () => void;
}

export function Alert(props: AlertProps) {
  return (
    <div id="global-alert" class="global-alert" role="alert" aria-live="assertive">
      <div class="global-alert-body">
        <Show when={props.title}>
          <strong>{props.title}</strong>
        </Show>
        <p>{props.message}</p>
        <Show when={props.detail}>
          <details class="global-alert-details">
            <summary>Technical details</summary>
            <pre>{props.detail}</pre>
          </details>
        </Show>
      </div>
      <Show when={props.onDismiss}>
        <Button variant="ghost" size="sm" onClick={() => props.onDismiss?.()}>
          Dismiss
        </Button>
      </Show>
    </div>
  );
}
