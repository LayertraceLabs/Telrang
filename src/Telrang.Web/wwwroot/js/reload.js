// A page reload ordered by the circuit (DESIGN.md §24.6): returns at once, so the call's answer reaches the server
// before the page unloads, and reloads on the next task.
export function reloadSoon() {
  setTimeout(() => location.reload(), 0);
}
