import { createSignal, Show } from 'solid-js';
import type { JSX } from 'solid-js';
import { Button } from '../components/primitives';
import { IconEye, IconEyeOff, IconLogOut, IconZap } from '../components/icons';
import {
  dismissGlobalError,
  signedIn,
  useAuthSignals,
  verifyConnection,
  handleApiError,
} from '../stores/auth';

export interface AuthGateProps {
  children: JSX.Element;
}

export function AuthGate(props: AuthGateProps) {
  const { globalError, authLoading } = useAuthSignals();
  const [gateKey, setGateKey] = createSignal('');
  const [showKey, setShowKey] = createSignal(false);
  const [localError, setLocalError] = createSignal('');

  const saveKey = async () => {
    setLocalError('');
    dismissGlobalError();
    try {
      const ok = await verifyConnection(gateKey());
      if (!ok) setLocalError('Could not connect with that key.');
    } catch (e) {
      handleApiError(e);
      const err = e as { message?: string };
      setLocalError(err.message ?? 'Authentication failed.');
    }
  };

  return (
    <Show when={signedIn()} fallback={
      <div class="auth-gate" role="main">
        <div class="auth-brand">
          <span class="brand-mark"><span class="icon"><IconZap /></span></span>
          <div>
            <h1>33pol control plane</h1>
            <span class="brand-sub">LLM gateway admin</span>
          </div>
        </div>
        <p class="lede">
          Sign in with an Admin API key. It's stored in this browser only (localStorage) — use a private window on shared machines.
        </p>
        <label for="gate-apiKey">Admin API key</label>
        <div class="key-row">
          <input
            id="gate-apiKey"
            type={showKey() ? 'text' : 'password'}
            value={gateKey()}
            onInput={(e) => setGateKey(e.currentTarget.value)}
            placeholder="sk-…"
            onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), saveKey())}
            autocomplete="off"
          />
          <button type="button" class="icon-btn" onClick={() => setShowKey((v) => !v)} aria-label={showKey() ? 'Hide key' : 'Show key'}>
            <span class="icon">{showKey() ? <IconEyeOff /> : <IconEye />}</span>
          </button>
        </div>
        <Button onClick={saveKey} disabled={authLoading()}>
          <span class="icon"><IconLogOut /></span> Connect
        </Button>
        <Show when={localError() || globalError()?.message}>
          <p class="field-error">{localError() || globalError()?.message}</p>
        </Show>
      </div>
    }>
      {props.children}
    </Show>
  );
}
