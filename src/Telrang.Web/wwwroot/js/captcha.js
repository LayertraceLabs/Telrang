// Captcha widget in the vote form (CaptchaWidget.razor; Turnstile or hCaptcha, admin setting; DESIGN.md §21.1).
// Initialised from the box's data attributes. The provider puts its token into the form itself
// (cf-turnstile-response / h-captcha-response), which the server verifies once per submit. The vote page loads the
// provider script with the CSP nonce; after an enhanced navigation it may be missing, so it is added here (the CSP
// allows the provider's origin while a captcha is enabled).
const scripts = {
  Turnstile: { global: 'turnstile', src: 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit' },
  HCaptcha: { global: 'hcaptcha', src: 'https://js.hcaptcha.com/1/api.js?render=explicit' },
};

function api(provider) {
  const info = scripts[provider];
  if (!info) return Promise.reject(new Error('Unknown captcha provider'));
  if (window[info.global]?.render) return Promise.resolve(window[info.global]);
  if (!document.querySelector(`script[src="${info.src}"]`)) {
    const s = document.createElement('script');
    s.src = info.src;
    s.async = true;
    document.head.appendChild(s);
  }
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const timer = setInterval(() => {
      if (window[info.global]?.render) { clearInterval(timer); resolve(window[info.global]); }
      else if (Date.now() - started > 15000) { clearInterval(timer); reject(new Error('Captcha script did not load')); }
    }, 100);
  });
}

// Rendered widgets: box -> { lib, id }, for the reset below.
const widgets = new Map();

export async function init(box) {
  if (box.dataset.ready) return;
  box.dataset.ready = '1';
  try {
    const lib = await api(box.dataset.captcha);
    widgets.set(box, { lib, id: lib.render(box, { sitekey: box.dataset.sitekey }) });
  } catch {
    const failed = box.parentElement?.querySelector('[data-captcha-failed]');
    if (failed) failed.hidden = false;
  }
}

// Back/forward cache: a page restored after the POST still holds the token that POST used up; a fresh challenge
// gets a new one instead of a certain refusal.
window.addEventListener('pageshow', e => {
  if (!e.persisted) return;
  for (const [box, w] of widgets) {
    if (!box.isConnected) { widgets.delete(box); continue; }
    try { w.lib.reset(w.id); } catch { /* the provider's widget is gone; the next submit asks again */ }
  }
});
