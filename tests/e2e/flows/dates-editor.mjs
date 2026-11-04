// The dates calendar of the poll editor (DESIGN.md §27): mouse drag ranges, a touch long-press drag (and a swipe that
// selects nothing), Space and Shift+arrows, weekday headings, paged day settings, the clock picker (with an end before
// the start refused), the month / year / decade picker and a typed year, and a selection past the options limit refused.
// With E2E_SHOTS set (a directory), it also leaves screenshots of the calendar, the clock and the month picker at
// desktop and phone widths in light and dark.
import fs from 'node:fs';
import path from 'node:path';

export const name = 'dates-editor';

const day = iso => `.editor-cal-grid button[data-day="${iso}"]`;
const selected = page => page.eval(`[...document.querySelectorAll('.editor-cal-grid button[data-day][aria-pressed="true"]')].map(b => b.dataset.day)`);
const title = page => page.eval(`document.querySelector('.cal-nav-title').textContent.trim()`);
const countText = page => page.eval(`document.querySelector('.editor-days-count')?.textContent.trim() ?? ''`);
const SHIFT = '', RIGHT = '';

export async function run({ state, check }) {
  const page = state.admin;
  await page.gotoInteractive('/polls/new');
  await page.waitFor(`!!document.querySelector('.editor-form .editor-help-row button')`, 'the editor');
  await page.clickText('Dates', 'label');
  await page.waitFor(`!!document.querySelector('.editor-calendar')`, 'the calendar');
  await page.fill('.editor-form [id$="-f-title"]', 'Long dates (e2e)');
  const shown = await title(page);
  await page.click('.editor-calendar [aria-label="Next month"]');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() !== ${JSON.stringify(shown)}`, 'the next month');
  const prefix = await page.eval(`document.querySelector('.editor-cal-grid button[data-day]').dataset.day.slice(0, 8)`);
  const d = n => prefix + String(n).padStart(2, '0');

  // ---- mouse: drag selects, a drag from a selected day deselects -------------------------------------------------
  await page.pointerDrag(day(d(3)), day(d(12)));
  let s = await page.waitFor(`document.querySelectorAll('.editor-cal-grid button[aria-pressed="true"]').length === 10 && 'ok'`, 'ten days after the drag', 5000)
    .catch(() => null);
  check('mouse drag selects the range', s && JSON.stringify(await selected(page)) === JSON.stringify([3, 4, 5, 6, 7, 8, 9, 10, 11, 12].map(d)),
    JSON.stringify(await selected(page)));
  check('count line', (await countText(page)) === '10 days · 10 options', await countText(page));
  check('live count', (await page.eval(`document.querySelector('.editor-cal-foot .editor-count').textContent.trim()`)) === '10 / 100');
  await page.pointerDrag(day(d(7)), day(d(5)));
  await page.waitFor(`document.querySelectorAll('.editor-cal-grid button[aria-pressed="true"]').length === 7`, 'three days deselected', 5000).catch(() => { });
  check('drag from a selected day deselects (backwards too)', JSON.stringify(await selected(page)) === JSON.stringify([3, 4, 8, 9, 10, 11, 12].map(d)),
    JSON.stringify(await selected(page)));
  await page.click(day(d(14)));
  await page.waitFor(`document.querySelector('${day(d(14))}').getAttribute('aria-pressed') === 'true'`, 'a click to select one day', 5000).catch(() => { });
  check('a click still toggles one day', (await selected(page)).includes(d(14)));

  // ---- touch: a swipe scrolls past, a 200 ms hold then drag selects ---------------------------------------------
  const before = (await selected(page)).length;
  await page.pointerDrag(day(d(20)), day(d(22)), { pointerType: 'touch', moveMs: 150 });
  await new Promise(r => setTimeout(r, 600));
  check('touch swipe selects nothing', (await selected(page)).length === before, JSON.stringify(await selected(page)));
  await page.pointerDrag(day(d(20)), day(d(22)), { pointerType: 'touch', hold: 400 });
  await page.waitFor(`document.querySelector('${day(d(22))}').getAttribute('aria-pressed') === 'true'`, 'the touch range', 5000).catch(() => { });
  const touched = await selected(page);
  check('touch hold then drag selects the range', [20, 21, 22].every(n => touched.includes(d(n))), JSON.stringify(touched));

  // ---- keyboard: Space toggles, Shift+arrows extend -------------------------------------------------------------
  await page.eval(`document.querySelector('${day(d(25))}').focus(), true`);
  await page.keys(' ');
  await page.waitFor(`document.querySelector('${day(d(25))}').getAttribute('aria-pressed') === 'true'`, 'Space selects', 5000).catch(() => { });
  await page.keyActions([{ type: 'keyDown', value: SHIFT }, { type: 'keyDown', value: RIGHT }, { type: 'keyUp', value: RIGHT },
    { type: 'keyDown', value: RIGHT }, { type: 'keyUp', value: RIGHT }, { type: 'keyUp', value: SHIFT }]);
  await page.waitFor(`document.querySelector('${day(d(27))}').getAttribute('aria-pressed') === 'true'`, 'Shift+arrows extend', 5000).catch(() => { });
  const keyed = await selected(page);
  check('Space and Shift+arrows select a range', [25, 26, 27].every(n => keyed.includes(d(n))), JSON.stringify(keyed));
  check('focus moved with the arrows', (await page.eval(`document.activeElement?.dataset?.day ?? ''`)) === d(27), await page.eval(`document.activeElement?.outerHTML.slice(0, 120)`));

  // ---- weekday heading ------------------------------------------------------------------------------------------
  await page.click('.editor-cal-wd[aria-label^="Every Monday"]');
  await page.waitFor(`document.querySelector('.editor-cal-wd[aria-label^="Every Monday"]').getAttribute('aria-pressed') === 'true'`, 'every Monday', 5000).catch(() => { });
  const mondays = await page.eval(`[...document.querySelectorAll('.editor-cal-grid button[data-day]')].filter(b => new Date(b.dataset.day + 'T12:00:00Z').getUTCDay() === 1)
    .every(b => b.getAttribute('aria-pressed') === 'true')`);
  check('weekday heading selects every such day', mondays);

  // ---- paging ---------------------------------------------------------------------------------------------------
  const days = (await selected(page)).length;
  check('more than ten days', days > 10, String(days));
  const firstLabel = () => page.eval(`document.querySelector('.editor-day .fw-semibold').textContent.trim()`);
  const page1 = await firstLabel();
  check('ten days per page', (await page.eval(`document.querySelectorAll('.editor-days > .editor-day').length`)) === 10);
  await page.click('.editor-pager [aria-label="Page 2"]');
  await page.waitFor(`document.querySelector('.editor-pager [aria-current="page"]')?.textContent.trim() === '2'`, 'page 2', 5000).catch(() => { });
  check('page 2 shows the later days', (await firstLabel()) !== page1 && (await page.eval(`document.querySelectorAll('.editor-days > .editor-day').length`)) === days - 10,
    `${page1} / ${await firstLabel()}`);
  await page.click('.editor-pager [aria-label="Page 1"]');
  await page.waitFor(`document.querySelector('.editor-pager [aria-current="page"]')?.textContent.trim() === '1'`, 'page 1', 5000).catch(() => { });

  // ---- clock picker -----------------------------------------------------------------------------------------------
  const firstSlots = () => page.eval(`[...document.querySelector('.editor-day').querySelectorAll('.editor-slot')].map(e => e.textContent.trim()).join('|')`);
  await page.click('.editor-day .clock-toggle');
  await page.waitFor(`!!document.querySelector('.clock-picker')`, 'the clock', 5000);
  check('the clock opens on the hours (24)', (await page.eval(`document.querySelectorAll('.clock-picker .clock-num').length`)) === 24);
  await page.click('.clock-picker [aria-label="18 hours"]');
  await page.waitFor(`document.querySelectorAll('.clock-picker .clock-num').length === 12`, 'the minutes', 5000);
  await page.click('.clock-picker [aria-label="30 minutes"]');
  await page.waitFor(`document.querySelector('.clock-prompt').textContent.includes('End')`, 'the end', 5000);
  await page.click('.clock-picker [aria-label="21 hours"]');
  await page.waitFor(`document.querySelectorAll('.clock-picker .clock-num').length === 12`, 'the end minutes', 5000);
  await page.click('.clock-picker [aria-label="00 minutes"]');
  await page.waitFor(`!document.querySelector('.clock-picker')`, 'the clock to close', 5000).catch(() => { });
  check('clock adds a slot', (await firstSlots()).includes('18:30–21:00'), await firstSlots());

  await page.click('.editor-day .clock-toggle');
  await page.waitFor(`!!document.querySelector('.clock-picker')`, 'the clock again', 5000);
  for (const label of ['20 hours', '00 minutes', '19 hours', '00 minutes']) {
    await page.click(`.clock-picker [aria-label="${label}"]`);
    await new Promise(r => setTimeout(r, 150));
  }
  const refused = await page.waitFor(`document.querySelector('.clock-picker [role="alert"]')?.textContent.trim()`, 'the refusal', 5000).catch(() => '');
  check('an end before the start is refused', refused === 'Pick an end after 20:00.', refused);
  await page.clickText('Skip end', '.clock-picker button');
  await page.waitFor(`!document.querySelector('.clock-picker')`, 'the clock to close', 5000).catch(() => { });
  check('Skip end adds a start time', (await firstSlots()).split('|').includes('20:00'), await firstSlots());

  // ---- screenshots (calendar, clock, month picker; desktop / phone; light / dark) --------------------------------
  if (process.env.E2E_SHOTS) await screenshots(page, process.env.E2E_SHOTS, check);

  // ---- month / year / decade / century picker and a typed year ------------------------------------------------
  await page.click('[aria-label="Choose month"]');
  await page.waitFor(`document.querySelectorAll('.cal-nav-grid .cal-nav-item').length === 12`, 'the months', 5000);
  await page.click('[aria-label="Choose year"]');
  await page.waitFor(`document.querySelectorAll('.cal-nav-grid .cal-nav-item').length === 10`, 'the years', 5000);
  await page.click('[aria-label="Choose decade"]');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === '2000–2099'`, 'the century', 5000);
  check('no zoom past the century', !(await page.eval(`!!document.querySelector('.cal-nav-zoom')`)));
  await page.clickText('2030s', '.cal-nav-item');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === '2030–2039'`, 'the 2030s', 5000);
  await page.clickText('2031', '.cal-nav-item');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === '2031'`, '2031', 5000);
  await page.click('.cal-nav-item[aria-label="March 2031"]');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === 'March 2031'`, 'March 2031', 5000).catch(() => { });
  check('picking zooms back in to the month', (await title(page)) === 'March 2031', await title(page));

  await page.click('[aria-label="Choose month"]');
  await page.waitFor(`!!document.querySelector('.cal-nav-year-input')`, 'the year field', 5000);
  await page.fill('.cal-nav-year-input', '1');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === '0001'`, 'year 1', 5000).catch(() => { });
  await page.click('.cal-nav-item[aria-label="January 0001"]');
  await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() === 'January 0001'`, 'January 0001', 5000).catch(() => { });
  check('a typed year opens it', (await title(page)) === 'January 0001', await title(page));
  check('no month before January 0001', await page.eval(`document.querySelector('.editor-calendar [aria-label="Previous month"]').disabled`));
  await page.click(day('0001-01-01'));
  await page.waitFor(`document.querySelector('${day('0001-01-01')}').getAttribute('aria-pressed') === 'true'`, '0001-01-01 selected', 5000).catch(() => { });
  check('0001-01-01 can be selected', (await selected(page)).includes('0001-01-01'));

  // ---- past the options limit: refused, saying how many fit -------------------------------------------------------
  await page.clickText('Text', 'label');
  await page.waitFor(`!!document.querySelector('textarea.editor-options-text')`, 'the dates text');
  await page.fill('textarea.editor-options-text', '- 2031-01-01 to 2031-04-05\n');
  await page.clickText('Calendar', 'label');
  await page.waitFor(`!!document.querySelector('.editor-calendar')`, 'the calendar again');
  check('95 days from the text', (await countText(page)) === '95 days · 95 options', await countText(page));
  for (let i = 0; i < 4; i++) {
    const t = await title(page);
    await page.click('.editor-calendar [aria-label="Next month"]');
    await page.waitFor(`document.querySelector('.cal-nav-title').textContent.trim() !== ${JSON.stringify(t)}`, 'the next month', 5000);
  }
  check('May 2031', (await title(page)) === 'May 2031', await title(page));
  await page.pointerDrag(day('2031-05-01'), day('2031-05-10'));
  const over = await page.waitFor(`document.querySelector('.editor-calendar [role="alert"]')?.textContent.trim()`, 'the refusal', 5000).catch(() => '');
  check('a drag past the limit is refused, saying how many fit', over === 'Select at most 5 more days: at most 100 options.', over);
  check('nothing was added', (await page.eval(`document.querySelector('.editor-cal-foot .editor-count').textContent.trim()`)) === '95 / 100');

  await page.clickText('Create poll', 'button');
  await page.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page after Create poll', 20_000);
  await page.interactive();
  check('the poll with 95 days is created', true);
  check('editor circuit alive', await page.alive());
}

async function screenshots(page, dir, check) {
  // A narrow viewport settles its layout after the resize: a click may land while the element still moves.
  const click = async selector => {
    try { await page.click(selector); } catch (e) {
      if (!/out of bounds/.test(e.message)) throw e;
      await new Promise(r => setTimeout(r, 500));
      await page.click(selector);
    }
  };
  fs.mkdirSync(dir, { recursive: true });
  const files = [];
  // The visitor's choice as the theme switch stores it (the switch re-syncs <html> from the cookie on every click).
  const theme = mode => page.eval(`(() => { document.cookie = 'av-theme=${mode}; Path=/; SameSite=Lax';
    const r = document.documentElement; r.setAttribute('data-bs-theme', '${mode}'); r.setAttribute('data-av-theme', '${mode}'); return true; })()`);
  try {
    for (const mode of ['light', 'dark']) {
      await theme(mode);
      for (const [label, width] of [['desktop', 1280], ['phone', 390]]) {
        await page.viewport(width, width > 600 ? 900 : 844);
        await new Promise(r => setTimeout(r, 300));
        const shot = async (what, selector) => files.push(await page.screenshotElement(selector, path.join(dir, `${what}-${label}-${mode}.png`)));
        // The shell scrolls its own main element under a sticky header: centre the calendar instead of scrolling the window.
        await page.eval(`document.querySelector('.editor-calendar').scrollIntoView({ block: 'center', behavior: 'instant' }), true`);
        const view = path.join(dir, `calendar-${label}-${mode}.png`);
        const r = await page.browser.send('browsingContext.captureScreenshot', { context: page.ctx, origin: 'viewport' });
        fs.writeFileSync(view, Buffer.from(r.data, 'base64'));
        files.push(view);
        await click('.editor-day .clock-toggle');
        await page.waitFor(`!!document.querySelector('.clock-picker')`, 'the clock', 5000);
        await click('.clock-picker [aria-label="19 hours"]');
        await page.waitFor(`document.querySelectorAll('.clock-picker .clock-num').length === 12`, 'the minutes', 5000);
        await shot('clock-minutes', '.editor-day:has(.clock-picker)');
        await click('.clock-picker .clock-part');
        await page.waitFor(`document.querySelectorAll('.clock-picker .clock-num').length === 24`, 'the hours', 5000);
        await shot('clock-hours', '.editor-day:has(.clock-picker)');
        await click(await page.byText('Cancel', '.clock-picker button'));
        await page.waitFor(`!document.querySelector('.clock-picker')`, 'the clock to close', 5000);
        await click('[aria-label="Choose month"]');
        await page.waitFor(`document.querySelectorAll('.cal-nav-grid .cal-nav-item').length === 12`, 'the months', 5000);
        await shot('months', '.editor-calendar');
        await click('[aria-label="Choose year"]');
        await page.waitFor(`document.querySelectorAll('.cal-nav-grid .cal-nav-item').length === 10`, 'the years', 5000);
        await shot('years', '.editor-calendar');
        await page.keys(''); // Escape: back to the days
        await page.waitFor(`!document.querySelector('.cal-nav-grid')`, 'the days again', 5000);
      }
    }
  } finally {
    await page.eval(`(() => { document.cookie = 'av-theme=; Path=/; Max-Age=0; SameSite=Lax';
      document.documentElement.removeAttribute('data-bs-theme'); document.documentElement.removeAttribute('data-av-theme'); return true; })()`);
    await page.viewport(1280, 900);
  }
  check('screenshots written', files.length === 20, files.join('\n'));
}
