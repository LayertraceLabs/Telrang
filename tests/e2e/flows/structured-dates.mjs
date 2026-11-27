// The dates calendar inside a structured poll (DESIGN.md §34.1–§34.4, §18.2): a poll built in the editor with a ranked
// question and the group "When" holding a dates question (days picked by dragging over the editor calendar) on page 1, and a
// choice on page 2. An anonymous voter ranks by drag and answers days in the vote page's calendar view (the one DatesViews,
// fields under the question's prefix), carries page 1 to page 2 and submits; the owner's results show both questions
// and the ballots CSV holds exactly those answers. The structured-draft flow edits this poll.
import { formAge } from './setup.mjs';
import { addFromSimple } from './structured-editor.mjs';

export const name = 'structured-dates';

const field = f => `.editor-form [id$="-f-${f.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}"]`;
const calDay = iso => `.editor-cal-grid button[data-day="${iso}"]`;

async function waitPlace(page, text) {
  await page.waitFor(`document.querySelector('.editor-item-place')?.textContent.trim() === ${JSON.stringify(text)}`, `the item "${text}"`)
    .catch(async e => { throw new Error(`${e.message}: ${await page.eval(`document.querySelector('.editor-item-place')?.textContent.trim() ?? ''`)}`); });
}

async function outline(page, label) {
  const ok = await page.eval(`(() => { document.querySelectorAll('[data-e2e-target]').forEach(e => e.removeAttribute('data-e2e-target'));
    const b = [...document.querySelectorAll('.editor-outline .outline-item')].find(b => b.querySelector('.outline-label')?.textContent.trim() === ${JSON.stringify(label)});
    b?.setAttribute('data-e2e-target', ''); return !!b; })()`);
  if (!ok) throw new Error(`no outline item "${label}": ${await page.eval(`[...document.querySelectorAll('.outline-label')].map(l => l.textContent.trim()).join(' | ')`)}`);
  await page.click('[data-e2e-target]');
  await page.waitFor(`document.querySelector('.editor-outline .outline-item.selected .outline-label')?.textContent.trim() === ${JSON.stringify(label)}`, `"${label}" selected`);
}

async function options(page, labels) {
  for (const [i, v] of labels.entries()) {
    if (i >= await page.eval(`document.querySelectorAll('.editor-option-main input').length`)) {
      await page.clickText('Add option', 'button');
      await page.waitFor(`document.querySelectorAll('.editor-option-main input').length > ${i}`, 'a new option row');
    }
    await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  }
}

const onPage = (page, n) => page.waitFor(`document.querySelector('[aria-current="step"]')?.textContent.trim().startsWith('${n} · ')`, `page ${n}`, 15_000);

export async function run({ app, browser, state, check, defer }) {
  const admin = state.admin;

  // ---- the poll, in the editor: Venue (ranked), group "When" with Days (dates), page 2 with Bring (choice) ----
  await admin.gotoInteractive('/polls/new');
  await admin.waitFor(`!!document.querySelector('.editor-form .editor-help-row button')`, 'the editor');
  await admin.fill(field('title'), 'Team outing (e2e)');
  await options(admin, ['Lake', 'Forest', 'Museum']);
  await addFromSimple(admin, 'Add group');
  await waitPlace(admin, 'Group: Group 1 · Question 2 of 2');
  await admin.clickText('Dates', '[aria-label="Question type"] label');
  await admin.waitFor(`!!document.querySelector('.editor-item .editor-calendar')`, 'the calendar of the dates question');
  await admin.fill(field('questions[1].prompt'), 'Days');
  // Next month, so every day is ahead (the close date default is far enough).
  const shown = await admin.eval(`document.querySelector('.cal-nav-title').textContent.trim()`);
  await admin.click('.editor-calendar [aria-label="Next month"]');
  await admin.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() !== ${JSON.stringify(shown)}`, 'the next month');
  const prefix = await admin.eval(`document.querySelector('.editor-cal-grid button[data-day]').dataset.day.slice(0, 8)`);
  const d = n => prefix + String(n).padStart(2, '0');
  await admin.pointerDrag(calDay(d(8)), calDay(d(11)));
  await admin.waitFor(`document.querySelectorAll('.editor-cal-grid button[aria-pressed="true"]').length === 4`, 'four days dragged', 5000).catch(() => { });
  const picked = await admin.eval(`[...document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]')].map(b => b.dataset.day)`);
  check('editor: the drag picks four days', JSON.stringify(picked) === JSON.stringify([8, 9, 10, 11].map(d)), JSON.stringify(picked));
  await outline(admin, 'Group · Group 1');
  await admin.fill(field('groups[0].title'), 'When');
  await admin.waitFor(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Group · When')`, 'the group renamed');

  await admin.clickText('Add page', '.editor-outline .editor-addbar button');
  await waitPlace(admin, 'Page 2 · Question 3 of 3');
  await admin.fill(field('questions[2].prompt'), 'Bring');
  await options(admin, ['Snacks', 'Drinks']);
  await outline(admin, 'Team outing (e2e)');
  await admin.fill(field('questions[0].prompt'), 'Venue');
  await admin.waitFor(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Venue')`, 'the question renamed');

  // Results for the owner only (read below as the owner).
  await admin.click('[role=tab][id$="-tab-Voting"]');
  await admin.waitFor(`!!document.querySelector('.editor-form label[for$="-results-0"]')`, 'the voting tab');
  await admin.click('.editor-form label[for$="-results-0"]');
  await admin.waitFor(`document.querySelector('.editor-form [id$="-results-0"]').checked`, 'results for the owner only');
  await admin.interactive();
  await admin.clickText('Create poll', 'button');
  await admin.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page after Create poll', 20_000)
    .catch(async e => { throw new Error(`${e.message}: ${await admin.eval(`document.querySelector('.editor-form .alert-danger')?.innerText ?? ''`)}`); });
  const id = (await admin.eval('location.pathname')).split('/')[2];
  state.polls.structuredDates = id;
  check('share page: the size line', (await admin.eval(`document.querySelector('.mode-line').textContent.trim()`)).startsWith('2 pages · 3 questions'),
    await admin.eval(`document.querySelector('.mode-line').textContent.trim()`));

  // ---- an anonymous voter: drag the ranking, answer days in the calendar view ----
  const ctx = await browser.newUserContext();
  defer(() => browser.removeUserContext(ctx));
  const voter = await browser.newPage({ base: app.base, userContext: ctx });
  defer(() => voter.close());
  await voter.goto(`/p/${id}`);
  await onPage(voter, 1);
  await voter.waitFor(`!document.querySelector('[data-rank-board]').hidden`, 'the ranking board');
  await voter.waitFor(`!!document.querySelector('.dates-js') && [...document.querySelectorAll('[data-panel="calendar"] [data-cycle]')].every(x => !x.hidden)`, 'the dates calendar');
  check('vote page: the group legend', await voter.eval(`document.querySelector('.vote-group legend')?.textContent.trim() === 'When'`));
  check('vote page: the calendar view first', await voter.eval(`!document.querySelector('[data-panel="calendar"]').hidden
    && document.querySelectorAll('[data-panel="calendar"] [data-dates-cell]').length === 4`));

  const ranked = () => voter.eval(`[...document.querySelectorAll('[data-zone="ranked"] > [data-key]')].map(i => i.dataset.key).join()`);
  await voter.drag('[data-zone="pool"] [data-key="forest"] .drag-handle', '[data-zone="ranked"]');
  check('drag into the empty ranking', (await ranked()) === 'forest', await ranked());
  await voter.drag('[data-zone="pool"] [data-key="lake"] .drag-handle', '[data-zone="ranked"] [data-key="forest"]');
  const order = (await ranked()).split(',');
  check('drag next to it', order.length === 2 && order.includes('lake'), order.join());
  check('the ranking writes the question\'s fields', await voter.eval(`document.querySelector('[data-field="q1.o.${order[0]}"]').value === '1'
    && document.querySelector('[data-field="q1.o.${order[1]}"]').value === '2'`));

  // Calendar cells cycle no → yes → maybe: the 1st day yes, the 3rd day maybe, the others no.
  const cells = await voter.eval(`[...document.querySelectorAll('[data-panel="calendar"] [data-dates-cell]')].map(c => ({ key: c.dataset.key, label: c.dataset.label }))`);
  const cycle = key => voter.click(`[data-panel="calendar"] [data-dates-cell][data-key="${key}"] [data-cycle]`);
  await cycle(cells[0].key);
  await cycle(cells[2].key);
  await cycle(cells[2].key);
  const answer = key => voter.eval(`document.querySelector('[data-panel="calendar"] input[data-field="q2.o.${key}"]:checked')?.value ?? 'none'`);
  const answers = [];
  for (const c of cells) answers.push(await answer(c.key));
  check('calendar: the answers in the question\'s fields', JSON.stringify(answers) === '["yes","no","maybe","no"]', JSON.stringify(answers));
  const tally = await voter.eval(`document.querySelector('[data-dates-vote]').closest('[data-ballot], form').querySelector('[data-tally]')?.textContent ?? ''`);
  check('calendar: the tally', tally.includes('1 yes'), tally);

  await voter.clickText('Next', '.vote-submit-bar button');
  await onPage(voter, 2);
  await voter.click('input[name="q3.c"][value="drinks"]');
  await formAge(voter);
  await voter.clickText('Submit ballot', '.vote-submit-bar button');
  await voter.waitFor(`location.search === '?saved=1'`, 'the saved response', 15_000).catch(async e => {
    throw new Error(`${e.message}: ${await voter.eval(`location.href + ' ' + (document.querySelector('[role=alert]')?.textContent ?? '')`)}`);
  });

  // ---- the owner's results and ballots CSV ----
  await admin.goto(`/p/${id}/results`);
  await admin.waitFor(`document.readyState === 'complete' && !!document.querySelector('.results-question')`, 'the results sections');
  const sections = await admin.eval(`[...document.querySelectorAll('.results-question h3, .results-question h4')].map(h => h.textContent.trim())`);
  check('results: one section per question', JSON.stringify(sections) === JSON.stringify(['Venue', 'Days', 'Bring']), JSON.stringify(sections));
  check('results: the dates in the group', await admin.eval(`document.querySelector('.results-group h3')?.textContent.trim() === 'When'
    && !!document.querySelector('.results-group [data-dates-views]')`));
  const csv = await admin.eval(`fetch(document.querySelector('a[href$="/export.csv"]').href).then(async r => r.status + ' ' + r.headers.get('content-type') + '\\n' + await r.text())`);
  const labels = { lake: 'Lake', forest: 'Forest', museum: 'Museum' };
  const csvLines = csv.split('\r\n');
  check('ballots CSV: a column per question', csv.startsWith('200 text/csv') && csvLines[0].endsWith('Name,Alias,SubmittedAt,UpdatedAt,q1 · Venue,q2 · Days,q3 · Bring'), csv.slice(0, 400));
  // The calendar's cells are the days picked in the editor, in order: the 1st yes, the 3rd maybe.
  const row = `,${labels[order[0]]} > ${labels[order[1]]},${picked[0]}: yes; ${picked[2]}: maybe,Drinks`;
  check('ballots CSV: the ranking and the days as answered', csvLines.length === 3 && csvLines[1].endsWith(row), `${row}\n${csv.slice(0, 600)}`);
}
