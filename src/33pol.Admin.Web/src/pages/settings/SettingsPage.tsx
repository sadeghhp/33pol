import { For, Show, createEffect, lazy, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { Button, Tabs } from '../../components/primitives';
import { IconActivity, IconCheckCircle, IconRefresh } from '../../components/icons';
import { activateCorsPanel, disposeCorsPanel, loadCors, saveCors, useCorsStore } from '../../stores/cors';
import { activateSettingsPage, disposeSettingsPage, useSettingsStore, type SettingsSubTab } from '../../stores/settings';

const RateLimitsPage = lazy(() => import('../ratelimits/RateLimitsPage'));

export default function SettingsPage() {
  const store = useSettingsStore();
  const cors = useCorsStore();
  const [params] = useSearchParams();

  onMount(() => {
    activateSettingsPage();
    const sub = params.sub as SettingsSubTab | undefined;
    if (sub === 'ratelimits' || sub === 'cors' || sub === 'observability' || sub === 'runtime') {
      store.setSubTab(sub);
    }
  });
  onCleanup(() => {
    disposeSettingsPage();
    disposeCorsPanel();
  });

  createEffect(() => {
    void store.load();
  });

  createEffect(() => {
    store.setRateLimitsVisible(store.subTab() === 'ratelimits');
    if (store.subTab() === 'cors') activateCorsPanel();
    else disposeCorsPanel();
  });

  return (
    <section class="page" id="panel-settings">
      <header class="page-header">
        <div>
          <p class="eyebrow">Configuration</p>
          <h1>Settings</h1>
        </div>
      </header>

      <Tabs
        defaultId={store.subTab()}
        onChange={(id) => store.setSubTab(id as SettingsSubTab)}
        tabs={[
          {
            id: 'runtime',
            label: 'Runtime',
            content: () => (
              <div class="card">
                <Show when={store.phase() === 'loading'}>
                  <p class="loading-hint">Loading config status…</p>
                </Show>
                <pre class="config-dump">{JSON.stringify(store.configStatus(), null, 2)}</pre>
              </div>
            ),
          },
          {
            id: 'ratelimits',
            label: 'Rate limits',
            content: () => <RateLimitsPage visible={store.rateLimitsVisible()} />,
          },
          {
            id: 'cors',
            label: 'CORS',
            content: () => (
              <div class="card">
                <span class="eyebrow">Browser origins</span>
                <h3>CORS allowed origins</h3>
                <p class="hint">
                  Browser SPA origins that may call <code>/v1/*</code> in Production. Applied without a restart.
                </p>
                <Show when={cors.loadError()}>
                  <p class="notice error">{cors.loadError()}</p>
                </Show>
                <Show when={cors.loading() && cors.origins() == null}>
                  <p class="loading-hint">Loading CORS settings…</p>
                </Show>
                <Show when={cors.origins() != null}>
                  <For each={cors.origins() ?? []}>
                    {(origin, index) => (
                      <div class="repeat-row">
                        <label>
                          Origin
                          <input
                            type="text"
                            value={origin}
                            placeholder="https://example.com"
                            onInput={(e) => cors.updateRow(index(), e.currentTarget.value)}
                          />
                        </label>
                        <Button variant="ghost" size="sm" onClick={() => cors.removeRow(index())}>Remove</Button>
                      </div>
                    )}
                  </For>
                  <div class="toolbar">
                    <Button variant="ghost" size="sm" onClick={() => cors.addRow()}>Add origin</Button>
                    <span class="spacer" />
                    <Button variant="ghost" size="sm" onClick={() => void loadCors()} disabled={cors.loading()}>
                      <span class="icon"><IconRefresh /></span> Reload
                    </Button>
                    <Button size="sm" onClick={() => void saveCors()} disabled={cors.loading()}>Save CORS</Button>
                  </div>
                  <Show when={cors.fieldError()}>
                    <p class="field-error">{cors.fieldError()}</p>
                  </Show>
                </Show>
              </div>
            ),
          },
          {
            id: 'observability',
            label: 'Observability',
            content: () => (
              <div class="card">
                <span class="eyebrow">Endpoints</span>
                <h3>Observability</h3>
                <p class="hint">Raw operational endpoints (open in a new tab).</p>
                <div class="link-grid">
                  <a class="action secondary" href="/metrics" target="_blank" rel="noopener">
                    <span class="icon"><IconActivity /></span> Prometheus /metrics
                  </a>
                  <a class="action secondary" href="/health" target="_blank" rel="noopener">
                    <span class="icon"><IconActivity /></span> Health
                  </a>
                  <a class="action secondary" href="/health/live" target="_blank" rel="noopener">
                    <span class="icon"><IconActivity /></span> Liveness
                  </a>
                  <a class="action secondary" href="/health/ready" target="_blank" rel="noopener">
                    <span class="icon"><IconCheckCircle /></span> Readiness
                  </a>
                </div>
              </div>
            ),
          },
        ]}
      />
    </section>
  );
}
