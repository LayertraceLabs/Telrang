// Visitor light / dark / system switch (DESIGN.md §24.4). The choice lives in this browser only: the av-theme cookie
// (light | dark, none for System) so the server renders data-bs-theme and a reload never flashes, mirrored in
// localStorage. <html> carries data-bs-theme (what the CSS follows), data-av-theme (the choice, for the switch's icon)
// and data-av-default (the admin's default mode, used for System). Listeners sit on the document, so switches rendered
// later (enhanced navigation, interactive pages) work too. Module, no inline script (CSP).
const COOKIE = 'av-theme';
const CHOICES = ['system', 'light', 'dark'];
const LABELS = { system: 'System', light: 'Light', dark: 'Dark' };
const root = document.documentElement;

function cookieChoice() {
  const m = document.cookie.match(/(?:^|;\s*)av-theme=(light|dark)(?:;|$)/);
  return m ? m[1] : null;
}

function storedChoice() {
  try {
    const v = localStorage.getItem(COOKIE);
    return v === 'light' || v === 'dark' ? v : null;
  } catch {
    return null;
  }
}

export function choice() {
  return cookieChoice() ?? 'system';
}

function writeCookie(value) {
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = value === 'system'
    ? `${COOKIE}=; Path=/; Max-Age=0; SameSite=Lax${secure}`
    : `${COOKIE}=${value}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
  try {
    if (value === 'system') localStorage.removeItem(COOKIE);
    else localStorage.setItem(COOKIE, value);
  } catch { /* storage blocked: the cookie alone carries the choice */ }
}

/** data-bs-theme for a choice: the choice itself, or for System the admin's default (none = follow the OS). */
function mode(value) {
  if (value !== 'system') return value;
  const d = root.dataset.avDefault;
  return d === 'light' || d === 'dark' ? d : null;
}

/** Puts the choice on <html> and on every switch (pressed state, toggle name). */
export function sync() {
  const value = choice();
  const m = mode(value);
  if (m) {
    if (root.getAttribute('data-bs-theme') !== m) root.setAttribute('data-bs-theme', m);
  } else if (root.hasAttribute('data-bs-theme')) {
    root.removeAttribute('data-bs-theme');
  }
  if (value === 'system') delete root.dataset.avTheme;
  else if (root.dataset.avTheme !== value) root.dataset.avTheme = value;
  if (!root.hasAttribute('data-av-switch')) root.setAttribute('data-av-switch', '');
  for (const button of document.querySelectorAll('[data-theme-choice]')) {
    const pressed = button.dataset.themeChoice === value ? 'true' : 'false';
    if (button.getAttribute('aria-pressed') !== pressed) button.setAttribute('aria-pressed', pressed);
  }
  for (const toggle of document.querySelectorAll('[data-theme-toggle]')) {
    const name = `Theme: ${toggle.dataset.systemLabel && value === 'system' ? toggle.dataset.systemLabel : LABELS[value]}`;
    if (toggle.getAttribute('aria-label') !== name) toggle.setAttribute('aria-label', name);
  }
}

function choose(value) {
  if (!CHOICES.includes(value)) return;
  writeCookie(value);
  sync();
}

document.addEventListener('click', e => {
  const button = e.target.closest?.('[data-theme-choice]');
  if (!button) return;
  choose(button.dataset.themeChoice);
  const menu = button.closest('details[data-menu]');
  if (menu) {
    menu.open = false;
    menu.querySelector('summary')?.focus();
  }
});

// Interactive pages render the switch again (the circuit replaces the prerendered markup, a layout change creates new
// buttons) with aria-pressed="false": sync once per frame after markup was added. sync() only writes attributes that
// differ, and attribute writes do not trigger this observer (childList only).
let queued = false;
new MutationObserver(records => {
  if (queued || !records.some(r => r.addedNodes.length)) return;
  queued = true;
  requestAnimationFrame(() => { queued = false; sync(); });
}).observe(document.body, { childList: true, subtree: true });

// Cookie gone (cleared, expired) but the choice still in localStorage: restore it.
if (!cookieChoice() && storedChoice()) writeCookie(storedChoice());
sync();

// Enhanced navigation replaces the page's markup (and may copy <html>'s attributes from the server's render).
window.Blazor?.addEventListener?.('enhancedload', sync);
