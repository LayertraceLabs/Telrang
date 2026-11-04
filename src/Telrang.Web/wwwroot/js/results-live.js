// Live results for the poll's owner (DESIGN.md §21.2). Loaded only on the owner's results view (ResultsPage.razor); no
// inline script or style (CSP). Listens on the page's event stream ([data-live-src]) and, on `changed`, fetches the
// results page again and swaps in the children of its [data-live-results] element, built with DOMParser and
// importNode, never innerHTML. Without JS the page stays static and is refreshed by hand.
//
// - The EventSource is closed while the tab is hidden (frees one of HTTP/1.1's six connections per origin) and opened
//   again when it shows; every reopen refreshes once, since changes in between were not heard.
// - Fetches are at most one per 3 s; a change during the wait leads to one more fetch at its end.
// - A failed stream (404: no longer the owner or the poll is gone, 429: too many streams) closes the EventSource for
//   good (readyState CLOSED); the browser itself reconnects only after a normal end (lifetime, restart).
// - Blazor enhanced navigation does not run scripts it inserts, so links to the results page disable it; the module
//   still re-checks the page on `enhancedload` so it stops when the view is left and starts when it comes back.

const MIN_FETCH_GAP_MS = 3000;

let source = null;     // the open EventSource
let streamUrl = null;  // its URL (the page it belongs to)
let stopped = false;   // refused by the server: no more attempts for this page
let opened = false;    // an open event was seen on this page (later ones are reopens)
let lastFetch = 0;
let fetchTimer = null;
let fetching = false;

function container() {
  return document.querySelector('[data-live-results][data-live-src]');
}

function setBadge(on) {
  for (const badge of document.querySelectorAll('[data-live-badge]')) badge.hidden = !on;
}

function close() {
  if (source) {
    source.close();
    source = null;
  }
  setBadge(false);
}

function open() {
  const el = container();
  if (!el || stopped || source || document.visibilityState === 'hidden') return;
  const url = new URL(el.getAttribute('data-live-src'), document.baseURI);
  if (url.origin !== location.origin) return;
  streamUrl = url.href;
  const es = new EventSource(streamUrl);
  source = es;
  es.addEventListener('open', () => {
    if (source !== es) return;
    setBadge(true);
    if (opened) scheduleRefresh();
    opened = true;
  });
  es.addEventListener('changed', () => {
    if (source === es) scheduleRefresh();
  });
  es.addEventListener('error', () => {
    if (source !== es) return;
    setBadge(false);
    if (es.readyState === EventSource.CLOSED) {
      // Not a 200 event stream: 404 or 429 (or a proxy error). Stay static.
      stopped = true;
      source = null;
    }
  });
}

function scheduleRefresh() {
  if (fetchTimer !== null) return;
  const wait = Math.max(0, lastFetch + MIN_FETCH_GAP_MS - Date.now());
  fetchTimer = setTimeout(() => {
    fetchTimer = null;
    refresh();
  }, wait);
}

async function refresh() {
  if (fetching) {
    scheduleRefresh();
    return;
  }
  const target = container();
  if (!target) return;
  fetching = true;
  lastFetch = Date.now();
  const page = location.href.split('#')[0];
  try {
    const response = await fetch(page, {
      credentials: 'same-origin',
      cache: 'no-store',
      redirect: 'follow',
      headers: { Accept: 'text/html' },
    });
    const type = response.headers.get('Content-Type') || '';
    if (!response.ok || response.redirected || !type.toLowerCase().startsWith('text/html')) return;
    const html = await response.text();
    const doc = new DOMParser().parseFromString(html, 'text/html');
    const fresh = doc.querySelector('[data-live-results]');
    // The page may have been left meanwhile (enhanced navigation): only swap into the same container.
    if (!fresh || container() !== target) return;
    target.replaceChildren(...Array.from(fresh.childNodes, node => document.importNode(node, true)));
    setBadge(source !== null && source.readyState === EventSource.OPEN);
  } catch {
    // Offline or aborted: the next change or reopen tries again.
  } finally {
    fetching = false;
  }
}

// The page shown changed (enhanced navigation): stop for another page, start for a results view.
function sync() {
  const el = container();
  const url = el ? new URL(el.getAttribute('data-live-src'), document.baseURI).href : null;
  if (url !== streamUrl) {
    close();
    stopped = false;
    opened = false;
    streamUrl = null;
  }
  if (url) open();
}

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') close();
  else open();
});
// Back/forward cache: the stream does not survive it.
window.addEventListener('pagehide', close);
window.addEventListener('pageshow', event => {
  if (event.persisted) open();
});
window.Blazor?.addEventListener?.('enhancedload', sync);

sync();
