// Poll looks (DESIGN.md §34.6, §34.7): a look set in the editor reaches the vote page as /p/{id}/look.css, whose tokens
// differ from the instance theme in light and in dark; the look is kept as a saved look, exported as a look code and
// imported again, and imported from the poll link; with the admin setting "Poll looks" off the vote page links no look
// and the editor has no Look section. With E2E_SHOTS set (a directory), it leaves screenshots of the editor's Look
// section, /looks and the vote page with the look at desktop and phone widths in light and dark.
import fs from 'node:fs';
import path from 'node:path';

export const name = 'looks';

const LIGHT_BASE = '#5a2a5f', DARK_BASE = '#2a6f2b', TITLE = 'Plum evening (e2e)';

/** A token's value on <html> in a mode, with or without the poll's look sheet. */
async function token(page, mode, name, withLook = true) {
  return page.eval(`(() => { const r = document.documentElement; const was = r.getAttribute('data-bs-theme');
    const link = document.querySelector('link[data-poll-look]'); if (!${withLook} && link) link.disabled = true;
    r.setAttribute('data-bs-theme', '${mode}'); const v = getComputedStyle(r).getPropertyValue('${name}').trim();
    if (link) link.disabled = false; if (was === null) r.removeAttribute('data-bs-theme'); else r.setAttribute('data-bs-theme', was); return v; })()`);
}

async function setColour(page, selector, value) {
  await page.fill(selector, value);
  await page.waitFor(`document.querySelector(${JSON.stringify(selector)})?.parentElement.querySelector('.font-monospace')?.textContent.trim() === '${value}'`, `the colour ${value}`);
}

const listed = (page, name) => page.waitFor(`[...document.querySelectorAll('.look-list [data-look]')].some(e => e.getAttribute('data-look') === ${JSON.stringify(name)})`, `the look "${name}" listed`, 10_000);

export async function run({ app, browser, state, check, defer }) {
  const page = state.admin;

  // ---- the editor: a custom look, kept as a saved look ----
  await page.gotoInteractive('/polls/new');
  await page.waitFor(`!!document.querySelector('[id$="-look-pick"]')`, 'the Look section');
  await page.fill('.editor-form [id$="-f-title"]', TITLE);
  for (const [i, v] of ['Soup', 'Stew'].entries()) await page.fill(`.editor-option:nth-child(${i + 1}) .editor-option-main input`, v);
  await page.fill('[id$="-look-pick"]', 'custom');
  await page.waitFor(`!!document.querySelector('[id$="-look-lightbase"]')`, 'the look controls');
  await setColour(page, '[id$="-look-lightbase"]', LIGHT_BASE);
  await setColour(page, '[id$="-look-darkbase"]', DARK_BASE);
  await page.fill('[id$="-look-font"]', 'serif');
  const previewed = await page.waitFor(`document.querySelectorAll('link[href*="looks/preview.css"]').length === 1
    && getComputedStyle(document.querySelector('.look-preview-light .btn-primary')).backgroundColor === 'rgb(90, 42, 95)'`, 'the preview in the look')
    .then(() => true, () => false);
  check('editor: the preview follows the look', previewed, await page.eval(`getComputedStyle(document.querySelector('.look-preview-light .btn-primary')).backgroundColor`));
  await page.fill('[id$="-look-name"]', 'Plum (e2e)');
  await page.clickText('Save as look', 'button');
  await page.waitFor(`document.body.innerText.includes('Look saved.')`, '"Look saved."', 10_000);
  check('editor: the saved look is picked', await page.eval(`(() => { const s = document.querySelector('[id$="-look-pick"]'); return s.options[s.selectedIndex].text === 'Plum (e2e)'; })()`));
  if (process.env.E2E_SHOTS) await screenshots(page, process.env.E2E_SHOTS, check, 'look-editor', '[id$="-look-pick"]');
  await page.interactive();
  await page.clickText('Create poll', 'button');
  await page.waitFor(`/^\\/polls\\/[^/]+\\/share$/.test(location.pathname)`, 'the share page', 20_000);
  const id = (await page.eval('location.pathname')).split('/')[2];

  // ---- the vote page: look.css after the theme, its tokens in both modes ----
  const ctx = await browser.newUserContext();
  defer(() => browser.removeUserContext(ctx));
  const voter = await browser.newPage({ base: app.base, userContext: ctx });
  defer(() => voter.close());
  await voter.goto(`/p/${id}`);
  await voter.waitFor(`!!document.querySelector('link[data-poll-look]')?.sheet`, 'the look sheet loaded');
  const href = await voter.eval(`document.querySelector('link[data-poll-look]').getAttribute('href')`);
  check('vote page links look.css', new RegExp(`^/p/${id}/look\\.css\\?v=[0-9a-f]+$`).test(href), href);
  check('look.css after the theme', await voter.eval(`(() => { const l = [...document.querySelectorAll('link[rel=stylesheet]')];
    return l.indexOf(document.querySelector('link[data-theme-css]')) < l.indexOf(document.querySelector('link[data-poll-look]')); })()`));
  for (const mode of ['light', 'dark']) {
    const look = await token(voter, mode, '--av-primary');
    const instance = await token(voter, mode, '--av-primary', false);
    check(`${mode}: --av-primary differs from the instance theme`, look !== '' && instance !== '' && look !== instance, `${look} vs ${instance}`);
  }
  check('light: the base is the look\'s', (await token(voter, 'light', '--av-primary')) === LIGHT_BASE);
  check('the font is the look\'s', (await token(voter, 'light', '--av-font')).startsWith('Charter'));
  if (process.env.E2E_SHOTS) await screenshots(voter, process.env.E2E_SHOTS, check, 'look-vote', 'h1');

  // ---- /looks: export the code, import it, import from the poll link ----
  await page.gotoInteractive('/looks');
  await listed(page, 'Plum (e2e)');
  const code = await page.eval(`document.querySelector('[data-look="Plum (e2e)"] [data-copy]').getAttribute('data-copy')`);
  check('the look code', /^tl1\.[A-Za-z0-9_-]+$/.test(code) && code.length <= 512, code);
  await page.fill('#look-import', code);
  await page.fill('#look-import-name', 'Plum copy (e2e)');
  await page.clickText('Import', 'button');
  await listed(page, 'Plum copy (e2e)');
  await page.fill('#look-import', `${app.base}/p/${id}`);
  await page.clickText('Import', 'button');
  await listed(page, TITLE);
  check('/looks: three looks, circuit alive', await page.eval(`document.querySelectorAll('.look-list [data-look]').length >= 3`) && await page.alive());
  await page.fill('#look-import', 'tl1.nonsense');
  await page.clickText('Import', 'button');
  await page.waitFor(`document.querySelector('.alert-danger')?.textContent.includes('not a look')`, 'a refused code');
  if (process.env.E2E_SHOTS) {
    await page.clickText('Edit', '[data-look="Plum (e2e)"] button');
    await page.waitFor(`!!document.querySelector('.look-editor .look-preview-dark')`, 'the look form');
    await screenshots(page, process.env.E2E_SHOTS, check, 'looks-page', 'h1');
    await page.clickText('Cancel', '.look-editor button');
  }

  // ---- the admin setting off: no look anywhere, no Look section ----
  const looksSwitch = async on => {
    await page.gotoInteractive('/admin?tab=settings');
    await page.waitFor(`!!document.getElementById('setting-pollLooks')`, 'the Poll looks switch');
    if (await page.eval(`document.getElementById('setting-pollLooks').checked`) !== on) await page.click('#setting-pollLooks');
    await page.clickText('Save', 'button');
    await page.waitFor(`document.body.innerText.includes('Saved.')`, '"Saved."', 10_000);
  };
  await looksSwitch(false);
  try {
    await voter.goto(`/p/${id}`);
    check('looks off: the vote page links no look', await voter.eval(`!document.querySelector('link[data-poll-look]') && !document.documentElement.outerHTML.includes('look.css')`));
    await page.gotoInteractive('/polls/new');
    await page.waitFor(`!!document.querySelector('.editor-addbar')`, 'the editor');
    check('looks off: no Look section', await page.eval(`!document.querySelector('[id$="-look-pick"]')`));
  } finally {
    await looksSwitch(true);
  }
  await voter.goto(`/p/${id}`);
  check('looks on again: linked', await voter.eval(`!!document.querySelector('link[data-poll-look]')`));
}

/** Viewport screenshots at desktop and phone widths in light and dark (`what`-width-mode.png), `selector` near the top. */
async function screenshots(page, dir, check, what, selector) {
  fs.mkdirSync(dir, { recursive: true });
  const files = [];
  const theme = mode => page.eval(`(() => { document.cookie = 'av-theme=${mode}; Path=/; SameSite=Lax';
    const r = document.documentElement; r.setAttribute('data-bs-theme', '${mode}'); r.setAttribute('data-av-theme', '${mode}'); return true; })()`);
  try {
    for (const mode of ['light', 'dark']) {
      await theme(mode);
      for (const [label, width] of [['desktop', 1280], ['phone', 390]]) {
        await page.viewport(width, width > 600 ? 900 : 844);
        await new Promise(r => setTimeout(r, 400));
        await page.eval(`document.querySelector(${JSON.stringify(selector)}).scrollIntoView({ block: 'start', behavior: 'instant' }), true`);
        const file = path.join(dir, `${what}-${label}-${mode}.png`);
        const r = await page.browser.send('browsingContext.captureScreenshot', { context: page.ctx, origin: 'viewport' });
        fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
        files.push(file);
        check(`${what} ${label} ${mode}: no horizontal scroll`, await page.eval(`document.documentElement.scrollWidth <= window.innerWidth + 1`));
      }
    }
  } finally {
    await page.eval(`(() => { document.cookie = 'av-theme=; Path=/; Max-Age=0; SameSite=Lax';
      document.documentElement.removeAttribute('data-bs-theme'); document.documentElement.removeAttribute('data-av-theme'); return true; })()`);
    await page.viewport(1280, 900);
  }
  check(`${what} screenshots written`, files.length === 4, files.join('\n'));
}
