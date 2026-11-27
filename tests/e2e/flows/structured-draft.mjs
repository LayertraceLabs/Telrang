// Restoring a structured browser draft on the edit page (DESIGN.md §20, §34.5): the owner edits the structured-dates
// flow's poll after its ballot (renames a question, adds a day in the dates question's calendar, adds a question), leaves
// without saving, comes back to "Draft restored." with every change in the outline and the calendar, saves, and the vote
// page (change log, calendar) and the results show them. The draft lives in this browser only (`draft:<userId>:<pollId>`) and is gone after
// the save.
export const name = 'structured-draft';

const field = f => `.editor-form [id$="-f-${f.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}"]`;
const labels = page => page.eval(`[...document.querySelectorAll('.editor-outline .outline-label')].map(l => l.textContent.trim())`);
const pressed = page => page.eval(`[...document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]')].map(b => b.dataset.day)`);

async function outline(page, label) {
  const ok = await page.eval(`(() => { document.querySelectorAll('[data-e2e-target]').forEach(e => e.removeAttribute('data-e2e-target'));
    const b = [...document.querySelectorAll('.editor-outline .outline-item')].find(b => b.querySelector('.outline-label')?.textContent.trim() === ${JSON.stringify(label)});
    b?.setAttribute('data-e2e-target', ''); return !!b; })()`);
  if (!ok) throw new Error(`no outline item "${label}": ${(await labels(page)).join(' | ')}`);
  await page.click('[data-e2e-target]');
  await page.waitFor(`document.querySelector('.editor-outline .outline-item.selected .outline-label')?.textContent.trim() === ${JSON.stringify(label)}`, `"${label}" selected`);
}

async function options(page, values) {
  for (const [i, v] of values.entries()) {
    if (i >= await page.eval(`document.querySelectorAll('.editor-option-main input').length`)) {
      await page.clickText('Add option', 'button');
      await page.waitFor(`document.querySelectorAll('.editor-option-main input').length > ${i}`, 'a new option row');
    }
    await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  }
}

async function openEditor(page, id) {
  await page.gotoInteractive(`/polls/${id}/edit`);
  await page.waitFor(`!!document.querySelector('.editor-outline')`, 'the structured editor on edit');
}

export async function run({ state, check }) {
  const page = state.admin;
  const id = state.polls.structuredDates;
  if (!id) throw new Error('needs the poll of the structured-dates flow');
  // The browser draft of this poll: draft.js keeps it under "av-" + the editor's key.
  const stored = `(() => { const k = Object.keys(localStorage).find(k => /^av-draft:[^:]+:${id}$/.test(k)); return k ? localStorage.getItem(k) : null; })()`;

  // ---- edit without saving ----
  await openEditor(page, id);
  check('edit: no draft at first', await page.eval(`!document.querySelector('.draft-notice') && ${stored} === null`));
  await outline(page, 'Bring');
  await page.fill(field('questions[2].prompt'), 'Bring along');
  await page.waitFor(`[...document.querySelectorAll('.outline-label')].some(l => l.textContent.trim() === 'Bring along')`, 'the question renamed');
  await outline(page, 'Days');
  await page.waitFor(`document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]').length === 4`, 'the stored days in the calendar');
  const days = await pressed(page);
  const added = days[3].slice(0, 8) + String(Number(days[3].slice(8)) + 1).padStart(2, '0');
  await page.click(`.editor-cal-grid button[data-day="${added}"]`);
  await page.waitFor(`document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]').length === 5`, 'a fifth day', 5000);
  await outline(page, 'Bring along');
  await page.clickText('Add question', '.editor-outline .editor-addbar button');
  await page.waitFor(`document.querySelector('.editor-item-place')?.textContent.trim() === 'Page 2 · Question 4 of 4'`, 'the new question');
  await page.fill(field('questions[3].prompt'), 'Car seats');
  await options(page, ['None', 'Two']);
  await page.waitFor(`(${stored} ?? '').includes('Car seats') && (${stored} ?? '').includes('Two') && (${stored} ?? '').includes('Bring along')`, 'the browser draft written', 5000);

  // ---- leave without saving (beforeunload is accepted), come back ----
  await page.gotoInteractive('/polls');
  check('left: the draft stays in the browser', await page.eval(`(${stored} ?? '').includes('Car seats')`));
  await openEditor(page, id);
  await page.waitFor(`document.querySelector('.draft-notice')?.textContent.includes('Draft restored.')`, 'the restored draft notice')
    .catch(async e => { throw new Error(`${e.message}: ${(await labels(page)).join(' | ')}`); });
  const restored = await labels(page);
  check('restored: the outline has the rename and the new question', restored.includes('Bring along') && restored.includes('Car seats') && !restored.includes('Bring'),
    restored.join(' | '));
  await outline(page, 'Car seats');
  check('restored: the new question\'s options', JSON.stringify(await page.eval(`[...document.querySelectorAll('.editor-option-main input')].map(i => i.value)`)) === '["None","Two"]');
  await outline(page, 'Days');
  await page.waitFor(`document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]').length === 5`, 'the restored days', 5000).catch(() => { });
  const restoredDays = await pressed(page);
  check('restored: the calendar has the added day', JSON.stringify(restoredDays) === JSON.stringify([...days, added]), JSON.stringify(restoredDays));
  await page.waitFor(`document.querySelectorAll('.change-preview li').length === 3`, 'the change preview').catch(() => { });
  const preview = await page.eval(`[...document.querySelectorAll('.change-preview li')].map(li => li.textContent.trim())`);
  check('restored: the change preview', preview.length === 3 && preview.includes('Question “Bring” renamed to “Bring along”')
    && preview.includes('Question “Car seats” added') && preview.some(l => l.startsWith('Days: ') && l.includes('added')), JSON.stringify(preview));

  // ---- save: the draft is gone, the vote page shows the changes ----
  await page.interactive();
  await page.clickText('Save changes', 'button');
  await page.waitFor(`[...document.querySelectorAll('.alert-success')].some(a => a.textContent.includes('Saved.'))`, 'Saved.')
    .catch(async e => { throw new Error(`${e.message}: ${await page.eval(`document.querySelector('.editor-form .alert-danger')?.innerText ?? ''`)}`); });
  check('saved: notice and browser draft gone', await page.eval(`!document.querySelector('.draft-notice') && ${stored} === null`));
  check('saved: circuit alive', await page.alive());
  await openEditor(page, id);
  check('reopened: nothing to restore', await page.eval(`!document.querySelector('.draft-notice')`) && (await labels(page)).includes('Car seats'));

  await page.goto(`/p/${id}`);
  const log = await page.eval(`[...document.querySelectorAll('.change-log li')].map(li => li.textContent.trim())`);
  check('vote page: the change log', preview.every(line => log.some(l => l.startsWith(line))), JSON.stringify(log));
  check('vote page: the added day in the calendar', await page.eval(`document.querySelectorAll('[data-panel="calendar"] [data-dates-cell]').length === 5`));
  await page.goto(`/p/${id}/results`);
  await page.waitFor(`document.readyState === 'complete' && !!document.querySelector('.results-question')`, 'the results sections');
  const sections = await page.eval(`[...document.querySelectorAll('.results-question h3, .results-question h4')].map(h => h.textContent.trim())`);
  check('results: the renamed and the new question', JSON.stringify(sections) === JSON.stringify(['Venue', 'Days', 'Bring along', 'Car seats']), JSON.stringify(sections));
}
