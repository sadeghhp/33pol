import { For, Show, createSignal, onCleanup, onMount } from 'solid-js';
import { useSearchParams } from '@solidjs/router';
import { ModelDrawer } from '../../components/ModelDrawer';
import { Button, Dialog, Field, Tabs } from '../../components/primitives';
import { IconBug, IconPause, IconPlay, IconPlus, IconRefresh, IconX } from '../../components/icons';
import { formatModelPrice, normalizeModelItem } from '../../domain/routingModels';
import {
  activateRoutingPage,
  disposeRoutingPage,
  isModelStopped,
  setModelState,
  useRoutingStore,
} from '../../stores/routing';
import {
  closeTestDialog,
  openEditModelDrawer,
  openNewModelDrawer,
  removeModel,
  rerunModelTest,
  testModel,
  useRoutingModelsStore,
} from '../../stores/routingModels';

export default function RoutingPage() {
  const store = useRoutingStore();
  const models = useRoutingModelsStore();
  const [params] = useSearchParams();
  const [confirmAction, setConfirmAction] = createSignal<{ id: string; action: 'stop' | 'start' } | null>(null);
  const [removeTarget, setRemoveTarget] = createSignal<string | null>(null);
  const [busy, setBusy] = createSignal(false);

  const refreshModels = () => void store.loadModels({ force: true });

  const subFromRoute = () => {
    if (params.sub === 'backends') return 'backends' as const;
    if (params.sub === 'models') return 'models' as const;
    return store.subTab();
  };

  onMount(() => {
    activateRoutingPage();
    const sub = subFromRoute();
    store.setSubTab(sub);
    if (sub === 'models') void store.loadModels({ force: true });
    else void store.loadBackends({ force: true });
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

  const runRemove = async () => {
    const id = removeTarget();
    if (!id) return;
    setBusy(true);
    try {
      const ok = await removeModel(id, refreshModels);
      if (ok) setRemoveTarget(null);
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
        <Show when={store.subTab() === 'models'}>
          <Button variant="primary" onClick={() => openNewModelDrawer()}>
            <span class="icon"><IconPlus /></span>
            Add model
          </Button>
        </Show>
      </header>

      <Tabs
        defaultId={subFromRoute()}
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
                  <Button variant="ghost" size="sm" onClick={refreshModels}>
                    <span class="icon"><IconRefresh /></span>
                  </Button>
                </div>
                <Show when={store.modelsPhase() === 'loading'}><p class="loading-hint">Loading models…</p></Show>
                <div class="table-wrap">
                  <table class="data-table">
                    <thead>
                      <tr>
                        <th>Model</th>
                        <th>Type</th>
                        <th>URL</th>
                        <th>Price</th>
                        <th>Access</th>
                        <th>State</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      <For each={store.filteredModels()}>
                        {(m) => {
                          const row = normalizeModelItem(m);
                          const id = row.id;
                          const stopped = isModelStopped(m);
                          return (
                            <tr>
                              <td>
                                <code>{id}</code>
                                <Show when={row.aliases.length}>
                                  <small class="hint">{row.aliases.join(', ')}</small>
                                </Show>
                              </td>
                              <td>{row.modelType ?? '—'}</td>
                              <td class="truncate">{row.url || '—'}</td>
                              <td>{formatModelPrice(row.pricing)}</td>
                              <td>{row.publicAccess ? 'Public' : 'Private'}{row.hasUpstreamCredential ? ' · cred' : ''}</td>
                              <td><span class="status-chip" classList={{ fail: stopped, ok: !stopped }}>{stopped ? 'Stopped' : 'Serving'}</span></td>
                              <td class="row-actions">
                                <Button variant="ghost" size="sm" title="Edit" onClick={() => openEditModelDrawer(m)}>Edit</Button>
                                <Button variant="ghost" size="sm" title="Test" onClick={() => void testModel(id)}>
                                  <span class="icon"><IconBug /></span>
                                </Button>
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
                                <Button variant="ghost" size="sm" title="Remove" onClick={() => setRemoveTarget(id)}>
                                  <span class="icon"><IconX /></span>
                                </Button>
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
                    <thead><tr><th>Model</th><th>URL</th><th>Health</th><th>Probe</th><th /></tr></thead>
                    <tbody>
                      <For each={store.filteredBackends()}>
                        {(b) => {
                          const stopped = String(b.state ?? 'serving').toLowerCase() === 'stopped';
                          const modelId = String(b.modelId ?? '');
                          const healthy = !!b.isHealthy;
                          return (
                            <tr>
                              <td><code>{modelId}</code></td>
                              <td class="truncate">{String(b.url ?? '—')}</td>
                              <td>
                                <span class="status-chip" classList={{ ok: healthy && !stopped, fail: !healthy && !stopped, warn: stopped }}>
                                  {stopped ? 'Stopped' : healthy ? 'Healthy' : 'Unhealthy'}
                                </span>
                              </td>
                              <td>{b.lastProbeAt ? String(b.lastProbeAt) : '—'}</td>
                              <td class="row-actions">
                                <Button variant="ghost" size="sm" onClick={() => {
                                  store.setSubTab('models');
                                  const match = store.filteredModels().find((m) => String(m.id ?? (m.model as { id?: string })?.id) === modelId);
                                  if (match) openEditModelDrawer(match);
                                }}>Edit model</Button>
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
        ]}
      />

      <ModelDrawer onSaved={refreshModels} />

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

      <Dialog open={!!removeTarget()} title="Remove model?" onClose={() => setRemoveTarget(null)}>
        <p>Remove “{removeTarget()}” from the registry? This cannot be undone.</p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setRemoveTarget(null)}>Cancel</Button>
          <Button variant="danger" onClick={runRemove} disabled={busy()}>Remove</Button>
        </div>
      </Dialog>

      <Dialog
        open={!!models.testDialog()}
        title="Model test"
        onClose={() => closeTestDialog()}
      >
        <Show when={models.testDialog()?.loading}>
          <p class="loading-hint">Testing model…</p>
        </Show>
        <Show when={!models.testDialog()?.loading && models.testDialog()?.error}>
          <p class="hint error-text">{models.testDialog()?.error}</p>
        </Show>
        <Show when={models.testDialog()?.result}>
          {(result) => (
            <div>
              <Show when={result().supported === false}>
                <p>No automated health check exists for this model type.</p>
              </Show>
              <Show when={result().supported !== false}>
                <p class={result().ok ? 'hint' : 'hint error-text'}>
                  {result().ok ? 'Test succeeded.' : result().detail || 'Test failed.'}
                </p>
                <Show when={result().endpoint}><p>Endpoint: {result().endpoint}</p></Show>
                <Show when={result().latencyMs != null}><p>Latency: {result().latencyMs} ms</p></Show>
                <Show when={result().statusCode != null}><p>Status: {result().statusCode}</p></Show>
                <Show when={result().hint}><p class="hint">{result().hint}</p></Show>
              </Show>
            </div>
          )}
        </Show>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => closeTestDialog()}>Close</Button>
          <Button variant="primary" onClick={() => rerunModelTest()} disabled={models.testDialog()?.loading}>
            Rerun
          </Button>
        </div>
      </Dialog>
    </section>
  );
}
