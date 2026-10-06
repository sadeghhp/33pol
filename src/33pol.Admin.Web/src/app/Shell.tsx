import { For, Show, createEffect, createSignal, onMount } from 'solid-js';
import { useLocation, useNavigate } from '@solidjs/router';
import { Alert, Button, ToastContainer } from '../components/primitives';
import {
  IconActivity,
  IconBarChart,
  IconBug,
  IconFileText,
  IconGauge,
  IconGitBranch,
  IconKey,
  IconLogOut,
  IconMoon,
  IconSettings,
  IconSun,
  IconZap,
} from '../components/icons';
import { tabFromPath } from './router';
import { applyTheme, getStoredTheme, type ThemeMode } from './theme';
import {
  clearSession,
  dismissGlobalError,
  keyPrefix,
  persistApiKey,
  signedIn,
  useAuthSignals,
  verifyConnection,
  handleApiError,
  setVerifyConnection,
} from '../stores/auth';
import { setActiveTab, setErrorsAutoRefresh, setLogsAutoRefresh, useConnectionLifecycle, useConnectionSnapshot } from '../stores/connection';
import { useSummary } from '../stores/summary';
import { liveBadgeView } from './liveBadge';
import { formatNum } from '../domain/format';

const NAV = [
  { key: 'dashboard', label: 'Overview', path: '/dashboard', Icon: IconGauge },
  { key: 'usage', label: 'Usage', path: '/usage', Icon: IconBarChart },
  { key: 'routing', label: 'Routing', path: '/routing', Icon: IconGitBranch },
  { key: 'keys', label: 'Keys', path: '/keys', Icon: IconKey },
  { key: 'logs', label: 'Logs', path: '/logs', Icon: IconFileText },
  { key: 'errors', label: 'Errors', path: '/errors', Icon: IconBug },
  { key: 'settings', label: 'Settings', path: '/settings', Icon: IconSettings },
] as const;

export function Shell(props: { children: unknown }) {
  const location = useLocation();
  const navigate = useNavigate();
  const conn = useConnectionSnapshot();
  const { summary } = useSummary();
  const { toasts, globalError } = useAuthSignals();
  const [theme, setTheme] = createSignal<ThemeMode>(getStoredTheme());
  const [showChangeKey, setShowChangeKey] = createSignal(false);
  const [headerKey, setHeaderKey] = createSignal('');
  const [showKey, setShowKey] = createSignal(false);

  setVerifyConnection(verifyConnection);
  useConnectionLifecycle();

  createEffect(() => {
    const tab = tabFromPath(location.pathname);
    setActiveTab(tab);
    setLogsAutoRefresh(tab === 'logs');
    setErrorsAutoRefresh(tab === 'errors');
  });

  onMount(() => applyTheme(theme()));

  const setThemeMode = (mode: ThemeMode) => {
    setTheme(mode);
    applyTheme(mode);
  };

  const saveHeaderKey = async () => {
    try {
      await verifyConnection(headerKey());
      setShowChangeKey(false);
      setHeaderKey('');
    } catch (e) {
      handleApiError(e);
    }
  };

  const s = () => summary();
  const totalErrors = () => formatNum(s()?.totalErrors ?? 0);
  const activeRequests = () => formatNum(s()?.activeRequests ?? 0);
  const live = () => liveBadgeView(conn());

  return (
    <>
      <ToastContainer toasts={toasts()} />
      <Show when={signedIn()}>
        <div class="app-shell">
          <aside class="rail" role="navigation" aria-label="Main">
            <div class="rail-top">
              <div class="brand">
                <span class="brand-mark"><span class="icon"><IconZap /></span></span>
                <div>
                  <span class="brand-name">33pol</span>
                  <span class="brand-sub">Gateway</span>
                </div>
              </div>
              <nav class="rail-nav" role="tablist" aria-label="Sections">
                <For each={NAV}>
                  {(item) => {
                    const active = () => tabFromPath(location.pathname) === item.key;
                    return (
                      <button
                        type="button"
                        role="tab"
                        id={`tab-${item.key}`}
                        aria-selected={active()}
                        class={active() ? 'rail-link active' : 'rail-link'}
                        onClick={() => navigate(item.path)}
                      >
                        <span class="icon"><item.Icon /></span>
                        <span>{item.label}</span>
                      </button>
                    );
                  }}
                </For>
              </nav>
            </div>
            <div class="rail-foot">
              <div class="theme-switch" role="group" aria-label="Theme">
                <button type="button" class={theme() === 'light' ? 'active' : ''} aria-pressed={theme() === 'light'} onClick={() => setThemeMode('light')} title="Light">
                  <span class="icon"><IconSun /></span>
                </button>
                <button type="button" class={theme() === 'dark' ? 'active' : ''} aria-pressed={theme() === 'dark'} onClick={() => setThemeMode('dark')} title="Dark">
                  <span class="icon"><IconMoon /></span>
                </button>
              </div>
            </div>
          </aside>

          <div class="app-main">
            <header class="topbar">
              <div class="vitals">
                <span class={live().className} title={live().title} role="status">
                  <span class="pulse-dot" classList={{ live: live().dotClass === 'live' }} />
                  <span>{live().text}</span>
                </span>
                <Show when={conn().status === 'ok' || conn().status === ''}>
                  <span class="vital-chip is-ok"><span class="pulse-dot" /> Connected</span>
                </Show>
                <Show when={conn().status === 'fail'}>
                  <span class="vital-chip is-fail">Invalid key</span>
                </Show>
                <Show when={conn().degraded && conn().status !== 'fail'}>
                  <span class="vital-chip is-warn">Session check failed</span>
                </Show>
                <Show when={Number(s()?.activeRequests ?? 0) > 0}>
                  <span class="vital-chip is-live">
                    <span class="pulse-dot live" />
                    <span class="vc-value">{activeRequests()}</span> <span class="vc-label">in flight</span>
                  </span>
                </Show>
                <Show when={Number(s()?.totalErrors ?? 0) > 0}>
                  <button type="button" class="vital-chip is-fail is-actionable" onClick={() => navigate('/errors')}>
                    <span class="vc-value">{totalErrors()}</span> <span class="vc-label">errors</span>
                  </button>
                </Show>
              </div>
              <div class="topbar-right">
                <span class="key-tag">
                  <span class="vc-label">key</span> <code>{keyPrefix()}</code>
                </span>
                <button type="button" class="icon-btn" title="Change key" aria-label="Change key" onClick={() => setShowChangeKey((v) => !v)}>
                  <span class="icon"><IconKey /></span>
                </button>
                <Button variant="ghost" size="sm" onClick={() => { clearSession(); navigate('/dashboard'); }}>
                  <span class="icon"><IconLogOut /></span> Sign out
                </Button>
              </div>
            </header>

            <Show when={showChangeKey()}>
              <div class="card change-key-panel">
                <label for="header-apiKey">Admin API key</label>
                <div class="key-row">
                  <input
                    id="header-apiKey"
                    type={showKey() ? 'text' : 'password'}
                    value={headerKey()}
                    onInput={(e) => setHeaderKey(e.currentTarget.value)}
                    placeholder="Paste the new admin key"
                    onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), saveHeaderKey())}
                    autocomplete="off"
                  />
                  <button type="button" class="icon-btn" onClick={() => setShowKey((v) => !v)} aria-label="Toggle key visibility">
                    <span class="icon"><IconActivity /></span>
                  </button>
                  <Button onClick={saveHeaderKey}>Save</Button>
                </div>
              </div>
            </Show>

            <Show when={globalError()}>
              <Alert
                title={globalError()!.title}
                message={globalError()!.message}
                detail={globalError()!.detail}
                onDismiss={dismissGlobalError}
              />
            </Show>

            <main class="page-content" role="main" data-solid-root>
              {props.children as never}
            </main>
          </div>
        </div>
      </Show>
    </>
  );
}
