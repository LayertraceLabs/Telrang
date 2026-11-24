// The structured editor (DESIGN.md §34.1, §34.5): Add group / Add page turn the simple editor into the outline, a poll with
// two pages, a group and choice, text and number questions is built with the outline and the one question editor, the
// preview tab shows it page by page, and after a ballot an edit renames a question, removes one and adds one: the editor
// previews the change log lines and the vote page shows them. `buildSurvey` also builds the vote-pages flow's poll.
// With E2E_SHOTS set (a directory), it leaves screenshots of the simple and the structured editor at desktop and phone
// widths in light and dark.
import fs from 'node:fs';
import path from 'node:path';
import { formAge } from './setup.mjs';

export const name = 'structured-editor';

const field = f => `.editor-form [id$="-f-${f.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}"]`;

/** Clicks the outline item whose label is `label` (a question's text, "1 · Attending", "Group · You"). */
async function outline(page, label) {
  const ok = await page.eval(`(() => { document.querySelectorAll('[data-e2e-target]').forEach(e => e.removeAttribute('data-e2e-target'));
    const b = [...document.querySelectorAll('.editor-outline .outline-item')].find(b => b.querySelector('.outline-label')?.textContent.trim() === ${JSON.stringify(label)});
    b?.setAttribute('data-e2e-target', ''); return !!b; })()`);
  if (!ok) throw new Error(`no outline item "${label}": ${await page.eval(`[...document.querySelectorAll('.outline-label')].map(l => l.textContent.trim()).join(' | ')`)}`);
  await page.click('[data-e2e-target]');
  await page.waitFor(`document.querySelector('.editor-outline .outline-item.selected .outline-label')?.textContent.trim() === ${JSON.stringify(label)}`, `"${label}" selected`);
}

/** The selected question's place line ("Page 1 · Group: You · Question 2 of 3"). */
const place = page => page.eval(`document.querySelector('.editor-item-place')?.textContent.trim() ?? ''`);

async function waitPlace(page, text) {
  await page.waitFor(`document.querySelector('.editor-item-place')?.textContent.trim() === ${JSON.stringify(text)}`, `the item "${text}"`)
    .catch(async e => { throw new Error(`${e.message}: ${await place(page)}`); });
}

/** Fills the options of the question shown (the list rows of its editor), adding rows as needed. */
async function options(page, labels) {
  for (const [i, v] of labels.entries()) {
    if (i >= await page.eval(`document.querySelectorAll('.editor-option-main input').length`)) {
      await page.clickText('Add option', 'button');
      await page.waitFor(`document.querySelectorAll('.editor-option-main input').length > ${i}`, 'a new option row');
    }
    await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  }
}

async function type(page, label, group = 'Question type') {
  await page.clickText(label, `[aria-label="${group}"] label`);
}

/**
 * Builds, in a new poll's editor, the survey of the vote-pages flow: page "Attending" with Dishes (ranked: Soup, Salad,
 * Stew) and the group "You" (Coming?: a required choice Yes / No; Guests: a number 0 to 5 people), page "Food" with Diet
 * (checkboxes, at most 2, Vegan / Fish, Other) and Notes (text, several lines). Results for the owner only. Returns the
 * editor at the point before Create poll.
 */
export async function buildSurvey(page, title, { check = () => { } } = {}) {
  await page.gotoInteractive('/polls/new');
  await page.waitFor(`!!document.querySelector('.editor-form .editor-help-row button')`, 'the editor');
  await page.fill(field('title'), title);
  await options(page, ['Soup', 'Salad', 'Stew']);
  check('simple editor: the add bar', await page.eval(`['Add question', 'Add group', 'Add page'].every(t => [...document.querySelectorAll('.editor-addbar button')].some(b => b.textContent.trim() === t))`));

  // Add group: the structured editor, a group after the question, its new question selected.
  await page.clickText('Add group', '.editor-addbar button');
  await page.waitFor(`!!document.querySelector('.editor-outline')`, 'the outline');
  await waitPlace(page, 'Group: Group 1 · Question 2 of 2');
  check('add group: the first question takes the title', await page.eval(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === ${JSON.stringify(title)})`));
  check('add group: the tabs', await page.eval(`[...document.querySelectorAll('[role=tab]')].map(t => t.textContent.trim()).join('|') === 'Questions|Voting and results|Look|Preview'`));
  await page.fill(field('questions[1].prompt'), 'Coming?');
  await page.click(field('questions[1].required'));
  await options(page, ['Yes', 'No']);

  await page.clickText('Add question', '.editor-outline .editor-addbar button');
  await waitPlace(page, 'Group: Group 1 · Question 3 of 3');
  await type(page, 'Number');
  await page.waitFor(`!!document.querySelector('${field('questions[2].unit')}')`, 'the number settings');
  await page.fill(field('questions[2].prompt'), 'Guests');
  await page.fill(field('questions[2].min'), '0');
  await page.fill(field('questions[2].max'), '5');
  await page.fill(field('questions[2].unit'), 'people');

  await outline(page, 'Group · Group 1');
  await page.fill(field('groups[0].title'), 'You');
  await page.waitFor(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Group · You')`, 'the group renamed');

  // Add page: page 1 takes the questions so far, page 2 its new question.
  await page.clickText('Add page', '.editor-outline .editor-addbar button');
  await waitPlace(page, 'Page 2 · Question 4 of 4');
  await type(page, 'Checkboxes');
  await page.waitFor(`document.querySelector('${field('questions[3].maxSelect')}')?.value === '2'`, 'checkboxes: most picks = the options');
  await page.fill(field('questions[3].prompt'), 'Diet');
  await options(page, ['Vegan', 'Fish']);
  await page.click(field('questions[3].allowOther'));

  await page.clickText('Add question', '.editor-outline .editor-addbar button');
  await waitPlace(page, 'Page 2 · Question 5 of 5');
  await type(page, 'Text');
  await page.waitFor(`!!document.querySelector('${field('questions[4].multiline')}')`, 'the text settings');
  await page.fill(field('questions[4].prompt'), 'Notes');
  await page.click(field('questions[4].multiline'));

  for (const [label, value] of [['1 · Page 1', 'Attending'], ['2 · Page 2', 'Food']]) {
    await outline(page, label);
    await page.fill(field(`pages[${label[0] - 1}].title`), value);
  }
  await outline(page, title);
  await page.fill(field('questions[0].prompt'), 'Dishes');
  await page.waitFor(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Dishes')`, 'the question renamed');

  // Voting and results: the owner only (the vote-pages flow reads the results as the owner).
  await page.click('[role=tab][id$="-tab-Voting"]');
  await page.waitFor(`!!document.querySelector('.editor-form label[for$="-results-0"]')`, 'the voting tab');
  await page.click('.editor-form label[for$="-results-0"]');
  await page.waitFor(`document.querySelector('.editor-form [id$="-results-0"]').checked`, 'results for the owner only');
}

async function create(page) {
  await page.interactive();
  await page.clickText('Create poll', 'button');
  await page.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page after Create poll', 20_000)
    .catch(async e => { throw new Error(`${e.message}: ${await page.eval(`document.querySelector('.editor-form .alert-danger')?.innerText ?? ''`)}`); });
  await page.interactive();
  return (await page.eval('location.pathname')).split('/')[2];
}

export async function run({ app, browser, state, check, defer }) {
  const page = state.admin;
  if (process.env.E2E_SHOTS) {
    await page.gotoInteractive('/polls/new');
    await page.waitFor(`!!document.querySelector('.editor-addbar')`, 'the simple editor');
    await screenshots(page, process.env.E2E_SHOTS, check, 'simple', '.editor-addbar');
  }
  await buildSurvey(page, 'Club evening (e2e)', { check });

  // Preview tab: page by page, through the vote page's own fields.
  await page.click('[role=tab][id$="-tab-Preview"]');
  await page.waitFor(`!!document.querySelector('.editor-preview-card .vote-group')`, 'the preview of page 1');
  check('preview: page 1 with the group and its fields', await page.eval(`document.querySelector('.editor-preview-card .vote-group legend')?.textContent.trim() === 'You'
    && !!document.querySelector('.editor-preview-card input[type=number]') && document.querySelectorAll('.preview-pages button').length === 2`));
  await page.clickText('2 · Food', '.preview-pages button');
  await page.waitFor(`!!document.querySelector('.editor-preview-card textarea.text-answer')`, 'the preview of page 2');
  check('preview: page 2 with checkboxes and Other', await page.eval(`document.querySelectorAll('.editor-preview-card input[type=checkbox]').length === 3`));
  check('size line', (await page.eval(`document.querySelector('.editor-size').textContent.trim()`)) === '2 pages · 5 questions');

  // Moves: Guests up (swaps with Coming? in the group), then back down.
  await page.click('[role=tab][id$="-tab-Questions"]');
  await page.waitFor(`!!document.querySelector('.editor-outline')`, 'the outline again');
  await outline(page, 'Guests');
  await page.click('[aria-label="Move question up"]');
  await waitPlace(page, 'Page 1 · Group: You · Question 2 of 5');
  await page.click('[aria-label="Move question down"]');
  await waitPlace(page, 'Page 1 · Group: You · Question 3 of 5');

  if (process.env.E2E_SHOTS) await screenshots(page, process.env.E2E_SHOTS, check, 'structured', '.editor-form');

  const id = await create(page);
  check('structured poll created', !!id);
  check('share page: the size line', (await page.eval(`document.querySelector('.mode-line').textContent.trim()`)).startsWith('2 pages · 5 questions'));

  // ---- a ballot (Notes answered) ----
  const ctx = await browser.newUserContext();
  defer(() => browser.removeUserContext(ctx));
  const voter = await browser.newPage({ base: app.base, userContext: ctx });
  defer(() => voter.close());
  await voter.goto(`/p/${id}`);
  await voter.waitFor(`!!document.querySelector('input[name="q2.c"]')`, 'page 1');
  await voter.click('input[name="q2.c"][value="yes"]');
  await voter.clickText('Next', '.vote-submit-bar button');
  await voter.waitFor(`!!document.querySelector('textarea[name="q5.text"]')`, 'page 2');
  await voter.fill('textarea[name="q5.text"]', 'See you');
  await formAge(voter);
  await voter.clickText('Submit ballot', '.vote-submit-bar button');
  await voter.waitFor(`location.search === '?saved=1'`, 'the saved response', 15_000);

  // ---- edit after the ballot: rename, remove, add ----
  await page.gotoInteractive(`/polls/${id}/edit`);
  await page.waitFor(`!!document.querySelector('.editor-outline')`, 'the structured editor on edit');
  check('edit: a stored question is fixed', await page.eval(`!!document.querySelector('.editor-item .editor-fixed-note') && !document.querySelector('.editor-item [aria-label="Question type"]')`));
  await outline(page, 'Guests');
  await page.fill(field('questions[2].prompt'), 'Guests coming');
  await outline(page, 'Notes');
  await page.click('[aria-label="Remove question"]');
  await page.waitFor(`![...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Notes')`, 'Notes removed');
  await outline(page, 'Diet');
  await page.clickText('Add question', '.editor-outline .editor-addbar button');
  await waitPlace(page, 'Page 2 · Question 5 of 5');
  check('edit: a new question is free', await page.eval(`!!document.querySelector('.editor-item [aria-label="Question type"]')`));
  await page.fill(field('questions[4].prompt'), 'Shirt size');
  await options(page, ['S', 'M']);
  const expected = ['Question “Notes” removed (answered on 1 ballot)', 'Question “Guests” renamed to “Guests coming”', 'Question “Shirt size” added'];
  await page.waitFor(`document.querySelectorAll('.change-preview li').length === 3`, 'the change preview').catch(() => { });
  const preview = await page.eval(`[...document.querySelectorAll('.change-preview li')].map(li => li.textContent.trim())`);
  check('edit: the preview lists the question changes', JSON.stringify(preview) === JSON.stringify(expected), JSON.stringify(preview));
  await page.interactive();
  await page.clickText('Save changes', 'button');
  await page.waitFor(`[...document.querySelectorAll('.alert-success')].some(a => a.textContent.includes('Saved.'))`, 'Saved.')
    .catch(async e => { throw new Error(`${e.message}: ${await page.eval(`document.querySelector('.editor-form .alert-danger')?.innerText ?? ''`)}`); });
  check('edit: saved, the stored questions keep their keys', await page.eval(`!document.querySelector('.change-preview')`) && await page.alive());

  await page.goto(`/p/${id}`);
  const log = await page.eval(`[...document.querySelectorAll('.change-log li')].map(li => li.textContent.trim())`);
  check('vote page: the change log lines', expected.every(line => log.some(l => l.startsWith(line))), JSON.stringify(log));
  check('vote page: the new question, the removed one gone', await page.eval(`document.body.innerText.includes('Guests coming') && !document.querySelector('textarea[name="q5.text"]')`));
}

/** Viewport screenshots at desktop and phone widths in light and dark, `selector` scrolled to the top (`what`-width-mode.png). */
async function screenshots(page, dir, check, what, selector) {
  fs.mkdirSync(dir, { recursive: true });
  const files = [];
  const theme = mode => page.eval(`(() => { document.cookie = 'av-theme=${mode}; Path=/; SameSite=Lax';
    const r = document.documentElement; r.setAttribute('data-bs-theme', '${mode}'); r.setAttribute('data-av-theme', '${mode}'); return true; })()`);
  const shot = async name => {
    await page.eval(`document.querySelector('${selector}').scrollIntoView({ block: '${what === 'simple' ? 'center' : 'start'}', behavior: 'instant' }), true`);
    const file = path.join(dir, name + '.png');
    const r = await page.browser.send('browsingContext.captureScreenshot', { context: page.ctx, origin: 'viewport' });
    fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
    files.push(file);
  };
  try {
    for (const mode of ['light', 'dark']) {
      await theme(mode);
      for (const [label, width] of [['desktop', 1280], ['phone', 390]]) {
        await page.viewport(width, width > 600 ? 900 : 844);
        await new Promise(r => setTimeout(r, 400));
        await shot(`${what}-${label}-${mode}`);
        const wide = await page.eval(`document.documentElement.scrollWidth <= window.innerWidth + 1`);
        check(`${what} editor ${label} ${mode}: no horizontal scroll`, wide);
      }
    }
  } finally {
    await page.eval(`(() => { document.cookie = 'av-theme=; Path=/; Max-Age=0; SameSite=Lax';
      document.documentElement.removeAttribute('data-bs-theme'); document.documentElement.removeAttribute('data-av-theme'); return true; })()`);
    await page.viewport(1280, 900);
  }
  check(`${what} editor screenshots written`, files.length === 4, files.join('\n'));
}
