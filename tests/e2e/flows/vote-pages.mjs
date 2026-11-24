// A poll with several questions on two pages (DESIGN.md §34.4): voted with JS (the form, enhanced), by a signed-in voter
// who leaves after page 1 and resumes the draft, and without JS (the app's scripts fail to load: plain form posts, the
// carry field between the pages).
import { formAge } from './setup.mjs';

export const name = 'vote-pages';

// TEMPORARY (§34 step 2, remove in step 4): the editor cannot build such a poll yet. With Polls:TemporaryAllowStructuredPolls
// on (lib/app.mjs) it restores a new poll's browser draft with several questions, and Create poll saves it whole.
const survey = {
  title: 'Party (e2e)', voterMode: 'Public', responses: 'One', names: 'Optional', resultsVisibility: 'Creator',
  pages: [{ key: 'a', title: 'Attending', description: '' }, { key: 'b', title: 'Food', description: '' }],
  groups: [{ key: 'g', title: 'You', description: '' }],
  questions: [
    { prompt: 'Dishes', type: 'Ranked', page: 'a', options: [{ label: 'Soup' }, { label: 'Salad' }, { label: 'Stew' }] },
    { prompt: 'Coming?', type: 'Choice', required: true, page: 'a', group: 'g', options: [{ label: 'Yes' }, { label: 'No' }] },
    { prompt: 'Guests', type: 'Number', page: 'a', group: 'g', min: 0, max: 5, unit: 'people' },
    { prompt: 'Diet', type: 'Choice', page: 'b', maxSelect: 2, allowOther: true, options: [{ label: 'Vegan' }, { label: 'Fish' }] },
    { prompt: 'Notes', type: 'Text', page: 'b', multiline: true },
  ],
};

const onPage = (page, n) => page.waitFor(`document.querySelector('[aria-current="step"]')?.textContent.trim().startsWith('${n} · ')`, `page ${n}`, 15_000);
const checked = (page, selector) => page.eval(`!!document.querySelector(${JSON.stringify(selector)})?.checked`);

async function saved(page) {
  await page.waitFor(`location.search === '?saved=1'`, 'the saved response', 15_000).catch(async e => {
    throw new Error(`${e.message}: ${await page.eval(`location.href + ' ' + (document.querySelector('[role=alert]')?.textContent ?? '')`)}`);
  });
}

/** Page 1: one choice, a number; Next. */
async function fillPage1(page) {
  await page.click('input[name="q2.c"][value="yes"]');
  await page.fill('input[name="q3.number"]', '2');
  await page.clickText('Next', '.vote-submit-bar button');
  await onPage(page, 2);
}

export async function run({ app, browser, state, check, tolerate, defer }) {
  // ---- the poll ----
  const admin = state.admin;
  await admin.gotoInteractive('/polls');
  await admin.eval(`localStorage.setItem('av-draft:' + document.body.dataset.avUser + ':new', ${JSON.stringify(JSON.stringify(survey))}), true`);
  await admin.gotoInteractive('/polls/new');
  await admin.waitFor(`document.querySelector('.editor-form input')?.value === ${JSON.stringify(survey.title)} || [...document.querySelectorAll('.editor-form input')].some(i => i.value === ${JSON.stringify(survey.title)})`,
    'the restored draft in the editor');
  await admin.interactive();
  await admin.clickText('Create poll', 'button');
  await admin.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page after Create poll', 20_000);
  const id = (await admin.eval('location.pathname')).split('/')[2];
  state.polls.pages = id;

  // ---- with JS: the same form, enhanced ----
  const jsContext = await browser.newUserContext();
  defer(() => browser.removeUserContext(jsContext));
  const js = await browser.newPage({ base: app.base, userContext: jsContext });
  defer(() => js.close());
  await js.goto(`/p/${id}`);
  await onPage(js, 1);
  await js.waitFor(`!document.querySelector('[data-rank-board]').hidden`, 'the ranking board');
  await js.click('[data-zone="pool"] [data-key="salad"] [data-action="rank"]');
  check('JS: the board writes the prefixed field', await js.eval(`document.querySelector('[data-field="q1.o.salad"]').value === '1'`),
    await js.eval(`document.querySelector('[data-field="q1.o.salad"]').value`));
  check('JS: the tally is the question\'s own', (await js.eval(`document.querySelector('[data-ballot] [data-tally]')?.textContent ?? ''`)).startsWith('1 ranked'));
  await fillPage1(js);
  await js.click('input[name="q4.c"][value="fish"]');
  await formAge(js);
  await js.clickText('Submit ballot', '.vote-submit-bar button');
  await saved(js);
  check('JS: the summary shows the ranking', (await js.eval('document.body.innerText')).includes('Salad'));

  // ---- signed in: page 1, leave, resume the draft ----
  await admin.goto(`/p/${id}`);
  await onPage(admin, 1);
  await admin.waitFor(`!document.querySelector('[data-rank-board]').hidden`, 'the ranking board');
  await fillPage1(admin);
  await admin.goto(`/p/${id}`);
  await admin.waitFor(`document.body.innerText.includes('Draft · saved')`, 'the draft line');
  check('draft: no fields until Continue', await admin.eval(`!document.querySelector('input[name="q2.c"]')`));
  await admin.clickText('Continue', 'a');
  await onPage(admin, 2);
  await admin.clickText('Back', '.vote-submit-bar button');
  await onPage(admin, 1);
  check('draft: page 1 comes back from the draft', await checked(admin, 'input[name="q2.c"][value="yes"]'));
  await admin.clickText('Next', '.vote-submit-bar button');
  await onPage(admin, 2);
  await formAge(admin);
  await admin.clickText('Submit ballot', '.vote-submit-bar button');
  await saved(admin);
  await admin.goto(`/p/${id}`);
  check('draft: gone after Submit', await admin.eval(`!document.body.innerText.includes('Draft · saved') && document.body.innerText.includes('You have already voted.')`));

  // ---- without JS: plain posts, the answers travel in the carry field ----
  // Firefox has no per-context switch for scripting (emulation.setScriptingEnabled is unknown to it), so every script
  // request of the app fails from here on: the page gets no module and no Blazor script, as with JS off. Last in the
  // flow, since the failed loads are tolerated from here to its end.
  const restore = await browser.failRequests(url => /^\/(js|lib|_framework)\//.test(new URL(url).pathname));
  defer(restore);
  tolerate(/\.js|module|script/i, 'the app\'s scripts are made to fail on purpose (no JS)');
  const plainContext = await browser.newUserContext();
  defer(() => browser.removeUserContext(plainContext));
  const plain = await browser.newPage({ base: app.base, userContext: plainContext });
  defer(() => plain.close());
  await plain.goto(`/p/${id}`);
  await onPage(plain, 1);
  check('no JS: the ranking is position fields', await plain.eval(`!!document.querySelector('input[name="q1.o.soup"]') && document.querySelector('[data-rank-board]').hidden && !document.querySelector('[data-ranked]').dataset.ready`));
  check('no JS: a carry field', await plain.eval(`!!document.querySelector('input[name="_carry"]')?.value`));
  await plain.fill('input[name="q1.o.stew"]', '1');
  await fillPage1(plain);
  check('no JS: page 2 has the name field and Submit', await plain.eval(`!!document.querySelector('input[name="name"]') && [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Submit ballot')`));
  await plain.clickText('Back', '.vote-submit-bar button');
  await onPage(plain, 1);
  check('no JS: Back keeps page 1', await checked(plain, 'input[name="q2.c"][value="yes"]')
    && await plain.eval(`document.querySelector('input[name="q1.o.stew"]').value === '1'`));
  await plain.clickText('Next', '.vote-submit-bar button');
  await onPage(plain, 2);
  await plain.click('input[name="q4.c"][value="vegan"]');
  await plain.fill('textarea[name="q5.text"]', 'No nuts, please');
  await formAge(plain);
  await plain.clickText('Submit ballot', '.vote-submit-bar button');
  await saved(plain);
  const plainSummary = await plain.eval('document.body.innerText');
  check('no JS: the summary shows every answer', ['Stew', 'Yes', '2 people', 'Vegan', 'No nuts, please'].every(t => plainSummary.includes(t)), plainSummary.slice(0, 600));

}
