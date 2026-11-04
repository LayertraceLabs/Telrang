// Anonymous voting (DESIGN.md §21.1) in a separate browser context: ranking by drag, points, a dates vote, then the
// results the voter may see.
import { formAge } from './setup.mjs';

export const name = 'vote-anonymous';

async function submit(page) {
  await formAge(page);
  await page.click('[data-submit]');
  await page.waitFor(`location.search === '?saved=1'`, 'the saved ballot', 15_000).catch(async e => {
    throw new Error(`${e.message}: ${await page.eval(`location.href + ' ' + (document.querySelector('[role=alert], .validation-message, .invalid-feedback')?.textContent ?? '')`)}`);
  });
}

export async function run({ app, browser, state, check, defer }) {
  const voter = await browser.newUserContext();
  const page = await browser.newPage({ base: app.base, userContext: voter });
  defer(() => browser.removeUserContext(voter));
  defer(() => page.close());
  // ---- Ranked: drag two options into the ranking ----
  await page.goto(`/p/${state.polls.ranked}`);
  await page.waitFor(`!document.querySelector('[data-rank-board]').hidden`, 'the ranking board');
  check('anonymous header offers Log in', await page.eval(`[...document.querySelectorAll('header a')].some(a => a.textContent.trim() === 'Log in')`));
  const ranked = () => page.eval(`[...document.querySelectorAll('[data-zone="ranked"] > [data-key]')].map(i => i.dataset.key).join()`);
  await page.drag('[data-zone="pool"] [data-key="noodle-bar"] .drag-handle', '[data-zone="ranked"]');
  check('drag into the empty ranking', (await ranked()) === 'noodle-bar', await ranked());
  await page.drag('[data-zone="pool"] [data-key="pizza-place"] .drag-handle', '[data-zone="ranked"] [data-key="noodle-bar"]');
  const order = (await ranked()).split(',');
  check('drag next to it', order.length === 2 && order.includes('pizza-place'), order.join());
  const fields = await page.eval(`Object.fromEntries([...document.querySelectorAll('[data-field]')].filter(i => i.name).map(i => [i.name, i.type === 'checkbox' ? i.checked : i.value]))`);
  check('fields follow the drag', fields[`o.${order[0]}`] === '1' && fields[`o.${order[1]}`] === '2', JSON.stringify(fields));
  await submit(page);
  const label = { 'noodle-bar': 'Noodle bar', 'pizza-place': 'Pizza place' };
  check('summary shows the order', await page.eval(`document.body.innerText.indexOf(${JSON.stringify(label[order[0]])}) < document.body.innerText.indexOf(${JSON.stringify(label[order[1]])})`));
  await page.goto(`/p/${state.polls.ranked}/results`);
  check('results after voting: 1 voter', await page.eval(`document.body.innerText.includes('1 voter')`), await page.eval('document.body.innerText.slice(0, 400)'));
  // DESIGN.md §25, §33: the ranked poll shows ballots to voters on the ballots view, one column per response with the
  // name as entered; the summary carries no ballot table.
  check('no ballot table on the summary', await page.eval(`!document.querySelector('table.ballots-table')`));
  await page.clickText('Ballots', 'a');
  await page.waitFor(`location.search === '?view=ballots' && document.readyState === 'complete'`, 'the ballots view');
  const columns = await page.eval(`[...document.querySelectorAll('table.ballots-ranked thead th.ballots-name')].map(th => th.textContent.trim())`);
  check('ballots view: one anonymous response', JSON.stringify(columns) === '["Anonymous"]', JSON.stringify(columns));
  const firsts = await page.eval(`[...document.querySelectorAll('table.ballots-ranked tbody tr')].map(tr => tr.querySelector('th').textContent.trim()).join()`);
  check('ballots view: a row per position', firsts === '1st,2nd', firsts);
  check('results line on the vote page', await page.eval(`fetch('/p/${state.polls.ranked}').then(r => r.text()).then(t => t.includes('Results: voters, while voting, with ballots.'))`));

  // ---- Points: type amounts ----
  await page.goto(`/p/${state.polls.points}`);
  await page.waitFor(`!!document.querySelector('[data-points]') && !document.querySelector('[data-points] [data-step]').hidden`, 'the points ballot');
  const typeInto = async (key, value) => {
    await page.eval(`(() => { const i = document.querySelector('input[type=number][data-field="o.${key}"]'); i.focus(); i.select(); return true; })()`);
    await page.keys(...String(value));
  };
  await typeInto('beach', 6);
  await typeInto('city', 4);
  check('points line shows 0 left', (await page.eval(`document.querySelector('[data-remaining-value]').textContent`)) === '0',
    await page.eval(`document.querySelector('[data-remaining-value]').textContent`));
  await submit(page);

  // ---- Dates: one cell to yes ----
  await page.goto(`/p/${state.polls.dates}`);
  await page.waitFor(`!!document.querySelector('.dates-js') && [...document.querySelectorAll('.dates-calendar [data-cycle]')].every(x => !x.hidden)`, 'the dates ballot');
  await page.click('[data-panel="calendar"] [data-dates-cell] [data-cycle]');
  const tally = await page.eval(`document.querySelector('[data-tally]').textContent`);
  check('dates tally counts the yes', tally.startsWith('1 yes'), tally);
  await submit(page);
  await page.goto(`/p/${state.polls.dates}/results`);
  check('dates results: 1 voter', await page.eval(`document.body.innerText.includes('1 voter')`), await page.eval('document.body.innerText.slice(0, 400)'));
}
