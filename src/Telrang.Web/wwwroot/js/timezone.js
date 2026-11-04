// Pre-selects the browser's IANA time zone in <select data-default-tz> elements that have no value
// yet. Loaded as a module from App.razor (no inline script: CSP); re-runs after enhanced navigation.
function fill() {
  let tz;
  try { tz = Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return; }
  for (const select of document.querySelectorAll('select[data-default-tz]')) {
    if (!select.value && [...select.options].some(o => o.value === tz)) select.value = tz;
  }
}

fill();
window.Blazor?.addEventListener?.('enhancedload', fill);

/** The browser's IANA time zone, or null (DESIGN.md §23.5: a new poll's default when the account has none). */
export function browserZone() {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch { return null; }
}
