// The owner's results page (DESIGN.md §24.3) and the links from interactive pages to static ones (§24.6): those load
// the page fully instead of going through the circuit, so no RemoteNavigationManager error reaches the server log.
import { formAge } from './setup.mjs';

export const name = 'owner-results';

/** Records whether Blazor's router took the next link click (it calls history.pushState). */
const watchPushState = page => page.eval(`(() => { window.__pushed = null; const ps = history.pushState.bind(history);
  history.pushState = (...a) => { window.__pushed = String(a[2]); sessionStorage.setItem('e2e-pushed', String(a[2])); return ps(...a); };
  sessionStorage.removeItem('e2e-pushed'); return true; })()`);
const pushed = page => page.eval(`sessionStorage.getItem('e2e-pushed')`);

export async function run({ app, browser, state, check, defer }) {
  const page = state.admin;
  const id = state.polls.ranked;

  // My polls (interactive) -> title -> results (static): a full load.
  await page.gotoInteractive('/polls');
  await watchPushState(page);
  await page.click(`a[href="p/${id}/results"]`);
  await page.waitFor(`location.pathname === '/p/${id}/results' && document.readyState === 'complete'`, 'the results page');
  check('My polls title opens results as a full page load', (await pushed(page)) === null, await pushed(page));
  check('owner bar shown', await page.eval(`!!document.querySelector('.owner-bar') && document.querySelector('.owner-bar').textContent.includes('Open poll')`));
  check('the anonymous ballot is counted', await page.eval(`document.body.innerText.includes('1 voter')`), await page.eval('document.body.innerText.slice(0, 400)'));

  // Close, then reopen, from the results page (named forms, post/redirect/get).
  const badge = () => page.eval(`[...document.querySelectorAll('.badge')].map(b => b.textContent.trim()).filter(t => t === 'Open' || t === 'Closed').join()`);
  await page.clickText('Close poll now', 'button');
  await page.waitFor(`[...document.querySelectorAll('.owner-bar button')].some(b => b.textContent.trim() === 'Reopen poll')`, 'Reopen poll');
  check('closed on the results page', (await badge()) === 'Closed' && (await page.path()) === `/p/${id}/results`, `${await badge()} ${await page.path()}`);
  await page.clickText('Reopen poll', 'button');
  await page.waitFor(`[...document.querySelectorAll('.owner-bar button')].some(b => b.textContent.trim() === 'Close poll now')`, 'Close poll now');
  check('reopened', (await badge()) === 'Open', await badge());

  // The ballots view (DESIGN.md §33): Ballots next to Refresh, Summary back; every type for the owner.
  await page.clickText('Ballots', 'a');
  await page.waitFor(`location.search === '?view=ballots' && document.readyState === 'complete'`, 'the ballots view');
  const columns = await page.eval(`document.querySelectorAll('table.ballots-ranked thead th.ballots-name').length`);
  check('owner ballots view: one column per ballot', columns === 1, String(columns));
  check('owner ballots view: no summary bars', await page.eval(`!document.querySelector('.results-table')`));
  await page.clickText('Summary', 'a');
  await page.waitFor(`location.pathname === '/p/${id}/results' && location.search === '' && document.readyState === 'complete'`, 'the summary');
  check('summary: no ballot table', await page.eval(`!document.querySelector('table.ballots-table')`));
  await page.goto(`/p/${state.polls.dates}/results?view=ballots`);
  check('owner dates ballots view: who can when', await page.eval(`document.querySelectorAll('table.dates-grid tbody tr').length === 1`),
    await page.eval('document.body.innerText.slice(0, 400)'));

  // Live updates keep working on the ballots view (DESIGN.md §21.2): a new points ballot appears as a column. The voter's
  // tab fills its ballot in front, then the owner's tab comes to the front (js/results-live.js streams only while visible)
  // and the voter's hidden tab submits.
  await page.goto(`/p/${state.polls.points}/results?view=ballots`);
  const pointsColumns = () => page.eval(`document.querySelectorAll('table.ballots-points thead th.ballots-name').length`);
  const voter = await browser.newUserContext();
  const other = await browser.newPage({ base: app.base, userContext: voter });
  defer(() => browser.removeUserContext(voter));
  defer(() => other.close());
  await other.goto(`/p/${state.polls.points}`);
  await other.waitFor(`!!document.querySelector('[data-points]') && !document.querySelector('[data-points] [data-step]').hidden`, 'the points ballot');
  await other.eval(`(() => { const i = document.querySelector('input[type=number][data-field="o.city"]'); i.focus(); i.select(); return true; })()`);
  await other.keys('1', '0');
  await formAge(other);
  await page.activate();
  await page.waitFor(`document.visibilityState === 'visible' && !document.querySelector('[data-live-badge]').hidden`, 'the live badge');
  const before = await pointsColumns();
  await other.eval(`(() => { document.querySelector('[data-submit]').click(); return true; })()`);
  await other.waitFor(`location.search === '?saved=1'`, 'the saved ballot', 15_000);
  await page.waitFor(`document.querySelectorAll('table.ballots-points thead th.ballots-name').length === ${before + 1}`, 'the new column, live', 15_000);
  check('live update on the ballots view', (await pointsColumns()) === before + 1 && (await page.path()) === `/p/${state.polls.points}/results?view=ballots`,
    `${before} -> ${await pointsColumns()}`);

  // Share page (interactive) -> Results (static), and the brand -> home: full loads too.
  await page.gotoInteractive(`/polls/${id}/share`);
  await watchPushState(page);
  await page.click(`a[href="p/${id}/results"]`);
  await page.waitFor(`location.pathname === '/p/${id}/results' && document.readyState === 'complete'`, 'the results page');
  check('share page Results link is a full load', (await pushed(page)) === null, await pushed(page));
  await page.gotoInteractive('/polls');
  await watchPushState(page);
  await page.click('a.app-brand');
  await page.waitFor(`location.pathname === '/' && document.readyState === 'complete'`, 'the home page');
  check('brand link is a full load', (await pushed(page)) === null, await pushed(page));

  // Interactive -> interactive stays in the circuit.
  await page.gotoInteractive('/polls');
  await watchPushState(page);
  await page.click(`a[href="polls/${id}/share"]`);
  await page.waitFor(`location.pathname === '/polls/${id}/share'`, 'the share page');
  check('My polls -> share stays in the circuit', (await pushed(page)) !== null);
  await page.interactive();
}
