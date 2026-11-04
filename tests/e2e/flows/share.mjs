// Share page copy buttons (DESIGN.md §23.1): navigator.clipboard.writeText runs inside the click (user activation),
// with no server round trip; also after reaching the page by in-app navigation from My polls.
export const name = 'share-copy';

export async function run({ app, state, check }) {
  const page = state.admin;
  const id = state.polls.ranked;
  const copies = () => page.eval('window.__e2e.copies');
  const status = () => page.eval(`document.getElementById('public-link-status').textContent`);

  await page.gotoInteractive(`/polls/${id}/share`);
  await page.click('[data-copy-status="public-link-status"]');
  await page.waitFor(`window.__e2e.copies.length === 1 && 'ok' in window.__e2e.copies[0] || 'error' in (window.__e2e.copies[0] ?? {})`, 'the clipboard write', 5000);
  let c = await copies();
  check('poll link copied within the click', c.length === 1 && c[0].active && c[0].ok && c[0].text === `${app.base}/p/${id}`, JSON.stringify(c));
  check('status says copied', (await status()) === 'Copied to clipboard.', await status());
  const read = await page.eval('navigator.clipboard.readText()', { userActivation: true }).catch(e => `(readText refused: ${e.message})`);
  check('clipboard holds the link', read === `${app.base}/p/${id}`, read);

  // In-app navigation (circuit) from My polls to the share page, then copy again.
  await page.gotoInteractive('/polls');
  await page.click(`a[href="polls/${id}/share"]`);
  await page.waitFor(`location.pathname === '/polls/${id}/share' && !!document.querySelector('[data-copy-status="public-link-status"]')`, 'the share page');
  await page.interactive();
  await page.click('[data-copy-status="public-link-status"]');
  await page.waitFor(`window.__e2e.copies.some(c => 'ok' in c || 'error' in c)`, 'the clipboard write', 5000);
  c = await copies();
  check('copy after in-app navigation', c.at(-1)?.active && c.at(-1)?.ok && c.at(-1)?.text === `${app.base}/p/${id}`, JSON.stringify(c));
  check('share page circuit alive', await page.alive());
}
