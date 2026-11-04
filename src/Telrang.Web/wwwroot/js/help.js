// Option help (DESIGN.md §22.2, §23.4): remembers the "Show help" / "Hide help" choice per browser and per area
// ("admin", "editor"). Storage can be missing or throw (private windows, blocked site data); the page then starts folded
// and still works. The admin key keeps its name from before the editor had help.
function key(area) {
  return `av:${String(area).replace(/[^a-z]/g, '')}-help`;
}

export function load(area) {
  try {
    return window.localStorage.getItem(key(area)) === 'open';
  } catch {
    return false;
  }
}

export function save(area, open) {
  try {
    if (open) window.localStorage.setItem(key(area), 'open');
    else window.localStorage.removeItem(key(area));
  } catch {
    // Not remembered; nothing else depends on it.
  }
}
