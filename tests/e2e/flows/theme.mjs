// The visitor's light / dark switch (DESIGN.md §24.4): a cookie the server renders into <html>, so a reload does not
// flash, on a static page and on an interactive one.
export const name = 'theme-switch';

const state = page => page.eval(`({ bs: document.documentElement.getAttribute('data-bs-theme'), av: document.documentElement.getAttribute('data-av-theme'),
  cookie: document.cookie.split('; ').filter(c => c.startsWith('av-theme=')).join(''), stored: localStorage.getItem('av-theme'),
  bg: getComputedStyle(document.body).backgroundColor, visible: [...document.querySelectorAll('.theme-switch')].filter(e => e.offsetParent !== null).length })`);

/** Uses the switch like a person: the header menu, or the sidebar's row of buttons. */
async function choose(page, mode) {
  if (await page.eval(`!!document.querySelector('[data-theme-toggle]') && document.querySelector('[data-theme-toggle]').offsetParent !== null`)) {
    await page.click('[data-theme-toggle]');
    await page.click(`details.theme-switch [data-theme-choice="${mode}"]`);
  } else {
    await page.click(`.theme-switch-inline [data-theme-choice="${mode}"]`);
  }
  await page.waitFor(`(document.documentElement.getAttribute('data-av-theme') ?? 'system') === ${JSON.stringify(mode)}`, `the ${mode} mode`, 5000);
}

/** The <html> tag as the server sends it for this page with this browser's cookies. */
const serverHtmlTag = page => page.eval(`fetch(location.href, { credentials: 'same-origin' }).then(r => r.text()).then(t => (t.match(/<html[^>]*>/) || [''])[0])`);

export async function run({ app, browser, state: shared, check, defer }) {
  const visitor = await browser.newUserContext();
  const page = await browser.newPage({ base: app.base, userContext: visitor });
  defer(() => browser.removeUserContext(visitor));
  defer(() => page.close());
  await page.goto('/');
  let s = await state(page);
  check('no choice: no mode attribute, switch shown', s.bs === null && s.av === null && s.visible === 1, JSON.stringify(s));
  const lightBg = s.bg;
  await choose(page, 'dark');
  s = await state(page);
  check('dark: attribute, cookie, storage, colours', s.bs === 'dark' && s.cookie === 'av-theme=dark' && s.stored === 'dark' && s.bg !== lightBg, JSON.stringify(s));
  check('server renders dark from the cookie (no flash)', (await serverHtmlTag(page)).includes('data-bs-theme="dark"'), await serverHtmlTag(page));
  await page.reload();
  await page.settled();
  s = await state(page);
  check('dark after reload', s.bs === 'dark' && s.bg !== lightBg, JSON.stringify(s));

  // The vote page (static) keeps it; System clears the cookie.
  await page.goto(`/p/${shared.polls.ranked}`);
  check('vote page dark from the cookie', (await state(page)).bs === 'dark');
  await choose(page, 'system');
  s = await state(page);
  check('system clears attribute, cookie and storage', s.bs === null && s.cookie === '' && s.stored === null, JSON.stringify(s));

  // On an interactive page the switch works without the circuit and survives a reload.
  const admin = shared.admin;
  await admin.gotoInteractive('/polls');
  await choose(admin, 'dark');
  check('My polls: dark chosen', (await state(admin)).bs === 'dark');
  check('My polls circuit alive after the switch', await admin.alive());
  await admin.reload();
  await admin.interactive();
  check('My polls: dark after reload', (await state(admin)).bs === 'dark');
  await choose(admin, 'system');
  check('My polls: back to system', (await state(admin)).bs === null);
}
