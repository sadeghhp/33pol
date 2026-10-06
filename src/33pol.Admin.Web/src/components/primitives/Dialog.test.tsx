/** @vitest-environment jsdom */
import { fireEvent, render, screen } from '@solidjs/testing-library';
import { createSignal } from 'solid-js';
import { describe, expect, it } from 'vitest';
import { Dialog } from './Dialog';

function DialogHarness() {
  const [open, setOpen] = createSignal(true);
  return (
    <div>
      <button type="button" id="outside">Outside</button>
      <Dialog open={open()} title="Test dialog" onClose={() => setOpen(false)}>
        <input id="first-field" />
        <button type="button" id="last">Last</button>
      </Dialog>
    </div>
  );
}

describe('Dialog', () => {
  it('Dialog_Escape_closesAndRestoresFocus', async () => {
    const outside = document.createElement('button');
    outside.id = 'trigger';
    document.body.appendChild(outside);
    outside.focus();

    render(() => <DialogHarness />);
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toBeTruthy();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('Dialog_Tab_trapsFocusInsidePanel', async () => {
    render(() => <DialogHarness />);
    const dialog = await screen.findByRole('dialog');
    const last = document.getElementById('last') as HTMLButtonElement;
    last.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(last);
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(dialog.contains(document.activeElement)).toBe(true);
  });
});
