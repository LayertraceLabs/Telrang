// Home page showcase (DESIGN.md §23.2): the three example ballots work in the browser and nothing leaves the page.
export const name = 'home-showcase';

export async function run({ app, browser, check, defer }) {
  const visitor = await browser.newUserContext();
  const page = await browser.newPage({ base: app.base, userContext: visitor });
  defer(() => browser.removeUserContext(visitor));
  defer(() => page.close());
  await page.goto('/');
  await page.waitFor(`!document.querySelector('[data-showcase-switch]').hidden && !document.querySelector('[data-rank-board]').hidden`, 'the showcase to be enhanced');
  check('one example shown', (await page.eval(`[...document.querySelectorAll('[data-showcase-panel]')].filter(p => !p.hidden).map(p => p.dataset.showcasePanel).join()`)) === 'ranked');
  // The results views' layout radios (CSS-only switcher) have names; the ballots must not.
  check('no form and no named ballot field', await page.eval(`!document.querySelector('[data-showcase] form') && document.querySelectorAll('[data-showcase] [data-ballot] [name]').length === 0`));
  const before = browser.requests.length;

  // Ranked: a click, then drags into the ranking and onto the ban list.
  const panel = '[data-showcase-panel="ranked"]';
  const rankedKeys = () => page.eval(`[...document.querySelectorAll('${panel} [data-zone="ranked"] > [data-key]')].map(i => i.dataset.key).join()`);
  await page.click(`${panel} [data-zone="pool"] [data-key="sushi-bar"] [data-action="rank"]`);
  check('rank by click', (await rankedKeys()) === 'sushi-bar', await rankedKeys());
  check('rank field written', (await page.eval(`document.querySelector('${panel} [data-field="o.sushi-bar"]').value`)) === '1');
  await page.drag(`${panel} [data-zone="pool"] [data-key="thai-kitchen"] .drag-handle`, `${panel} [data-zone="ranked"] [data-key="sushi-bar"]`, panel);
  await page.waitFor(`document.querySelectorAll('${panel} [data-zone="ranked"] > [data-key]').length === 2`, 'the dragged option in the ranking', 3000).catch(() => { });
  check('drag into the ranking', ['sushi-bar,thai-kitchen', 'thai-kitchen,sushi-bar'].includes(await rankedKeys()), await rankedKeys());
  await page.drag(`${panel} [data-zone="pool"] [data-key="burger-grill"] .drag-handle`, `${panel} [data-zone="banned"]`, panel);
  const banned = await page.eval(`[...document.querySelectorAll('${panel} [data-zone="banned"] > [data-key]')].map(i => i.dataset.key).join()`);
  check('drag onto the ban list', banned === 'burger-grill', banned);
  check('ban field written', await page.eval(`document.querySelector('${panel} [data-field="b.burger-grill"]').checked`));
  check('ranked tally', (await page.eval(`document.querySelector('${panel} [data-tally]').textContent`)) === '2 ranked · 1/1 bans',
    await page.eval(`document.querySelector('${panel} [data-tally]').textContent`));

  // Points: steps, typing, and the per-option cap.
  await page.click('[data-showcase-tab="points"]');
  const remaining = () => page.eval(`document.querySelector('[data-showcase-panel="points"] [data-remaining-value]').textContent`);
  for (let i = 0; i < 3; i++) await page.click('[data-points] [data-key]:first-child [data-step="5"]');
  check('points left after +15', (await remaining()) === '85', await remaining());
  await page.eval(`(() => { const i = document.querySelectorAll('[data-points] input[type=number]')[1]; i.focus(); i.select(); return true; })()`);
  await page.keys('4', '0');
  check('points left after typing 40', (await remaining()) === '45', await remaining());
  for (let i = 0; i < 12; i++) await page.click('[data-points] [data-key]:first-child [data-step="5"]');
  check('cap of 60 holds', (await page.eval(`document.querySelector('[data-points] [data-key]:first-child input[type=number]').value`)) === '60');

  // Dates: a cell cycles, and keeps its answer across the three layouts.
  await page.click('[data-showcase-tab="dates"]');
  const cell = '[data-showcase-panel="dates"] [data-panel="calendar"] [data-dates-cell] [data-cycle]';
  await page.click(cell);
  const key = await page.eval(`document.querySelector('${cell}').closest('[data-dates-cell]').dataset.key`);
  const answer = () => page.eval(`[...document.querySelectorAll('[data-showcase-panel="dates"] input[data-field="o.${key}"]')].filter(r => r.checked).map(r => r.value).join()`);
  check('calendar cell: yes', (await answer()) === 'yes', await answer());
  await page.click(cell);
  check('calendar cell: maybe', (await answer()) === 'maybe', await answer());
  await page.click('[data-showcase-panel="dates"] [data-view="list"]');
  check('list keeps the answer', await page.eval(`document.querySelector('[data-showcase-panel="dates"] [data-panel="list"] input[data-field="o.${key}"][value="maybe"]').checked`));
  await page.click(`[data-showcase-panel="dates"] [data-panel="list"] input[data-field="o.${key}"][value="yes"] + label`);
  check('list radio: yes, one checked', (await answer()) === 'yes', await answer());
  await page.click('[data-showcase-panel="dates"] [data-view="timeline"]');
  check('timeline shows yes', (await page.eval(`document.querySelector('[data-showcase-panel="dates"] [data-panel="timeline"] [data-dates-cell][data-key="${key}"]').dataset.state`)) === 'yes');

  const sent = browser.requests.slice(before).filter(r => r.context === page.ctx);
  check('no request while trying the examples', sent.length === 0, sent.map(r => `${r.method} ${r.url}`).join(' | '));
}
