// Links from an interactive page to a static page are plain page loads (DESIGN.md §24.6). Blazor's interactive router
// intercepts every in-app link click (also those with data-enhance-nav="false"); for a page that never runs in a circuit
// the circuit then orders a full reload, and the browser unloads the page while that call is in flight, which the
// server logs as a failed navigation. App.razor puts the static pages' routes into <meta name="av-static-routes"> on
// interactive pages only; a click on a link to one of them stops here, so only the browser handles it.
const meta = document.querySelector('meta[name="av-static-routes"]');
const staticRoute = meta ? new RegExp(meta.content, 'i') : null;

function link(e) {
  for (const el of e.composedPath ? e.composedPath() : []) {
    if (el instanceof HTMLAnchorElement || el instanceof SVGAElement) return el;
  }
  return null;
}

if (staticRoute) {
  window.addEventListener('click', e => {
    if (e.button !== 0 || e.ctrlKey || e.shiftKey || e.altKey || e.metaKey || e.defaultPrevented) return;
    const a = link(e);
    if (!a || !a.hasAttribute('href') || a.hasAttribute('download')) return;
    const target = a.getAttribute('target');
    if (target && target !== '_self') return;
    const url = new URL(a.href instanceof SVGAnimatedString ? a.href.baseVal : a.href, document.baseURI);
    const base = new URL(document.baseURI); // <base href="/">, or a path prefix behind a proxy
    if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) return;
    const path = '/' + url.pathname.slice(base.pathname.length);
    if (!staticRoute.test(path)) return;
    // Same page with only a hash: leave it to the browser and Blazor (scrolling, no load).
    if (url.pathname === location.pathname && url.search === location.search && url.hash) return;
    e.stopPropagation(); // Blazor's listener sits on the document; the default action (the page load) goes ahead
  }, true);
}
