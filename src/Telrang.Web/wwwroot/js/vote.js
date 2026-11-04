// Vote page enhancements (DESIGN.md §21.1). The vote form works without JS; this module only adds the drag zones and
// buttons (ranking.js), the budget line (points.js), the dates cells and layout switch (dates.js) and the captcha
// (captcha.js, which needs JS), and disables the submit button once the form is sent. Runs on load and after Blazor's
// enhanced navigation; every init is idempotent.
const parts = [
  ['[data-ranked]', () => import('./ranking.js')],
  ['[data-points]', () => import('./points.js')],
  ['[data-dates-vote]', () => import('./dates.js')],
  ['[data-captcha]', () => import('./captcha.js')],
];

function guardSubmit(form) {
  if (form.dataset.voteReady) return;
  form.dataset.voteReady = '1';
  form.addEventListener('submit', e => {
    if (e.submitter?.name === 'view') return; // layout switch without JS: not a save
    if (form.dataset.sent) {
      e.preventDefault(); // a second click while the first POST is on its way
      return;
    }
    form.dataset.sent = '1';
    for (const b of form.querySelectorAll('[data-submit]')) b.disabled = true;
  });
}

function initAll() {
  for (const [selector, load] of parts) {
    const roots = [...document.querySelectorAll(selector)];
    // A module that fails to load (network, or the page left while it loaded) leaves the plain form as it is.
    if (roots.length) load().then(m => roots.forEach(r => m.init(r)), e => console.warn('vote: enhancement not loaded', e?.message ?? e));
  }
  document.querySelectorAll('form[data-vote-form]').forEach(guardSubmit);
}

// Back/forward cache: a page restored after the POST must be usable again.
window.addEventListener('pageshow', e => {
  if (!e.persisted) return;
  for (const form of document.querySelectorAll('form[data-vote-form]')) {
    delete form.dataset.sent;
    for (const b of form.querySelectorAll('[data-submit]')) b.disabled = false;
  }
});

initAll();
window.Blazor?.addEventListener?.('enhancedload', initAll);
