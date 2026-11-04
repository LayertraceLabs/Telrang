// Theme stylesheet swap (DESIGN.md §17): the admin page switches to the variant without custom CSS and back, and the
// live preview after a save re-links the new version. One <link data-theme-css> element whose href is replaced:
// idempotent, so quick enter/leave sequences cannot leave a second, stale sheet behind. Module (no inline script,
// no inline style) because of the CSP.
// defaultMode is the admin's default mode ("light", "dark" or null for system); the visitor's own choice
// (js/theme-switch.js, DESIGN.md §24.4) still wins.
import { sync } from './theme-switch.js';

export function apply(href, defaultMode) {
  const link = document.querySelector('link[data-theme-css]');
  if (link && link.getAttribute('href') !== href) link.setAttribute('href', href);
  document.documentElement.dataset.avDefault = defaultMode || 'system';
  sync();
}
