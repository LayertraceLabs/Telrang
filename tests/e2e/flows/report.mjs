// Reports and take-down (DESIGN.md §34.8): an anonymous visitor reports a poll through the plain report form (no look,
// the stylesheet without custom CSS); the admin sees the report on the Reports tab and disables the poll; the vote page
// answers 410 with one line for the visitor and the owner sees "Disabled"; the admin enables it again and dismisses the
// report.
import { formAge } from './setup.mjs';

export const name = 'report';

const DETAILS = 'Spam link in the description (e2e)';

export async function run({ app, browser, state, check, defer }) {
  const admin = state.admin; // also the poll's owner
  const id = state.polls.ranked;
  const group = `li:has(a[href="p/${id}"])`;

  // ---- the visitor: the link, the form, the confirmation ----
  const ctx = await browser.newUserContext();
  defer(() => browser.removeUserContext(ctx));
  const visitor = await browser.newPage({ base: app.base, userContext: ctx });
  defer(() => visitor.close());
  await visitor.goto(`/p/${id}`);
  check('vote page: a Report link', await visitor.eval(`!!document.querySelector('a[href="p/${id}/report"]')`));
  await visitor.clickText('Report', 'a');
  await visitor.waitFor(`location.pathname === '/p/${id}/report' && !!document.querySelector('form.report-form')`, 'the report form');
  check('report form: the stylesheet without custom CSS, no poll look', await visitor.eval(
    `document.querySelector('link[data-theme-css]')?.getAttribute('href').includes('admin=1') && !document.querySelector('link[data-poll-look]')`),
    await visitor.eval(`document.querySelector('link[data-theme-css]')?.getAttribute('href')`));
  await visitor.click('#report-spam');
  await visitor.fill('#report-text', DETAILS);
  await visitor.fill('#report-contact', 'reporter@example.test');
  await formAge(visitor);
  await visitor.clickText('Send report', 'button');
  await visitor.waitFor(`location.search === '?sent=1' && document.body.innerText.includes('Report sent.')`, '"Report sent."', 15_000);
  check('report sent, no form left', await visitor.eval(`!document.querySelector('form.report-form')`));

  // ---- the admin: the report on its tab, disable ----
  await admin.gotoInteractive('/admin?tab=reports');
  await admin.waitFor(`document.querySelector(${JSON.stringify(group)})?.innerText.includes(${JSON.stringify(DETAILS)})`, 'the report on the Reports tab');
  check('Reports tab: the count on its label', await admin.eval(`!!document.querySelector('.admin-tabs a[href="admin?tab=reports"] .badge')`));
  check('Reports tab: contact and category', await admin.eval(
    `(() => { const t = document.querySelector(${JSON.stringify(group)}).innerText; return t.includes('reporter@example.test') && t.includes('Spam'); })()`));
  await admin.clickText('Disable', `${group} button`);
  await admin.waitFor(`document.body.innerText.includes('Disabled “')`, '"Disabled …"', 10_000);
  check('disabled: still listed, with Enable', await admin.eval(
    `[...document.querySelectorAll(${JSON.stringify(group + ' button')})].some(b => b.textContent.trim() === 'Enable')`));

  // ---- the visitor gets 410 with one line; the owner sees Disabled ----
  await visitor.goto(`/p/${id}`);
  check('visitor: the vote page says unavailable', await visitor.eval(
    `document.body.innerText.includes('This poll is unavailable.') && !document.querySelector('h1')`), await visitor.eval('document.body.innerText.slice(0, 300)'));
  const status = await visitor.eval(`Promise.all(['', '/results', '/report'].map(p => fetch('/p/${id}' + p).then(r => r.status))).then(s => s.join())`);
  check('visitor: 410 on the vote, results and report pages', status === '410,410,410', status);
  await admin.gotoInteractive('/polls'); // the circuit up before leaving, or its negotiation is cut off mid-way
  check('owner: Disabled on My polls', await admin.eval(
    `[...document.querySelectorAll('.poll-list .list-group-item')].some(i => i.querySelector('a[href="p/${id}/results"]') && i.querySelector('.badge')?.textContent.trim() === 'Disabled')`));
  await admin.goto(`/p/${id}`);
  check('owner: the vote page says why, no vote form', await admin.eval(
    `document.body.innerText.includes('Disabled by an admin.') && !document.querySelector('[data-vote-form]')`));

  // ---- enable again, dismiss ----
  await admin.gotoInteractive('/admin?tab=reports');
  await admin.waitFor(`!!document.querySelector(${JSON.stringify(group)})`, 'the poll on the Reports tab');
  await admin.clickText('Enable', `${group} button`);
  await admin.waitFor(`document.body.innerText.includes('Enabled “')`, '"Enabled …"', 10_000);
  await admin.waitFor(`document.querySelector(${JSON.stringify(group)})?.innerText.includes(${JSON.stringify(DETAILS)})`, 'the report open again');
  await admin.clickText('Dismiss', `${group} button`);
  await admin.waitFor(`document.body.innerText.includes('Dismissed the report')`, '"Dismissed the report …"', 10_000);
  check('dismissed: the poll left the tab, circuit alive', await admin.eval(`!document.querySelector(${JSON.stringify(group)})`) && await admin.alive());
  const again = await visitor.eval(`fetch('/p/${id}').then(r => r.status)`);
  check('visitor: the poll is back', again === 200, again);
}
