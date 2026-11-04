// Home page examples (Showcase.razor, DESIGN.md §23.2). Loaded on every page (App.razor) so it also runs after
// Blazor's enhanced navigation; it does nothing without [data-showcase]. Without JS the three examples are stacked;
// this module shows the Ranked / Points / Dates switch and one example at a time, and loads vote.js, which enhances
// the example fields with the vote page's own modules (ranking.js, points.js, dates.js). There is no form: nothing
// is sent anywhere.
function init(root) {
  if (root.dataset.ready) return;
  root.dataset.ready = '1';

  const buttons = [...root.querySelectorAll('[data-showcase-tab]')];
  const panels = [...root.querySelectorAll('[data-showcase-panel]')];
  const show = name => {
    for (const p of panels) p.hidden = p.dataset.showcasePanel !== name;
    for (const b of buttons) {
      const on = b.dataset.showcaseTab === name;
      b.classList.toggle('active', on);
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
    }
  };
  for (const b of buttons) b.addEventListener('click', () => show(b.dataset.showcaseTab));
  for (const s of root.querySelectorAll('[data-showcase-switch]')) s.hidden = false;
  root.classList.add('showcase-js');
  show(buttons[0]?.dataset.showcaseTab);
}

function initAll() {
  const roots = [...document.querySelectorAll('[data-showcase]')];
  if (!roots.length) return;
  roots.forEach(init);
  import('./vote.js').catch(e => console.warn('showcase: vote.js not loaded', e?.message ?? e));
}

initAll();
window.Blazor?.addEventListener?.('enhancedload', initAll);
