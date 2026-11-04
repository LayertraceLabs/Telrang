// Drag selection in the poll editor's dates calendar (PollEditor.razor, DESIGN.md §27). Every day is a button with
// data-day="yyyy-MM-dd" and aria-pressed; a click on one toggles it in .NET (that works without this module). This
// module adds ranges: the days from the first to the current one (date order, which is reading order in one month)
// take the state the first day switches to. The preview is drawn here; only the finished range goes to the circuit
// (SelectDays(from, to, select)).
//  - Mouse: the drag starts on press. A press and release on the same day is a plain click (Blazor toggles it).
//  - Touch and pen: a tap toggles (the click); a press held 200 ms without moving starts the drag (as the ranked
//    ballot's drag) and the page stops scrolling; moving earlier scrolls the page as usual.
//  - Keyboard: arrows move between the days of the month, Space toggles (the button's click), Shift+arrows extend the
//    state of the day where Shift went down over the days passed; releasing Shift applies it.
const HOLD_MS = 200;
const SLOP_PX = 6;

const dayOf = el => el?.closest?.('button[data-day]') ?? null;
const isOn = b => b.getAttribute('aria-pressed') === 'true';

function addDays(iso, n) {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(0);
  t.setUTCFullYear(y, m - 1, d + n); // setUTCFullYear keeps years 0001–0099 as written
  const yy = String(t.getUTCFullYear()).padStart(4, '0');
  return `${yy}-${String(t.getUTCMonth() + 1).padStart(2, '0')}-${String(t.getUTCDate()).padStart(2, '0')}`;
}

export function attach(root, dotnet) {
  if (!root || root.dataset.dateSelect === 'on') return;
  root.dataset.dateSelect = 'on';

  let drag = null;     // { start, current, select, pointerId }
  let pending = null;  // touch / pen press waiting for the hold: { button, x, y, pointerId, timer }
  let anchor = null;   // keyboard range: { start, current, select }
  let swallowUntil = 0; // a pointer click right after a range drag does not toggle the day it ended on

  const days = () => [...root.querySelectorAll('button[data-day]')];
  const byDay = iso => root.querySelector(`button[data-day="${iso}"]`);

  function paint(from, to, select) {
    const [a, b] = from <= to ? [from, to] : [to, from];
    for (const button of days()) {
      const inside = button.dataset.day >= a && button.dataset.day <= b;
      button.classList.toggle('drag-on', inside && select);
      button.classList.toggle('drag-off', inside && !select);
    }
  }

  function clearPaint() {
    for (const button of root.querySelectorAll('.drag-on, .drag-off')) button.classList.remove('drag-on', 'drag-off');
  }

  function send(from, to, select) {
    const [a, b] = from <= to ? [from, to] : [to, from];
    dotnet.invokeMethodAsync('SelectDays', a, b, select).catch(() => { /* circuit gone: the page shows its reconnect UI */ });
  }

  // Move / up / cancel are followed on the window only while a press is in progress.
  function listen(on) {
    const method = on ? 'addEventListener' : 'removeEventListener';
    window[method]('pointermove', onMove);
    window[method]('pointerup', onUp);
    window[method]('pointercancel', onCancel);
  }

  function begin(button, pointerId) {
    drag = { start: button.dataset.day, current: button.dataset.day, select: !isOn(button), pointerId };
    root.classList.add('dragging');
    paint(drag.start, drag.current, drag.select);
  }

  function end(commit) {
    if (pending) clearTimeout(pending.timer);
    pending = null;
    listen(false);
    if (!drag) return;
    const { start, current, select } = drag;
    drag = null;
    root.classList.remove('dragging');
    clearPaint();
    // Across days: the range goes to .NET and no click toggles the last day. On one day the click (if any) toggles it.
    if (commit && start !== current) { swallowUntil = performance.now() + 500; send(start, current, select); }
  }

  function onDown(e) {
    const button = dayOf(e.target);
    if (!button || e.button !== 0 || drag || pending) return;
    swallowUntil = 0;
    listen(true);
    if (e.pointerType === 'mouse') { begin(button, e.pointerId); return; }
    pending = {
      button, x: e.clientX, y: e.clientY, pointerId: e.pointerId,
      timer: setTimeout(() => { const p = pending; pending = null; if (p) begin(p.button, p.pointerId); }, HOLD_MS),
    };
  }

  function onMove(e) {
    if (pending && e.pointerId === pending.pointerId) {
      if (Math.hypot(e.clientX - pending.x, e.clientY - pending.y) > SLOP_PX) end(false); // a scroll
      return;
    }
    if (!drag || e.pointerId !== drag.pointerId) return;
    // Touch pointers stay captured by the first button: find the day under the finger.
    const button = dayOf(document.elementFromPoint(e.clientX, e.clientY));
    if (!button || !root.contains(button) || button.dataset.day === drag.current) return;
    drag.current = button.dataset.day;
    paint(drag.start, drag.current, drag.select);
  }

  function onUp(e) {
    if ((pending && e.pointerId === pending.pointerId) || (drag && e.pointerId === drag.pointerId)) end(true);
  }

  function onCancel(e) {
    if ((pending && e.pointerId === pending.pointerId) || (drag && e.pointerId === drag.pointerId)) end(false);
  }

  // While a touch drag runs the page must not scroll (touch-action cannot change mid-gesture).
  function onTouchMove(e) { if (drag) e.preventDefault(); }
  function onContextMenu(e) { if (drag || pending) e.preventDefault(); }

  // After a drag across days no pointer click toggles the day it ended on (keyboard clicks, detail 0, always count).
  function onClick(e) {
    if (e.detail === 0 || performance.now() > swallowUntil) return;
    swallowUntil = 0;
    if (dayOf(e.target)) { e.preventDefault(); e.stopPropagation(); }
  }

  function onKeyDown(e) {
    const button = dayOf(e.target);
    if (!button) return;
    const step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[e.key];
    if (step === undefined) return;
    e.preventDefault();
    const next = byDay(addDays(button.dataset.day, step));
    if (e.shiftKey) {
      anchor ??= { start: button.dataset.day, current: button.dataset.day, select: isOn(button) };
      if (next) anchor.current = next.dataset.day;
      paint(anchor.start, anchor.current, anchor.select);
    } else {
      applyAnchor();
    }
    next?.focus();
  }

  function applyAnchor() {
    if (!anchor) return;
    const { start, current, select } = anchor;
    anchor = null;
    clearPaint();
    if (current !== start) send(start, current, select);
  }

  function onKeyUp(e) { if (e.key === 'Shift') applyAnchor(); }
  function onFocusOut(e) { if (anchor && !root.contains(e.relatedTarget)) applyAnchor(); }

  root.addEventListener('pointerdown', onDown);
  root.addEventListener('touchmove', onTouchMove, { passive: false });
  root.addEventListener('contextmenu', onContextMenu);
  root.addEventListener('click', onClick, true);
  root.addEventListener('keydown', onKeyDown);
  root.addEventListener('keyup', onKeyUp);
  root.addEventListener('focusout', onFocusOut);
  root.avDateSelectDetach = () => { end(false); delete root.dataset.dateSelect; };
}

export function detach(root) {
  root?.avDateSelectDetach?.();
}
