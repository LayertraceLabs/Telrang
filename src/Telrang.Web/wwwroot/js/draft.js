// Unsaved poll forms survive a lost session or circuit: every change of the editor's PollDraft
// (as JSON, sent by PollEditor) is kept in localStorage under a per-account, per-poll key
// ("draft:<userId>:<pollId|new>", from PollEditor.DraftKeyFor) until the poll is saved, and leaving the page with unsaved
// changes asks first. Another account on the same browser has another key, so it never sees the draft. Per-browser
// convenience only; the server never sees it.
// Drafts do not outlive the account's session on a shared computer (AV-15f): signing out (the Account/Logout form, on
// any page: App.razor loads this module everywhere) removes every draft in this browser, and every page (and opening an
// editor) removes the drafts of accounts other than the one it was rendered for (left by a session that expired, or a
// browser closed without signing out).
const prefix = 'av-';
const legacy = /^av-draft:[^:]*$/; // keys from before the account was part of the key: dropped on sight
const owned = /^av-draft:([^:]*):/;
const forms = new Map();

/** Removes legacy drafts and the drafts of every account but `keepUser` (every draft when it is undefined). */
function dropDrafts(keepUser) {
    try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const k = localStorage.key(i);
            if (!k) continue;
            const m = owned.exec(k);
            if (legacy.test(k) || (m && m[1] !== keepUser)) localStorage.removeItem(k);
        }
    } catch { /* blocked */ }
}

/** The account id in a draft key ("draft:<userId>:<pollId|new>"). */
function userOf(key) {
    const m = /^draft:([^:]*):/.exec(key);
    return m ? m[1] : undefined;
}

function isSignOut(form) {
    try {
        return new URL(form.action, document.baseURI).pathname.replace(/\/+$/, '').toLowerCase().endsWith('/account/logout');
    } catch {
        return false;
    }
}

// Once per page, also when the module is imported again. Capture phase: runs before the form is posted. Unsaved
// changes in an open editor still ask first (beforeunload); the browser's copy is gone either way.
const signOutHook = Symbol.for('av.drafts.signOut');
if (!window[signOutHook]) {
    window[signOutHook] = true;
    document.addEventListener('submit', e => {
        if (e.target instanceof HTMLFormElement && isSignOut(e.target)) dropDrafts(undefined);
    }, true);
    dropForPage();
    window.Blazor?.addEventListener?.('enhancedload', dropForPage);
}

// The page names the account it was rendered for (App.razor: <body data-av-user>, empty for no account). A session that
// ended without the sign-out form (expired, account deleted) leaves drafts behind: the first page rendered without that
// account removes them (pentest R2-POLLS-7, AV-15f residual).
function dropForPage() {
    const user = document.body?.dataset.avUser;
    if (user === undefined) return;
    dropDrafts(user === '' ? undefined : user);
}

function read(key) {
    try { return localStorage.getItem(prefix + key); } catch { return null; }
}
function write(key, value) {
    try { localStorage.setItem(prefix + key, value); } catch { /* storage full or blocked */ }
}
function remove(key) {
    try { localStorage.removeItem(prefix + key); } catch { /* blocked */ }
}

function onBeforeUnload(e) {
    for (const state of forms.values()) {
        if (state.current !== state.baseline) {
            e.preventDefault();
            e.returnValue = '';
            return;
        }
    }
}

/**
 * Starts tracking a form whose saved state is `baseline` (JSON). Returns a stored draft that differs from it, or null.
 * With `replace` (a copy of another poll was opened) a stored draft is dropped instead.
 */
export function attach(key, baseline, replace) {
    dropDrafts(userOf(key));
    detach(key);
    forms.set(key, { baseline, current: baseline });
    if (forms.size === 1) window.addEventListener('beforeunload', onBeforeUnload);
    const draft = read(key);
    if (replace || draft === null || draft === baseline) {
        remove(key);
        return null;
    }
    const plain = compact(draft);
    if (plain === null) remove(key);
    return plain;
}

// The stored draft is the editor's JSON, where System.Text.Json writes every non-ASCII character as \uXXXX. Re-serialised
// here it travels as plain UTF-8 (at most 3 bytes a character instead of 7 once escaped again), so a large draft fits the
// circuit's message limit (LimitSettings.MaxCircuitMessageBytes, pentest AV-09). Not JSON: not a draft, dropped.
function compact(json) {
    try { return JSON.stringify(JSON.parse(json)); } catch { return null; }
}

/** The form changed: keep it, or drop the draft when it is back at the baseline. */
export function update(key, json) {
    const state = forms.get(key);
    if (state) state.current = json;
    if (state && json === state.baseline) remove(key);
    else write(key, json);
}

/** The form was saved (or the draft discarded): `json` is the new baseline and the draft is gone. */
export function markSaved(key, json) {
    const state = forms.get(key);
    if (state) { state.baseline = json; state.current = json; }
    remove(key);
}

export function detach(key) {
    if (!forms.delete(key)) return;
    if (forms.size === 0) window.removeEventListener('beforeunload', onBeforeUnload);
}
