import type { JSX } from 'solid-js';
import { For, Show, createSignal, splitProps } from 'solid-js';

export interface TabItem {
  id: string;
  label: string;
  icon?: JSX.Element;
  content: () => JSX.Element;
}

export interface TabsProps {
  tabs: TabItem[];
  defaultId?: string;
  class?: string;
  onChange?: (id: string) => void;
}

export function Tabs(props: TabsProps) {
  const [active, setActive] = createSignal(props.defaultId ?? props.tabs[0]?.id ?? '');
  const select = (id: string) => {
    setActive(id);
    props.onChange?.(id);
  };
  return (
    <div class={props.class}>
      <div class="tab-bar" role="tablist">
        <For each={props.tabs}>
          {(tab) => (
            <button
              type="button"
              role="tab"
              id={`tab-${tab.id}`}
              aria-selected={active() === tab.id}
              class={active() === tab.id ? 'tab active' : 'tab'}
              onClick={() => select(tab.id)}
            >
              {tab.icon}
              <span>{tab.label}</span>
            </button>
          )}
        </For>
      </div>
      <For each={props.tabs}>
        {(tab) => (
          <Show when={active() === tab.id}>
            <div role="tabpanel" aria-labelledby={`tab-${tab.id}`} id={`panel-${tab.id}`}>
              {tab.content()}
            </div>
          </Show>
        )}
      </For>
    </div>
  );
}
