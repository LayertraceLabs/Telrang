// Ranked ballot (RankedVote.razor, DESIGN.md §21.1). The form's fields are a position per option (o.{key}) and a Ban
// checkbox (b.{key}); without JS the voter types the positions. This module hides those fields behind the drag zones
// and buttons the server rendered (hidden) from the same values, and writes the fields after every move, so what is
// sent is always exactly what the zones show. Moves by button keep focus on the moved option and are announced in the
// aria-live region. The server validates every ballot again. Fields are found by data-field and the tally inside the
// form or the home page example ([data-ballot]), which renders the same fields without names and no form (§23.2).
// SortableJS 1.15.7, vendored ESM build (MIT, see ../lib/sortablejs/LICENSE).
import Sortable from '../lib/sortablejs/sortable.esm.js';

export function init(root) {
  if (root.dataset.ready) return;
  root.dataset.ready = '1';

  const fields = root.querySelector('[data-rank-fields]');
  const board = root.querySelector('[data-rank-board]');
  const live = root.querySelector('[data-announce]');
  const scope = root.closest('form, [data-ballot]');
  const tally = scope?.querySelector('[data-tally]');
  const maxBans = Number(root.dataset.maxBans) || 0;
  const zone = name => board.querySelector(`[data-zone="${name}"]`);
  const ranked = zone('ranked'), pool = zone('pool'), banned = zone('banned');
  const items = list => list ? [...list.querySelectorAll(':scope > [data-key]')] : [];
  const field = (prefix, key) => fields.querySelector(`[data-field="${prefix}${CSS.escape(key)}"]`);
  const label = item => item.dataset.label;

  const announce = message => {
    // Clear first so the same text twice is still read out.
    live.textContent = '';
    setTimeout(() => { live.textContent = message; }, 30);
  };

  // Writes the form fields and the visible state (numbers, counts, hints, button states) from the zones.
  function write() {
    const r = items(ranked), b = items(banned), p = items(pool);
    r.forEach((item, i) => {
      field('o.', item.dataset.key).value = String(i + 1);
      const ban = field('b.', item.dataset.key);
      if (ban) ban.checked = false;
      const pos = item.querySelector('[data-pos]');
      pos.textContent = String(i + 1);
      pos.setAttribute('aria-label', `Position ${i + 1}`);
      pos.classList.toggle('rank-pos-first', i === 0);
    });
    for (const item of [...p, ...b]) {
      field('o.', item.dataset.key).value = '';
      const ban = field('b.', item.dataset.key);
      if (ban) ban.checked = b.includes(item);
      const pos = item.querySelector('[data-pos]');
      pos.textContent = '';
      pos.removeAttribute('aria-label');
    }
    const full = b.length >= maxBans;
    for (const item of [...r, ...p, ...b]) {
      const i = r.indexOf(item);
      setDisabled(item, 'up', i <= 0);
      setDisabled(item, 'down', i < 0 || i === r.length - 1);
      setDisabled(item, 'ban', full);
    }
    count('ranked', r.length);
    count('pool', p.length);
    count('banned', b.length);
    if (tally) tally.textContent = `${r.length} ranked` + (maxBans > 0 ? ` · ${b.length}/${maxBans} bans` : '');
  }

  function setDisabled(item, action, disabled) {
    const button = item.querySelector(`[data-action="${action}"]`);
    if (button) button.disabled = disabled;
  }

  function count(name, n) {
    const badge = board.querySelector(`[data-count="${name}"]`);
    if (badge) badge.textContent = String(n);
    const hint = board.querySelector(`[data-empty="${name}"]`);
    if (hint) hint.hidden = n > 0;
  }

  // Back into the unranked list at its place in the poll's option order.
  function toPool(item) {
    const index = Number(item.dataset.index);
    const after = items(pool).find(other => Number(other.dataset.index) > index);
    pool.insertBefore(item, after ?? null);
  }

  // After a move, keep focus on the moved option (preferring the given action's button, else any usable one).
  function focus(item, action) {
    const usable = b => !b.disabled && b.offsetParent !== null;
    const buttons = [...item.querySelectorAll('button[data-action]')];
    (buttons.find(b => b.dataset.action === action && usable(b)) ?? buttons.find(usable))?.focus();
  }

  function act(item, action) {
    const name = label(item);
    const r = items(ranked);
    switch (action) {
      case 'up':
      case 'down': {
        const i = r.indexOf(item);
        const j = action === 'up' ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= r.length) return;
        ranked.insertBefore(item, action === 'up' ? r[j] : r[j].nextSibling);
        write();
        announce(`${name} moved to position ${j + 1} of ${r.length}.`);
        focus(item, action);
        return;
      }
      case 'rank':
        ranked.appendChild(item);
        write();
        announce(`${name} ranked at position ${items(ranked).length}.`);
        focus(item, 'up');
        return;
      case 'unrank':
        toPool(item);
        write();
        announce(`${name} removed from your ranking.`);
        focus(item, 'rank');
        return;
      case 'ban':
        if (items(banned).length >= maxBans) { announce('All bans used.'); return; }
        banned.appendChild(item);
        write();
        announce(`${name} banned. ${items(banned).length} of ${maxBans} bans used.`);
        focus(item, 'unban');
        return;
      case 'unban':
        toPool(item);
        write();
        announce(`${name} is no longer banned.`);
        focus(item, 'rank');
        return;
    }
  }

  board.addEventListener('click', e => {
    const button = e.target.closest('button[data-action]');
    const item = button?.closest('[data-key]');
    if (!button || !item || button.disabled) return;
    act(item, button.dataset.action);
  });

  const group = 'ballot-' + Math.random().toString(36).slice(2);
  [ranked, pool, banned].filter(Boolean).forEach(list => Sortable.create(list, {
    group: {
      name: group,
      pull: true,
      // The banned zone refuses drops once all bans are used.
      put: list === banned ? () => items(banned).length < maxBans : true,
    },
    draggable: '[data-key]',
    // Buttons, links and the details toggle keep working; they never start a drag.
    filter: 'button, a, summary, input, textarea',
    preventOnFilter: false,
    animation: 150,
    // Touch: long-press (200 ms) to drag so the page can still be scrolled with a swipe.
    delay: 200,
    delayOnTouchOnly: true,
    touchStartThreshold: 5,
    fallbackTolerance: 5,
    scroll: true,
    bubbleScroll: true,
    ghostClass: 'rank-ghost',
    chosenClass: 'rank-chosen',
    dragClass: 'rank-drag',
    onEnd: evt => {
      const item = evt.item;
      if (evt.to === pool && evt.from !== pool) toPool(item); // the unranked list keeps the poll's order
      write();
      const name = label(item);
      if (evt.to === ranked) announce(`${name} moved to position ${items(ranked).indexOf(item) + 1} of ${items(ranked).length}.`);
      else if (evt.to === banned) announce(`${name} banned. ${items(banned).length} of ${maxBans} bans used.`);
      else if (evt.from !== pool) announce(`${name} is not ranked.`);
    },
  }));

  // The zones are the truth from here on: write them into the fields once (this also resolves ties a refused
  // submit re-rendered), then swap the typed fields for the zones.
  write();
  fields.hidden = true;
  board.hidden = false;
}
