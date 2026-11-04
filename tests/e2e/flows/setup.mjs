// First run: the admin account from the setup key in the server log, then the mandatory authenticator enrolment.
import { totp } from '../lib/totp.mjs';

export const ADMIN_EMAIL = 'admin@example.com';
export const PASSWORD = 'correct horse battery staple';

/** Public forms refuse a post sooner than Abuse:FormMinSeconds (2 s) after the page was served. */
export const formAge = page => page.waitFor('performance.now() > 2300', 'the form to be old enough', 10_000);

export const name = 'setup';
export const required = true;

export async function run({ app, browser, state, check }) {
  const key = app.setupKey();
  if (!key) throw new Error('no admin setup key in the server log');

  const page = await browser.newPage({ base: app.base });
  state.admin = page;
  await page.goto('/setup');
  await page.waitFor(`!!document.getElementById('Input.Key')`, 'the setup form');
  await page.fill('#Input\\.Key', key);
  await page.fill('#Input\\.Email', ADMIN_EMAIL);
  await page.fill('#Input\\.Password', PASSWORD);
  await page.fill('#Input\\.ConfirmPassword', PASSWORD);
  await formAge(page);
  await page.clickText('Create', 'button');
  await page.waitFor(`location.pathname === '/Account/Manage/EnableAuthenticator'`, 'the authenticator enrolment page', 20_000);

  state.totpSecret = (await page.eval(`document.querySelector('kbd.authenticator-key').innerText`)).replace(/\s/g, '');
  check('enrolment shows a key', /^[a-z2-7]{16,}$/i.test(state.totpSecret), state.totpSecret);
  state.totpStep = Math.floor(Date.now() / 30_000);
  await page.fill('#Input\\.Code', totp(state.totpSecret));
  await page.clickText('Verify', 'button');
  await page.waitFor(`document.body.innerText.includes('Recovery codes')`, 'the recovery codes');

  await page.clickText('Continue', 'a');
  await page.waitFor(`location.pathname === '/polls'`, 'My polls after enrolment');
  await page.interactive();
  check('lands on My polls', (await page.path()) === '/polls');
}
