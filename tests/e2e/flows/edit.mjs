// Editing a poll after voting started (DESIGN.md §26): the fixed settings are read-only, option changes keep the ballots,
// the editor previews what the change log will record, and the vote page shows it.
export const name = 'edit-after-vote';

export async function run({ state, check }) {
  const page = state.admin;
  const id = state.polls.ranked;

  await page.gotoInteractive(`/polls/${id}/edit`);
  await page.waitFor(`!!document.querySelector('.editor-form .editor-option-main input')`, 'the editor');
  const text = () => page.eval(`document.querySelector('.editor-form').innerText`);
  check('fixed settings are read-only', await page.eval(`!!document.querySelector('.editor-fixed-note .icon') && !document.querySelector('.editor-form [aria-label="Poll type"]')`),
    (await text()).slice(0, 400));
  check('fixed note shown', (await text()).includes('Fixed when the poll was created.'));

  // Rename one option, remove the one the voter ranked (vote flow: noodle bar and pizza place).
  const labels = await page.eval(`[...document.querySelectorAll('.editor-option-main input')].map(i => i.value)`);
  const salad = labels.indexOf('Salad shop') + 1;
  await page.fill(`.editor-option:nth-child(${salad}) .editor-option-main input`, 'Salad bar');
  await page.click(`[aria-label="Remove option ${labels.indexOf('Pizza place') + 1}"]`);
  await page.waitFor(`document.querySelectorAll('.change-preview li').length === 2`, 'the change preview');
  const preview = await page.eval(`[...document.querySelectorAll('.change-preview li')].map(li => li.textContent.trim())`);
  check('preview lists the removal with its ballots', preview.includes('“Pizza place” removed (on 1 ballot)'), JSON.stringify(preview));
  check('preview lists the rename', preview.includes('“Salad shop” renamed to “Salad bar”'), JSON.stringify(preview));

  await page.clickText('Save changes', 'button');
  await page.waitFor(`[...document.querySelectorAll('.alert-success')].some(a => a.textContent.includes('Saved.'))`, 'Saved.');
  check('saved without a confirmation, preview cleared', await page.eval(`!document.querySelector('.change-preview')`));
  check('editor circuit alive', await page.alive());

  // The vote page shows the log to everyone; the ballot still counts.
  await page.goto(`/p/${id}`);
  const log = await page.eval(`[...document.querySelectorAll('.change-log li')].map(li => li.textContent.trim())`);
  check('vote page shows the change log', await page.eval(`document.querySelector('.change-log h2')?.textContent.trim() === 'Changed after voting started'`)
    && log.some(l => l.startsWith('“Pizza place” removed (on 1 ballot)')) && log.some(l => l.startsWith('“Salad shop” renamed to “Salad bar”')), JSON.stringify(log));
  await page.goto(`/p/${id}/results`);
  check('results still count the ballot', await page.eval(`document.body.innerText.includes('1 voter')`), await page.eval('document.body.innerText.slice(0, 400)'));
  check('results page shows the change log', await page.eval(`document.querySelectorAll('.change-log li').length === 2`));
}
