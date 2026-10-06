import { For, Show, createSignal, onCleanup, onMount } from 'solid-js';
import { useNavigate } from '@solidjs/router';
import { Button, Dialog, Drawer, Field, Select } from '../../components/primitives';
import { IconPlus, IconRefresh } from '../../components/icons';
import { canGrantKey } from '../../domain/keyGrants';
import { formatNum, formatTime } from '../../domain/format';
import type { KeyStatusFilter } from '../../domain/filters';
import { closeDrawer, openKeyAccessDrawer, saveGrants, toggleModel, useKeyGrantsStore } from '../../stores/keyGrants';
import { queueAddRuleIntent } from '../../stores/rateLimits';
import {
  DEFAULT_KEY_EDIT,
  DEFAULT_NEW_KEY,
  activateKeysPage,
  bulkRevoke,
  createKey,
  deleteKey,
  disposeKeysPage,
  revokeKey,
  updateKey,
  useKeysStore,
  type KeyEditDraft,
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

function SortHeader(props: { label: string; sortKey: string; current: string; dir: number; onSort: (k: string) => void }) {
  const active = () => props.current === props.sortKey;
  return (
    <th>
      <button type="button" class="sort-header" classList={{ active: active() }} onClick={() => props.onSort(props.sortKey)}>
        {props.label}{active() ? (props.dir === 1 ? ' ↑' : ' ↓') : ''}
      </button>
    </th>
  );
}

export default function KeysPage() {
  const store = useKeysStore();
  const grants = useKeyGrantsStore();
  const navigate = useNavigate();
  const [createOpen, setCreateOpen] = createSignal(false);
  const [draft, setDraft] = createSignal<NewKeyDraft>({ ...DEFAULT_NEW_KEY });
  const [editDraft, setEditDraft] = createSignal<KeyEditDraft>({ ...DEFAULT_KEY_EDIT });
  const [editOpen, setEditOpen] = createSignal(false);
  const [createdSecret, setCreatedSecret] = createSignal('');
  const [createdAck, setCreatedAck] = createSignal(false);
  const [revokeId, setRevokeId] = createSignal<string | null>(null);
  const [bulkRevokeOpen, setBulkRevokeOpen] = createSignal(false);
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

  const openEdit = (row: Record<string, unknown>) => {
    setEditDraft({
      id: keyId(row),
      keyPrefix: String(row.keyPrefix ?? ''),
      label: String(row.label ?? ''),
      assignee: String(row.assignee ?? ''),
      description: String(row.description ?? ''),
      costCenter: String(row.costCenter ?? ''),
    });
    setEditOpen(true);
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

  const submitEdit = async () => {
    setBusy(true);
    try {
      await updateKey(editDraft());
      setEditOpen(false);
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

  const confirmBulkRevoke = async () => {
    setBusy(true);
    try {
      await bulkRevoke([...store.selectedIds()]);
      setBulkRevokeOpen(false);
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

  const goUsage = (id: string) => navigate(`/usage?key=${encodeURIComponent(id)}`);
  const goRateLimit = (id: string) => {
    queueAddRuleIntent('api_key', id);
    navigate('/settings?sub=ratelimits');
  };

  return (
    <section class="page" id="panel-keys" data-solid-root="keys">
      <header class="page-header">
        <div>
          <p class="eyebrow">Access</p>
          <h1>API keys</h1>
        </div>
        <div class="page-actions">
          <Show when={store.selectedIds().size > 0}>
            <Button variant="danger" size="sm" onClick={() => setBulkRevokeOpen(true)}>
              Revoke selected ({store.selectedIds().size})
            </Button>
          </Show>
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
              <th />
              <SortHeader label="Prefix" sortKey="keyPrefix" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
              <SortHeader label="Role" sortKey="role" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
              <th>Label</th>
              <th>Status</th>
              <SortHeader label="Last used" sortKey="lastUsedAt" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
              <SortHeader label="MTD requests" sortKey="mtdRequests" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
              <SortHeader label="MTD cost" sortKey="mtdCost" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
              <th>Assignee</th>
              <SortHeader label="Created" sortKey="createdAt" current={store.sortSpec().key} dir={store.sortSpec().dir} onSort={store.toggleSort} />
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
                    <td>
                      <Show when={active()}>
                        <input
                          type="checkbox"
                          checked={store.selectedIds().has(id())}
                          onChange={() => store.toggleSelected(id())}
                          aria-label={`Select ${row.keyPrefix}`}
                        />
                      </Show>
                    </td>
                    <td><code>{String(row.keyPrefix ?? '')}</code></td>
                    <td>{String(row.role ?? '—')}</td>
                    <td>{String(row.label ?? '—')}</td>
                    <td>
                      <span class="status-chip">
                        {row.isArchived ? 'archived' : row.isRevoked ? 'revoked' : 'active'}
                      </span>
                    </td>
                    <td>{formatTime(row.lastUsedAt as string | undefined)}</td>
                    <td>{formatNum(row.mtdRequests as number | undefined)}</td>
                    <td>{row.mtdCost != null ? `$${Number(row.mtdCost).toFixed(2)}` : '—'}</td>
                    <td>{String(row.assignee ?? '—')}</td>
                    <td>{formatTime(row.createdAt as string | undefined)}</td>
                    <td class="row-actions">
                      <Show when={active()}>
                        <Button variant="ghost" size="sm" onClick={() => openEdit(row)}>Edit</Button>
                        <Show when={canGrantKey(row)}>
                          <Button variant="ghost" size="sm" onClick={() => void openKeyAccessDrawer(row)}>Models</Button>
                        </Show>
                        <Button variant="ghost" size="sm" onClick={() => goUsage(id())}>Usage</Button>
                        <Button variant="ghost" size="sm" onClick={() => goRateLimit(id())}>Limit</Button>
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

      <Drawer open={editOpen()} title="Edit API key" onClose={() => setEditOpen(false)}>
        <p class="hint">Prefix: <code>{editDraft().keyPrefix}</code></p>
        <Field label="Label">
          <input value={editDraft().label} onInput={(e) => setEditDraft((d) => ({ ...d, label: e.currentTarget.value }))} />
        </Field>
        <Field label="Assignee">
          <input value={editDraft().assignee} onInput={(e) => setEditDraft((d) => ({ ...d, assignee: e.currentTarget.value }))} />
        </Field>
        <Field label="Cost center">
          <input value={editDraft().costCenter} onInput={(e) => setEditDraft((d) => ({ ...d, costCenter: e.currentTarget.value }))} />
        </Field>
        <Field label="Description">
          <textarea rows={2} value={editDraft().description} onInput={(e) => setEditDraft((d) => ({ ...d, description: e.currentTarget.value }))} />
        </Field>
        <Button onClick={submitEdit} disabled={busy()}>Save</Button>
      </Drawer>

      <Drawer open={grants.drawerOpen()} title={`Model access — ${grants.keyLabel()}`} onClose={() => closeDrawer()}>
        <Show when={grants.loading()}><p class="loading-hint">Loading…</p></Show>
        <Show when={!grants.loading()}>
          <For each={grants.registryModels()}>
            {(m) => (
              <label class="checkbox-row">
                <input
                  type="checkbox"
                  checked={grants.selected().includes(m.id)}
                  onChange={() => toggleModel(m.id)}
                />
                {m.label}
              </label>
            )}
          </For>
          <Button onClick={() => void saveGrants()} disabled={grants.saving()}>{grants.saving() ? 'Saving…' : 'Save access'}</Button>
        </Show>
      </Drawer>

      <Dialog open={!!revokeId()} title="Revoke API key?" onClose={() => setRevokeId(null)}>
        <p>This will revoke the key immediately. This cannot be undone.</p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setRevokeId(null)}>Cancel</Button>
          <Button variant="danger" onClick={confirmRevoke} disabled={busy()}>Revoke</Button>
        </div>
      </Dialog>

      <Dialog open={bulkRevokeOpen()} title="Revoke selected keys?" onClose={() => setBulkRevokeOpen(false)}>
        <p>Revoke {store.selectedIds().size} selected key(s)? This cannot be undone.</p>
        <div class="modal-actions">
          <Button variant="ghost" onClick={() => setBulkRevokeOpen(false)}>Cancel</Button>
          <Button variant="danger" onClick={confirmBulkRevoke} disabled={busy()}>Revoke all</Button>
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
