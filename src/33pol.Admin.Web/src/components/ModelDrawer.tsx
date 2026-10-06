import { Show } from 'solid-js';
import { Button, Drawer, Field, Select } from './primitives';
import type { ModelEditDraft } from '../domain/routingModels';
import {
  useRoutingModelsStore,
  saveModel,
  closeModelDrawer,
} from '../stores/routingModels';

export interface ModelDrawerProps {
  onSaved?: () => void;
}

export function ModelDrawer(props: ModelDrawerProps) {
  const store = useRoutingModelsStore();

  const updateDraft = (patch: Partial<ModelEditDraft>) => {
    store.setDraft({ ...store.draft(), ...patch });
  };

  return (
    <Drawer
      open={store.drawerOpen()}
      title={store.draft()._existing ? 'Edit model' : 'Add model'}
      onClose={() => closeModelDrawer()}
    >
      <Show when={store.fieldError()}>
        <p class="hint error-text">{store.fieldError()}</p>
      </Show>
      <Field label="Model id">
        <input
          type="text"
          value={store.draft().id}
          disabled={store.draft()._existing}
          onInput={(e) => updateDraft({ id: e.currentTarget.value })}
          placeholder="provider/model-name"
        />
      </Field>
      <Field label="Upstream URL">
        <input
          type="url"
          value={store.draft().url}
          onInput={(e) => updateDraft({ url: e.currentTarget.value })}
          placeholder="http://host.docker.internal:8080"
        />
      </Field>
      <Field label="Model type">
        <Select
          value={store.draft().modelType}
          onChange={(v) => updateDraft({ modelType: v })}
          options={store.modelTypes().map((t) => ({ value: t.value, label: t.label }))}
        />
      </Field>
      <Field label="Aliases">
        <input
          type="text"
          value={store.draft().aliasesText}
          onInput={(e) => updateDraft({ aliasesText: e.currentTarget.value })}
          placeholder="alias1, alias2"
        />
      </Field>
      <Field label="Max context">
        <input
          type="number"
          min={1}
          value={String(store.draft().maxContextLength)}
          onInput={(e) => updateDraft({ maxContextLength: Number(e.currentTarget.value) || 8192 })}
        />
      </Field>
      <label class="checkbox-row">
        <input
          type="checkbox"
          checked={store.draft().publicAccess}
          onChange={(e) => updateDraft({ publicAccess: e.currentTarget.checked })}
        />
        Public access (visible in /v1/models)
      </label>
      <Field label="Upstream API key">
        <input
          type="password"
          value={store.draft().apiKey}
          onInput={(e) => updateDraft({ apiKey: e.currentTarget.value })}
          placeholder={store.draft().hasUpstreamCredential ? 'Leave blank to keep existing' : 'Optional'}
        />
      </Field>
      <Show when={store.draft().hasUpstreamCredential}>
        <label class="checkbox-row">
          <input
            type="checkbox"
            checked={store.draft().clearApiKey}
            onChange={(e) => updateDraft({ clearApiKey: e.currentTarget.checked })}
          />
          Remove stored credential
        </label>
      </Show>
      <Field label="Input price / 1M tokens">
        <input
          type="number"
          min={0}
          step="0.01"
          value={String(store.draft().inputPricePerMillion)}
          onInput={(e) => updateDraft({ inputPricePerMillion: e.currentTarget.value })}
        />
      </Field>
      <Field label="Output price / 1M tokens">
        <input
          type="number"
          min={0}
          step="0.01"
          value={String(store.draft().outputPricePerMillion)}
          onInput={(e) => updateDraft({ outputPricePerMillion: e.currentTarget.value })}
        />
      </Field>
      <div class="drawer-actions">
        <Button variant="ghost" onClick={() => closeModelDrawer()}>Cancel</Button>
        <Button
          variant="primary"
          disabled={store.saving()}
          onClick={() => void saveModel(props.onSaved)}
        >
          {store.saving() ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </Drawer>
  );
}
