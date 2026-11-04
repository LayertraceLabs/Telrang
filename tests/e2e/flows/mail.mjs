// Outbound mail (DESIGN.md §32): only when E2E_SMTP_HOST names an SMTP server the app can reach (scripts/npm-stack-test.sh
// starts one on the app's outbound network). Sets the Public URL to the address the flows use, configures SMTP without
// TLS or login (a test server; the app refuses a login without TLS) and sends the admin's test mail through the outbox.
export const name = 'mail';
export const when = () => !!process.env.E2E_SMTP_HOST;

export async function run({ app, state, check }) {
  const page = state.admin;

  await page.gotoInteractive('/admin?tab=settings');
  await page.waitFor(`!!document.getElementById('setting-url')`, 'the settings tab');
  await page.fill('#setting-url', app.base);
  await page.clickText('Save', 'button');
  await page.waitFor(`document.body.innerText.includes('Saved.')`, '"Saved."', 10_000);

  await page.gotoInteractive('/admin?tab=mail');
  await page.waitFor(`!!document.getElementById('mail-host')`, 'the mail tab');
  await page.fill('#mail-host', process.env.E2E_SMTP_HOST);
  await page.fill('#mail-port', process.env.E2E_SMTP_PORT ?? '25');
  await page.fill('#mail-security', 'None');
  await page.fill('#mail-from', 'vote@example.com');
  await page.fill('#mail-from-name', 'Telrang e2e');
  await page.clickText('Save', 'button');
  await page.waitFor(`document.body.innerText.includes('Configured')`, 'mail configured', 10_000);
  check('mail configured', await page.eval(`[...document.querySelectorAll('.badge')].some(b => b.textContent === 'Configured')`));

  await page.clickText('Send test mail to me', 'button');
  // The outbox sends every 10 seconds; the tab shows the row once queued, "Sent" after a reload.
  const deadline = Date.now() + 45_000;
  let state_ = '';
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 3000));
    await page.gotoInteractive('/admin?tab=mail');
    state_ = await page.eval(`[...document.querySelectorAll('.admin-table tbody tr td:nth-child(3)')].map(td => td.textContent).join(',')`);
    if (state_.includes('Sent') || state_.includes('Dead') || state_.includes('Retrying')) break;
  }
  check('test mail sent', state_.split(',')[0] === 'Sent', state_);
}
