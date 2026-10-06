const FOCUSABLE =
  'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])';

export function getFocusable(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (el) => !el.hasAttribute('disabled') && el.getAttribute('aria-hidden') !== 'true',
  );
}

export function trapFocus(root: HTMLElement, event: KeyboardEvent): void {
  if (event.key !== 'Tab') return;
  const items = getFocusable(root);
  if (items.length === 0) {
    event.preventDefault();
    return;
  }
  const first = items[0];
  const last = items[items.length - 1];
  const active = document.activeElement as HTMLElement | null;
  if (event.shiftKey) {
    if (active === first || !root.contains(active)) {
      event.preventDefault();
      last.focus();
    }
  } else if (active === last) {
    event.preventDefault();
    first.focus();
  }
}

export function setInertBackground(inert: boolean, except?: HTMLElement): void {
  const shell = document.querySelector('.app-shell');
  if (!shell) return;
  for (const child of Array.from(shell.children)) {
    if (except && child.contains(except)) continue;
    if (inert) child.setAttribute('inert', '');
    else child.removeAttribute('inert');
  }
}
