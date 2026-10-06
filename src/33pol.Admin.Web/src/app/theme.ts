const STORAGE_KEY = '33pol-admin-theme';

export type ThemeMode = 'light' | 'dark' | 'system';

export function getStoredTheme(): ThemeMode {
  const v = localStorage.getItem(STORAGE_KEY);
  if (v === 'light' || v === 'dark' || v === 'system') return v;
  return 'system';
}

export function applyTheme(mode: ThemeMode): void {
  localStorage.setItem(STORAGE_KEY, mode);
  const root = document.documentElement;
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  const dark = mode === 'dark' || (mode === 'system' && prefersDark);
  root.dataset.theme = dark ? 'dark' : 'light';
}

export function initTheme(): void {
  applyTheme(getStoredTheme());
}
