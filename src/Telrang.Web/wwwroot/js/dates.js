// Dates ballot (DatesVote.razor, DatesCell.razor, DESIGN.md §21.1). The form's fields are a radio group per option
// (o.{key}: yes / maybe / no), repeated in the three layouts under the same name, so the browser keeps one answer per
// option; without JS the layout switch is a submit button that re-renders. This module switches the layout
// client-side (moving each answer's check into the new layout), replaces the calendar and timeline radios by one
// button per cell that cycles no → yes → maybe → no, and keeps the tally current. Radios are found by data-field and
// checking one unchecks the option's others itself: the home page example (DESIGN.md §23.2) renders them without names
// (so nothing can be posted) and has no form; its layout switch is plain buttons, rendered hidden ([data-view-switch]).
const icons = { yes: 'check', maybe: 'question', no: 'x' };

export function init(root) {
  if (root.dataset.ready) return;
  root.dataset.ready = '1';

  const allowMaybe = root.dataset.maybe === 'true';
  const scope = root.closest('form, [data-ballot]');
  const live = root.querySelector('[data-announce]');
  const shown = root.querySelector('[data-shown-view]');
  const tally = scope?.querySelector('[data-tally]');
  const panels = [...root.querySelectorAll('[data-panel]')];
  const activePanel = () => panels.find(p => !p.hidden) ?? panels[0];
  const cells = panel => [...panel.querySelectorAll('[data-dates-cell]')];
  const keys = [...new Set(cells(root).map(c => c.dataset.key))];
  const radios = key => root.querySelectorAll(`input[data-field="o.${CSS.escape(key)}"]`);
  const answer = key => [...radios(key)].find(r => r.checked)?.value ?? 'no';

  const announce = message => {
    live.textContent = '';
    setTimeout(() => { live.textContent = message; }, 30);
  };

  const next = v => v === 'no' ? 'yes' : v === 'yes' && allowMaybe ? 'maybe' : 'no';

  // Shows a cell's answer: its radio, its state class and, in compact layouts, the cycle button.
  function show(cell, value) {
    // One answer per option across the three layouts (what a shared name does in the vote form).
    for (const r of radios(cell.dataset.key)) r.checked = r.closest('[data-dates-cell]') === cell && r.value === value;
    cell.dataset.state = value;
    const button = cell.querySelector('[data-cycle]');
    if (!button) return;
    button.classList.remove('dates-cell-yes', 'dates-cell-maybe', 'dates-cell-no');
    button.classList.add(`dates-cell-${value}`);
    button.setAttribute('aria-pressed', value === 'yes' ? 'true' : 'false');
    const use = button.querySelector('use');
    if (use) use.setAttribute('href', use.getAttribute('href').replace(/#.*$/, '#' + icons[value]));
    button.querySelector('[data-cycle-text]').textContent = `${cell.dataset.label}: ${value}`;
  }

  function refreshTally() {
    if (!tally) return;
    const values = keys.map(answer);
    const yes = values.filter(v => v === 'yes').length;
    const maybe = values.filter(v => v === 'maybe').length;
    tally.textContent = allowMaybe ? `${yes} yes · ${maybe} maybe` : `${yes} yes`;
  }

  function switchTo(view) {
    const target = panels.find(p => p.dataset.panel === view);
    if (!target || !target.hidden) return;
    const values = new Map(keys.map(k => [k, answer(k)]));
    for (const p of panels) p.hidden = p !== target;
    for (const cell of cells(target)) show(cell, values.get(cell.dataset.key));
    for (const b of root.querySelectorAll('[data-view]')) {
      const on = b.dataset.view === view;
      b.classList.toggle('active', on);
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
    }
    if (shown) shown.value = view;
  }

  root.addEventListener('click', e => {
    const viewButton = e.target.closest('[data-view]');
    if (viewButton) {
      e.preventDefault();
      switchTo(viewButton.dataset.view);
      return;
    }
    const cycle = e.target.closest('[data-cycle]');
    if (!cycle) return;
    const cell = cycle.closest('[data-dates-cell]');
    const value = next(answer(cell.dataset.key));
    show(cell, value);
    refreshTally();
    announce(`${cell.dataset.label}: ${value}.`);
  });

  root.addEventListener('change', e => {
    const cell = e.target.closest?.('[data-dates-cell]');
    if (!cell) return;
    show(cell, e.target.value);
    refreshTally();
  });

  root.classList.add('dates-js');
  for (const s of root.querySelectorAll('[data-view-switch]')) s.hidden = false;
  for (const button of root.querySelectorAll('[data-cycle]')) button.hidden = false;
  for (const cell of cells(activePanel())) show(cell, answer(cell.dataset.key));
  refreshTally();
}
