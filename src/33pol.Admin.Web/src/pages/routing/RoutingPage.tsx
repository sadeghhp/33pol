import { For, Show, createSignal, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { Button, Dialog, Field, Tabs } from '../../components/primitives';
import { IconPause, IconPlay, IconRefresh } from '../../components/icons';
import {
  activateRoutingPage,
  disposeRoutingPage,
  isModelStopped,
  setModelState,
  useRoutingStore,
} from '../../stores/routing';

export default function RoutingPage() {
  const store = useRoutingStore();
  const [params] = useSearchParams();
  const [confirmAction, setConfirmAction] = createSignal<{ id: string; action: 'stop' | 'start' } | null>(null);
  const [busy, setBusy] = createSignal(false);

  onMount(() => {
    activateRoutingPage();
    if (params.sub === 'backends') store.setSubTab('backends');
    else if (params.sub === 'models') store.setSubTab('models');
    if (store.subTab() === 'models') void store.loadModels();
    else void store.loadBackends();
  });
  onCleanup(() => disposeRoutingPage());

  const runStateChange = async () => {
    const action = confirmAction();
    if (!action) return;
    setBusy(true);
    try {
      await setModelState(action.id, action.action);
      setConfirmAction(null);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="page" id="panel-routing">
      <header class="page-header">
        <div>
          <p class="eyebrow">Traffic</p>
          <h1>Routing</h1>
        </div>
      </header>

      <Tabs
        defaultId={store.subTab()}
        onChange={(id) => {
          store.setSubTab(id as 'models' | 'backends');
          if (id === 'models') void store.loadModels();
          else void store.loadBackends();
        }}
        tabs={[
          {
            id: 'models',
            label: 'Models',
            content: () => (
              <>
                <div class="filter-row">
                  <Field label="Filter">
                    <input type="search" value={store.modelsFilter()} onInput={(e) => store.setModelsFilter(e.currentTarget.value)} placeholder="Model id, url, alias" />
                  </Field>
                  <Button variant="ghost" size="sm" onClick={() => store.loadModels({ force: true })}>
                    <span class="icon"><IconRefresh /></span>
                  </Button>
                </div>
                <Show when={store.modelsPhase() === 'loading'}><p class="loading-hint">Loading models…</p></Show>
                <div class="table-wrap">
                  <table class="data-table">
                    <thead><tr><th>Model</th><th>URL</th><th>Aliases</th><th>State</th><th /></tr></thead>
                    <tbody>
                      <For each={store.filteredModels()}>
                        {(m) => {
                          const id = String(m.id ?? '');
                          const stopped = isModelStopped(m);
                          return (
                            <tr>
                              <td><code>{id}</code></td>
                              <td class="truncate">{String(m.url ?? '—')}</td>
                              <td>{Array.isArray(m.aliases) ? m.aliases.join(', ') : '—'}</td>
                              <td><span class="status-chip" classList={{ fail: stopped, ok: !stopped }}>{stopped ? 'Stopped' : 'Serving'}</span></td>
                              <td class="row-actions">
                                <Show when={!stopped}>
                                  <Button variant="ghost" size="sm" onClick={() => setConfirmAction({ id, action: 'stop' })} title="Stop model">
                                    <span class="icon"><IconPause /></span>
                                  </Button>
                                </Show>
                                <Show when={stopped}>
                                  <Button variant="ghost" size="sm" onClick={() => setConfirmAction({ id, action: 'start' })} title="Start model">
                                    <span class="icon"><IconPlay /></span>
                                  </Button>
                                </Show>
                              </td>
                            </tr>
                          );
                        }}
                      </For>
                    </tbody>
                  </table>
                </div>
              </>
            ),
          },
          {
            id: 'backends',
            label: 'Backends',
            content: () => (
              <>
                <div class="filter-row">
                  <Field label="Filter">
                    <input type="search" value={store.backendsFilter()} onInput={(e) => store.setBackendsFilter(e.currentTarget.value)} placeholder="Model, url, alias" />
                  </Field>
                  <Button variant="ghost" size="sm" onClick={() => store.loadBackends({ force: true })}>
                    <span class="icon"><IconRefresh /></span>
                  </Button>
                </div>
                <Show when={store.backendsPhase() === 'loading'}><p class="loading-hint">Loading backends…</p></Show>
                <div class="table-wrap">
                  <table class="data-table">
                    <thead><tr><th>Model</th><th>URL</th><th>Healthy</th></tr></thead>
                    <tbody>
                      <For each={store.filteredBackends()}>
                        {(b) => {
                          const stopped = String(b.state ?? 'serving').toLowerCase() === 'stopped';
                          return (
                            <tr>
                              <td><code>{String(b.modelId ?? '')}</code></td>
                              <td class="truncate">{String(b.url ?? '—')}</td>
                              <td>{stopped ? 'Stopped' : b.isHealthy ? 'Yes' : 'No'}</td>
                            </tr>
                          );
                        }}
                      </For>
                    </tbody>
                  </table>
                </div>
              </>
            ),
          },
        ]}
      />

      <Dialog
        open={!!confirmAction()}
        title={confirmAction()?.action === 'stop' ? 'Stop model?' : 'Start model?'}
        onClose={() => setConfirmAction(null)}
      >
        <Show when={confirmAction()?.action === 'stop'}>
          <p>
            “{confirmAction()?.id}” stops serving: it disappears from /v1/models and requests for it are rejected.
            Its aliases, credential, pricing and grants are kept, so you can start it again.
          </p>
        </Show>
        <Show when={confirmAction()?.action === 'start'}>
          <p>Start “{confirmAction()?.id}” and resume serving inference traffic.</p>
        </Show>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setConfirmAction(null)}>Cancel</Button>
          <Button variant={confirmAction()?.action === 'stop' ? 'danger' : 'primary'} onClick={runStateChange} disabled={busy()}>
            {confirmAction()?.action === 'stop' ? 'Stop' : 'Start'}
          </Button>
        </div>
      </Dialog>
    </section>
  );
}
