/** Legacy Alpine hashes used `#tab?params`; Solid HashRouter uses `#/tab?params`. */
export function migrateLegacyHash(): void {
  const hash = location.hash;
  if (!hash || hash.startsWith('#/')) return;
  const match = /^#([a-z][a-z0-9-]*)(\?.*)?$/i.exec(hash);
  if (!match) return;
  location.replace(`#/${match[1]}${match[2] ?? ''}`);
}
