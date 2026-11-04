// Admin settings (DESIGN.md §17, §24.4, §24.6): a settings save shows on the public pages, and a layout change reloads
// the page into the new shell (a reload from the browser, not a circuit-driven navigation). The header shows no email
// in any shell.
import { emailInHeader } from './signin.mjs';

export const name = 'admin-settings';

async function settings(page) {
  await page.gotoInteractive('/admin?tab=settings');
  await page.waitFor(`!!document.getElementById('setting-name') && !!document.getElementById('theme-layout')`, 'the settings tab');
}

const saved = page => page.waitFor(`document.body.innerText.includes('Saved.')`, '"Saved."', 10_000);

export async function run({ state, check }) {
  const page = state.admin;

  // General settings: the display name and the welcome text reach the home page.
  await settings(page);
  await page.fill('#setting-name', 'Club votes');
  await page.fill('#setting-welcome', 'Welcome to **the club**.');
  await page.clickText('Save', 'button');
  await saved(page);
  check('settings saved, circuit alive', await page.alive());
  await page.goto('/');
  check('home shows the display name', await page.eval(`document.querySelector('.app-brand')?.textContent.includes('Club votes')`),
    await page.eval(`document.querySelector('.app-brand')?.textContent`));
  check('home shows the welcome Markdown', await page.eval(`[...document.querySelectorAll('strong')].some(s => s.textContent === 'the club')`));

  // Layouts: each save reloads the admin page into the new shell.
  for (const layout of ['sidebar', 'compact', 'topbar']) {
    await settings(page);
    await page.eval(`window.__e2eBefore = true`);
    await page.fill('#theme-layout', layout);
    await page.clickText('Save theme', 'button');
    await page.waitFor(`!window.__e2eBefore && !!document.querySelector('.shell-${layout}')`, `the page reloaded into the ${layout} shell`, 15_000);
    await page.interactive();
    check(`${layout}: reloaded into the shell, circuit alive`, await page.alive());
    for (const p of ['/', '/polls']) {
      await page.goto(p);
      await page.interactive();
      const ok = await page.eval(`!!document.querySelector('.shell-${layout}')`);
      const found = await emailInHeader(page);
      check(`${layout}: ${p} in the shell, no email in the header`, ok && found === null, `shell ${ok}, email ${found}`);
    }
  }
}
