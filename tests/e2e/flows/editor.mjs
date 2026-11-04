// The poll editor (DESIGN.md §20, §23.4, §23.5): one poll of each type, the List / Text switch, the help toggle and the
// browser's time zone as the default. The polls are used by the share, vote and results flows.
export const name = 'editor';
export const required = true; // the later flows use its polls

const field = f => `.editor-form [id$="-f-${f.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}"]`;

async function newEditor(page) {
  await page.gotoInteractive('/polls/new');
  await page.waitFor(`!!document.querySelector('.editor-form .editor-help-row button')`, 'the editor');
}

async function create(page) {
  await page.clickText('Create poll', 'button');
  await page.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page after Create poll', 20_000);
  await page.interactive();
  return (await page.eval('location.pathname')).split('/')[2];
}

const optionInputs = page => page.eval(`[...document.querySelectorAll('.editor-option-main input')].map(i => i.value)`);

export async function run({ browser, state, check, tolerate, defer }) {
  const page = state.admin;
  state.polls = {};

  // ---- Ranked: time zone, help, List <-> Text ---------------------------------------------------------------
  await newEditor(page);
  const browserZone = await page.eval('Intl.DateTimeFormat().resolvedOptions().timeZone');
  const zone = await page.waitFor(`document.querySelector('${field('timezone')}').value === ${JSON.stringify(browserZone)} && document.querySelector('${field('timezone')}').value`,
    'the browser time zone in the editor', 5000).catch(() => page.eval(`document.querySelector('${field('timezone')}').value`));
  check('time zone defaults to the browser zone', zone === browserZone, `${zone} (browser ${browserZone})`);

  const help = () => page.eval(`(() => { const b = document.querySelector('.editor-help-row button'); const panels = [...document.querySelectorAll('.editor-form .option-help-panel')];
    return { text: b.textContent.trim(), pressed: b.getAttribute('aria-pressed'), open: panels.filter(p => !p.hidden).length, total: panels.length }; })()`);
  let h = await help();
  check('help starts folded', h.text === 'Show help' && h.open === 0 && h.total > 5, JSON.stringify(h));
  await page.click('.editor-form .editor-help-row button');
  await page.waitFor(`document.querySelector('.editor-help-row button').textContent.trim() === 'Hide help'`, 'Hide help').catch(() => { });
  h = await help();
  check('Show help opens every panel', h.text === 'Hide help' && h.pressed === 'true' && h.open === h.total, JSON.stringify(h));
  await page.click('[aria-label="About Scoring"]');
  await page.waitFor(`[...document.querySelectorAll('.editor-form .option-help-panel')].some(p => p.hidden)`, 'the Scoring panel to close', 5000).catch(() => { });
  h = await help();
  check('one toggle closes only its own panel', h.open === h.total - 1, JSON.stringify(h));
  await page.click('.editor-form .editor-help-row button');
  await page.waitFor(`document.querySelector('.editor-help-row button').textContent.trim() === 'Show help'`, 'Show help').catch(() => { });
  h = await help();
  check('Hide help folds every panel', h.text === 'Show help' && h.open === 0, JSON.stringify(h));

  await page.fill(field('title'), 'Where do we eat (e2e)');
  // DESIGN.md §31 (pentest R3-RES-2): text past the field's maxlength from a crafted input event is cut on the server at
  // twice the limit (2 x 2000 for the description), so the circuit never holds it; the field shows the cut text.
  await page.eval(`(() => { const e = document.querySelector('${field('description')}'); e.value = 'x'.repeat(150000);
    e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
  const cut = await page.waitFor(`document.querySelector('${field('description')}').value.length === 4000 && 4000`, 'the description cut to 4000', 5000)
    .catch(() => page.eval(`document.querySelector('${field('description')}').value.length`));
  check('oversized editor text is cut on the server', cut === 4000 && await page.alive(), `${cut} characters`);
  await page.fill(field('description'), '');
  const inputs = await page.eval(`document.querySelectorAll('.editor-option-main input').length`);
  for (const [i, v] of ['Pizza place', 'Noodle bar', 'Salad shop'].entries()) {
    if (i >= inputs) { await page.clickText('Add option', 'button'); await page.waitFor(`document.querySelectorAll('.editor-option-main input').length > ${i}`, 'a new option row'); }
    await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  }
  await page.clickText('Text', 'label');
  await page.waitFor(`!!document.querySelector('textarea.editor-options-text')`, 'the options text');
  const text = await page.eval(`document.querySelector('textarea.editor-options-text').value`);
  check('Text shows the list', ['Pizza place', 'Noodle bar', 'Salad shop'].every(o => text.includes(o)), text);
  await page.fill('textarea.editor-options-text', text.trimEnd() + '\nBurger grill\n  Open late.\n');
  await page.clickText('List', 'label');
  await page.waitFor(`document.querySelectorAll('.editor-option-main input').length === 4`, 'four options in the list');
  const list = await optionInputs(page);
  check('List shows the text', JSON.stringify(list) === JSON.stringify(['Pizza place', 'Noodle bar', 'Salad shop', 'Burger grill']), JSON.stringify(list));
  check('details kept from the text', (await page.eval(`document.querySelectorAll('.editor-option textarea')[3].value`)) === 'Open late.');
  // Results (DESIGN.md §25): results for anyone only on public polls, after closing only with an audience, ballots only
  // with names. The ranked poll shows ballots to its voters (the vote flow checks the rows).
  const results = () => page.eval(`(() => { const r = i => document.querySelector('.editor-form [id$="-results-' + i + '"]');
    const t = i => document.querySelector('.editor-form [id$="-resultsTiming-' + i + '"]');
    return { results: [0, 1, 2].find(i => r(i).checked), anyoneOff: r(2).disabled, timingOff: t(1).disabled,
      ballots: document.querySelector('${field('showBallots')}').checked, ballotsOff: document.querySelector('${field('showBallots')}').disabled }; })()`);
  await page.click('.editor-form label[for$="-results-2"]');
  await page.waitFor(`document.querySelector('.editor-form [id$="-results-2"]').checked`, 'results for anyone');
  await page.clickText('Invite links', 'label');
  await page.waitFor(`document.querySelector('.editor-form [id$="-results-2"]').disabled`, 'anyone disabled for invite links');
  let r = await results();
  check('invite links move results for anyone to voters', r.results === 1 && r.anyoneOff, JSON.stringify(r));
  await page.clickText('Anyone with the link', 'label');
  await page.clickText('Owner only', 'label');
  await page.waitFor(`document.querySelector('.editor-form [id$="-resultsTiming-1"]').disabled`, 'show results disabled for owner only');
  r = await results();
  check('owner only disables show results', r.results === 0 && r.timingOff && !r.anyoneOff, JSON.stringify(r));
  await page.clickText('Voters', 'label');
  await page.waitFor(`!document.querySelector('.editor-form [id$="-resultsTiming-1"]').disabled`, 'show results enabled');
  await page.clickText('None', 'label');
  await page.waitFor(`document.querySelector('${field('showBallots')}').disabled`, 'show ballots disabled without names');
  await page.clickText('Optional', 'label');
  await page.waitFor(`!document.querySelector('${field('showBallots')}').disabled`, 'show ballots enabled');
  await page.click(field('showBallots'));
  await page.waitFor(`document.querySelector('${field('showBallots')}').checked`, 'show ballots on');
  r = await results();
  check('voters, while voting, with ballots', r.results === 1 && !r.timingOff && r.ballots, JSON.stringify(r));
  state.polls.ranked = await create(page);
  check('ranked poll created', !!state.polls.ranked);

  // ---- Points ---------------------------------------------------------------------------------------------
  await newEditor(page);
  await page.clickText('Points', 'label');
  await page.waitFor(`!!document.querySelector('${field('questions[0].budget')}')`, 'the budget field');
  await page.fill(field('title'), 'Club trips (e2e)');
  for (const [i, v] of ['Beach', 'Mountains', 'City'].entries()) {
    if (i >= await page.eval(`document.querySelectorAll('.editor-option-main input').length`)) {
      await page.clickText('Add option', 'button');
      await page.waitFor(`document.querySelectorAll('.editor-option-main input').length > ${i}`, 'a new option row');
    }
    await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  }
  await page.fill(field('questions[0].budget'), '10');
  await page.click('.editor-form label[for$="-results-2"]');
  state.polls.points = await create(page);
  check('points poll created', !!state.polls.points);

  // ---- Dates: calendar and text -----------------------------------------------------------------------------
  await newEditor(page);
  await page.clickText('Dates', 'label');
  await page.waitFor(`!!document.querySelector('.editor-calendar')`, 'the calendar');
  await page.fill(field('title'), 'Board games night (e2e)');
  await page.click('.editor-calendar [aria-label="Next month"]');
  const month = await page.eval(`document.querySelector('.editor-cal-month').textContent`);
  await page.waitFor(`document.querySelector('.editor-cal-month').textContent !== ''`, 'the next month');
  for (const day of [10, 12]) {
    await page.click(await page.byText(String(day), '.editor-cal-grid button.editor-cal-day'));
    await page.waitFor(`document.querySelectorAll('.editor-days > .editor-day').length >= ${day === 10 ? 1 : 2}`, `day ${day} selected`);
  }
  await page.clickText('Text', 'label');
  await page.waitFor(`!!document.querySelector('textarea.editor-options-text')`, 'the dates text');
  const datesText = (await page.eval(`document.querySelector('textarea.editor-options-text').value`)).trimEnd();
  const lines = datesText.split('\n').filter(l => /\d{4}-\d{2}-\d{2}/.test(l));
  check('Text shows the two days', lines.length === 2, datesText);
  const firstDay = /\d{4}-\d{2}-\d{2}/.exec(lines[0] ?? '')?.[0];
  await page.fill('textarea.editor-options-text', `${datesText}\n${firstDay} 19:00-22:00\n`);
  await page.clickText('Calendar', 'label');
  await page.waitFor(`!!document.querySelector('.editor-calendar')`, 'the calendar again');
  check('a time slot from the text shows on its day', (await page.eval(`[...document.querySelectorAll('.editor-cal-dot')].map(d => d.textContent.trim()).join()`)) === '1',
    `${month}: ${await page.eval(`document.querySelector('.editor-days').innerText`)}`);
  await page.click('.editor-form label[for$="-results-2"]');
  state.polls.dates = await create(page);
  check('dates poll created', !!state.polls.dates);

  check('editor circuit alive', await page.alive());

  // ---- draft.js fails to load (DESIGN.md §23.1): the circuit lives on and the controls still work ----------------
  const restore = await browser.failRequests(url => /\/js\/draft(\.[a-z0-9]+)?\.js/.test(new URL(url).pathname));
  defer(restore);
  tolerate(/draft(\.[a-z0-9]+)?\.js|dynamically imported module/i, 'draft.js is made to fail on purpose');
  await newEditor(page);
  await page.clickText('Points', 'label');
  check('without draft.js: type switch works', await page.waitFor(`!!document.querySelector('${field('questions[0].budget')}')`, 'the budget field', 5000).catch(() => false));
  await page.clickText('Text', 'label');
  check('without draft.js: Text works', await page.waitFor(`!!document.querySelector('textarea.editor-options-text')`, 'the options text', 5000).catch(() => false));
  check('without draft.js: circuit alive', await page.alive());
  const failed = browser.requests.filter(r => /\/js\/draft/.test(r.url)).length;
  check('draft.js was requested (and made to fail)', failed > 0);
  await restore();
}
