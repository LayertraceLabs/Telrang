// Copy buttons (DESIGN.md §23.1): a click on any [data-copy] element copies the attribute's text and writes the outcome
// into the element named by its data-copy-status (role="status"). Loaded once from App.razor (no inline script: CSP);
// one delegated listener on the document, so it serves static pages, interactive components and pages reached by
// enhanced navigation alike. It runs in the browser within the click, with no server round trip, so the click's user
// activation is still valid when navigator.clipboard.writeText runs (Firefox requires it).
const copiedText = 'Copied to clipboard.';
const failedText = 'Copy failed. Select the text and copy it.';
const clearAfterMs = 6000;

// Plain-HTTP LAN testing has no navigator.clipboard (not a secure context): a temporary textarea and execCommand.
function copyFallback(text) {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.setAttribute('readonly', '');
  ta.className = 'clipboard-scratch';
  document.body.appendChild(ta);
  ta.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  ta.remove();
  return ok;
}

function copy(text) {
  if (window.isSecureContext && navigator.clipboard?.writeText) {
    try {
      return navigator.clipboard.writeText(text).then(() => true, () => copyFallback(text));
    } catch {
      // fall through
    }
  }
  return Promise.resolve(copyFallback(text));
}

function report(button, ok) {
  const id = button.getAttribute('data-copy-status');
  const status = id ? document.getElementById(id) : null;
  if (!status) return;
  const text = ok ? copiedText : failedText;
  status.textContent = text;
  // A later copy of other text must not be mistaken for this one: the line goes again after a while.
  clearTimeout(status.avCopyTimer);
  status.avCopyTimer = setTimeout(() => { if (status.textContent === text) status.textContent = ''; }, clearAfterMs);
}

const hook = Symbol.for('av.copy');
if (!window[hook]) {
  window[hook] = true;
  document.addEventListener('click', e => {
    const button = e.target instanceof Element ? e.target.closest('[data-copy]') : null;
    if (!button || button.disabled) return;
    e.preventDefault();
    copy(button.getAttribute('data-copy') ?? '').then(ok => report(button, ok), () => report(button, false));
  });
}
