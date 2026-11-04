// Signing in with password and authenticator code in a fresh browser context (DESIGN.md §23.3), and the header never
// showing the email address (§24.2) on static and interactive pages.
import { totp } from '../lib/totp.mjs';
import { ADMIN_EMAIL, PASSWORD, formAge } from './setup.mjs';

export const name = 'signin-header';

/** The email (or any address) anywhere in the header or sidebar, text or attributes; null when there is none. */
export const emailInHeader = page => page.eval(`(() => {
  const html = [...document.querySelectorAll('header, aside, .app-header, .sidebar')].map(e => e.outerHTML).join('');
  const m = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}/.exec(html);
  return m ? m[0] : null; })()`);

export async function run({ app, browser, state, check, defer }) {
  const ctx = await browser.newUserContext();
  const page = await browser.newPage({ base: app.base, userContext: ctx });
  defer(() => browser.removeUserContext(ctx));
  defer(() => page.close());
  await page.goto('/Account/Login');
  await page.waitFor(`!!document.getElementById('Input.Email')`, 'the login form');
  await page.fill('#Input\\.Email', ADMIN_EMAIL);
  await page.fill('#Input\\.Password', PASSWORD);
  await formAge(page);
  await page.clickText('Log in', 'button');
  await page.waitFor(`location.pathname.includes('LoginWith2fa')`, 'the authenticator code page');
  await page.fill('#Input\\.TwoFactorCode', totp(state.totpSecret));
  await page.clickText('Log in', 'button');
  const landed = await page.waitFor(`location.pathname === '/polls'`, 'My polls after signing in', 10_000).catch(() => false);
  if (!landed) {
    // Refused (a code is good for one 30 s step, and the enrolment may have used this one): the next step's code.
    await new Promise(r => setTimeout(r, 30_000 - (Date.now() % 30_000) + 500));
    await page.waitFor(`!!document.getElementById('Input.TwoFactorCode')`, 'the authenticator code form');
    await page.fill('#Input\\.TwoFactorCode', totp(state.totpSecret));
    await page.clickText('Log in', 'button');
    await page.waitFor(`location.pathname === '/polls'`, 'My polls after signing in', 20_000);
  }
  await page.interactive();
  check('signed in, lands on My polls', true);

  for (const p of ['/polls', '/', `/p/${state.polls.ranked}/results`, `/p/${state.polls.ranked}`, '/admin', '/Account/Manage']) {
    await page.goto(p);
    await page.interactive();
    await page.settled();
    const found = await emailInHeader(page);
    check(`no email in the header on ${p.replace(state.polls.ranked, '{id}')}`, found === null, found);
  }
  check('header shows the part before @', (await page.eval(`document.querySelector('.user-menu-name')?.textContent.trim()`)) === 'admin',
    await page.eval(`document.querySelector('.user-menu-name')?.textContent`));
}
