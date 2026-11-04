// Dropdown menus are <details data-menu> (DESIGN.md §11): they open and close without JS. This module only adds what
// <details> lacks: close on a click outside, on Escape (focus back to the toggle) and after choosing an item.
// Listeners sit on the document, so menus rendered later (enhanced navigation, interactive pages) are covered.
// It also toggles OptionHelp panels on static pages (button[data-static-help]); on interactive pages the
// component's own click handler does that and the attribute is absent.
const openMenus = () => document.querySelectorAll('details[data-menu][open]');

document.addEventListener('click', e => {
  for (const menu of openMenus()) {
    if (!menu.contains(e.target) || (e.target.closest('a') && !e.target.closest('summary'))) menu.open = false;
  }
});

document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  for (const menu of openMenus()) {
    menu.open = false;
    menu.querySelector('summary')?.focus();
  }
});

document.addEventListener('click', e => {
  const button = e.target instanceof Element ? e.target.closest('button[data-static-help]') : null;
  const panel = button && document.getElementById(button.getAttribute('aria-controls'));
  if (!panel) return;
  const open = panel.hidden;
  panel.hidden = !open;
  button.setAttribute('aria-expanded', open ? 'true' : 'false');
});
