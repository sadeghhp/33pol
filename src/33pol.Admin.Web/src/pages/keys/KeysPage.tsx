import { For, Show, createSignal, onCleanup, onMount } from 'solid-js';
import { Button, Dialog, Drawer, Field, Select } from '../../components/primitives';
import { IconPlus, IconRefresh } from '../../components/icons';
import { formatTime } from '../../domain/format';
import type { KeyStatusFilter } from '../../domain/filters';
import {
  DEFAULT_NEW_KEY,
  activateKeysPage,
  createKey,
  deleteKey,
  disposeKeysPage,
  revokeKey,
  useKeysStore,
  type NewKeyDraft,
} from '../../stores/keys';

const FILTERS = [
  { value: 'active', label: 'Active' },
  { value: 'revoked', label: 'Revoked' },
  { value: 'archived', label: 'Archived' },
  { value: 'all', label: 'All' },
];

const ROLES = [
  { value: 'Inference', label: 'Inference' },
  { value: 'Admin', label: 'Admin' },
];

export default function KeysPage() {
  const store = useKeysStore();
  const [createOpen, setCreateOpen] = createSignal(false);
  const [draft, setDraft] = createSignal<NewKeyDraft>({ ...DEFAULT_NEW_KEY });
  const [createdSecret, setCreatedSecret] = createSignal('');
  const [createdAck, setCreatedAck] = createSignal(false);
  const [revokeId, setRevokeId] = createSignal<string | null>(null);
  const [deleteKeyRow, setDeleteKeyRow] = createSignal<Record<string, unknown> | null>(null);
  const [deleteConfirmText, setDeleteConfirmText] = createSignal('');
  const [busy, setBusy] = createSignal(false);

  onMount(() => {
    activateKeysPage();
    void store.load();
  });
  onCleanup(() => disposeKeysPage());

  const keyId = (row: Record<string, unknown>) => String(row.id ?? row.keyId ?? '');

  const openCreate = () => {
    setDraft({ ...DEFAULT_NEW_KEY });
    setCreatedSecret('');
    setCreatedAck(false);
    setCreateOpen(true);
  };

  const closeCreate = () => {
    if (createdSecret() && !createdAck()) return;
    setCreateOpen(false);
  };

  const submitCreate = async () => {
    setBusy(true);
    try {
      const secret = await createKey(draft());
      setCreatedSecret(secret);
    } finally {
      setBusy(false);
    }
  };

  const confirmRevoke = async () => {
    const id = revokeId();
    if (!id) return;
    setBusy(true);
    try {
      await revokeKey(id);
      setRevokeId(null);
    } finally {
      setBusy(false);
    }
  };

  const deleteMatches = () => {
    const row = deleteKeyRow();
    if (!row) return false;
    return deleteConfirmText().trim() === String(row.keyPrefix ?? '');
  };

  const confirmDelete = async () => {
    const row = deleteKeyRow();
    if (!row || !deleteMatches()) return;
    setBusy(true);
    try {
      await deleteKey(keyId(row), String(row.keyPrefix ?? ''));
      setDeleteKeyRow(null);
      setDeleteConfirmText('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="page" id="panel-keys" data-solid-root="keys">
      <header class="page-header">
        <div>
          <p class="eyebrow">Access</p>
          <h1>API keys</h1>
        </div>
        <div class="page-actions">
          <Button variant="ghost" size="sm" onClick={() => store.load({ force: true })}>
            <span class="icon"><IconRefresh /></span> Refresh
          </Button>
          <Button size="sm" onClick={openCreate}>
            <span class="icon"><IconPlus /></span> Create key
          </Button>
        </div>
      </header>

      <div class="filter-row">
        <Field label="Status">
          <Select options={FILTERS} value={store.statusFilter()} onChange={(v) => store.setStatusFilter(v as KeyStatusFilter)} />
        </Field>
        <Field label="Filter">
          <input type="search" placeholder="Prefix, label, assignee…" value={store.textFilter()} onInput={(e) => store.setTextFilter(e.currentTarget.value)} />
        </Field>
      </div>

      <Show when={store.totalMatches() > store.capped().length}>
        <p class="field-hint">Showing first {store.capped().length} of {store.totalMatches()} matching keys — narrow your filter to find others.</p>
      </Show>

      <div class="table-wrap">
        <table class="data-table">
          <thead>
            <tr>
              <th>Prefix</th>
              <th>Label</th>
              <th>Status</th>
              <th>Assignee</th>
              <th>Created</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            <For each={store.capped()}>
              {(row) => {
                const id = () => keyId(row);
                const active = () => row.active === true;
                return (
                  <tr>
                    <td><code>{String(row.keyPrefix ?? '')}</code></td>
                    <td>{String(row.label ?? '—')}</td>
                    <td>
                      <span class="status-chip">
                        {row.isArchived ? 'archived' : row.isRevoked ? 'revoked' : 'active'}
                      </span>
                    </td>
                    <td>{String(row.assignee ?? '—')}</td>
                    <td>{formatTime(row.createdAt as string | undefined)}</td>
                    <td class="row-actions">
                      <Show when={active()}>
                        <Button variant="ghost" size="sm" onClick={() => setRevokeId(id())}>Revoke</Button>
                        <Button variant="ghost" size="sm" onClick={() => store.archiveKey(id())}>Archive</Button>
                      </Show>
                      <Show when={row.canArchive === true && !row.isArchived}>
                        <Button variant="ghost" size="sm" onClick={() => store.archiveKey(id())}>Archive</Button>
                      </Show>
                      <Show when={row.canUnarchive === true}>
                        <Button variant="ghost" size="sm" onClick={() => store.unarchiveKey(id())}>Restore</Button>
                      </Show>
                      <Show when={row.canDelete === true}>
                        <Button variant="ghost" size="sm" onClick={() => { setDeleteKeyRow(row); setDeleteConfirmText(''); }}>
                          Delete
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

      <Drawer open={createOpen()} title="Create API key" onClose={closeCreate}>
        <Show when={!createdSecret()} fallback={
          <div>
            <p class="notice warn"><strong>Copy now — this secret is shown only once.</strong></p>
            <code class="secret-display">{createdSecret()}</code>
            <Button onClick={() => navigator.clipboard?.writeText(createdSecret())}>Copy secret</Button>
            <label class="checkbox-label">
              <input type="checkbox" checked={createdAck()} onChange={(e) => setCreatedAck(e.currentTarget.checked)} />
              I have saved this secret
            </label>
          </div>
        }>
          <Field label="Role">
            <Select options={ROLES} value={draft().role} onChange={(v) => setDraft((d) => ({ ...d, role: v }))} />
          </Field>
          <Field label="Label">
            <input value={draft().label} onInput={(e) => setDraft((d) => ({ ...d, label: e.currentTarget.value }))} placeholder="e.g. prod-chatbot" />
          </Field>
          <Field label="Assignee">
            <input value={draft().assignee} onInput={(e) => setDraft((d) => ({ ...d, assignee: e.currentTarget.value }))} placeholder="Person or team name" />
          </Field>
          <Field label="Cost center">
            <input value={draft().costCenter} onInput={(e) => setDraft((d) => ({ ...d, costCenter: e.currentTarget.value }))} placeholder="FinOps grouping (optional)" />
          </Field>
          <Field label="Description">
            <textarea rows={2} value={draft().description} onInput={(e) => setDraft((d) => ({ ...d, description: e.currentTarget.value }))} placeholder="Optional notes" />
          </Field>
          <Button onClick={submitCreate} disabled={busy()}>Create key</Button>
        </Show>
      </Drawer>

      <Dialog open={!!revokeId()} title="Revoke API key?" onClose={() => setRevokeId(null)}>
        <p>This will revoke the key immediately. This cannot be undone.</p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setRevokeId(null)}>Cancel</Button>
          <Button variant="danger" onClick={confirmRevoke} disabled={busy()}>Revoke</Button>
        </div>
      </Dialog>

      <Dialog open={!!deleteKeyRow()} title="Delete API key permanently?" onClose={() => { setDeleteKeyRow(null); setDeleteConfirmText(''); }}>
        <p>Type the key prefix <code>{String(deleteKeyRow()?.keyPrefix ?? '')}</code> to confirm permanent deletion.</p>
        <Field label="Prefix confirmation">
          <input value={deleteConfirmText()} onInput={(e) => setDeleteConfirmText(e.currentTarget.value)} />
        </Field>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => { setDeleteKeyRow(null); setDeleteConfirmText(''); }}>Cancel</Button>
          <Button variant="danger" onClick={confirmDelete} disabled={!deleteMatches() || busy()}>Delete permanently</Button>
        </div>
      </Dialog>
    </section>
  );
}
