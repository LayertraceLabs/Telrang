// Points ballot (PointsVote.razor, DESIGN.md §21.1). The form's fields are a number per option (o.{key}); without JS
// the voter types them and the server checks the budget. This module shows the steppers and sliders the server
// rendered hidden, keeps the remaining budget current and refuses steps past the budget or the per-option cap with a
// hint. Typed numbers are taken as they are (the line turns red when over budget); the server validates every ballot.
// The budget line is looked up in the form or in the home page example ([data-ballot], no form, DESIGN.md §23.2).
export function init(root) {
  if (root.dataset.ready) return;
  root.dataset.ready = '1';

  const budget = Number(root.dataset.budget) || 0;
  const cap = Number(root.dataset.cap) || budget;
  const live = root.querySelector('[data-announce]');
  const scope = root.closest('form, [data-ballot]');
  const remainingLine = scope?.querySelector('[data-remaining]');
  const remainingValue = scope?.querySelector('[data-remaining-value]');
  const must = scope?.querySelector('[data-must]');
  const cards = [...root.querySelectorAll('[data-key]')];
  const input = card => card.querySelector('input[type="number"]');
  const range = card => card.querySelector('input[type="range"]');
  const valueOf = card => {
    const v = Number.parseInt(input(card).value, 10);
    return Number.isFinite(v) && v > 0 ? v : 0;
  };
  const spent = () => cards.reduce((sum, c) => sum + valueOf(c), 0);

  const announce = message => {
    live.textContent = '';
    setTimeout(() => { live.textContent = message; }, 30);
  };

  function hint(card, text) {
    card.querySelector('[data-hint]').textContent = text ?? '';
  }

  function refresh() {
    const left = budget - spent();
    if (remainingValue) remainingValue.textContent = String(left);
    remainingLine?.classList.toggle('over-budget', left < 0);
    remainingLine?.classList.toggle('all-spent', left === 0);
    if (must) must.hidden = left === 0;
    for (const card of cards) {
      const v = valueOf(card);
      range(card).value = String(Math.min(v, cap));
      for (const b of card.querySelectorAll('[data-step]')) {
        const step = Number(b.dataset.step);
        b.disabled = step < 0 ? v === 0 : v >= cap || left <= 0;
      }
    }
    return left;
  }

  // Clamps to [0, cap]; refuses (with a hint) a change that would go over the budget.
  function trySet(card, requested) {
    const old = valueOf(card);
    const value = Math.max(0, Math.min(requested, cap));
    if (value > old && spent() - old + value > budget) {
      const left = Math.max(0, budget - spent());
      const text = left === 0 ? 'No points left. Take some from another option first.' : `Only ${left} ${left === 1 ? 'point' : 'points'} left.`;
      hint(card, text);
      announce(`${card.dataset.label}: ${text}`);
      refresh();
      return;
    }
    input(card).value = String(value);
    hint(card, requested > cap ? `At most ${cap} points per option.` : '');
    const left = refresh();
    announce(`${card.dataset.label}: ${value}. Remaining ${left} of ${budget}.`);
  }

  for (const card of cards) {
    for (const b of card.querySelectorAll('[data-step]')) {
      b.hidden = false;
      b.addEventListener('click', () => trySet(card, valueOf(card) + Number(b.dataset.step)));
    }
    const slider = range(card);
    slider.hidden = false;
    slider.addEventListener('input', () => trySet(card, Number(slider.value)));
    input(card).addEventListener('input', () => {
      hint(card, '');
      refresh();
    });
  }
  refresh();
}
