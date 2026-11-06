# Telrang — Design

Status: draft v1, 2026-10-25. This document is the brief for implementation; keep it
updated when decisions change.

## 1. Goals

- Self-hostable voting tool for "complex" polls (ranked-with-bans, point-distribution),
  with more poll types added over time.
- Poll creators have accounts; voters normally do not.
- Polls are reachable only via an unguessable link (poll passwords were removed, §20).
- Single Docker container, SQLite on a volume by default, HTTPS by cert file or reverse proxy.
- Polls are edited in a form whose controls fit each setting; only descriptions are Markdown (§20; the original
  text DSL is gone).

Non-goals for v1: OAuth/social login, multi-tenant orgs, i18n. (Real-time results were one too; owners now have a
live results stream, §21.2.)
(Email delivery was a v1 non-goal; Phase 5a added it, see §14.5.)

## 2. Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10, ASP.NET Core | Current LTS |
| UI | Blazor Web App, **Interactive Server** render mode, global | One project, no API layer, secrets stay server-side. Drag-and-drop via JS interop. |
| Drag and drop | SortableJS (vendored, pinned, SRI hash) | HTML5 DnD does not work on touch; SortableJS does. Always offer up/down buttons as fallback. |
| Data | EF Core 10 + SQLite (default), or PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`, §28) chosen by environment variable | SQLite = zero-config Docker. PostgreSQL for many concurrent votes or reporting tools; two migration sets, one model. |
| Auth | ASP.NET Core Identity (cookie auth) | Standard, maintained, lockout + TOTP 2FA + passkeys built in |
| Markdown | Markdig (descriptions only), NuGet `Markdig` 1.5.0 | Rendered with HTML disabled (`DisableHtml`) to avoid XSS |
| Mail | MailKit 4.18.2 (SMTP), outbox table + background sender | `System.Net.Mail.SmtpClient` is obsolete; MailKit is the recommended replacement. Settings live in the DB (admin page). |
| Tests | xUnit for Core; bUnit optional for components; browser smoke tests in `tests/e2e` (§24.7) | Node 22+ and Firefox are dev/CI-only tools for the browser tests (no npm packages); they never ship and do not touch the NuGet rule |

Trade-off to know: Interactive Server needs a persistent WebSocket. Reverse proxies must
forward `Upgrade`/`Connection` headers. .NET 10 circuit state persistence helps mobile users
who switch apps mid-vote.

## 3. Solution layout

```
Telrang.sln
src/
  Telrang.Core/        pure C#, no ASP.NET/EF references
    Polls/                    PollDraft (form model), PollDraftValidator (draft -> PollDefinition or field errors),
                              PollDefinition/PollQuestion/PollOption, OptionListParser (Text mode of the options
                              editor), PollRules (shared rule texts and helpers), PollType, visibility enums
    Ballots/                  RankedBallot, PointsBallot, validation
    Scoring/                  IScoringStrategy, BordaScoring, DowdallScoring, PointsScoring, ResultSet
    Ids/                      PollId (128 random bits <-> 22-char base64url)
  Telrang.Web/         Blazor Web App
    Data/                     AppDbContext, entities, migrations
    Identity/                 ApplicationUser, Identity UI pages (login, 2FA, passkeys)
    Features/
      Polls/                  create/edit/copy (PollEditor form + live PollPreview), share page, close/reopen, PollService
      Voting/                 /p/{id} vote page, ranked + points + dates components, BallotService
      Results/                results page, ResultsService, CSV export
      Admin/                  users, polls, settings, mail, log tabs; AdminService, admin action log, backup
      Accounts/               AccountService (setup links, recovery, verification, soft delete, export), /setup, /about
      Settings/               SettingsService (DB-backed, cached) + SettingKeys
      Mail/                   IMailQueue (outbox), MailSender (BackgroundService), IMailTransport (MailKit)
    Security/                 rate limiting, headers, PollAccessService, poll cookies, invite tokens,
                              setup key, account gate, AppSignInManager
    wwwroot/                  lib/sortablejs/sortable.esm.js, js/ranking.js, js/clipboard.js, app.css
tests/
  Telrang.Core.Tests/  validator + option text + ballots + scoring + id round-trip
docker/
  Dockerfile, compose.yml
packaging/
  linux/                      telrang.service (systemd unit), example telrang.json
  windows/                    install-service.ps1, uninstall-service.ps1, example telrang.json
docs/deploy/                  README.md (shared reference), docker.md, linux.md, windows.md, security.md
```

Core is deliberately dependency-free: validation and scoring are the parts most likely to be
wrong in subtle ways, so they must be unit-testable without a web host.

## 4. Data model

```
User            (Identity)  Id, Email (= UserName; null when pending/deleted), ..., IsAdmin via role,
                DisplayName?, TimeZone?, CreatedAt, LastLoginAt?, DeletedAt?, PendingSetup, MustEnrolTwoFactor
AppSetting      Key (PK), Value, UpdatedAt, UpdatedBy         DB settings (§14.4/§14.5); secrets protected
SetupLink       Id, UserId, TokenHash (unique), Purpose (AccountSetup|PasswordReset|EmailInvite),
                CreatedAt, ExpiresAt, UsedAt?, CreatedBy?, SecurityStamp?
OutboxMessage   Id, To, Subject, TextBody, HtmlBody?, Type, CreatedAt, SendAfter, Attempts, LastError?,
                SentAt?, DeadAt?
AdminActionLog  Id (long, identity), At, AdminUserId, Action, TargetType?, TargetId?, Note?
Poll            Id (Guid, random v4, PK), OwnerId, Title, Type, Scoring (the first question's; for lists and mails),
                DefinitionJson (validated PollDefinition: poll-wide settings + Questions[], each with Key, type,
                type settings and options; the only stored form of a poll),
                ResultsVisibility, VoterMode, ClosesAt?, CreatedAt, UpdatedAt,
                ClosedAt? (manual close), ClosedNotifiedAt? (results-ready sweep handled this close; kept on reopen, §17)
                                                ClosesAt = end of the "Closes on" day in the poll's time zone
Ballot          Id, PollId, VoterKey?, UserId? (FK, SET NULL), VoterIdentity, ResponseNo, DisplayName?, Alias?,
                SubmittedAt, UpdatedAt, PayloadJson ({"q1": {...}}: one part per question key), NotifyOnClose (account ballots of a verified address only)
                Alias = owner-only name (§20.3): the invite link's alias when the response is created, then the owner's
                VoterIdentity = "u:" + UserId | "k:" + VoterKey (stored column, filled by BallotService)
                unique (PollId, VoterIdentity, ResponseNo); index (UserId, PollId)
InviteToken     Id, PollId, TokenHash (unique), Alias?, CreatedAt, BallotId?, ClaimedByUserId?   (VoterMode = Invite)
                unique (PollId, ClaimedByUserId): one claimed invitation per account and poll (§16)
PollCreation    Id, UserId (no FK), At        one row per poll creation, for the polls-per-day limit;
                rows older than 48 h are deleted by the next creation (no background job)
```

Rules:
- Ballots are stored raw. Scores are computed on read, never stored. Keeps the data honest
  and makes exports and new result views cheap.
- `Type` and `scoring` are fixed at creation (per question). The editor shows them read-only on edit and
  `PollDraftValidator` (with `existing`) rejects a change. Changing the rules once people have voted (or can see the
  rules) is unfair to voters.
- `Poll.Id` is a random (v4) Guid. **Do not** use v7/time-ordered ids anywhere that becomes
  public: ordering leaks guessability.
- URL form: `Base64Url.EncodeToString(guid.ToByteArray())` → 22 chars, e.g.
  `/p/Z3hK9qvYTkG1cE0A8m2bXw`. Decoding must reject anything that is not exactly 22
  base64url chars. No endpoint ever lists polls to non-owners.

## 5. Poll settings

> The text DSL that used to define polls is gone (§20, 2026-10-31). Polls are a `PollDraft` edited in the form
> (§20.2) and validated by `PollDraftValidator` into a `PollDefinition`. This section keeps what each setting means.

Poll-wide (on `PollDefinition`):

| Setting (form label) | Values (default) | Meaning |
|---|---|---|
| Title | text, ≤ title limit | Required. |
| Description | Markdown, ≤ poll description limit | Optional; rendered with HTML disabled. |
| Who votes (`Voters`) | Anyone with the link (`Public`, default) · Signed-in accounts · Invite links | §7, §16. |
| Responses | One (default) · Several (`Many`) | §6.4. |
| Names | None · Optional (default) · Required | Name field on the ballot. |
| Results | Owner only (`Creator`, default) · After voting · Anyone | §6.4. |
| Closes on (`ClosesOn`) | date or empty | End of that day in the poll's time zone; empty = automatic close after the owner's max duration (§14.4), fixed at creation. |
| Time zone | IANA id | Default: the creator's account zone, else the browser's (§23.5), else UTC, stored at creation so a later account change never moves a close. Syntax checked in Core (≤ 64 chars, no whitespace), the zone database in Web (`PollParsing`; Windows ids refused). |

Per question (on `PollQuestion`; exactly one question for now, key `q1`):

| Setting | Types | Meaning |
|---|---|---|
| Type | all | Ranked · Points · Dates; fixed at creation. |
| Scoring | ranked | Borda (default) · Dowdall; fixed at creation. |
| Bans per voter | ranked | 0 = none; must be less than the number of options. |
| Rank all | ranked | Every option must be ranked or banned. |
| Budget | points | Required, ≥ 1 (the form starts at 100). |
| Cap per option | points | Optional, 1 … budget. |
| Spend all | points | The ballot must use the whole budget. |
| Allow maybe | dates | Default yes. |
| Options | all | ≥ 2, ≤ options limit. Label ≤ option label limit (dates: the normalised date label), details Markdown ≤ option description limit. Keys are the slug of the label (lowercase ASCII, `-` runs, empty → `option`, duplicates `-2`, `-3`, …); labels are unique case-insensitively. Dates options are sorted chronologically and normalised (`2026-11-07 18:30–21:00`), duplicates refused (§18.1). |

Editing a poll that has ballots: superseded by §26 (settings fixed at creation, stable option keys, ballots never
deleted by an edit, a change log). Before §26: text changes kept them; changing option keys, the voter mode, several → one
response while someone had several, or a rule change that made a stored ballot invalid needed a confirmation that
deleted all ballots (and unlinked invite tokens).

## 6. Poll types

### 6.1 Ranked with optional bans
- Voter drags options into an ordered list. Unranked options stay in an "unranked" pool
  (unless `rank-all: yes`). Separate "banned" zone if `bans > 0`.
- Scoring, computed per ballot over N options:
  - **borda**: rank 1 → N points, rank 2 → N-1, … unranked → 0.
  - **dowdall**: rank r → 1/r points. Stronger emphasis on first choices, which is what
    "see when a subgroup agrees on one item" wants.
  - Creator picks one at creation (default borda); immutable afterwards. The vote page
    states which scheme is in use so voters know the rules.
- Bans are **not** subtracted. They are a separate tally per option (count and % of voters).
  Results show: score, first-choice count, ban count. Optional "net" column (score minus
  ban penalty) can come later; keep raw data so it is possible.
- A banned option cannot also be ranked.

### 6.2 Points distribution
- Each voter has `budget` integer points. Per-option stepper + slider, remaining counter.
- Validation (server-side, authoritative): integers ≥ 0, sum ≤ budget (or == budget if
  `spend-all`), each ≤ `max-per-option`.
- Results: total per option, mean, number of voters who gave > 0, distribution histogram.

### 6.3 Dates (availability; decided 2026-10-29, §18)
- Options are dates or time slots. The voter answers each with yes / maybe / no (`maybe: no` drops the middle
  one); unanswered is no. Three interchangeable views of the same ballot, chosen by the voter: calendar (month
  grids, weeks start on Monday), list, timeline.
- Results: per date the yes, maybe and no counts, in chronological order, with the best date(s) marked (most
  yes, then most maybe). Same three views, read-only. The owner also sees who answered what (names as entered)
  when the poll collects names; everybody else sees counts only, like the other types.

### 6.4 Shared
- A voter can revise their ballot until the poll closes. Identity: `accounts` and signed-in
  `public` voters → account; anonymous `public` voters → `vk` cookie; `invite` → token (the account
  is recorded too when signed in, for "My votes").
- `responses: one` (default): one ballot per identity. `responses: many`: each submit adds a
  response (its own row, `ResponseNo` 1, 2, … per identity, each with its own name); the voter sees
  "Your responses" with edit links (`/p/{id}?r=N`) and can delete their own responses while the
  poll is open.
- After close the vote page shows the voter's own ballot(s) read-only.
- Results visibility: `creator` (only owner), `after-vote` (voter sees results once they
  have submitted), `open` (anyone who can reach the poll).

## 7. Voter identity (decided; implemented in Phase 5b; identity rule in §16)

Who a visitor is follows §16: signed in = the account, everything server-side; anonymous = the browser's
cookies.

- `voters: public` (default) — anyone with the link. Signed-in visitors are recorded
  by account (`Ballot.UserId`, identity `u:<id>`), anonymous ones by a random `VoterKey` in a
  protected cookie (identity `k:<HMAC(vk, poll)>`). A ballot cast anonymously stays separate from
  one cast later after signing in on the same browser (no merging, §16). Cheap, fine for
  friends/teams, trivially bypassed by clearing cookies: honour system (§15).
- `voters: accounts` — signed-in users with a verified email only (not gated, not deleted). Anonymous
  visitors see the poll and "Sign in to respond" with a login link that returns to the poll.
- `voters: invite` — creator generates N links (`/p/{id}?t={token}`), each with an optional alias (owner-only, §20.3). For anonymous
  visitors the token is the identity, a bearer link ("One link, one response"). The first write by a
  signed-in account claims the token: its ballots move to the account and the link then works only for
  that account (§16). Token is 128 random bits, stored hashed.

A poll's creator may vote in their own poll under the same rules as everyone else.

## 8. Security

### Accounts
- Identity with cookie auth. Cookie: `Secure`, `HttpOnly`, `SameSite=Lax`, sliding 14 days.
- Password hashing: Identity `PasswordHasher` V3 = PBKDF2-HMAC-SHA512, 128-bit salt.
  Default is 100 000 iterations; set `PasswordHasherOptions.IterationCount` to **600 000**
  (OWASP 2025 guidance for PBKDF2-SHA512 is ≥ 210 000). Identity auto-rehashes on next
  login if the count is raised later. Argon2id would be marginally better but needs a
  third-party package; not worth the dependency for v1. Revisit if it ever lands in the BCL.
- "Passed along in the browser safely" = TLS only. Never hash client-side; the hash would
  just become the password. HSTS on (365 days), HTTP → HTTPS redirect when a cert is present.
- Pipeline order is explicit (`Program.cs`): forwarded headers → security headers → host filter → exception
  handler / HSTS / HTTPS redirect → status pages → routing → abuse block → rate limiter → authentication →
  authorization → antiforgery → form guard. Challenges therefore carry the forwarded scheme and the headers.
- 2FA: TOTP authenticator app + recovery codes (Identity built-in). Passkeys (WebAuthn) are
  built into .NET 10 Identity and the Blazor template; enable them as a primary sign-in
  option. SMS is out (NIST and Microsoft both advise against it). Passkeys are created discoverable and the
  sign-in options never name a user (no `allowCredentials`, pentest R2-ACC-3), so they reveal no account.
- Lockout after 5 failed attempts, 15 min. Rate limit `/Account/Login`, `/Account/Register`,
  the other Identity POSTs (incl. `PasskeyRequestOptions`) with the
  built-in rate limiter (per IP, sliding window; `/Account/ConfirmEmail` too, it takes the password). Login with an unknown email verifies against a
  dummy hash so it costs the same PBKDF2 time as a wrong password.
- Accepted enumeration oracle: a locked-out account redirects to `/Account/Lockout` (Identity
  checks lockout before the password), so someone who fails 5 times on an address learns that it
  exists. Hiding it would mean faking lockout for unknown addresses too; not worth it for v1.
- Registration is a DB setting (`Registration.Enabled`, default off, admin Settings tab). Off hides the
  Register link and `/Account/Register` is 404. The first admin is created with the one-time setup key
  at `/setup` (§14.1); nobody becomes admin by registering.
- Registration never says an address is taken (pentest AV-10): new and taken addresses both get
  `/Account/RegisterConfirmation` ("Check your mail"), and registering no longer signs in (a session cookie would
  tell the two apart). A taken address gets a `RegistrationAttempt` note instead (verified owner: log in or reset the
  password; unverified: it expires). Self-registered accounts still unverified 7 days after creation are soft-deleted
  by the daily pass of `PollSweepService` (`AccountService.PurgeUnverifiedAsync`; admins and admin-created accounts
  excepted), so nobody keeps an address they cannot verify.
- Email verification by mail (outbox, §14.5). Self-registered accounts cannot sign in until verified (pentest
  R2-ACC-3; the login answers exactly as for an unknown address, dummy hash included); admin-created and setup-link
  accounts are not self-registered. Confirmation links never act on a GET (mail scanners prefetch links; R2-ACC-1):
  the registration link shows "Confirm X for the account created on <date>" and its POST takes the account's password
  (unless the browser is signed in as that account), so the owner of an address can never verify someone else's
  account on it by clicking; the email-change link needs that account signed in (others get 403, signed-out visitors
  go through sign-in) and acts on its POST. With mail off, a self-registered account needs an admin to verify it.
  The email-change page always answers "Confirmation link sent" (mail not configured excepted).
- Account states: `PendingSetup` (admin-created, no email/password yet, cannot sign in) and
  `MustEnrolTwoFactor` (setup flows, admin "disable 2FA"): the `NotGated` authorization requirement
  (see `AccountGate`, §15) lets such a session reach only TOTP enrolment, logout, email confirmation,
  `/setup/*` and static files (303 to enrolment over HTTP, redirect on interactive navigation).
- Soft delete: email/password/2FA/passkeys/roles removed, UserName = Id, `DeletedAt` set, own polls
  deleted; `AppSignInManager.CanSignInAsync` refuses deleted and pending accounts.
- Re-authentication (`AccountService.ReauthenticateAsync`: password, or a current TOTP code with 2FA on; a wrong one
  is a failed sign-in) for deleting the account, the data download, 2FA off, a new authenticator key, adding a
  passkey, new recovery codes, changing the email, turning 2FA on by choice (the forced enrolment gate excepted)
  and resetting the authenticator key with 2FA off (R2-ACC-5). A wrong password or code counts towards the account
  lockout (R2-ACC-4) and a locked account cannot re-authenticate. With 2FA on, `EnableAuthenticator` never shows the key again
  (redirect to `ResetAuthenticator`). A wrong old password on ChangePassword counts as a failed sign-in. These POSTs
  share the auth rate limit. Adding a passkey, new recovery codes, 2FA on, a new authenticator key, 2FA off and an admin removing the account's 2FA mail a
  `SecurityNotice` to the verified address (exempt from the per-recipient limits; an email change already notifies the
  old address).
- Admin rights are read from the database on every admin action (`AdminService.IsAdminAsync`: role and no admin
  lock, not the principal's claims; R2-ACC-2), so a demoted or locked admin's open page cannot act. Nobody unlocks
  themselves; locking an admin runs under the admin-change lock with the last-admin check. `AccountGate.MayActAsync`
  refuses admin-locked accounts (a lockout from failed sign-ins does not count: anyone can cause one). Every admin
  action is also written to the application log (admin id, action, target type, account id; no notes, emails, poll
  ids or ranges).
- The Public URL must be `https://` unless its host is loopback. The Blazor hub (`/_blazor`) refuses requests whose
  `Origin` is neither the Public URL's origin nor the request's own host (`BlazorOriginMiddleware`).
- Security stamp validated every minute (cookie and circuit), so "log out everywhere", password resets,
  locks and deletes reach other sessions within a minute.

### Polls
- No poll passwords (removed 2026-10-31, §20.3): a poll link is a capability (§15); `voters: accounts` or
  `invite` narrow who may respond.
- All poll data access goes through a `PollAccessService` that answers
  `CanView / CanVote / CanSeeResults / CanEdit` from one place. Pages never query polls
  directly by id without it.
- Antiforgery on every form (Blazor forms do this by default); no custom JS fetch endpoints
  for mutations in v1.
- Markdown: Markdig with HTML disabled, links `rel="noopener"`, no raw HTML pass-through. `MarkdownRenderer` takes at
  most 10 000 characters (the legal page passes its own 100 000) and renders longer text, and anything Markdig
  refuses (nesting limit), as encoded plain text (R2-POLLS-1/4). The editor preview shows "Too long to preview."
  for text over its limit.
- Per-owner limits (open polls, polls, invites) are checked and written under `OwnerLock`, an in-process lock per
  account (single instance), so parallel requests cannot pass a check together (R2-POLLS-5). Every owner operation
  checks `AccountGate.MayActAsync`. The admin's max poll duration has a ceiling of 3 650 days.
- Security headers: CSP (`default-src 'self'`, no inline scripts except Blazor's nonce'd
  one), `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors 'none'`.
- Data Protection keys **must** persist on the volume (`/data/keys`), otherwise every
  restart invalidates all cookies (auth, `vk`, `pi_`).

### Abuse handling and captcha (5b part 2)
- Bot checks on public forms (register, forgot password, `/setup`, `/setup/recover`,
  `/setup/account/*`): a honeypot field (`website`, off-screen via CSS, `tabindex=-1`, `autocomplete=off`)
  that must stay empty (the resend-verification form too, also padded to 300 ms), and a signed render time (`_fts`, Data Protection purpose
  `Telrang.FormStamp.v1`) that must be 2 s to 2 h old (`Abuse:FormMinSeconds`, `FormMaxMinutes`).
  `FormGuardMiddleware` checks guarded POSTs after antiforgery and answers the static "Form expired" page
  (400). The vote form (§21.1) carries the same fields, but `/p/{id}` is not guarded by the middleware:
  `BallotService` checks them, with a 12 h maximum instead of 2 h (a voter with a page open for hours is not
  refused), and a refusal re-renders the posted ballot instead of the static page.
- `AbuseTracker` (singleton, memory only): signals per client key (IPv4 /32, IPv6 /48) and per account.
  Weights: honeypot 10, form timestamp and captcha failure 2, everything else 1 (failed sign-ins, 429s,
  limit refusals). No signal where honest users behind one address would add up: a captcha provider outage or a
  missing secret (`CaptchaResult.Unavailable`), a full poll, the vote rate limit, refused circuits and live streams
  (R2-VOTE-2, R2-VOTE-3c). At most 10 000 keys are tracked; past that the least recently seen go, unblocked first
  (R2-ACC-6). 20 within 10 minutes (`Abuse:Threshold`, `Window`) blocks for 1,
  then 10, then 60 minutes; 24 h without signals resets the level. Failed sign-ins count only for the
  address, never for the account (someone else's guessing must not lock its owner out; Identity's lockout
  covers the account). `AbuseBlockMiddleware` answers blocked clients and accounts with the static 429
  page, also for the Blazor hub; signed-in admins are never blocked; endpoints with `AbuseExemptAttribute` pass (static
  assets, `/branding/*`, `/healthz`; endpoint metadata after routing, not a path suffix). Admin "Abuse" tab:
  list, clear, manual block of an account (by email) with a duration and note (audit fixes: the manual in-memory
  address/CIDR block is gone; addresses are blocked only as persistent ranges, below). A restart clears
  everything; addresses are never written to disk, the database or the log.
- Persistent CIDR blocks (Phase 6, §17): admin-entered ranges in the `BlockedRanges` table, checked by the
  same middleware before the tracker; 403 page "Access from your network is blocked." A range with host bits set, a
  prefix shorter than /16 (IPv4) or /32 (IPv6), or one containing the acting admin's own address is refused
  (R2-ACC-7); `Abuse:ClearBlockedRangesOnStart=true` deletes them all at startup (the way out of a lock-out). The
  admin's own address is held in the scoped `ClientInfo` for that check only, never stored.
- Forwarded headers are trusted from a Unix socket peer (the Linux service's default) and, with
  `DOTNET_RUNNING_IN_CONTAINER=true`, from the networks of the container's own interfaces (the Docker network the proxy
  and the published port come from). Loopback TCP is not trusted by default (§31, R3-DEP-7). Any other proxy must be
  listed in `ReverseProxy:KnownProxies` / `KnownNetworks` (R2-ACC-6).
- Circuits (pentest AV-09): SignalR's receive limit is `LimitSettings.MaxCircuitMessageBytes` (about 316 kB: twice the
  editor's Text-mode text at the limit ceilings, plus 16 kB), not 1 MB. The admin's text and option caps have
  ceilings (`LimitSettings.Ceilings`: title 200, option label 200, option description 1 000, poll description 10 000,
  options 128 (100 until §27); stored values above read as the ceiling). `draft.js` returns a restored draft re-serialised as plain
  UTF-8 so it fits; a restored draft over `OptionsMax` options or twice a text limit is dropped, and the add controls
  stop at `OptionsMax` (R2-POLLS-2). `AppCircuitHandler` holds `CircuitLimiter` leases per circuit: per account
  (`RateLimiting:CircuitsPerAccount`, default 20) and per client key (`RateLimiting:CircuitsPerClient`, default 60,
  the `unknown` key included), and fails the circuit's start beyond them, without an abuse signal. Voters open no
  circuit (§21.1).
- Vote posts (§21.1): 120 per minute per client key (IPv6 per /48), no abuse signal; the single-use form ids are
  kept for 12 h in a store bounded by its queue length, reserved only after the ballot validated and marked used
  right after the commit (R2-VOTE-1, R2-VOTE-3f).
- In interactive circuits the request is gone: `AppCircuitHandler` captures the client key into the scoped
  `ClientInfo` when the circuit connects, so services record signals with the right address.
- Optional captcha (`Captcha.Provider` = None | Turnstile | HCaptcha, site key, secret protected like the
  SMTP password), only for anonymous votes in `voters: public` polls; never for sign-in, registered users
  or invite voters. The vote page loads the provider script with the CSP nonce; the CSP lists the
  provider's origins (script, style, frame, connect) only while a captcha is enabled. `BallotService`
  verifies the token through `ICaptchaVerifier` (the provider's siteverify, without the voter's IP) before
  the write transaction. Privacy/DNS note: with a captcha on, voters' browsers contact the provider (DNS
  lookup and HTTPS to Cloudflare or hCaptcha), which sees their IP address and may set its own cookies;
  mention it on the privacy page. Off by default.
- Mail (R2-POLLS-3, R2 Info d): a poll's results-ready mail goes out at most once per 24 h (reopening keeps the last
  mailing time), at most 20 results-ready mails per recipient per day, and the sender sends account and security mail
  first, giving results-ready mail at most ¾ of the hourly cap. Queued mail still unsent after 7 days (the longest
  link lifetime) is dead-lettered and its body cleared.
- Error pages: 429 (rate limiter, poll lock, blocks) and "Form expired" (antiforgery failure,
  `AntiforgeryFailureMiddleware`, and the bot checks) are static HTML written by middleware: no script,
  no nonce, one line plus a Home link (and Back when the Referer is this site). 404 pages that know more
  (expired setup key or link) put one line into the generic "Not found" page.

## 9. Deployment

- Multi-stage Dockerfile: `sdk:10.0` build, `aspnet:10.0` runtime, run as non-root user,
  `EXPOSE 8080 8443`, volume `/data` holding `app.db`, `keys/`, and optional `cert.pfx`.
- HTTPS options, in priority order:
  1. Reverse proxy terminates TLS (Caddy/Traefik/nginx). App listens HTTP on 8080,
     `ForwardedHeaders` enabled for `X-Forwarded-For/Proto` from a Unix socket peer and, in a container,
     the container's own networks by default (§8; `ReverseProxy:KnownProxies`/`KnownNetworks` for any other
     proxy, `ReverseProxy:TrustAll` to widen), WebSocket upgrade forwarded.
  2. Drop `cert.pfx` in `/data` and set
     `ASPNETCORE_Kestrel__Certificates__Default__Path=/data/cert.pfx` and
     `...__Password`. Kestrel then serves 8443 directly.
- `compose.yml` with the SQLite volume and env vars; a second commented-out service for
  Postgres for later.
- Config via env vars: `ConnectionStrings__Default` (or `ConnectionStrings__Default_FILE`), `Database__Provider=Sqlite|Postgres`
  (§28), `ASPNETCORE_URLS`. Registration, mail (SMTP), display name, public URL, legal page and limits are DB
  settings edited on the admin page, not configuration.
- First start: the log prints `Admin setup key: <key> (valid 1 h, open /setup)` at Warning; a restart
  issues a new key while no admin exists.
- Backups: SQLite: admin "Download backup" (`VACUUM INTO` into `{DataDir}/tmp`, owner-only, emptied at startup); `{DataDir}/keys`
  must be backed up separately. The data directory is created `0700` on Unix (an existing one keeps its mode;
  the systemd unit sets `UMask=0077` and `StateDirectoryMode=0700`; §29). Identity's own tokens (authenticator key, recovery codes) are stored encrypted
  with the key ring (`ProtectedTokenUserStore`, purpose `Telrang.IdentityTokens.v1`, values prefixed `dp1:`), so a
  database copy alone does not bypass 2FA (pentest AV-12).
- Migrations applied at startup (single-instance app; acceptable). PostgreSQL has its own set (§28); a moved instance
  is copied with `copy-to-postgres` while the app is stopped. PostgreSQL backups use `pg_dump` (docs/deploy).
- Health endpoint `/healthz`.
- Admin recovery (only admin lost TOTP and recovery codes, or a password with mail off): start once with
  `App__RecoveryKey=1` (or `App:IssueRecoveryKey=true`). While an admin exists, startup logs
  `Admin recovery key: <key> (valid 1 h, open /setup/recover)`; the page takes the key, then offers the
  admin accounts, sets a new password, clears 2FA and the passkeys (enrolment is required at the next sign-in), ends all
  sessions and uses up the key. Every start with the variable set logs a Warning until it is removed.

## 10. Admin page

Admin role only, tabs at `/admin`:
- Users: email (or "pending setup"), verified, 2FA, admin, created, last login, polls; actions: create
  account (setup link), invite by email, new setup link, lock/unlock, make/remove admin, mark email
  verified, reset password (24 h link; using it removes the passkeys), disable 2FA (forces re-enrolment, removes the
  passkeys), delete (soft).
  Search field (email or display name, case-insensitive, server-side, 300 ms debounce) with the match count; at most
  200 rows, "Refine your search" beyond. *Make admin…* / *Remove admin…* open a confirm tick box ("Grant admin rights
  to <email>" / "Remove admin rights from <email>"), like delete; only accounts that may act with a verified email
  (`AccountGate.MayActAsync`) can be made admin, other rows show the reason ("Unverified", "Pending setup",
  "2FA required"); removing yourself or the last admin is refused (other admins count only when they may act and are not
  locked; the check and the change run under one process-wide lock, pentest AV-15d). Logged as `user.promote` / `user.demote`.
  Promoting an account without 2FA sets `MustEnrolTwoFactor` before the stamp bump, so the new admin is gated to
  enrolment until it has 2FA; the row shows "2FA required" (pentest AV-07).
  Admins see registered emails (and display names) because administering accounts requires it; the tab is
  admin-only and every role change is in the admin action log.
  Pending accounts show when their setup link expires and which admin issued it. A "Limits" disclosure
  per account holds the per-user overrides (§14.4).
- Polls: close/reopen/delete any poll; *Transfer to…* (email of an existing, non-deleted account with a
  verified address, confirm tick box, logged as `poll.transfer`).
- Settings: registration toggle, "Auto-verify created accounts" (§22.1; disabled while mail is configured), display name, public URL (explicit field; required for mail, no mailed link is
  ever built from the request; once set, other Host headers get 400 except loopback names and `/healthz`, and the
  field refuses a host the page is not open on; share links shown on pages fall back to the request URL only while
  it is empty), about/privacy Markdown, welcome Markdown (home page), captcha,
  limits, "Delete closed polls after (days)" (retention, 0 = never); "Branding" (upload/replace/remove logo,
  banner, background and favicon with previews, formats and caps in one line, the folder path for manual
  replacement); "Theme" (layout, default mode, font, light/dark base and highlight overrides (§24.4), font size,
  background on/off and overlay 0–90 %, "Advanced: custom CSS, applied as-is" with "Not applied on admin pages.",
  preview, *Save theme*, *Reset*); "Download backup" (§17).
- Mail: SMTP settings, "Send test mail to me", queue length, dead count with *Purge dead* (a dead message has no body left to retry),
  last error, last 50 messages; "Mail needs a Public URL." while it is empty (mail counts as not configured then).
- Abuse: current blocks (key, reason, until, signals), clear (logged by kind only, never an address), manual account
  block (§8); "Blocked ranges"
  (persistent CIDR blocks: CIDR, note, expires, created by; add with optional expiry in days, remove), §17.
- Status: version, environment, uptime, public URL, database size, free disk on the data volume, counts,
  mail queue, live circuits, active blocks, 1 h / 24 h rates, last 200 Warning+ log entries (§14.8).
- Log: admin action log, newest first, 50 per page, rotated at 10 000 rows.

## 11. UI notes

- Mobile-first layout, single column; ranked list is full-width cards with a grab handle on
  the left and up/down buttons on the right (keyboard and screen-reader path).
- SortableJS via a small `js/ranking.js` interop module: Blazor owns the data, JS only reports
  `(optionKey, fromZone, toZone, newIndex)` and puts the DOM back before Blazor re-renders.
- Points poll: stepper buttons (±1, ±5) plus a range input; remaining-points pill is sticky
  at the bottom on mobile.
- Results use plain HTML/inline-SVG bars, no chart library.
- **App shells** (2026-10-28). Every page names its layout (`@layout`; a test checks it):
  - `MainLayout` for signed-in pages (home, My polls, editor, share, My votes, admin, `/Account/Manage*` via
    `ManageLayout`). It renders one of three shells chosen by `Theme.Layout` (§17); a visitor who is not signed in
    (only the home page allows that) gets the `PublicLayout` shell instead.
    - **TopBar** (default): sticky header with logo/name, primary nav (My polls, My votes, Admin for admins) and the
      account menu on the right (email, Account, Log out); content column 1040 px. Below 768 px the nav folds into a
      menu button.
    - **Sidebar**: 240 px column in the theme's nav colours (`--av-nav-start/end`, dark shades of the base colour) with the
      logo, *New poll*, grouped nav (Polls / Server) and the account block (email, Account, Log out) pinned at the
      bottom; content up to 1200 px. Below 768 px an off-canvas drawer opened by a CSS-only checkbox toggle.
    - **Compact**: 56 px strip (logo, small text nav from 576 px, account menu that also holds the nav on phones);
      760 px column; the editor stacks text above preview. For small, form-centred deployments.
  - `PublicLayout` for anonymous-facing pages: `/p/{id}`, `/p/{id}/results`, `/setup*`,
    `/notify/*`, `/about`, `/Error`, `/not-found` and the `/Account/*` sign-in pages (login, register, forgot/reset
    password, 2FA, confirmations). Slim header: logo/name and either *Log in* (back to the current page, hidden on
    `/Account/*`) or the account menu, which then also lists My polls / My votes / Admin. No app nav; 720 px
    column. The static 403/429/form-expired pages (`StaticPages`) copy its header (name only).
  - Shared pieces in `Components/Layout`: `PageHeader` (h1 + optional actions slot, used by the signed-in pages),
    `UserMenu`, `AppNav`, `AppBrand`, `SiteFooter` (About link, version), `EmptyState`, `Icon`. Icons are one SVG
    sprite, `wwwroot/icons.svg` (`<use href>`; no icon font, no CDN).
  - Dropdowns are `<details data-menu>`: they work without JS; `js/menus.js` only closes them on an outside click,
    Escape or a chosen item. No inline styles or scripts anywhere (CSP).
  - Look: a faint grey canvas (`--av-canvas`) with off-white surfaces (header, cards, lists, tables) on `--bs-body-bg`;
    type scale h1 1.75rem/600, h2 1.25rem, h3 1.125rem, body 1rem, small 0.875rem; 1 px borders, no card shadows;
    muted table headers; neutral `btn-outline-secondary`; subtle status badges (one style per colour); visible
    `:focus-visible` outlines. Every colour is a `--av-*` token from the theme colours (below); app.css maps Bootstrap's
    variables onto them once, and the light/dark values switch in the `data-bs-theme=dark` and system-dark blocks.
- **Identity: base and highlight** (2026-10-28; per mode since §24.4). Each mode has two colours, the built-in
  Teal / Coral or the admin's override for that mode (§17, §24.4); everything else in that mode is derived from them (`PaletteDerivation`, OKLCH, WCAG AA checked in `PaletteTests`). Components never hard-code a
  colour; they use the tokens.
  - **Base** (identity, carries weight): primary buttons (`btn-primary`, one per page: Submit ballot, Create poll,
    Save; secondary actions stay outline), brand mark and name, links, active nav item (base at low alpha), the
    account avatar, checked inputs and sliders, result bars and histograms, rank badges 2 and on, Sidebar background.
  - **Highlight** (sparingly, never for text on a surface): the 3 px band on top of every header (`--av-header-band`),
    the active-nav underline, focus rings/outlines, the rank-1 badge, the leading result bar (`bar-fill-lead`, every
    option in first place), the link-hover underline and the "1" of the brand mark. Not the "Open" badge
    (`badge-open`, base tint: the highlight's tint reads as a warning in some palettes). Text in the highlight's hue
    uses `--av-accent-text` (darkened/lightened to AA on its tint).
  - **Dark mode**: near-black surfaces with a trace of the base hue, base lifted as far as white text on it still passes
    AA, highlight lifted to OKLCH L 0.8, body text off-white (never `#fff`). **Light mode**: off-white surfaces on the
    faint grey canvas.
  - Status colours (success, warning, danger) are fixed hues nudged 12 % toward the base hue, as tint + AA text pairs.
  - **Brand mark**: a budget split, points spread over options as dots of unequal size (`icons.svg#brand-mark`, 24-unit
    box): a square with three round holes, radius 3.5 at (19, 6.5), 4.5 at (18, 16.5) and 2.8 at (8, 19) (one even-odd
    path, `currentColor` = base; the holes show the surface, or the Sidebar's band), and one large dot, radius 6.5 at
    (9, 9), that inherits `fill` = highlight. 1.5 rem (24 px) in every header; it sits next to the name when no logo is
    uploaded (the logo replaces both). `/branding/favicon.svg?v=<theme version>` (`ThemeCss.Favicon`) is the same
    geometry: base square, small dots in the base's text colour, large dot in the highlight, linked from every page and
    the static error pages; `wwwroot/favicon.png` (64 px, the default palette) is the fallback for browsers without SVG
    icons; an uploaded favicon replaces both.

## 12. Implementation phases

Phase 1 — skeleton:
solution + projects, Identity (login, register, TOTP, passkeys), EF + SQLite, Docker, CI
build, security headers, rate limiting, admin seed. Deliverable: deployable app with accounts
and an empty "My polls" page.

Phase 2 — Core (in parallel with phase 1):
`PollId`, DSL parser, ballot validation, Borda/Dowdall/Points scoring, full xUnit coverage.
Deliverable: green tests, no web dependency.

Phase 3 — Polls + ranked voting:
create/edit with live preview, share page, password gate, `voters: link` and `invite`,
ranked DnD vote page, results page.

Phase 4 — Points voting, CSV export, admin page, polish, deployment doc.

Phase 5a — accounts (setup key, setup links, soft delete), DB settings, mail outbox, verification,
recovery, self-service account page, admin tabs (settings, mail, log), backup. Phase 5b — voter modes,
"my votes", limit enforcement, abuse handling. See §14.

Phase 6 — branding images, theme stylesheet, welcome text, persistent CIDR blocks. See §17.

Phases 3–4 after both land, sequential, with a review pass between them.

## 13. Decisions log

Finding IDs (AV-, R2-, R3-) refer to internal audit and pentest reports; each entry states the fix.

- 2026-10-25: voters default `link`, `invite` optional. Scoring chosen at creation, locked
  afterwards. Creator may vote in own poll. Postgres deferred (superseded: §28). Namespace stays
  `Telrang` (matches the repo); the user-facing app name is configurable via
  `App:Name` and can be changed any time. (Since 5a the name is the DB setting `App.DisplayName`, admin Settings
  tab; `App:Name` is only the fallback while it is empty and is no longer in appsettings.json, audit fixes.)
- 2026-10-25 (Core, phase 2): spec ambiguities resolved in `Telrang.Core`:
  - `closes` parses `yyyy-MM-dd` into `DateTimeOffset?` = 23:59:59.9999999 UTC of that day (inclusive end of day).
  - `#` starts a comment only at line start or after whitespace, so `title: C# night` keeps its `#`. Header keys are case-insensitive; enum values case-insensitive.
  - Points polls carry `Scoring = Borda` (placeholder) and `MaxBans = 0`/`RankAll = false`; `budget` is required for points polls; `max-per-option` must be > 0.
  - Errors not tied to a header line (missing `title`/`type`/`budget`, `ValidateEdit`) use line 1; missing `---` reports the last line; "fewer than 2 options" reports the line after the separator; duplicate-label errors report the duplicate item's line; `bans` >= option count reports the `bans` line. A `bans` value of 0 never errors.
  - A missing `---` separator is an error. Unindented non-list text after the option list began is also an error (to avoid silently dropping text); a second list after such text reports "second list". Indented lines (including nested lists) under an item belong to that item's description.
  - Empty slug (e.g. non-ASCII-only label) becomes `option`; de-dup loops until unique (`foo`, `foo-2`, `foo-3`; a label whose slug is already `foo-2` gets `foo-2-2`).
  - `BallotValidator`: banned keys count toward `MaxBans` after de-duplication but duplicates are also reported; under `RankAll` every non-banned option must be ranked; wrong-type ballot for the poll returns an error; omitted options in a points ballot count as 0.
  - `PollId.TryDecode` also rejects non-canonical encodings (non-zero trailing bits in the 22nd char) so each Guid has exactly one URL spelling.
  - Result ordering compares scores rounded to 9 decimals (float noise in Dowdall sums must not break ties), then BanCount asc, then Label (case-insensitive, then ordinal).
  - `PointsResults.Histogram(ballots, key)` returns value -> voter count (zeros included).
- 2026-10-25 (phase 1, web skeleton):
  - Layout: Identity pages stay where the template put them (`Components/Account/`), and
    `ApplicationUser`/`ApplicationDbContext` stay in `Data/`; no separate `Identity/` folder. New
    pages live under `Features/` (`Polls/`, `Admin/`), security plumbing under `Security/`.
  - Admin bootstrap: config key is `Admin:Email` (env `Admin__Email`); `ADMIN_EMAIL` is accepted
    as a fallback. Without it, the user who is the only account at creation time gets Admin.
    Without email verification, whoever registers that address first wins; documented in
    the deploy notes (now `docs/deploy/`).
  - Registration mode is config-only (restart to change); the admin-page toggle from §10 is
    deferred to phase 4. InviteOnly: admins create accounts at `/admin/users/new`.
  - CSP: `script-src 'self' 'nonce-…'` (the nonce is only used by Blazor's import map),
    `style-src 'self'` with no `unsafe-inline`, `img-src 'self' data:` (Bootstrap icons),
    `connect-src 'self'` plus the explicit same-host ws/wss origin, `form-action 'self'`,
    `frame-ancestors 'none'`. The template's inline `onclick` (nav menu) and one inline `style`
    attribute were removed. Verified in headless Firefox: no violations on home, account,
    manage, admin and polls pages; circuit connects; @onclick works.
  - Rate limiting: Identity pages share the single Razor Components endpoint, so a global
    partitioned limiter matches POSTs to `/Account/{Login,Register,LoginWith2fa,
    LoginWithRecoveryCode,ForgotPassword,ResetPassword,ResendEmailConfirmation}`: sliding
    window, 10/min per client IP (IPv6 per /64; superseded 2026-11-05: per /48), configurable. Named policy `PollPassword` for
    phase 3. Login now counts failures toward lockout (`lockoutOnFailure: true`).
  - Forwarded headers trust any source by default (proxy IP unknown in containers), with
    `ForwardLimit = 1`; `ReverseProxy:KnownProxies`/`KnownNetworks` restrict it. HTTPS
    redirection only when an `https://` URL is configured, never for `/healthz`.
  - Cookies: auth (and 2FA/external) cookies `Secure=Always` outside Development;
    `Security:AllowInsecureCookies=true` is a documented plain-HTTP-testing escape hatch.
    Antiforgery cookie is `SameAsRequest` because `Always` makes every plain-HTTP page throw.
  - The template's RegisterConfirmation page displayed the email-confirmation link whenever the
    no-op email sender was registered (anyone could confirm any address); removed.
  - `Database:Provider` other than `Sqlite` fails fast at startup.
  - Test-only package added: `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory) in
    `tests/Telrang.Web.Tests`.
- 2026-10-25 (phase 3, polls + ranked voting):
  - Schema (migration `AddPolls`): no `PollOption` table and no `Settings` column. Options and
    every setting live in `DefinitionJson` (the parsed `PollDefinition`, enums as strings); Title,
    Type, Scoring, ResultsVisibility, VoterMode and ClosesAt are denormalised columns. The DSL
    source is re-parsed only on edit (`Source` dropped 2026-10-31, §20). Enums stored as strings, Guid keys generated in code,
    `DateTimeOffset` columns; SQLite cannot ORDER BY/compare `DateTimeOffset`, so such sorting
    is done in memory (lists are per owner and small). Poll -> Ballot/InviteToken cascade;
    InviteToken.BallotId is SET NULL.
  - `Poll.PasswordVersion` (int) is bumped whenever the password is set, changed or removed;
    grants carry it, so changing the password signs everyone out. Poll passwords use
    `PasswordHasher<Poll>` (same 600k-iteration options); minimum 4, maximum 200 characters
    (they are shared secrets, not account passwords).
  - Data access: `AddDbContextFactory` (also registers the scoped context Identity uses). Poll
    services create a short-lived context per operation, because a scoped context lives as long
    as a Blazor circuit and would serve stale tracked entities.
  - `PollAccessService.GetAsync(encodedId, VisitorContext)` is the single entry point; an overload
    takes the caller's DbContext and can return a tracked Poll for owner mutations. Null = 404
    (undecodable, unknown, and - for owner operations - not yours). `VisitorContext` = {UserId,
    password grant, link voter key, invite token hash}, built from cookies (HTTP) or a ticket
    (circuit). Denial reasons are an enum plus text.
  - Rendering: `/p/{id}` and `/p/{id}/results` are static SSR (`[ExcludeFromInteractiveRouting]`)
    because they read and set cookies (grant, invite, `vk`), which a circuit cannot. The ballot
    editor is an Interactive Server island (`RankedVote`) that receives a "voter ticket": the
    `VisitorContext` (minus the user, which comes from the circuit's auth state) protected with a
    time-limited Data Protection protector (12 h). Owner pages (`/polls`, new, edit, share) stay
    globally interactive.
  - Cookies (all HttpOnly, SameSite=Lax, Secure under the auth-cookie policy, Data Protection
    with distinct purposes): `pg_{id}` grant {pollId, issuedAt, passwordVersion}, 30 days, path
    `/p/{id}`; `pi_{id}` invite token hash {pollId, hash}, 1 year, path `/p/{id}`; `vk` raw
    128-bit voter key, 1 year, path `/`, created when a link-mode poll is first viewed.
  - Password gate: `POST /p/{id}/unlock` minimal API (form-bound, so antiforgery is validated by
    `UseAntiforgery`; missing token = 400), `RequireRateLimiting("PollPassword")`. Wrong password
    redirects to `/p/{id}?pw=wrong` (generic message). Correct password re-hashes if needed.
  - Invite links: token = 22-char base64url of 16 random bytes, stored as base64url(SHA-256(bytes)).
    `/p/{id}?t=` validates, stores the hash in `pi_{id}`, redirects to `/p/{id}`. Unknown or
    malformed token = 404. A token presented while the password gate is still closed is stored
    unverified and checked after unlocking. Tokens per batch: max(count, label lines), up to 500.
    The owner votes in invite mode only with a token generated for themself.
  - Editing: when ballots exist, they must all be deleted (with the "I understand" confirmation,
    same transaction) if option keys change **or** any stored ballot is invalid under the new
    definition (e.g. `bans` lowered, `rank-all` turned on). Text-only edits keep ballots.
    Switching `voters:` between link and invite is allowed; existing ballots stay counted.
  - Ranked UI: "Not ranked" is always shown in definition order (drops into it ignore the index).
    Touch: long-press 200 ms (`delay` + `delayOnTouchOnly`), `fallbackTolerance` 5; buttons,
    links and `<summary>` never start a drag. After a keyboard move focus stays on the moved
    option and an `aria-live` region announces it.
  - Results bars are inline SVG (`width`/`height` attributes) because the CSP forbids `style`
    attributes. Points results show "not supported yet"; `ResultsService` already computes
    `PointsResultSet`, and `BallotService.SubmitPointsAsync` exists for phase 4.
  - Markdown: Markdig pipeline `DisableHtml` + autolinks, emphasis extras, pipe tables, soft line
    breaks as hard; links get `rel="noopener noreferrer" target="_blank"`; link/image URLs with a
    scheme other than http/https/mailto are replaced by `#`. Package `Markdig` 1.5.0 added.
  - SortableJS 1.15.7 vendored as the ESM build `wwwroot/lib/sortablejs/sortable.esm.js` (+ MIT
    `LICENSE`), from the npm tarball whose sha512 matched the registry `integrity`
    (`Kk8wLQPl...ux0A==`); file sha256 `a48a4ec931f04c50a1b80c095d0610dc2ec2014d001d4f083405f808fc9be251`.
    No SRI attribute: it is same-origin and loaded by `import`, not a `<script>` tag.
  - Tooling: `dotnet-ef` 10.0.13 is a local tool (`dotnet-tools.json`); run `dotnet tool restore`.
- 2026-10-25 (phase 4, points voting, CSV, admin, review fixes):
  - Security review fixes (each has a test in `tests/Telrang.Web.Tests/SecurityTests.cs`):
    - A1 `ADMIN_EMAIL` fallback never fired because `Admin:Email` is bound to `""` (appsettings,
      compose), not null. Blank/whitespace now counts as unset everywhere.
    - A2 Forwarded headers are no longer trusted from any source. Default: loopback + 10/8,
      172.16/12, 192.168/16, ::1, fc00::/7. `ReverseProxy:KnownProxies`/`KnownNetworks` replace that
      list; `ReverseProxy:TrustAll=true` is the explicit opt-in to the old behaviour. Cert-file mode
      should set `KnownProxies=127.0.0.1` (Docker's userland proxy can make every client look like
      the bridge gateway). Deploy notes explain the three settings.
    - A3 Poll password minimum raised from 4 to 8. New in-memory per-poll failure window
      (`PollPasswordFailures`, default 20 per 10 min, any IP): when full, the unlock endpoint answers
      429 "Too many attempts" before checking the hash, so a correct guess cannot be confirmed
      either. Trade-off: an attacker can keep a poll's gate shut for new browsers; browsers that
      already hold a grant are unaffected, and the window is short. The per-IP `PollPassword`
      policy now buckets IPv6 per /48 (other limits stay per /64; superseded 2026-11-05: all per /48).
    - A4 `POST /Account/PasskeyRequestOptions` joins the rate-limited Identity POSTs. Login looks
      the user up first and, if unknown, verifies against `DummyPasswordHash` (random password,
      hashed once at startup with the real 600k options). The lockout-redirect oracle stays (§8).
    - A5 `PollAccessService` no longer overwrites an earlier denial with `Closed`, so an unknown
      invite token on a closed poll is a 404 instead of a redirect.
    - A6 Link-mode `Ballot.VoterKey` is now base64url(HMAC-SHA256(secret, vk || pollId bytes))
      (`VoterKeys`), not the raw `vk` cookie value, so the database cannot link one browser across
      polls. The 32-byte secret is random, stored Data-Protection-encrypted in
      `{DataDir}/keys/voterkey` (stable across restarts as long as the key ring is). **VoterKey
      format changed**: link-mode ballots from earlier dev databases no longer match their browser
      (they still count; those voters just cannot revise). Discard old dev data.
    - A7 Added the reviewer's missing tests (stranger owner-ops, cross-poll grant, Clear-then-Set
      grant version, stale-grant ticket, ticket expiry, Markdown URL schemes, after-vote results
      for another browser). To make ticket expiry testable with a fake `TimeProvider` the ticket
      now carries `IssuedAt`, checked against the injected clock (the time-limited protector's own
      wall-clock expiry stays as a second check).
  - Points ballot (`PointsVote.razor`): same ticket-based island as `RankedVote`. Per option:
    -5/-1/number/+1/+5 and a range slider on one value. Values clamp to [0, min(max-per-option,
    budget)]; an increase that would exceed the budget is refused with a hint (never silently
    clamped), so the sum can never exceed the budget in the UI. The inputs use
    `@bind:get`/`@bind:set`, not `value` + `@onchange`: only bind-generated attributes are synced
    from the browser before re-render, so with plain `value` a refused edit left the typed number
    in the field (found in the browser run). The remaining counter sits in a sticky bar at every
    width (`aria-live`), says "must spend all" under `spend-all` until it reaches 0.
  - Points results: total, mean (over all voters), supporters (count and % of voters), max, total
    bar, and a five-bucket spread per option (0, 1–25%, 26–50%, 51–75%, 76–100% of the per-voter
    cap; labels show the real point ranges). `ResultsService` adds the buckets (`PointsBuckets`)
    from `PointsResults.Histogram`; still computed on read.
  - CSV: `GET /polls/{id}/export.csv` (one row per ballot: DisplayName, SubmittedAt, UpdatedAt in
    ISO UTC, then per option the rank, `BAN` or empty / the points) and `/polls/{id}/results.csv`.
    Minimal APIs, `RequireAuthorization` (anonymous -> login redirect), owner-only through
    `PollAccessService` (others and unknown -> 404), RFC 4180, CRLF, UTF-8 with BOM,
    `Content-Disposition: attachment` with an ASCII file name from the title, `Cache-Control:
    no-store`. Text cells starting with `= + - @` (tab/CR) get a leading `'` against CSV/formula
    injection (voter names are untrusted). Links use `download` so Blazor's router leaves them alone.
  - Admin (`/admin`, tabs via `?tab=polls`): `AdminService` re-checks the Admin policy on every call
    and is resolved from a fresh scope per action. Polls tab lists every poll with close/reopen and
    delete. Users tab adds reset 2FA (disable, remove authenticator key and recovery codes, new
    security stamp) and delete user (refused for yourself and for the last admin; deletes their
    polls first, same transaction). Admin by-id poll lookups go through the new
    `PollAccessService.GetForAdminAsync`. Destructive actions use an inline "I understand" checkbox,
    never a JS confirm. Registration mode stays config-only and is shown read-only.
  - Deleting polls (owner on `/polls`, admin) is explicit (`PollService.DeleteCascadeAsync`: invite
    tokens, ballots, poll) rather than relying on the provider's FK cascade. Owners can revoke
    unused invite tokens (used ones identify a ballot and stay). Results page has a Refresh link
    (plain GET; results are computed per request) and shows when they were computed.
  - B6 glitch ("error applying batch" / "There is no browser renderer with ID 1" after registering):
    not reproduced in ~20 registration runs (incl. navigating away 150 ms after submit). Checked:
    `Register.razor` signs in and redirects from a static, non-enhanced form POST (plain 302 to `/`),
    so the redirect itself cannot race a circuit. What does reproduce, every time, is a browser
    console "uncaught exception: WebSocket is not in the OPEN state" when an Account link is
    clicked on an interactive page: the interactive router forces a full reload into the
    `[ExcludeFromInteractiveRouting]` page while the circuit is still sending. A typed URL causes
    none; `data-enhance-nav="false"` on the link does not change it. That is the same
    interactive-to-static teardown race and most likely the source of the reported batch error. It
    is a framework behaviour, harmless (the new page loads normally, nothing is logged server-side),
    so no app change was made.
- 2026-10-25 (phase 5a, accounts, settings, mail, admin; migration `Phase5aAccounts`):
  - Supersedes the phase-1 admin bootstrap (`Admin:Email`/`ADMIN_EMAIL`, first registrant) and the
    config-only `Registration:Mode`; both are gone from code, appsettings and compose.
  - Setup key (§14.1): `SetupKey` singleton keeps SHA-256 + expiry only (1 h); `AdminBootstrapper` issues
    it at startup while no non-deleted admin exists and logs `Admin setup key: <key> (valid 1 h, open
    /setup)` once at Warning. `/setup` is static SSR, 404 when an admin exists or no key is live, POSTs
    limited to 5/min per IP (`RateLimiting:SetupPermitPerMinute`). Admin creation is serialised (static
    semaphore) and the key is revoked afterwards, so two correct submissions cannot both win; a wrong
    password/email does not burn the key. The admin's email is marked verified; TOTP is forced.
  - Gate (superseded 2026-10-26, see the fix-round entry below: now an authorization requirement):
    `AppClaimsPrincipalFactory` adds claim `av:gate` (`setup` | `2fa`), `AccountGate` middleware
    (after the rate limiter) answered 303 to `/Account/Manage/EnableAuthenticator` for everything except
    enrolment, logout, `/Account/ConfirmEmail`, `/setup*`, `/about`, framework paths and static files (by
    extension, never under `/polls` or `/admin`). Enrolment clears `MustEnrolTwoFactor` and refreshes the
    cookie. A claim instead of a per-request DB read: the flag can only be set by an admin action that
    also bumps the security stamp, so a stale cookie dies within the 1-minute validation interval.
  - Security stamp validation interval lowered from 30 to 1 minute (cookie validator and circuit
    revalidation). Pages that bump the current user's stamp (enable/disable 2FA, new authenticator key)
    now refresh the sign-in; before, the template signed such users out at the next validation.
  - `AppSignInManager` (subclass): `CanSignInAsync` refuses `DeletedAt`/`PendingSetup`;
    `SignInWithClaimsAsync` stamps `LastLoginAt` except during `RefreshSignInAsync`. Covers password,
    2FA, recovery-code, passkey and registration sign-ins.
  - Pending/deleted accounts have no email, which `RequireUniqueEmail` forbids; it is off and
    `OptionalEmailValidator` enforces "valid and unique when set". Pending and deleted accounts use their
    Id as UserName. Admin-created accounts and invites get a `SetupLink` (32 random bytes, SHA-256
    stored, 7 days; a new link kills older unused ones). Invite links verify the address; "create
    without email" sends a verification mail at setup when mail is configured, else the admin marks it.
  - Settings: `AppSetting` rows read through `SettingsService` (singleton, snapshot cached, invalidated
    on every write; blank = delete = default). `Mail.Password` is protected with purpose
    `Telrang.Settings.Secret.v1`. `IOptions<AppOptions>` is served by `DynamicAppOptions`, so
    `App.DisplayName` reaches every page without touching them. Added `App.PublicUrl`: links in mails use
    it rather than the request's Host header (host-header poisoning of reset links); saving the mail
    settings pins it to the admin's current URL when empty.
  - Outbox: limits applied at enqueue (cooldown per recipient+type, daily max per recipient over all
    types; recipient compared case-insensitively), drops are silent to the caller and logged at
    Information. The admin's test mail is exempt from the per-recipient limits (so SMTP can be retried
    right away) but counts toward the hourly cap. Mail is not queued at all while mail is not configured.
    Sender: 10 s `PeriodicTimer`, rolling-hour cap, backoff 1/5/15/60 min, the fifth failure
    dead-letters (the 240-minute step of the list is therefore never reached). `Mail:BackgroundSender=
    false` disables the loop (tests drive `ProcessDueAsync`). Messages are plain text only.
  - Timestamps compared in SQL on the new tables (`OutboxMessage`, `SetupLink.CreatedAt/ExpiresAt`) use
    `DateTimeOffsetToBinaryConverter` (long column, 0.1 ms precision), because SQLite cannot compare
    `DateTimeOffset`; provider-neutral.
  - Verification/email change keep Identity's own tokens (`DataProtectorTokenProvider`, 1 day, bound
    to the security stamp). Email change mails the new address; the switch (Email + UserName) happens on
    the link; the old address gets a notice then. A new address that is taken gets the same answer and
    no mail.
  - Password recovery: `SetupLink` with purpose PasswordReset, 30 min (self) or 24 h (admin), stores the
    security stamp at issue and is dead once it differs (any password change). One self-service link per
    account per 15 min. The reset uses Identity's `ResetPasswordAsync` with an internally generated token
    (validates, bumps the stamp), then marks all open reset links used and clears failed-attempt lockout;
    an admin lock (end = max) is kept. `/Account/ResetPassword?token=` replaces the template flow.
  - Soft delete as §14.3 (also removes passkeys, external logins and roles, sets an admin-style lock).
    The last admin cannot delete themselves on the account page either. Admin delete is the same code.
  - Download my data: JSON with profile, passkey names, roles and owned polls with their DSL source (since 2026-10-31: their definitions).
    `ballots` is an empty array: ballots are not linked to accounts until Phase 5b adds `Ballot.UserId`.
  - Admin action log: every AdminService mutation (incl. settings/mail saves with the changed keys, never
    secret values, test mail, backup). Ordered by the identity Id; trimmed to 10 000 after each insert.
  - Backup: `POST /admin/backup` (antiforgery checked explicitly, Admin policy, fixed window 1/min per
    admin), `VACUUM INTO` a temp file streamed with `DeleteOnClose`.
  - Time zone: `<select data-default-tz>` on the profile page; `wwwroot/js/timezone.js` (module loaded in
    App.razor, re-run on `enhancedload`) selects the browser's IANA zone when the field is empty. The
    list is `TimeZoneInfo.GetSystemTimeZones()` (Windows ids converted to IANA).
  - Migration data fix: existing accounts are marked email-verified, so upgrading does not stop them
    from creating polls.
  - Template leftovers removed: no-op email sender, RegisterConfirmation, InvalidPasswordReset,
    DeletePersonalData (merged into "Data and sessions"), the external-login hint text, the "admin
    creates user with password" page (`/admin/users/new`): no temporary passwords in the clear.
  - New package: MailKit 4.18.2 (MimeKit, BouncyCastle transitively).

- 2026-10-26 (fix round for the user-flow review (2026-10-26): findings 1-4, 6, 9, 20, 26, 30; tests in `FlowFixTests.cs`):
  - Account gate is the `NotGatedRequirement` (fails while the principal has `av:gate`; anonymous
    passes), part of the default policy (`[Authorize]`, `RequireAuthorization()`), the Admin policy, the
    fallback policy (everything without metadata, incl. the Blazor hub and unknown URLs) and the named
    `NotGated` policy that otherwise-anonymous interactive pages (`/`, `/about`) carry, because
    `AuthorizeRouteView` ignores the fallback policy. Opt-outs: `[AllowAnonymous]` on enrolment,
    `/Account/ConfirmEmail`, `/setup*`, the logout endpoint, static assets and `/healthz`.
    `GateAuthorizationResultHandler` turns a gate-only refusal into 303 to enrolment (pending-setup:
    sign out). `NotAuthorized` in `Routes.razor` force-loads the enrolment page for gated principals.
    The `AccountGate` middleware and its path allow-list are gone. AdminService, PollService
    (create/update) and BallotService (signed-in submit) also check the account row
    (`AccountGate.MayActAsync`), so a circuit holding an older principal cannot act. Enrolment's
    `RefreshSignInAsync` re-runs the claims factory, so the user proceeds without logging out. A test
    asserts every interactively routable page declares authorization (NotFound/Error excepted).
  - Link-mode identity requires a cookie round trip: the first vote page view without `vk` sets it
    and redirects once to `?ck=1`; with the cookie back, a second redirect drops the marker; without
    it, the page shows the options and "Cookies are required to vote." + *Reload*, no ticket. Closed
    polls skip the check. Chosen over "render disabled on first view" so normal first visits never see
    the cookie line; the marker prevents loops.
  - Changing `voters:` with ballots present counts as a ballot reset like an option change
    (acknowledgement, ballots deleted, invite tokens unlinked, one transaction).
  - Reset links: the 15 min self-service cooldown counts every reset link (admin-issued too); inside
    it a request is a no-op. A self-service link is created (and replaces older ones) only when its
    mail was queued; `IMailQueue.EnqueueAsync` now returns whether it queued (never shown to users).
    Admin-issued links always replace older ones (the admin has the URL in hand).
  - `vk` is deleted on every real sign-in and on sign-out (`AppSignInManager`), not on cookie refresh.
  - Share/invite links use `App.PublicUrl` when set (`SettingsService.LinkBase`, shared with mail
    links). Edit/share of foreign or unknown polls answer 404 via `NavigationManager.NotFound()`.
    *Close* is hidden for date-closed polls (share page, admin Polls tab). Forgot password with mail
    off shows "Password recovery is not available on this server." instead of the form.

- 2026-10-26 (phase 5b part 1: voter modes, responses, my votes, caps, time zones, copy pass;
  migrations `Phase5bVoters` + `Phase5bVotersBackfill`; tests in `Phase5bTests.cs`):
  - Core: `VoterMode` is `Accounts | Public | Invite`; the parser reads the old `link` as `Public`
    (stored sources keep parsing) but error messages and the editor example only name the new values.
    New keys `responses: one | many` (`ResponsePolicy`, init property, default One) and `timezone:`
    (`PollDefinition.TimeZone`, syntax only). `PollDefinition.ClosesAt` (end of day UTC) is replaced by
    `ClosesOn` (`DateOnly?`): Core has no zone database, so the instant is the host's job.
    `ParseResult.KeyLines` gives header key lines so the host can report its own checks (unknown zone)
    on the right line. `PollLimits` + `Parse(source, limits)`; the source cap reports the line on which
    the byte count passes the limit.
  - Web parse path: `PollParsing.Parse` = Core parser with the admin's limit settings + IANA check
    (`PollTime.Find`: `TimeZoneInfo.TryFindSystemTimeZoneById` and `HasIanaId`, or `UTC`). Used by the
    editor preview and by PollService on create and edit. The 100 000-char pre-check stays as a cheap
    guard before parsing.
  - Close instant: `PollTime.CloseInstant(closesOn, zone)` = one tick before the next local midnight
    (a midnight that falls into a DST gap moves forward to the first valid time), stored in the
    existing `Poll.ClosesAt` column, so `IsClosed` and all SQL-free comparisons are unchanged. When the
    source has no `timezone:`, the creator's account zone (else UTC) is written into the definition
    snapshot only (the source stays as typed); edits without the line keep the stored zone.
    Display: viewer's account zone when set and valid, else the poll's, always with the zone name.
  - Stored definitions from before 5b are upgraded on read (`PollJson`): `"voters":"Link"` →
    Public, legacy `closesAt` → `ClosesOn` (UTC date), missing zone → UTC. The `Poll.VoterMode`
    column is rewritten by the migration (EF's string enum conversion would throw on `Link`).
  - Ballot identity: `VoterIdentity` is a plain stored column filled by BallotService (`u:` + user
    id, `k:` + voter key), not a computed column, to stay provider-neutral; unique (PollId,
    VoterIdentity, ResponseNo). `VoterKey` is null for account ballots. `UserId` FK is SET NULL (users
    are only soft-deleted anyway). `PollAccess.Voter` is now a `VoterIdentity` record and
    `PollAccess.ResponseCount` the visitor's response count.
  - Migrations: two, because SQLite rebuilds the Ballots table for the nullable `VoterKey` and the new
    FK, and EF warns (30200) when data SQL runs while a rebuild is pending. `Phase5bVoters` changes the
    schema; `Phase5bVotersBackfill` sets `VoterIdentity = 'k:' || VoterKey`, `ResponseNo = 1`,
    `VoterMode 'Link' → 'Public'`, then creates the unique index. Standard SQL. Down reverses it
    (keeps response 1 only). Verified by a test that seeds a Phase-5a-shaped SQLite file, and by
    up/down/up with `dotnet ef database update` on a scratch file.
  - Access: `accounts` needs a signed-in account with a verified email that is not pending, gated or
    deleted (new denials `SignInRequired` "Sign in to respond.", `EmailNotVerified` "Verify your email
    to respond."). `public` + signed in = account identity: the vote page then sets no `vk` and skips
    the `?ck=1` round trip. `invite` votes by token even when signed in, and stores `UserId` when
    known. after-vote results also open for a signed-in user whose account has a ballot in the poll
    under another identity (invite link used while signed in). `Closed` text is now "Poll closed.".
  - Responses: `one` revises response 1 whatever number is asked for. `many`: a submit without a
    number adds max+1, with a number revises that response of the same identity (else "That response
    no longer exists."). The ticket carries the response number from `?r=N` (VisitorContext gets an
    optional `ResponseNo`, not part of the identity). Delete is `many`-only, open polls only; an invite
    token pointing at the deleted response moves to the voter's latest remaining one.
  - Edit: switching `responses: many` to `one` needs the ballot-deletion acknowledgement (and deletes
    all ballots, as other resets do) only when some identity has more than one response; `timezone:`
    and `closes:` changes never do.
  - UI: the vote island's "after submit" view is a shared `VoteSummary`; `ResponseList` and
    `BallotView` are static components reused by the closed-poll view (finding 16: the voter's ballot
    or responses read-only, "Poll closed.", results link when visible). Rules are one line under the
    title (`PollText.RulesLine`) with a "How scoring works" `<details>` (`PollText.ScoringDetails`),
    also on the results page. The anonymous cookie line ("A cookie keeps your ballot editable in this
    browser.") links "cookie" to `/about` only when the about page exists (it is 404 otherwise). The
    share page states the mode in one line (`PollText.ModeLine`) and the close time with its zone.
  - `/my/votes` (default policy, so not for gated sessions): polls with a ballot whose `UserId` is
    the user, each loaded through `PollAccessService`; the results link follows the visibility rule
    (owner, open, after-vote), the results page re-checks (including the password gate). Anonymous
    ballots are not listed (they cannot be attributed). "Download my data" now lists the same ballots
    (poll title, type, response number, name, times, ballot payload).
  - Known limit: in invite mode the vote page identifies by token cookie only, so a signed-in user who
    voted with an invite link on another browser sees the poll on "My votes" but not their ballot on
    the vote page without the link.

- 2026-10-26 (phase 5b part 2: limits, abuse handling, captcha, Status tab, remaining user-flow review findings;
  migration `Phase5bLimits`; tests in `Phase5b2Tests.cs`):
  - Per-user limits: `AspNetUsers` gets six nullable override columns (`LimitActivePolls`,
    `LimitPollsPerDay`, `LimitBallotsPerPoll`, `LimitInvitesPerPoll`, `LimitPollDurationDays`,
    `LimitResponsesPerVoter`) and `SelfRegistered`. `EffectiveLimits.From` = override, else global
    setting; 0 = no limit. Self-registered accounts younger than 24 h get half of the two creation limits
    (at least 1) unless overridden. Ballot, invite, duration and response limits are the poll owner's.
  - Open polls: owner's polls with no `ClosedAt` and no past `ClosesAt`; checked on create and on reopen.
    New polls per day: polls created in the last 24 h that still exist (a deleted poll no longer counts;
    accepted, the open-polls limit still holds). Ballots per poll counts rows (responses), checked only
    when a new row would be added, so revisions always pass. Invites per poll: existing tokens + the
    batch (revoked tokens are deleted, so they free room). Refusals are one line "Limit reached: N …"
    and count as an abuse signal.
  - Max duration: `closes:` may be at most N days after the poll's creation date (line-numbered parser
    error in the preview and on save, also on edit, counted from creation, so edits cannot extend it).
    Without `closes:` the stored `ClosesAt` is `CreatedAt + N days` (recomputed on edit with the current
    setting), so every existing "now ≥ ClosesAt" check (vote, results, exports, lists) agrees and the
    share and vote pages show the date. No background job. Polls created before this change keep a null
    `ClosesAt` until edited.
  - SQLite: `Poll.CreatedAt`/`ClosesAt` have no binary converter, so the two limit counts compare in
    memory (per owner, small).
  - Abuse handling, captcha, error pages: §8. Default thresholds are generous (20 weighted signals in
    10 min); tests use lower ones through configuration.
  - Metrics: `AppMetrics` (meter `Telrang` via `IMeterFactory`) with counters for requests, 429s,
    failed sign-ins (`AppSignInManager` overrides plus unknown accounts and failed re-authentication),
    poll password failures, ballots, mails sent/failed. `RateCounters` is an in-process `MeterListener`
    bound to that meter instance (so parallel test hosts do not mix) keeping a per-minute ring of 24 h.
    `LogRing` is an `ILoggerProvider` keeping the last 200 Warning+ entries; the Status tab can clear it.
    Live circuits are counted by `AppCircuitHandler`. All of it resets on restart.
  - Mail (findings 11, 24): the per-recipient cooldown and daily maximum count messages of the same type
    only. `IMailQueue.TryEnqueueAsync` returns the drop reason and when it would be accepted again; only
    pages about the signed-in user's own address show it ("Already sent recently. Try again in N
    minutes."); anonymous forms still answer the same for every address. Dead mail: retry (fresh attempt
    count) or purge on the Mail tab. The Mail tab no longer pins `App.PublicUrl` silently.
  - Recovery key (finding 5): `RecoveryKey` (same mechanics as `SetupKey`), §9.
  - Re-authentication (finding 10): data download and account deletion need the password or a current
    authenticator code. 2FA (findings 28, 29): turning it off or resetting the key needs a current code;
    admins cannot turn their own 2FA off, and an admin who resets the key is gated to enrolment again.
  - Invites (finding 12): a used invite link can be revoked after a confirm; the ballot stays and counts,
    the link and its cookie stop working.
  - Copy: lockout page tells an admin lock from failed attempts (finding 22, a deliberate oracle like the
    existing one); duplicate registration says "already taken" once (23); register page says when mail
    is off (8); 404 page reworded (32).
  - Not done (later): the per-poll notify-me mail, retention, ownership transfer.

- 2026-10-26 (identity model, §16; migration `IdentityModel`; tests in `IdentityModelTests.cs`):
  - Rule: signed in → account, server-side; anonymous → cookies. `VoterIdentity` is now {Kind: Account |
    Cookie | Invite, Key}, resolved only in `PollAccessService.ResolveVoterAsync`; BallotService derives
    `VoterIdentity`/`VoterKey`/`UserId` from it (no more "UserId else VoterKey" branches). `PollCookies.Read`
    does not read `pg_`/`vk` for a signed-in user.
  - Password grants of signed-in accounts are `PollGrant` rows (upsert on unlock; deleted when the password
    version changes, with the poll, and on account deletion). No server-side expiry: the version is the
    revocation; the cookie keeps its 30 days.
  - Invite claims happen on the first write (submit/delete), not on view: a view by the owner or a link
    preview must not take an invitee's token. Claimed tokens count as used on the share page. Claims are
    cleared by the ballot reset of an edit; revoking a claimed token re-keys its ballots to the token
    identity (UserId kept, so my votes/export still list them).
  - Superseded from 5b part 1: "invite votes by token even when signed in", the after-vote special case for
    a ballot under another identity (claiming makes the account the identity), and the "known limit" that
    the vote page needed the token cookie for a signed-in invitee.
  - `InviteTokens.ClaimedByUserId` has no foreign key: accounts are only soft-deleted, and an FK would make
    SQLite rebuild the table outside the migration transaction. `PollGrants` (a new table) has FKs.
  - The island ticket is bound to the user id it was minted for (null = anonymous).
  - Secrets: the setup-key and recovery-key log lines carry event ids 9001/9002 (`SecretLogEvents`);
    `LogRing` drops them, so they reach the console but never the admin Status tab.

- 2026-10-26 (phase 6: branding, theme, welcome, persistent CIDR blocks, §17; migration `Phase6Branding`;
  tests in `Phase6Tests.cs`; browser check in headless Firefox):
  - Branding: `BrandingService` over `{DataDir}/branding/`. Format by magic bytes only (PNG, JPEG, WebP);
    caps logo/banner 1 MB, background 2 MB; atomic write (temp file + rename), the name's other extensions
    removed. Files dropped in by hand are served only when the header matches the extension (an SVG renamed
    `.png` or a PNG named `.svg` is 404). `?v=` is `<mtime unix ms>-<size>` (size too, so `cp -p` of a new
    file still busts caches). `GET /branding/{name}` and `/branding/theme.css` are anonymous endpoints
    (outside the account gate, like static files); a blocked client still gets the 403 for images (only
    `.css` passes the block middleware).
  - Theme: `ThemeSettings` (in the settings snapshot) reads stored values tolerantly (invalid = default);
    `ThemeSettings.Validate` runs in `AdminService.SaveSettingsAsync`, so invalid values are refused at save
    whatever the caller. `ThemeCss.Generate` interpolates only validated values: `--av-*` custom
    properties, plus derived shades computed server-side (hover/active/light, nav gradient, text colour on
    primary by WCAG contrast), font size as a percentage of the browser default (keeps the visitor's own
    font-size preference), then the background rule and the custom CSS verbatim. Version = first 16 hex of
    SHA-256 of the CSS; cached per settings snapshot and background file version. Defaults are stored as
    "no row". Accent drives the sidebar gradient end and focus rings; primary drives buttons, links,
    checkboxes, rank badges and result bars.
  - Presets: `data-bs-theme` on `<html>` (light/dark), none for "system"; app.css copies Bootstrap 5.3's
    dark variables under `@media (prefers-color-scheme: dark) { :root:not([data-bs-theme]) }`, so "system"
    works without script. App.css colours now come from custom properties and Bootstrap's subtle/emphasis
    variables, so the ranked zones, confirm panel and charts follow dark mode. The admin preview re-links
    the stylesheet through `js/theme.js` (new `<link>`, old one removed on load) and sets the attribute.
    The static error pages carry the preset attribute and link theme.css too.
  - Reset to preset clears colours, radius, font size and font; preset, background and custom CSS stay.
  - Welcome: `Home.WelcomeMarkdown` (≤ 8 kB) through `MarkdownRenderer` replaces the default landing line.
  - Blocked ranges: `BlockedRangeService` (singleton) caches parsed networks, reloads after each change,
    checks expiry on every match (an expired row stops blocking at once; rows are purged when the list
    loads). `AbuseBlockMiddleware` checks it before the tracker on the post-forwarded-headers address
    (IPv4-mapped addresses mapped); admins are never blocked. Answer: static page, **403** "Blocked",
    "Access from your network is blocked." (403, not 429: it is not rate based and has no Retry-After).
    Refused: ranges overlapping loopback (127.0.0.0/8, ::1), containing a `KnownProxies` address or
    overlapping a trusted proxy network (by default the private ranges, so private ranges cannot be
    blocked unless `ReverseProxy:Known*` is narrowed; superseded: §8, §31), duplicates, more than 1000 rows. Host bits are
    masked (`198.51.100.7/24` → `198.51.100.0/24`); a single address becomes /32 or /128.
  - Rule change: admin-typed CIDRs are stored in the database. Client addresses seen by the app are still
    never written anywhere (project rule updated).
  - No new packages; CSP unchanged (`style-src 'self'`, no inline styles).

- 2026-10-26 (cleanup round; migration `TodoSweep`; tests in `TodoSweepTests.cs`; browser check in headless Firefox):
  - Password changed mid-vote (user-flow review finding 18): a submit or delete refused for a stale grant answers
    "The poll password changed." (`BallotService.PasswordChangedMessage`); the islands show it with an *Unlock*
    link to the poll page, which shows the gate.
  - Auto-close backfill: the migration sets `ClosesAt = CreatedAt + duration` where it is null (the owner's
    override, else the stored `Limits.MaxPollDurationDays`, else 180; 0 leaves the poll open-ended). This closes
    old open-ended polls on upgrade. The date arithmetic is SQLite's `strftime`; the migration refuses another
    provider (a Postgres migration set needs its own step).
  - Polls per day count `PollCreation` rows (UserId, At), written on every create; rows older than 48 h are
    purged by the same write (no job). Deleting and recreating counts twice. The table starts empty on upgrade.
  - Favicon: fourth branding name, PNG only, ≤ 256 kB, `/branding/favicon?v=`; App.razor links it, else the
    static `favicon.png`.
  - Admin pages and broken custom CSS: App.razor links `theme.css?admin=1` (no custom CSS) for `/admin`; after
    in-app navigation `AdminPage` swaps the stylesheet through `js/theme.js` and swaps back on leaving. The
    custom CSS field says "Not applied on admin pages."
  - Mail retries: 1, 5, 15, 60 minutes, dead-letter on the 5th failure (the unreachable 240-minute step is gone).
  - Notify me (§14.3a): `Ballot.NotifyOnClose`, `Poll.ClosedNotifiedAt`, `PollSweepService` (one 5-minute timer,
    `Polls:BackgroundSweep=false` turns it off). A poll is claimed by a conditional update before its mails
    are queued, so a close is never mailed twice; whenever a poll is open again (owner or admin reopen, a later
    `closes:`) the marker is cleared and the next close mails again. Visibility is re-checked at send time.
    Without mail or a public URL the checkbox is hidden and the sweep does nothing (closes wait until both are
    set; a close more than 7 days old when the sweep first sees it is marked without mail). A poll whose results
    voters cannot see is left unmarked, so a later switch to `after-vote`/`open` still mails its close. `ResultsReady` mails skip the per-recipient cooldown and daily cap (several polls can close within
    15 min; the recipient opted in with a verified address); the hourly cap still applies. The unsubscribe link
    `/notify/off/{token}` is a static page (Data Protection purpose `Telrang.NotifyOff.v1`, payload poll id
    and account id), anonymous, idempotent; a token that does not unprotect is 404.
  - Ownership transfer: `AdminService.TransferPollAsync`, admin Polls tab *Transfer to…* with a confirm tick box;
    the target must exist, not be deleted or pending, and have a verified email. Logged as `poll.transfer`.
  - Retention: `Retention.DeleteClosedAfterDays` (0 = off) on the Settings tab; the same sweep deletes polls whose
    close (the earlier of `ClosedAt` and a passed `ClosesAt`) is more than N days ago, with cascade, at most once a
    day (in-memory last-run time, so also right after a restart). The share page shows "Deleted automatically on
    <date>" (for an open poll: from its close date).
  - Results on phones: below 576 px the ranked and points results render one card per option (label, total or
    score with its bar, the stats on one line, the histogram below) instead of the wide table.
  - CI: `.github/workflows/ci.yml` (restore, `build --no-incremental -warnaserror`, test; ubuntu-latest, .NET 10).
    `dotnet format --verify-no-changes` runs in CI since 2026-11-01 (`.editorconfig` at the root; migrations are
    generated code and skipped).

- 2026-10-26 (audit fixes, the 2026-10-26 audit; migration `AuditFixes`; tests in `AuditFixTests.cs`; browser
  check in headless Firefox):
  - Closing time is frozen at creation: `ClosesAt` = the typed `closes:` date (end of day in the poll zone) or
    creation + the owner's max duration (0 = none, null), with `Poll.AutoClose` telling the two apart ("Closes
    automatically on …" vs "Closes …"). An edit recomputes it only when the close date changes (`closes:` added,
    moved or removed, or `timezone:` with a `closes:`), never from the current setting; only then is the duration
    limit checked, so a transferred poll beyond the new owner's limit stays editable. Removing `closes:` means
    creation + the owner's current limit. The migration sets `AutoClose` where `ClosesAt` is set and the stored
    definition has no typed date (plain SQL both providers accept).
  - `PollState.IsClosed` (and `ClosedOn`, `AutoCloseAt`) is the one "closed" rule; every page, service, sweep and
    count uses it. `AccountGate.MayActAsync(…, verifiedEmail)` is the one "may this account act" check (accounts
    mode, notify-me, poll creation, transfer target, results mail).
  - Invite cookie bound to the principal it was issued under; the accounts-mode password handoff (§16). Results
    mail re-checks each recipient's access; revoke and transfer clear the tick; unsubscribe is GET-button/POST.
  - Drafts in the browser are keyed `draft:<userId>:<pollId|new>`; older unkeyed drafts are dropped on sight.
  - Voter name limit is the setting (10–120, default 60) in the service and the field's `maxlength`. One poll-text
    limit: `Limits.SourceBytes` with a 256 kB ceiling (the 100 000-char constant is gone); the SignalR receive
    limit is 1 MB so the editor can send that much text.
  - One block mechanism for addresses: `BlockedRange` (with `ExpiresAt`); manual in-memory blocks are for accounts
    only, automatic abuse blocks stay in memory.
  - `/my/votes` takes the results link from `CanSeeResults` and lists a revoked invitation as "Invitation revoked"
    without links (simpler than dropping it: the ballot is still the account's data, export lists it).
  - Kept on purpose: cookie grant vs `PollGrant` (identity rule), the two ways to close a poll (`ClosedAt` by hand,
    `ClosesAt` by date), the notify tick per response with the sweep de-duplicating per account, `App:Name` as the
    fallback name only.

- 2026-10-26 (admin roles, `AdminRolesTests.cs`): Users tab search (email/display name, server-side, debounced, 200-row
  cap with match count); *Make admin* / *Remove admin* behind the delete-style confirm panel (`AdminConfirm`); promotion
  only for accounts passing `AccountGate.MayActAsync(verifiedEmail: true)`, enforced in `AdminService.SetAdminAsync` (§10).

- 2026-10-28 (app shells, `LayoutTests.cs`; browser check in headless Firefox at 1280 and 390 px, light and dark):
  `Theme.Layout` picks one of three signed-in shells (TopBar default, Sidebar, Compact); anonymous-facing pages use
  `PublicLayout`; every page names its layout. Shared `PageHeader`/`UserMenu`/`EmptyState`, an SVG icon sprite, a
  `<details>` account menu, and app.css polish (type scale, canvas + surfaces, borders, badges, focus) (§11, §17).

- 2026-10-28 (palettes, `PaletteTests.cs`; browser check in headless Firefox, TopBar, 1280×800, light and dark, all three
  palettes): `Theme.Palette` with three designed palettes (Indigo / Amber default, Teal / Coral, Plum / Gold) and Custom;
  every colour derived from base + highlight in OKLCH with AA contrast fixes (`PaletteDerivation`), light and dark
  blocks in `theme.css`, Bootstrap variables mapped onto `--av-*` tokens once in app.css. Identity rules for where base
  and highlight may appear (§11): header band, brand mark (new, `icons.svg#brand-mark`, `favicon.svg`), active nav,
  avatar ring, rank-1 badge, leading result bar, "Open" badge tint. Default primary changes from `#1b6ec2` to
  `#3730a3`; existing stored colours keep working as Custom. "Generate links" on the share page is now outline (one
  primary action per page). The Settings "Reset to preset" button is "Reset".

- 2026-10-29 (palette chosen): Teal / Coral is the default palette (Indigo / Amber and Plum / Gold stay selectable;
  rejected: looked like a bank's site). The "Open" badge uses the base tint, not the highlight's
  (coral read as a warning). The favicon follows the palette: `/branding/favicon.svg?v=` generated from the light base
  and highlight (`ThemeCss.Favicon`), linked from App.razor and the static error pages;
  `wwwroot/favicon.svg` is gone, `favicon.png` re-rendered on Teal / Coral; app.css defaults regenerated. A theme saved
  before this change keeps its palette; an install with no theme row changes from Indigo / Amber to Teal / Coral.

- 2026-11-01 (name and mark): the app is **Telrang** (Dutch *tel*, count, and *rang*, rank; "tell rank" to an English
  ear), published by LayerTrace Labs. Renamed everywhere: solution, projects, namespaces, assemblies, Docker image and
  compose names, the `App:Name` fallback, docs. The data-protection application name changed with it, so a data directory from before
  this commit loses its cookies and cannot read values encrypted at rest (2FA secrets, the SMTP password): no release
  exists, start fresh. The mark changed too: the ballot box with a "1" read as a generic number-one badge. It is now a
  "budget split" (§11): one large highlight dot and three smaller holes of unequal size, points spread over options.
  Same two paint channels as before, so no component changed; `ThemeCss.Favicon` emits the same geometry,
  `favicon.png` re-rendered.

- 2026-10-30 (brand mark): the three ranked bars looked like existing services' logos. The mark is now a ballot box
  (§11): a sharp square with a round hole almost touching its edge, in the base colour, and a "1" in the highlight on
  top. A first cut used a font glyph (Noto Sans Black); it sat off-centre in the favicon and brought a licence note
  for nothing, so the "1" is two rectangles (stem and 45° flag), which also lets it fill the hole. Same two paint
  channels as before (`currentColor`, inherited `fill`), so no component changed; the symbol has no padding, so the CSS
  sizes drop from 1.75/1.5 rem to 1.5/1.375 rem. `ThemeCss.Favicon` emits the same geometry (hole in the base's text
  colour), `favicon.png` re-rendered from it on Teal / Coral. Review: the stem's right edge at 14.8 rendered as a grey
  half-pixel column. Hole radius 11 → 10 (2-unit ring), stem 3.8 → 4 wide at whole units (11–15); the favicon's stem starts
  at 10.5 so its edges are pixel edges at 16/32/64 px. The "1" keeps its height; the flag is unchanged. The mark is 1.5 rem
  in every header (the slim header and sidebar no longer step down to 1.375 rem): 24 px is the one size where the stem is
  sharp.

- 2026-10-29 (dates polls, §18): third poll type `dates` for availability (yes / maybe / no per date or time slot),
  with calendar / list / timeline views of the same ballot and results. Decisions: unanswered = no; the per-person
  grid is owner-only (names are collected for the owner today; the results page never showed them to others, and
  keeps not doing so); slot times are wall-clock in the poll's zone, shown as typed with the zone named, not
  converted to the viewer's zone; options are sorted chronologically by the parser; past dates are allowed.
- 2026-10-29 (results and the admin): the system admin does not see results or ballots of polls they moderate; the
  Polls tab stays at list / close / transfer / delete (user-flow review finding 15, decided). Creators always see results.
- 2026-10-29 (QR code on TOTP enrolment, §19): yes; rendered server-side as inline SVG by a small encoder in Web
  (no package, no JS, CSP-clean), next to the key that stays shown.
- 2026-10-29 (QR encoder, §19, implemented): `Features/Accounts/QrCode` (byte mode UTF-8, level M, versions 1–10,
  max 213 bytes, all 8 masks scored); used only by `EnableAuthenticator`, which falls back to the key alone on overflow.
  Verified with zbarimg; codewords match qrencode apart from the mask choice; `QrCodeTests` pins three matrices.
- 2026-10-29 (dates polls implemented): the view switcher is three visually hidden radio inputs with labels styled
  as segmented buttons and CSS showing the checked panel, not component state: the results page is static SSR (no
  circuit), so the same `DatesViews` works there and in the island; still no script, not persisted, Calendar first.
  All three views are in the DOM, so every cell renders three times. The cell fragment is `RenderFragment<DatesSlot>`
  (option + view), since the cell's label differs per view (time or day number in calendar/timeline, the answer word
  in the list). "No" answers are not stored (absent key = no). The best-date marking needs at least one yes or maybe
  (with no answers every date would tie for first). Timeline columns are per day (slots stacked), not per option.
  `PollJson.ValidatePayload` is the one exhaustive switch for re-validating stored ballots on edit, so turning
  `maybe:` off asks to discard ballots that used it. Stacked bar: yes `--av-success-fg`, maybe `--av-warning-border`.
- 2026-10-31 (form editor, §20, proposed): the whole-poll DSL and poll passwords are removed; the poll is a form
  (controls per setting, Markdown only for descriptions), options in List / Text (Calendar / Text for dates); copy
  from an existing poll; invite links unchanged apart from an alias per link; owner-set ballot aliases; definitions and ballots are
  shaped per question for multi-question polls later. Description default 2 000 characters. No backwards
  compatibility needed (no release yet).
- 2026-10-31 (§20.3 implemented): poll passwords are gone (entity columns, `PollGrant`, `pg_`, gate page and
  endpoint, `PollGrantHandoff`, per-poll failure window, `PollPassword` rate limit, FormGuard entry, ticket grant,
  "password changed" notice, metrics row, abuse signal); `PollAccess.CanView` is always true for a found poll.
  `InviteToken.Label` is `Alias`; `Ballot.Alias` is copied from the link only when the response is created, so an
  owner's edit (including a clear) always wins and a voter's revision never brings the link alias back; a new
  response through the same link (`responses: many`) starts from the link's alias again. Alias editing lives on
  the share page (interactive, owner-only) rather than the static results page. Ballots CSV header `DisplayName`
  became `Name`, `Alias`. One migration, `RemovePollPasswordsAddAliases` (renames the column, keeps old labels).
- 2026-10-31 (§20.1, §20.2, §20.4 implemented; migration `FormEditorQuestions` drops `Poll.Source`):
  - Model: `PollDefinition(Title, DescriptionMarkdown, Questions)` with poll-wide settings as init properties;
    `PollQuestion(Key, Type, Options)` with the type settings (other types keep their defaults). Question keys are
    `q1`, `q2`, … (`PollDefinition.QuestionKey`), kept on edit. Ballot payloads are `{"<key>": {...}}`
    (`PollJson.Serialize(key, ballot)` / `Deserialize*(json, key)`); a missing part reads as an empty answer, a part
    for an unknown question makes the payload invalid (so a stored ballot that no longer fits asks to be discarded).
    `BallotValidator` and the results take a `PollQuestion`. No legacy reading: old snapshots (`Link`, `closesAt`) and
    flat payloads are not upgraded; the dev DB is recreated.
  - `PollDslParser` and everything DSL-only are deleted (`Dsl/`, parser tests, `PollText.ExampleSource`, `DslEditor`,
    `Limits.SourceBytes`, the 256 kB ceiling). Rule texts moved to `PollRules` (now public, with `Slugify`,
    `DateItemHelp`, `TimeZoneMaxLength`) and read as instructions for a form ("Allow fewer bans than there are
    options (3).", "Lower the cap to the budget (50) or less.", "Pick a time zone from the list, such as
    Europe/Berlin.", "Add at least 2 options (now 1).").
  - `PollParsing.Validate(draft, limits, maxDurationDays, createdAt, existing)` = validator + IANA zone check (field
    `timezone`) + max duration (field `closesOn`, skipped while the date equals the stored one). Same call in the
    editor (every change) and on save.
  - Editor: errors show under a field once it was changed, all of them after a save attempt (the create button is
    never disabled for errors; it reveals them and says "Fix the marked fields."). Field ids derive from the
    `DraftError` path. Errors no control shows are listed above the buttons. The type and List/Text switches are
    radio inputs styled as a segmented control (`.seg`, native arrow-key handling). Changing between dates and the
    other types keeps each family's options aside, so switching back restores them; ranked ↔ points share them. A
    new points question starts with budget 100. The dates calendar shows one month at a time (starting at the first
    selected date, else this month in the poll's zone); a day's details are one description shared by its slots;
    a slot is typed (`19:00`, `18:30-21:00`, `18:30–21:00`) and parsed with the Text mode's rules. The preview
    renders from the draft as typed (so it follows invalid states too) and from the definition once valid; for
    dates it is the voters' read-only calendar. Description: Write / Preview switch, counter "n / limit".
  - Browser drafts: `draft.js` now stores the `PollDraft` JSON the editor sends on every change (no textarea
    listener); a stored entry that is not a draft (the old DSL text) is dropped. A copy (`?from=`) replaces a
    pending new draft (`attach(..., replace)`).
  - Copy: "Copy" on My polls and on the owner's vote page, "Copy poll" on the share page (the share page already
    has a "Copy" button for the link). `PollService.GetCopyAsync` is `PollDraft.FromDefinition` of a poll the caller
    can edit; the close date stays only while its end is still ahead and within the caller's max duration from now.
  - `OptionDraft` is a record (value equality), so the editor keys options by reference (`ReferenceEqualityComparer`,
    a `ConditionalWeakTable` for `@key`).
  - Data export lists each owned poll's `definition` instead of its DSL source.
- 2026-10-31 (pentest AV-01): mailed links come only from `App.PublicUrl` (`SettingsService.MailLinkBase`, null without
  it); `LinkBase(request)` is for links shown on a page (share, invite, an admin's setup/reset link). `MailSettings.IsConfigured`
  now also needs the Public URL (`HasTransport` = host + from, which the sender uses for mail already queued), so
  forgot password, verification, email change and invites show their "not available" path without it.
  `PublicHostFilterMiddleware` refuses (400) a Host other than the Public URL's or a loopback name once it is set
  (`/healthz` exempt); config `AllowedHosts` stays `*` because the URL is a DB setting. The Settings tab refuses a Public URL
  whose host differs from the one the page is open on (unless on loopback). nginx example gained a catch-all default server.
- 2026-10-31 (pentest AV-05): `UseRouting`, `UseAuthentication` and `UseAuthorization` are called explicitly after
  the forwarded headers, security headers, status pages, abuse block and rate limiter, so a login challenge behind a TLS
  proxy redirects to `https://` with CSP and friends (test `AV05_…`). HSTS max-age 365 days.
- 2026-10-31 (pentest AV-02): adding a passkey, generating recovery codes and changing the email need the password or a
  current code (same field as PersonalData, `Secret`); EnableAuthenticator redirects to ResetAuthenticator while 2FA is on
  and the 2FA page offers "Set up app" only while it is off. ChangePassword, Email, Passkeys, GenerateRecoveryCodes and
  EnableAuthenticator POSTs are in `LimitedAccountPosts`; a wrong old password calls `AppSignInManager.RecordFailure`.
  New mail type `SecurityNotice` ("A passkey was added", "New recovery codes were generated"), verified address only.
- 2026-10-31 (pentest AV-04): `OutboxMessage.TextBody` nullable (migration `OutboxBodiesNullable`, which also clears the
  bodies of rows already sent or dead). `MailSender` nulls both bodies on send and on dead-letter and deletes sent rows
  older than 48 h on every pass. *Retry dead* is gone (nothing left to resend; the person asks again, or the admin issues
  a new link); *Purge dead* stays.
- 2026-10-31 (pentest AV-06): `AccountService.RemovePasskeysAsync` runs in the recovery-key flow, admin "Disable 2FA" and
  every password reset (self-service and admin-issued links). The admin outcome names the count, the reset link line
  says "removes their passkeys", the reset form says "Passkeys on the account are removed.", the recovery button reads
  "Reset password, 2FA and passkeys".
- 2026-10-31 (pentest AV-07): `AdminService.SetAdminAsync` sets `MustEnrolTwoFactor` when promoting an account without 2FA
  (saved with the role and the stamp bump); the outcome says "must set up 2FA before using the role", the Users row its
  "2FA required" badge. Admins thus always have 2FA before they can act, like the first admin and admin-created accounts.
- 2026-10-31 (pentest AV-08): `ClearBlockAsync` logs only the kind ("automatic address block", "manual/automatic account
  block" with the account id as target), never an address block's key, which held the client address (§8 rule).
- 2026-10-31 (pentest AV-09): hub `MaximumReceiveMessageSize` 1 MB → `LimitSettings.MaxCircuitMessageBytes` (256 784 bytes),
  computed from new ceilings on the text and option limits (refused above on save, clamped on read). The bound is the
  Text-mode text (the largest legitimate message, same as the restored draft); pure non-Latin text near every ceiling at
  once (3 bytes a character) could still exceed it, accepted. The draft restore is not chunked (that would need
  PollEditor changes); `draft.js` compacts the stored JSON instead (`\uXXXX` → UTF-8). Open circuits per client key are
  capped by `CircuitLimiter` (concurrency limiter, default 20); the deploy notes document nginx `limit_conn`.
- 2026-10-31 (pentest AV-10): Register answers a taken address exactly like a new one (redirect to the new
  `RegisterConfirmation` page, no sign-in for either; the user logs in next) and mails the owner a `RegistrationAttempt`
  note. Unverified self-registered accounts are soft-deleted 7 days after creation in the daily sweep pass
  (`PollSweepService.PurgeUnverifiedAccountsAsync` → `AccountService.PurgeUnverifiedAsync`).
- 2026-10-31 (pentest AV-11): `AdminService.SaveSettingsAsync` clears `Mail.Password` when the effective host, port,
  security or username change without a new password, and refuses a username with security None; `SmtpMailTransport`
  refuses to authenticate without TLS. The Mail tab says "Cleared when host, port, security or username change."
- 2026-10-31 (pentest AV-12): `ProtectedTokenUserStore` (subclass of the EF `UserStore`, registered over it) protects the
  values of `[AspNetUserStore]` tokens with Data Protection. Identity's `ProtectPersonalData` was not used: it also
  encrypts emails and user names, which the admin search (`Contains`), ordering and several queries need in clear. No
  migration and no DB recreation: a value without the `dp1:` prefix is read as is and protected on its next write.
  `DataDirectory.Prepare` creates the data dir and `tmp/` with 0700 and deletes backup leftovers; the backup endpoint
  writes there instead of the system temp; the backup button says the file holds password hashes and emails.
- 2026-10-31 (pentest AV-15a): `AbuseBlockMiddleware` exempts by endpoint metadata (`ExemptFromAbuseBlock()` on
  `MapStaticAssets`, the branding endpoints and `/healthz`) instead of the path suffixes `.css`, `.png`, … and `/healthz`.
- 2026-10-31 (pentest AV-15b): `IdentityRedirectManager.RedirectTo` falls back to the home page when a return URL is
  outside the app (it threw, a 500); `/Account/Logout` redirects to `~/` when `returnUrl` starts with `/` or `\` or
  holds control characters (`LocalRedirect` threw).
- 2026-10-31 (pentest AV-15c): no external login provider is registered, so the template's `ExternalLogin` and
  `Manage/ExternalLogins` pages, `ExternalLoginPicker` and the `PerformExternalLogin` / `LinkExternalLogin` endpoints are
  deleted (they would skip 2FA and create accounts if a provider were added). Passkeys keep their endpoints; the
  external cookie scheme stays configured (Identity's defaults use it), unused.
- 2026-10-31 (pentest AV-15d): `IsLastAdminAsync` counts only other admins that are not deleted, pending, gated or locked.
  Demote, admin delete and self-delete run their check and change inside `AccountService.SerializeAdminChangeAsync` (the
  static lock the setup and recovery flows already use; single instance, so a lock rather than a serialisable SQLite
  transaction); demote also writes role and stamp in one transaction.
- 2026-10-31 (pentest AV-15e): ForgotPassword pads its answer to at least 300 ms (Stopwatch around the service call), so a
  verified account's extra inserts do not show in the timing. Padding rather than a background queue: tests and the
  cooldown logic expect the link and the outbox row to exist when the request returns.
- 2026-10-31 (pentest AV-15g): the deploy notes no longer list the `RateLimiting__PollPassword*` variables or poll-password
  grants (removed with poll passwords, §20.3); the key-ring warning names island tickets instead.
- 2026-10-31 (pentest fixes AV-03, AV-13, AV-14, AV-15f): options Text mode work is bounded by the poll limits, never
  by the input: text longer than OptionsMax × (OptionLabelMax + 2 × OptionDescriptionMax + 16) is refused before parsing,
  options past OptionsMax are only counted (ranges by their day count), at most 20 line errors are listed plus the count
  error; the validator checks the option count before per-option work and stops after 50 option errors. Text mode parses
  on change (blur, mode switch, save), not per keystroke; the textarea has the same maxlength. Cause: the rework removed
  the DSL source-size limit (§20.4) that used to bound this input. An edit whose new close date reopens a poll counts
  against the open-polls limit, like reopening. Close and option dates at most 100 years after the poll's creation day;
  retention never throws near year 9999 (`DeleteOn` null = never due) and evaluates each poll on its own. Signing out
  removes every poll draft in the browser (`draft.js` loaded on every page hooks the Account/Logout submit); opening an
  editor removes other accounts' drafts.
- 2026-10-31 (static voting, §21): the vote page and its ballot editors are static SSR forms (post/redirect/get,
  identity from the request's cookies, no voter ticket); JS modules only enhance them. Live results only for the poll
  owner, over a server-sent event stream (`/p/{id}/results/live`) that carries no data, capped per client and in all.
  Reason: voters need no live connection, and the per-client circuit limit (AV-09) must never block a voter behind a
  shared address (school, office, carrier NAT).
- 2026-10-31 (§21.1 implemented): deviations and additions in §21.1's "Implemented" note: the dates layout switch
  keeps the CSS-only switcher on the results page and the editor preview (only the vote form uses submit buttons), the
  body limit is also enforced by `FormLimitMiddleware` (servers other than Kestrel ignore the metadata), a missing or
  forged stamp counts as too old, and too-old stamps record no abuse signal.
- 2026-10-31 (live results, §21.2): the owner's stream re-checks more than `CanEdit` and `MayActAsync` before each
  event: address and account blocks, lockout and the security stamp, so nothing a new request would be refused keeps
  streaming for up to 10 minutes. A `ping` on open makes the headers and the retry reach the browser at once.
  `End` also on account deletion. The browser module stops on any failed stream, not only 404 / 429 (EventSource does
  not expose the status).
- 2026-11-01 (pentest round 2): fixes for 20 findings, one commit each; the resulting rules
  are in §8. Decisions worth noting: unverified self-registered accounts cannot sign in, and confirming a registration
  takes the account's password (so neither a mail scanner nor the address's owner can verify a squatter's account; the
  price: someone who forgets the password before verifying waits for the 7-day purge); passkeys are discoverable only;
  forwarded headers trusted from loopback (plus the container's own networks) by default (superseded: §31, R3-DEP-7); circuits per account 20 and
  per address 60; no abuse signals for outages, full polls or connection caps; results-ready mail once per poll per
  24 h. Not changed: logging out keeps the security stamp (only "log out everywhere" ends other sessions) and there
  is no sessions list (superseded in part: §31 DEP-2, sign-out deletes the server-side session; still no list); the admin log keeps its newest 10 000 rows (the application log now has every action too); an
  admin can still take over another admin's account through reset and 2FA removal (parity by design; the target is
  mailed); the vote form stamp is not bound to the form id (public polls stay an honour system).
- 2026-11-01 (§22): optional "Auto-verify created accounts" while mail is off (default off: the admin still clicks
  *Mark verified*); every admin option gets a hidden description with a per-option toggle and a show/hide-all button.
- 2026-11-01 (audit leftover): results-ready mail skips closes older than 7 days and waits, unmarked, while results
  are hidden from voters.
- 2026-11-01 (migration fix): `OutboxBodiesNullable` failed on any database with sent or dead mail (SQLite rebuilds
  the table at the end of a migration, after its SQL). The body clean-up moved to its own migration
  `ClearSentMailBodies`; `MigrationUpgradeTests` migrates databases that hold data.
- 2026-11-01 (§23): first test round: JS module failures are caught (a failed import had killed the editor's
  circuit), copy runs in the browser, `/` is static with an inert showcase of the three poll types and the name line,
  sign-in lands on My polls, the editor gets option help, new polls default to the browser's time zone.
- 2026-11-01 (§23.2 implemented): the showcase reuses the vote page's field components (`Preview`: no names) and JS
  modules, which now find fields by `data-field` and their tally inside `form, [data-ballot]`; `js/showcase.js` is
  global so the examples work after enhanced navigation. The home page widens the public shell to 1040 px.
- 2026-11-01 (§23.3 implemented): sign-ins without a local return URL land on My polls; an off-site return URL
  now also lands there (it fell back to `/`). Enrolment's Continue goes to My polls.
- 2026-11-01 (§23.1 implemented): every JS interop call catches `JSException` (source scan in `JsResilienceTests`);
  copy buttons are `data-copy` / `data-copy-status` markup handled by one delegated listener in `js/clipboard.js`.
- 2026-11-01 (§23.4 implemented): editor option help on every control but Title and Description, share page help on
  invite count and aliases; `AdminHelpState` renamed `HelpState(area)` with a shared `HelpAllButton`, localStorage key per
  area. Help text from the code; voters never see names, so the Names help says owner only.
- 2026-11-01 (§23.5 implemented): a blank new poll takes the browser's time zone when the account has none; the
  account zone still wins, copies and edits keep theirs.
- 2026-11-01 (§24): signed-in home keeps the app shell, the header never shows the email (display name or the
  part before @, optional hide), owners land on results and can close/reopen there, visitors switch light/dark,
  the admin palette choice and corner radius go (teal/coral with per-mode overrides), footer credit, browser smoke
  tests in `tests/e2e` fail on server errors and console errors.
- 2026-11-01 (§24.1 implemented): `/` uses `MainLayout` (the signed-in shell for accounts, `PublicLayout` otherwise),
  still static; the compact shell widens on the home page like the public one.
- 2026-11-01 (§24.2 implemented): the header text comes from claims (display name, hide flag) set by
  `AppClaimsPrincipalFactory`, refreshed on profile save and by the security stamp validator; no lookup per render.
  `OptionHelp` works on static pages through `js/menus.js`.
- 2026-11-01 (§24.3 implemented): close / reopen on the results page are named forms calling the share page's
  service method; a non-owner's POST is a 404. My polls: title → results, "Vote" → ballot.
- 2026-11-01 (§24.5 implemented): footer credit "Telrang by LayerTrace Labs" next to the version, every layout.
- 2026-11-01 (§24.6 implemented): the logged navigation failure was Blazor's interactive router intercepting links to
  static pages (it ignores `data-enhance-nav`) and forcing the load from the circuit. `js/links.js` lets the browser load
  links matching `StaticRoutes` before Blazor sees them; circuit reloads go through `PageReload.SoonAsync`.
- 2026-11-01 (§24.4 implemented): visitor light/dark/system switch in every header, stored in the `av-theme` cookie
  (and localStorage) per browser and rendered by the server; the admin Theme section keeps the default mode and gains
  four per-mode colour overrides; palette choice and corner radius (fixed 6 px) are gone, old settings upgrade on
  read. With a fixed default mode the switch's no-choice option is labelled "Default".
- 2026-11-01 (§24.7 implemented): browser smoke tests in `tests/e2e` (plain Node, Firefox over WebDriver BiDi, no npm
  packages), run by `scripts/e2e.sh` and a CI job against the published app in Production on `localhost`; a flow fails
  on any server `fail`/`crit` line, browser console error, CSP violation or dropped circuit. Node and Firefox are
  dev/CI-only requirements.
- 2026-11-01 (§25): the Results setting splits into Results for (owner / voters / anyone with the link, the last only
  for public polls), Show results (while voting / after closing) and Show ballots to voters (only with names, all
  types). Default voters, while voting, no ballots.
- 2026-11-01 (§25 implemented): CanSeeResults reads the definition; voters' ballot rows are a table under the results
  (dates: the "Who can when" grid with names as entered), not shown to the owner, whose views stay as they were; the
  editor adjusts dependent results settings instead of showing errors; invite/account polls stored as `Open` are
  rewritten by migration `ResultsSettings` and mapped on read.
- 2026-11-01 (§26 implemented): option keys without a carried key match a stored option by exact label (Text mode
  carries keys by label from the list it was opened from); copies drop keys; the log counts a removal's ballots as the
  ballots that answered the option; a rename plus new details are two entries; the editor shows fixed settings as one
  read-only list on edit. Existing ballots and polls need no data migration (missing `retiredKeys` reads as none).
- 2026-11-01 (§26): every poll setting is fixed at creation; edits change only title, description, options and the
  close date. Option keys are stable and never reused; removed options are ignored on read, ballots are never
  discarded by an edit. Option changes after the first ballot go into a change log every viewer sees.
- 2026-11-01 (§24.7 fix): with Firefox 159 the theme-switch and signin-header flows failed on HEAD: they left the home
  page while its examples were still importing `ranking.js` / Sortable, and Firefox reports the aborted import (and an
  empty-body integrity mismatch) as errors. `vote.js` and `showcase.js` now handle a failed import (a warning, the plain
  form stays), and those flows wait for the ranking boards (`Page.settled()`) before leaving a page.
- 2026-11-01 (§27 implemented): clicks stay Blazor's, the JS module sends only multi-day ranges; Shift+arrows extend the
  state of the day where Shift went down and apply on Shift release; day buttons are never disabled, refusals are a
  message; the clock adds its slot directly; the typed year sits in the zoomed-out picker. The 128 cap is in place; the
  options ceiling rose from 100 to 128 the same day so it can be reached (default stays 100).
- 2026-11-01: the options-per-poll ceiling rises from 100 to 128 (default stays 100), so date polls can reach their
  128 cap; the circuit message bound grows to about 316 kB.
- 2026-11-01 (§27): date editor gets drag selection (touch: press 200 ms), weekday-header toggles, paged day
  settings (10), a clock time picker and a month/year/decade picker; option dates span 0001–9999 (close dates keep
  the 100-year limit); date polls are capped at 128 options under the shared options limit.
- 2026-11-02 (§28): PostgreSQL as an alternative provider, configured only by environment variables (never the admin
  panel or the database); data moves from SQLite by a `copy-to-postgres` command with the app stopped; tests run on
  both providers. New package `Npgsql.EntityFrameworkCore.PostgreSQL`.
- 2026-11-02 (§28 implemented): the two migration sets are told apart by context type: `PostgresDbContext` derives from
  `ApplicationDbContext` without changing the model, and `AppDbContextFactory` hands it out on PostgreSQL, so EF finds
  `Data/PostgresMigrations` for it and `Data/Migrations` for the base type (one assembly, no migrations project).
  `dotnet ef` takes `--context` for both; Program registers `PostgresDbContext.ForModel()` only while `EF.IsDesignTime`
  (an `IDesignTimeDbContextFactory<PostgresDbContext>` is covariant: EF used it for the SQLite context as well).
  Identity takes its schema version from the application services; contexts built outside DI (design time, the copy
  command, tests) get `UseAppIdentityModel()` so their model has the passkeys table too. `PostgresTests` compares the
  two snapshots' relational models (tables, columns with nullability, length and value type, keys, foreign keys,
  indexes) and checks both sets are current. The PostgreSQL baseline needs none of the SQLite raw SQL (backfills of
  data that cannot exist in a new database; no PRAGMAs).
- 2026-11-02 (§28 implemented): locking. Every explicit transaction goes through `DbWrites` (a test checks the
  sources). `BeginWriteAsync` takes a transaction-level advisory lock exclusively: exactly what SQLite's `BEGIN
  IMMEDIATE` gave the existing code (poll edit, delete, revoke, account and admin changes, retention), so no
  read-then-write in them needed a new analysis. Ballot submit and response delete use `BeginPollWriteAsync`: the
  same lock shared plus `SELECT … FOR UPDATE` on the poll row, so votes on different polls run in parallel, votes on
  one poll (limits, response numbers, the invite claim) one after another, and an edit never interleaves with a vote
  (the change log's "first ballot" rule; `ConcurrencyTests` would fail without the locks). Not changed: the
  results-ready claim and the invite claim are conditional `UPDATE`s, atomic on both; `OwnerLock` stays in memory
  (one app instance). Serializable transactions with retry were not needed.
- 2026-11-02 (§28 implemented): provider differences kept invisible. On PostgreSQL strings are `text` (SQLite never
  enforced declared lengths, and some model lengths are below the app's limits, e.g. voter names: 60 vs up to 120);
  DateTimeOffset values are written in UTC (`timestamptz` takes offset 0 only) and keep microseconds, so the "end of
  the close day" instant reads back as …59.999999 instead of …59.9999999 (tests compare through `TestUtil.Stored`);
  U+0000 in any string parameter becomes U+FFFD (`PostgresNulInterceptor`; PostgreSQL text cannot hold it, and a NUL
  in a name or a sign-in form must not be a 500). In-memory DateTimeOffset ordering (comments "SQLite cannot ORDER
  BY") stays: correct on both.
- 2026-11-02 (§28 implemented): secrets. `DatabaseSetup.ToString()` is the provider name; config errors never quote
  the string (not even the parser's message). `RedactingLoggerFactory` wraps the app's logger factory: an exception
  with an Npgsql exception in its chain is logged as `RedactedDatabaseException` (types, SQL state, outer stack
  trace), for every provider including the Status tab's ring. EF's connection events (they name server and database)
  are ignored on PostgreSQL and Npgsql's own log categories are off. A start that cannot open or migrate the database
  logs one Critical line with type and SQL state and exits 3 (configuration errors: stderr, exit 2). On a new
  PostgreSQL database the history table is created before `Migrate`, which otherwise logs its expected failed read
  as an error; a missing database is still created by EF when the role may.
- 2026-11-02 (§28 implemented): `copy-to-postgres` copies through EF (both contexts, entity by entity in foreign-key
  order, 500 rows per batch, owned JSON types with their owner), so value conversions are the app's own; the SQLite
  file is opened read-only (SQLite may still create `-wal`/`-shm` next to it; the file itself is unchanged). Refusals
  (exit 3) come before anything is written; a target the app was started on once (it seeds roles) counts as
  non-empty. Counts are compared inside the transaction, so a mismatch commits nothing (exit 1).
- 2026-11-02 (§28 implemented): truly asynchronous database calls render a component while it awaits (Microsoft.Data.
  Sqlite completes synchronously, so SQLite never did): the profile page, the new-poll page and the edit page now load
  into locals and set their models together. Found by the suite on PostgreSQL; the e2e flows pass on both providers
  (database variables pass through `scripts/e2e.sh`).
- 2026-11-02 (§28 implemented): tests. `AppFactory` creates one database per factory when `TELRANG_TEST_POSTGRES` is
  set and drops it on dispose; factories given the same `App:DataDir` (restart tests) share one database, dropped when
  the run ends. `[SqliteOnlyFact]` marks tests about SQLite itself (old migrations, `app.db`, the download backup),
  `[PostgresFact]` tests that need a server.
- 2026-11-02 (§29): three install formats (Docker image, self-contained Linux tarball with a systemd unit, Windows zip
  with service scripts), one configuration model (CLI > env > operator JSON file > shipped defaults), DPAPI for the key
  ring on Windows, CI release workflow with per-format smoke tests. New packages
  `Microsoft.Extensions.Hosting.WindowsServices` and `.Systemd`. PostgreSQL performance is measured and documented
  side by side with SQLite.
- 2026-11-02 (§29 implemented): the operator file is read once at startup into an in-memory source inserted just below
  the unprefixed environment variables; the `ASPNETCORE_`-prefixed variables are added again right above it, because
  ASP.NET Core reads them below `appsettings.json` and `ASPNETCORE_URLS` must beat the file's `Urls`. `TELRANG_CONFIG=none`
  turns the file off (tests and e2e set it). A named file that is missing, any file that cannot be read or parsed, and a
  default file in a folder this account cannot list (`/etc/telrang` is `0750`: as another user it looked missing) stop
  the start with exit 2; parse errors give the line only (JSON reader messages quote characters). UTF-16 is refused with
  a hint, a UTF-8 BOM is fine. Permission warning: Unix other-read/write or group-write (`root:telrang 0640` is the
  documented setup; the file's group cannot be checked without native calls); Windows any allow rule for an identity
  other than SYSTEM, Administrators, the process account.
- 2026-11-02 (§29 implemented): `SystemdHelpers.IsSystemdService` reads the parent's `/proc/<pid>/comm`, which
  `ProtectProc=invisible` hides: the unit started as a plain process and `Type=notify` never got READY. `HostPlatform`
  also accepts `INVOCATION_ID` + `NOTIFY_SOCKET`, and `ServiceHosting` then registers what `AddSystemd` would (notifier,
  lifetime, journal formatter). The unit leaves out `SystemCallFilter=~@resources` and `MemoryDenyWriteExecute`: CoreCLR
  does not start with either ("Failed to create CoreCLR, HRESULT: 0x8007054F"). `systemd-analyze security`: 1.4.
- 2026-11-02 (§29 implemented): the Windows service never logs the admin setup/recovery key: the Application Event Log is
  readable by every interactive user. It goes to `{DataDir}\admin-key.txt` (data folder ACL), deleted at the next start;
  the Event Log line points there and `install-service.ps1` prints it. Linux keeps the journal (root, `adm`,
  `systemd-journal`), Docker `docker logs` (superseded for systemd: §31 DEP-12, the file there too).
- 2026-11-02 (§29 implemented): the app creates the data directory and `keys/` private on Windows as on Unix (ACL with
  inheritance off: SYSTEM, Administrators, the process account); `install-service.ps1` sets the same ACLs with the
  service account and keeps `C:\ProgramData`'s "Users may read" from being inherited. `DOTNET_EnableDiagnostics=0` in all
  three formats (no debugger, profiler or dump attach by another process of the same account).
- 2026-11-02 (§29 implemented): `healthcheck [--url]` subcommand (loopback addresses only, 5 s timeout, starts no server)
  for the image's `HEALTHCHECK`; the runtime image has no curl. The Dockerfile's build stage runs on `$BUILDPLATFORM`
  and the final stage has no `RUN`, so linux/arm64 builds without QEMU. The image had never served
  `_framework/blazor.web.js`: the project-files-only restore did not pull the framework assets pack and the publish used
  `--no-restore`; the publish now restores again.
- 2026-11-02 (§29 implemented): release archives are deterministic (`ContinuousIntegrationBuild`, no local paths in the
  binaries; tar with root ownership, fixed modes and the commit time; zip through Python's `zipfile`). Archive layout:
  one top folder `telrang-<v>-<rid>/` with the program plus the unit/scripts and the example `telrang.json`.
  `-linux-musl` is not built (Alpine users take the image). Actions in both workflows are pinned to commit SHAs
  resolved with `git ls-remote` (latest majors, all on node24).
- 2026-11-02 (§29, open): DPAPI machine scope means a Windows key ring cannot move to another machine (sessions, 2FA
  secrets, voter keys, stored SMTP/captcha secrets are lost on a move; windows.md says so). Options for later: an
  `export-keys`/`import-keys` command that re-protects the ring, or `ProtectKeysWithCertificate` with an operator
  certificate. The install scripts and the Windows smoke test have not run on Windows yet (not yet run; they parse
  under PowerShell 7 and pass PSScriptAnalyzer apart from `Write-Host`); the first release run on GitHub is their test.
  `LoadCredential=` for the database secret could not be verified in the systemd test container and is not documented.
- 2026-11-02 (performance, docs/PERFORMANCE.md): SQLite and PostgreSQL 17 measured side by side (same machine, data
  and build; app on 1–4 P-cores of a desktop-class 8-P-core machine; PostgreSQL on its own 4 cores or on the app's). Votes: SQLite 50–125/s whatever
  the cores (disk sync, 150 ms busy retry); PostgreSQL 750–1,000/s on 4 app cores with its own cores, 570–620/s on
  shared cores, scaling with cores, ~250/s into one poll (row lock). Reads about equal: vote pages ~4,800/s (SQLite),
  ~5,250/s (own cores), ~3,300/s (shared); results 15–30 % faster on PostgreSQL. `copy-to-postgres` of 110,100 ballots
  in 6.5 s. Advice: SQLite by default, PostgreSQL above ~50 votes/s, preferably on its own cores. Found: results are
  2–3× slower than on 2026-11-01 (bigger page; `BallotFilter.Known` rebuilds the key set per ballot), home page 117 KB
  per view and 1–3 GB RAM at 256 views, Npgsql pool 100 = `max_connections` 100, GSS connect delay. No code changed.
- 2026-11-02 (§30, §31): email addresses encrypted at rest with a keyed-hash lookup; pentest round 3 fixes: paged ballot
  tables and a results cache, editor input cut on arrival, a bounded change log that also logs title and description
  and hides counts from viewers without results, server-side sessions, a Unix socket for the Linux service, a
  Telrang-only writer lock on PostgreSQL. Ballot rows for voters keep submission order (accepted).
- 2026-11-02 (§31 fixed, results/voting/editor part): option keys added by an edit are `_1`, `_2`, … counted by
  `PollQuestion.KeysIssued` (no slug holds `_`), so no key ever comes back without storing removed keys; stored
  `retiredKeys` lists are ignored on read and dropped on the next save. Change-log entries per poll: at most 500
  (`PollChange.MaxEntries`, the merged `Earlier` entry included), pages show the newest 50 (`ShownEntries`); saves that
  add to the log (not only option changes: title and description too) take a token from a per-poll bucket
  (`ChangeLogLimiter`, 10 at once, one more every 6 minutes). New `OptionChangeKind` values `TitleChanged`,
  `DescriptionChanged`, `Earlier` fit the existing string column, so no migration. Labels refuse Cc/Cf/Co/Cs, which also
  refuses emoji joined with a zero-width joiner (U+200D is Cf). Edits: one pass outside the write transaction, then
  the definition and ballot count are checked inside it (the pass runs again when either moved, at most 3 times; a
  ballot changed in place between pass and write keeps the pass's count). Results: `ResultsCache` (memory, 64 MB
  estimated, `Results:CacheMegabytes`) holds the computed results and one number per option per response;
  `PollChangeNotifier.Invalidated` drops entries. `HeavyGetLimiter`: results page and CSVs 4 at once per client key,
  2 × cores in all, 503 + `Retry-After: 2`, no abuse signal. Page GETs 600/min per client key
  (`RateLimiting:PageGetsPerMinute`), static files, branding, Blazor and health not counted (since 2026-11-05: only
  abuse-exempt endpoints; missing files and dotted paths count), no abuse signal. The home
  examples are rendered once a day (`ShowcaseCache`; the dates example starts today, so the day is part of the key, the
  theme is not: colours come from the stylesheet). A circuit's out-of-memory records `AbuseSignal.CircuitOutOfMemory`
  (weight 5).
- 2026-11-02 (§30 implemented; §31 deployment items fixed): emails encrypted in `ProtectedTokenUserStore`
  (`EmailProtection`: Data Protection `Telrang.Email.v1` with the `e1:` prefix; HMAC-SHA256 keyed by `keys/emailkey` in the
  normalised columns and `OutboxMessage.ToHash`), not Identity's `ProtectPersonalData` (it would need one model per key
  ring and a reversible lookup protector); plain values assigned in code are encrypted on save. `EmailUpgrade` at startup,
  row by row, guarded by the old value. Admin log notes encrypted whole; mail server errors lose addresses. A lost key
  ring moves `emailkey` aside and starts a new one. Sessions: `AuthSessionStore` (`AuthSessions`, row id = SHA-256 of the
  cookie's key, ticket protected); the user store deletes a user's rows when its stamp changes; `SessionCookieHandler`
  gives every sign-in a new key (the stock handler renews the request's key, which kept a copied cookie alive across a
  password change). Writer lock: `WriterLocks` row `FOR UPDATE`/`FOR SHARE`, created at startup (not by a migration,
  so `copy-to-postgres` still sees an empty target); pool default 30. Linux socket: the unit's primary group is
  `telrang-proxy` (`Group=`, `SupplementaryGroups=telrang`), because systemd re-applies the runtime directory's owner
  before `ExecStart`, so a `chgrp` in `ExecStartPre` does not last (found under real systemd). Loopback is no longer a
  default trusted proxy. The setup key goes to `admin-key.txt` on systemd too. `copy-to-postgres` detects an open SQLite
  file by the `-shm` file's lock byte (an idle WAL connection holds no lock on the database file). Release: the tested
  image digest is tagged, no rebuild; base images pinned by digest. Not run: the Windows script (parsed with pwsh only)
  and the release workflow.
- 2026-11-02 (§32): reference setup = Docker behind Nginx Proxy Manager; app on a private two-container
  network trusting NPM's fixed address only, no published port, hardened container (read-only, no capabilities,
  memory limit); go-live checks rewritten for it; upgrade fixture test before the first update.
- 2026-11-02 (§32 upgrade safety implemented): one fixture per release in `tests/Telrang.Web.Tests/Fixtures/upgrade/<version>/`
  (`app.db`, `keys/`, `branding/`, `postgres.sql`, `manifest.json`), made by `UpgradeFixtureGenerator` through the app's
  services and pages, checked by `MigrationUpgradeTests` on SQLite and PostgreSQL. The PostgreSQL state is a plain-SQL
  `pg_dump --inserts` of the fixture after `copy-to-postgres` (from PostgreSQL 17's own pg_dump), restored with Npgsql:
  it needs no `psql` or `pg_restore` on the test machine, restores on any PostgreSQL 17+ server, and holds the schema the
  release's PostgreSQL migrations made; re-running `copy-to-postgres` at test time could not stand in for it, since the
  command refuses a SQLite file whose migrations are not current. Expected results are stored as a digest (keys, scores,
  counts), not as serialized result objects, so they stay comparable across releases. A tagged CI build fails without its
  own version's fixture at the current schema (`GITHUB_REF_TYPE`/`GITHUB_REF_NAME`). The first fixture is 0.3.0.
- 2026-11-02 (§32 implemented): `compose.npm.yml` runs **PostgreSQL** by default (changed the same day; `compose.yml`
  stays SQLite): `db` only on an `internal` network, as `postgres` (999) from the start, so `cap_drop: [ALL]` needs no
  capability back and the root file system is read-only (`tmpfs` for `/tmp`, `/var/run/postgresql`,
  `/docker-entrypoint-initdb.d`). The init script (own non-superuser role owning the database, `REVOKE CONNECT, TEMP
  ... FROM PUBLIC`) is written by an inline entrypoint, because compose cannot inject a `configs: content:` file into a
  read-only container. Secrets: three files (connection string for 1654, app password and superuser password for 999,
  `0600`). Same project name and `app-data` volume as `compose.yml`, so `copy-to-postgres` moves an existing instance with
  `run --rm app` (tested). Found with a real NPM 2.16: its nginx sets `real_ip_header X-Real-IP` for private and
  downloaded CDN ranges, so a client from such an address (a Cloudflare Worker, IPv6 through Docker's userland proxy, the
  LAN) picks the address Telrang sees; the proxy host's Advanced `set_real_ip_from unix:;` replaces that list for the
  host (the test checks it with a control run without the line). NPM's own default server claims `localhost`, so the
  test uses `vote.telrang.test` (Firefox `network.dns.localDomains`, curl `--resolve`) and trusts its self-signed
  certificate in the Firefox profile with `certutil` (an accepted certificate error makes WebAuthn throw). The proxy
  network is created with `--ip-range` above NPM's fixed address: otherwise Telrang, started first, takes `.2`. The app
  needed no change for the read-only file system. NPM's client `X-Forwarded-Proto` passes through, harmless with Force
  SSL (plain HTTP never reaches the app). Over HTTPS Firefox gets the brotli assets and reports the module scripts of a
  page left mid-load as integrity failures (console only, the page is gone; plain HTTP has no brotli, so local e2e never
  saw it): the harness's `goto` now lets the current page finish loading first. Not tested: the `DOCKER-USER` egress rules, STARTTLS to a real server (the
  fake SMTP server runs without TLS), Let's Encrypt, IPv6.
- 2026-11-04 (§14.2 revised, alpha feedback): copy pass against "paragraphs everywhere". Results captions became
  `<abbr title>` on the Ranks / Spread headers (also on the phone cards); the share page shows `Closes …` / `Closed …` /
  `Deleted on …` with the reason in a `title`, the voter mode as `Anyone with the link · one response each`, the invite
  explanation in an `OptionHelp`; the vote page drops "Rank by dragging" (the handles show it) and says "Editable in this
  browser only (cookie)". The home page loses the name line (README) and the showcase caption (now the examples'
  Markdown description). The stock ASP.NET Error page text (Development mode advice, shown in Production) is replaced.
  Help text that narrated the screen was trimmed. New `CopyTests` source scan keeps it that way.
- 2026-11-04 (§33): results readability from alpha feedback: values on hover for every bar, a ballots view
  (`?view=ballots`; owner always, voters with Show ballots; ranked/points one column per ballot, dates one row per voter),
  and `MainLayout` on `/p/*` so signed-in visitors keep the navigation (reverses the public-header rule of 2026-10-28).
- 2026-11-04 (§33 ballots view implemented): `ResultsService.BallotsAsync` builds one page of the view from the cached
  per-response cells (`BallotTable`); the summary (`ComputeAsync`) no longer carries ballot rows. A `?view=ballots` the
  viewer may not open renders the summary. Pager text says "Ballots 51–100 of N". See the note under §33.
- 2026-11-04 (deploy): the compose files set `pull_policy: ${TELRANG_PULL_POLICY:-never}` for the app: the image is
  local (`docker load` / build), so `compose pull` and a stack manager's re-pull (such as Portainer's "Re-pull image")
  failed with "pull access denied".
- 2026-11-04 (public repository): development moves to a fresh repository (LayertraceLabs/Telrang) with a clean
  history; the internal reports (audit, pentests, user-flow review) and the planning notes stay private. Release checks: a pre-release tag (1.2.0-rc.1) is checked against the upgrade fixture of the release it
  leads to (1.2.0); release.yml checks the archive set and the version stamped in every Telrang.Web.dll, and that the
  footer of every install format shows the tag's version and commit; the SDK no longer appends its own source hash
  (`IncludeSourceRevisionInInformationalVersion=false`). CodeQL (skipped while the repository is private), Dependabot
  for NuGet, Actions and the Docker base images, and SECURITY.md (GitHub private vulnerability reporting).
- 2026-11-05 (first CI runs): private-repo runners have 2 cores, so the heavy-GET total (2 × cores, at least 4) equals
  the per-client limit there; the limiter test pins the total. The e2e harness's `click` now also lets the current page
  finish loading first (as `goto` did): one run reported a page left mid-load as module integrity failures on plain HTTP.
  First release run (v0.3.0-rc.1, not published): `smoke-linux` still installed the pre-§31 way (no `telrang-proxy`
  group, probes on TCP 8080); it now follows docs/deploy/linux.md and probes through the Unix socket, and prints the
  journal on failure. The e2e `formAge` counts from the response's first byte, not navigation start (a slow cold first
  response made the setup form "too fast").
  Build provenance attestations need a public repository on the free plan: release.yml skips them while private (as
  CodeQL), and the release notes name `gh attestation verify` only when they exist.
  v0.3.0-rc.4 (not published): the same race on the vote page's lazy imports (`sortable.esm.js` via ranking.js),
  which start after `readyState` is complete; vote.js now marks `<html data-enhancing>` while they load and the
  harness's `loaded()` waits for it to go.
- 2026-11-06 (public repository): `main` is protected by a ruleset (pull request, required checks `build-test`, `e2e`,
  `web-tests-postgres`, no force push or deletion). Dependabot updates without a major bump merge by themselves once
  those pass. Every release also moves the image tag `:next`, so a deployment that follows release candidates re-pulls
  one tag; `:latest` stays for full releases. README badges: CI on `main`, newest release, licence, .NET.

- 2026-11-05 (request limits): every request gets small body and form limits. Kestrel refuses bodies over 256 kB (four
  times the vote form's 64 kB, the largest HTTP form; the editor and branding go over the circuit); forms allow at most
  64 values, 64 kB per value and 256-character keys; `/_blazor` allows four circuit messages so long-polling sends
  still fit. `FormLimitMiddleware` enforces this on any server, refuses an oversized Content-Length before reading and
  records `FormTooLarge` (weight 2); failed antiforgery checks record `Antiforgery` (weight 1, not on vote posts).
  Reason: before, any anonymous POST could make the server buffer a 30 MB form, enough to run it out of memory with a
  few dozen parallel requests. POSTs that no other partition takes get `RateLimiting:OtherPostsPerMinute` (120, no
  abuse signal). Page GETs are uncounted only for abuse-exempt endpoints, so missing files and dotted paths count.
  Heavy GETs match the path in any case (routing does), Kestrel sends no Server header, and every request limit,
  sign-in and setup included, buckets IPv6 per /48 like the abuse tracker (a /64 bucket let one /48 multiply the
  sign-in limit 65,536 times).
- 2026-11-05 (stability): the mail sender claims each message before sending (see §14.5): delivery is at least once,
  a failed save no longer re-sends every 10 s past the hourly cap. A results computation removes its own in-flight
  entry when it ends and a change drops a running one, so an abandoned computation no longer stays in memory
  uncounted. The close sweep drops polls mailed within `NotifyInterval` in SQL; open polls and closes mailed long ago
  are still filtered in memory, because `ClosedAt`/`ClosesAt` are not comparable on SQLite (a comparable column is a
  later migration). A database error during the live stream's recheck ends the stream with an Information line, not a
  failed request per open stream.

## 14. Phase 5 (approved 2026-10-25; 5a, 5b part 1 and 5b part 2 implemented)

Status: **[5a done]** marks what Phase 5a implemented (details and deviations in §13, "phase 5a").
**[5b-1 done]** marks Phase 5b part 1 (§13, "phase 5b part 1"): §14.3a without the notify-me mail,
the size caps of §14.4, time zones from §14.9 and the vote/results copy pass of §14.2. Part 2:
rate and per-user limits of §14.4, §14.7, the health/rates part of §14.8 **[5b-2 done]** (§13, "phase 5b
part 2"); the notify-me mail (§14.3a), retention and ownership transfer (§14.9) followed in the cleanup round
(§13, 2026-10-26 "cleanup round").

### 14.1 Admin bootstrap: one-time setup key [5a done]
Replaces both "first registrant becomes admin" and `ADMIN_EMAIL`. On startup, if no user holds
the Admin role: generate 32 random bytes, log it once at Warning level as
`Admin setup key: <base64url> — open /setup within 1 hour`, keep only its SHA-256 in memory.
`/setup` (rate limited, constant-time compare) takes the key, then creates the admin account:
email, password, mandatory TOTP enrolment. Key is single-use and regenerated on each
restart while no admin exists. `/setup` returns 404 once an admin exists. The key is written to
disk only by the Windows and systemd services (`{DataDir}/admin-key.txt`, deleted at the next start; §29, §31).

### 14.2 UI copy [5a: pages touched in 5a; 5b-1 done: vote, results, password gate]
Controls are not explained. One terse line per page at most, and only when the rule is not
obvious from the control ("Rank by dragging. Unranked options score 0."). No "You can…", no
"Simply…", no restating what a button does next to the button. Rules of a poll (scoring,
bans, budget) are shown as one compact line under the title, with a "How scoring works"
disclosure for the long form. Error text says what to change, nothing else. Do a copy pass
over every page in Phase 5 and keep this as a project rule.

Revised 2026-11-04 (alpha feedback, "paragraphs everywhere"): facts are shown as labels, badges, table headers or
`label · value` lines, never as sentences ("Closes 2026-11-12", not "Closes automatically on …"). An explanation of a
column, a status or a rule goes in a `title` (column headers as `<abbr title>`, a status line's `title`), an
`OptionHelp` or a `<details>`; nothing is explained under a table. Help text states rules, limits and consequences,
never what the screen already shows (no "Save stores the fields above", no list of the columns). Alerts and status
messages are exempt. `CopyTests` enforces it: no visible paragraph or form hint over 12 words outside `OptionHelp`,
`<details>` and alerts, and no filler ("You can", "Simply", "Please", "Note that").

### 14.3 Accounts (revised after user feedback) [5a done]
- Email is the login name and the only identity field. No usernames. Internal references
  are the Identity user Id (a GUID string already); GUIDs are shown only in admin.
- Data held per user: email, email-verified flag, password hash, 2FA data, optional default
  display name, timezone, created/last-login timestamps, limit overrides. Nothing else.
- Verified email is required to create polls and to recover a password.
- Admin bootstrap: one-time setup key (§14.1). `/setup` collects email + password and forces
  TOTP enrolment. The setup key proves control of the server, so the admin's email is marked
  verified without a mail round-trip (mail is not configured yet at that point). The admin
  then configures SMTP and uses "send test mail to me" to confirm it works.
- Admin creates accounts without an email: creates a user row with a placeholder (null
  email, `PendingSetup` state) and a single-use setup link (32 random bytes, hash stored,
  7 days). The link's page collects email (verification mail sent if mail is configured,
  otherwise the admin can mark it verified), password and TOTP. Admin can also invite by
  email, which is the same flow with the link mailed.
- Registration toggle lives in DB settings; off hides the Register link and the page 404s.
- Admin actions: reset password (issues a new setup link; no temporary passwords in the
  clear), disable 2FA, lock/unlock, delete, promote/demote, see last login, created, polls.
- Account deletion (self-service and admin): soft delete. Email set to null, password hash
  and 2FA data cleared, security stamp bumped, `DeletedAt` set, row kept so ballots and
  admin-log references stay valid. The user's own polls are deleted (warned beforehand).
  Their ballots in other people's polls stay, under the name they typed; the GUID no longer
  resolves to a person. The same email can register again later.
- Self-service: change email (verify the new address first, notify the old one), change
  password, 2FA management, "log out everywhere", download my data (JSON: profile, polls,
  ballots), delete account.
- Email change and recovery mails are sent through the outbox (§14.5).

### 14.3a Voters, ballots and "my votes" [5b-1 done; notify-me: cleanup round; open: the creator's name suggestion]
Identity follows §16 (signed in: account, server-side; anonymous: cookies). It replaces "invite tokens are the
identity even when signed in" below: a signed-in account claims the token it uses.
- `voters:` becomes `accounts | public | invite` (replaces `link`):
  - `accounts`: only logged-in users with a verified email.
  - `public`: anyone with the link; logged-in users are recorded by account, anonymous
    visitors by the browser voter key (§14.11).
  - `invite`: single-use tokens as today.
- Ballot carries `UserId` (nullable GUID) or `VoterKey` (anonymous/invite) plus the typed
  `DisplayName`. Names are free text; duplicates are allowed. Owners see names, never GUIDs.
  Admin UI shows GUIDs in the user list but has no "ballots per user" view; the account link
  on a ballot exists for enforcement and for the user's own page only.
- `responses: one | many` (default `one`). With `one`, a logged-in user who already
  responded is taken straight to their ballot. With `many`, they see a banner "You already
  responded" with a link to it and may submit another. Anonymous visitors behave the same
  way via the voter key.
- `/my/votes`: polls the user responded to, their ballot, open/closed state, and a results
  link when visible. Optional per-poll "notify me when results are available" (mail) **[done, cleanup round]**:
  a checkbox "Email me when this poll closes" on the vote page, saved with the ballot
  (`Ballot.NotifyOnClose`), offered only to signed-in voters with a verified email on polls whose results
  voters can see (`after-vote`, `open`) while mail and the public URL are configured. A 5-minute sweep mails
  each subscribed account once per close (`ResultsReady`, results link and `/notify/off/{token}`, a Data
  Protection payload of account and poll; no sign-in, idempotent). Anonymous ballots never carry it.
  Audit fixes: the tick is per response, the mail per account (the sweep de-duplicates; kept). Before queueing,
  each recipient is re-checked as that account (`AccountGate.MayActAsync` with a verified email, then
  `PollAccessService` → `CanSeeResults`); a revoked invitation and a transfer clear the tick. `/notify/off/{token}`
  shows one line and a *Stop emails* button on GET and switches off on POST (antiforgery), because mail scanners
  prefetch links. Invite mode needs no verified email (an invite link is a capability): an unverified account votes
  with it and claims it.
- Poll creator may set a default display name suggestion; the user's account default name
  pre-fills the field.

### 14.4 Limits (global defaults in DB settings, per-user nullable overrides) [5a: defaults stored and editable; 5b-1: text/count caps enforced; 5b-2: per-user limits and overrides enforced]
| Limit | Default |
|---|---|
| Active (open) polls per user | 20 |
| Poll creations per user per day | 20 |
| Max ballots per poll | 1000 |
| Max invite tokens per poll | 500 |
| Max poll duration (ClosesAt − now) | 180 days; polls without `closes` auto-close after this (fixed at creation; checked again only when the close date changes) |
| Title | 120 chars |
| Option label | 80 chars |
| Option description (Markdown) | 1 000 chars |
| Poll description (Markdown) | 2 000 chars (§20) |
| Options per poll | 100 |
| Voter display name | 60 chars; setting clamped to 10–120 (applied since the audit fixes) |
| Responses per voter under `responses: many` (5b-2) | 10 |
Text and count caps are enforced in Core (`PollLimits` passed to `PollDraftValidator` / `OptionListParser`, built from
the settings by `PollParsing.LimitsFrom`), so the editor shows them under their field, and re-checked on save. The
DSL source-size limit is gone (§20.4).

### 14.5 Mail [5a done]
- Settings in DB (admin page): host, port, STARTTLS/implicit TLS, username, password/token
  (encrypted at rest with Data Protection), from address, from name, "send test mail". The password is write-only and
  bound to the server: changing host, port, security or username without a new password clears it, and a username with
  security None is refused (also by the transport), so it cannot be sent in clear text to another server (pentest AV-11).
  Works with an SMTP provider's submission port (e.g. smtp.example.com:587, STARTTLS, token as
  password). Library: MailKit (add to the dependency list).
- All mail goes through an outbox table + background sender: each message is claimed before it is sent (`SendAfter`
  15 min ahead, saved first) and its outcome saved without the stopping token, so a failed save means one more send
  after the claim runs out (at least once), never a re-send every pass. Retries after 1, 5, 15 and 60 minutes,
  dead-letter on the 5th failure (so every step is used), per-hour global cap (default 100), per-recipient
  cooldown (default one message per type per 15 min), max 3 unanswered messages per recipient per day.
  The admin's test mail and results-ready mails (opt-in, verified address, one per poll close) skip the
  per-recipient limits but count toward the hourly cap. Admin sees queue length, last error, last 50 sent.
- Bodies are cleared as soon as a message is sent or dead-lettered (`LastError` stays), and sent rows are deleted
  after 48 h in the sender loop (pentest AV-04): the outbox held live setup and reset links. Recipient, type and
  times stay meanwhile, which is all the cooldown, the daily maximum and the hourly cap count.
- Message types: invite, password reset, email verification, results ready (cleanup round), (later) poll
  invite by email.
- Operator's DNS still needs SPF/DKIM/DMARC for the from domain; document it.

### 14.6 Password recovery [5a done]
Only for accounts with a verified email. `/Account/ForgotPassword` always says "if that
account exists and has a verified email, a message is on its way", returns in constant time
(enqueue only; the answer is padded to at least 300 ms, pentest AV-15e). Token: 32 random bytes, hash stored, 30 min, single use, invalidated by any
password change. Per-account cooldown 15 min, per-IP limit via the existing limiter. Reset
clears lockout, bumps the security stamp (logs out other sessions), keeps 2FA enabled and removes the passkeys (a
passkey signs in without the password, so one planted by an intruder must not survive; pentest AV-06).
Account recovery when 2FA is lost: recovery codes, or an admin disables 2FA. No email bypass.

### 14.7 Bot and abuse handling (no third-party captcha by default) [5b-2 done, details in §8]
- Forms with public exposure (register, forgot password, vote submit; the poll password gate until §20.3):
  honeypot field (CSS-hidden, must be empty) and a signed form timestamp (reject if
  submitted < 2 s or > 2 h after render).
- `AbuseTracker` (in-memory, per IP /32 or /48, and per account): counts failures and limit
  hits; crossing thresholds applies a temporary block with escalating duration
  (1 → 10 → 60 min), returning 429 with a plain page. Admin sees current blocks and can clear
  them or block an account manually (duration, note); addresses are blocked only as `BlockedRange` rows (§17).
  Tracker blocks are not persisted; a restart clears them.
- New accounts (< 24 h old, or created via open registration) get halved poll limits.
- Nothing stores client IPs beyond the in-memory trackers; ballots never carry an IP.

### 14.8 Health and rates (admin page, no activity log) [5a: admin action log; 5b-2: Status tab]
- Uptime, version, DB file size, free disk on the data volume, ballot and poll counts,
  mail queue length.
- Rate counters over the last 1 h / 24 h from `System.Diagnostics.Metrics`: requests,
  429s, failed logins, poll-password failures, ballots submitted, mails sent/failed, active
  circuits.
- Log ring buffer: last 200 entries at Warning or above, in memory, shown on the page.
- Admin action log (small, persisted): who did what to which user/poll and when. Not an
  activity log of users; just admin actions, for accountability. Rotates at 10 000 rows.

### 14.9 Things not in the request that are worth doing [5a: backup, sessions, legal page, export]
- **Backups**: document volume backup; add an admin "Download backup" (SQLite `VACUUM INTO`
  a temp file, streamed, admin only, rate limited) and note the keys dir must be backed up
  with it or every cookie and poll grant dies on restore.
- **Retention** [auto-delete done, cleanup round; "clear names" open]: optional auto-delete of closed polls
  after N days (`Retention.DeleteClosedAfterDays`, default 0 = off), checked at most once a day by the poll
  sweep; the owner's share page says "Deleted automatically on <date>" while it is on. User and admin can
  delete ballots of a poll; voter names are PII, so results CSV is owner-only (already) and a "clear names"
  action exists.
- **Time zones** [5b-1 done]: `closes:` means the end of that day in the poll's `timezone:` (IANA
  id, default from the creator's account setting at creation, which defaults to the browser's;
  else UTC). Close times are shown in the viewer's account zone when set, else in the poll's zone,
  always with the zone name.
- **Sessions**: "log out everywhere" on the account page (bumps the security stamp); password
  change already invalidates other sessions.
- **Legal pages**: admin-editable Markdown "About / Privacy / Imprint" page linked in the
  footer; needed once strangers register. Cookies are strictly necessary, so no banner.
- **Cert rotation** in cert-file mode: verify whether Kestrel reloads a changed .pfx without
  restart on .NET 10; if not, document a restart or recommend the reverse-proxy mode.
- **Ownership transfer** [admin action done, cleanup round; second owner open] of a poll to another user
  (admin action) and a second owner, for when someone leaves.
- **Upgrade notes**: migrations run at startup; document "back up before upgrading".
- **Notify me when results are available** [done, cleanup round]: see §14.3a.
- **Deferred, on purpose**: per-poll email invitations to voters (needs the outbox first),
  i18n, OAuth/social login. (Postgres was deferred too; implemented, §28.)

### 14.10 Decisions (2026-10-25)
1. "Poll registration limits" = max ballots per poll and max invite tokens per poll.
2. `ADMIN_EMAIL` removed; setup key only. The admin is a normal account: email + 2FA.
3. Verified email required for poll creation and recovery.
4. Captcha (Turnstile/hCaptcha) is an optional admin setting applied only to anonymous
   voting on `public` polls; never on login or registered users.

### 14.11 Cookies and the banner question
What the app stores in the browser: auth cookie, antiforgery cookie,
invite token cookie, and the anonymous voter key `vk`. Every one of these exists only to
deliver the thing the visitor just asked for (log in, cast or revise a
ballot). Under ePrivacy art. 5(3) that is the "strictly necessary" exemption, so no consent
is required, only transparency: a privacy page that lists them. There is no analytics or
cross-site tracking. Decision: **no consent banner**. The vote page shows one inline line
for anonymous voters: "A cookie keeps your ballot editable in this browser" linking to the
privacy page.

If a banner is ever wanted anyway: two buttons, Accept / Decline, no categories. Storing the
decision itself (including a decline) in a small cookie is allowed; regulators (CNIL, EDPB)
treat remembering a refusal as necessary to honour it, with a recommended retention of about
six months. Store `consent=v<version>:<accept|decline>`; a new policy version re-asks.
Declining the voter key means: ballot accepted, but it cannot be revised and one-per-browser
cannot be enforced, so the poll owner may disallow anonymous responses.

### 14.12 Still open
- (Settled 2026-10-26, §16: an anonymous ballot is never merged into an account by signing in on the same
  browser; only invite tokens are claimed, by the first write of a signed-in account.)
- (Settled in 5b-1: mode names `accounts | public | invite`, `responses: one` as the default.)

## 15. Trust model (decided 2026-10-26)

- A poll link is a capability. Whoever holds it is trusted to respond honestly; the app does
  not try to detect the same person behind several browsers. No IP logging, no device
  fingerprinting, nothing that profiles visitors: that data is personal data under GDPR and
  we do not want it.
- Creators choose the exposure: `voters: public` for links shared privately, `accounts` when
  a link will be posted somewhere public, `invite` for personal token links (no poll passwords, §20.3). Anything beyond
  that is the creator's responsibility; the server protects itself with per-poll and
  per-account limits and the abuse tracker, not with identification.
- `responses: many` is a creator option for the "one family member answers for the whole
  family" case: each response carries its own name. Default stays `one`.
- Isolation is absolute on the server side: no user can read another user's polls, ballots
  or account data through any route, id enumeration, or rendering path. Every poll read goes
  through `PollAccessService`; every owner mutation checks ownership; Markdown is rendered
  with HTML disabled; ids are 128-bit random; unknown or foreign ids return 404, never 403.
- Gated states (pending setup, must enrol 2FA) are enforced as an authorization requirement
  on every render, not as an HTTP middleware allow-list, because interactive navigation
  never passes through middleware.

## 16. Identity model (decided 2026-10-26)

The rule: **signed in → the account, stored server-side; anonymous → the browser's protected cookies.**
Never key a signed-in user's state by a cookie.

- One resolver. `PollAccessService` turns the visitor (`VisitorContext`: user id, `vk`, invite
  token hash) into a `VoterIdentity` {Kind: `Account` | `Cookie` | `Invite`, Key}. Everything else derives the
  stored columns from it (`Stored` = `Ballot.VoterIdentity`, `VoterKey`, `UserId`); `CanVote` and the
  `after-vote` part of `CanSeeResults` use the same identity.
- Signed in:
  - Ballots by account (`u:<id>`, `UserId` set, `VoterKey` null) in every voter mode.
  - Invite tokens are claimed: the first write (submit or delete of a response) by a signed-in account with an
    unclaimed token sets `InviteToken.ClaimedByUserId` and re-keys the token's existing ballots to the account
    (`u:<id>`, `UserId` set, `VoterKey` null) in the same transaction. Viewing alone claims nothing, so an
    owner checking a link or a link preview does not take an invitee's token. From then on the account finds
    its ballot without the token (vote page, `/my/votes`, results), on any browser; the vote page drops a
    leftover `pi_` cookie. Anyone else presenting the token, signed out or as another account, gets "This
    invitation is linked to another account. Sign in." (not 404: the invitee must not lose a real poll). One
    claimed token per account and poll (unique index); an account that already holds one ignores other tokens.
  - Revoking a claimed token moves its ballots back under the token's identity (account still recorded on
    them): the ballot counts, nobody can change it, a new invitation for the same account starts fresh. A
    voter-mode or option change that deletes ballots also clears the claims.
- Anonymous: everything stays in the browser's cookies, unchanged (`vk`, `pi_{id}`). Anonymous
  ballots stay anonymous; signing in later on the same browser merges nothing and claims nothing by itself.
- Shared browsers (audit fixes): `vk` is deleted on every sign-in and sign-out. `pi_{id}` carries the user id it was
  issued under (null = anonymous) and is read only for that same principal; the vote page deletes one issued to
  someone else (sign-in/out cannot: the cookie's path is `/p/{id}`). An account that wants to use an invite opens
  the link itself.
- Nothing ever moves from a cookie to an account (the one exception, the poll-password `PollGrantHandoff`, went with
  poll passwords, §20.3).
- The vote form takes nothing about the identity from the posted fields: every request (GET or POST) resolves it from
  its own cookies and principal (§21.1). A form rendered for one principal and posted by another (another account
  signed in, signed in, signed out) fails antiforgery's claim binding and writes nothing. (Until §21.1 an island
  ticket carried the identity, bound to the user id it was minted for.)
- Migration `IdentityModel`: adds `PollGrants` (dropped again by `RemovePollPasswordsAddAliases`) and `InviteTokens.ClaimedByUserId`. Tokens whose ballots were
  cast while signed in by exactly one account (the old model recorded `UserId`) are claimed by it and their
  ballots re-keyed; a token is left unclaimed when that account has other ballots in the poll (two used
  tokens), because they would collide. Anonymous ballots are untouched.

## 17. Phase 6: branding, theme, welcome, persistent blocks (decided 2026-10-26; implemented, §13 "phase 6")

- **Branding files** live in `{DataDir}/branding/` with fixed names (`logo.<ext>`,
  `banner.<ext>`, `background.<ext>`; ext = png|jpg|webp; since the cleanup round also `favicon.png`, PNG only,
  ≤ 256 kB, linked from every page instead of the static `favicon.png` when present). Uploaded from the admin Settings
  tab or replaced by hand. Validation by file header (magic bytes), never by extension or
  declared type; SVG is refused. Size caps: logo and banner 1 MB, background 2 MB. Served by
  `/branding/{name}` with a fixed content type, `X-Content-Type-Options: nosniff`, long cache
  and a `?v=<mtime>` version. Updates never touch the data volume.
- **Background** is opt-in, shown with a translucent overlay whose strength the admin sets.
- **Palette** (2026-10-28, replaced by §24.4 on 2026-11-01): one built-in palette, Teal / Coral (base `#0f5e63`,
  highlight `#ff7a5c`), and four optional overrides `Theme.LightBase`, `Theme.LightHighlight`, `Theme.DarkBase`,
  `Theme.DarkHighlight` (no row = the built-in colour; on save "Light base: use #rrggbb." etc.). The designed palettes
  Indigo / Amber and Plum / Gold and the Custom choice are gone; `ThemeSettings.From` upgrades stored themes (§24.4).
  All derived tokens (surfaces, borders, text, muted, link, focus, header band, nav active, status tints, sidebar) come
  from each mode's two colours through one deterministic algorithm (`PaletteDerivation`: fixed OKLCH lightness targets
  per mode with the base hue as a tint, then lightness steps until WCAG AA holds). `theme.css` carries a light block
  (from the light pair), a `data-bs-theme=dark` block and the same dark block for system-on-dark-OS (from the dark pair).
  Reset returns to the built-in colours, font size and font.
- **Theme**: default mode (light, dark, system; the visitor's own choice wins, §24.4) plus a small set of validated
  properties: the per-mode colour overrides, base font size, font stack chosen from a fixed list
  of system stacks (no font CDN). Rendered as a server-generated stylesheet `/branding/theme.css`
  (CSP `style-src 'self'` stays strict; no inline styles). Optional "custom CSS" field,
  admin-trusted, served in the same stylesheet, labelled as advanced. Admin pages link
  `/branding/theme.css?admin=1`, the same theme without the custom CSS, so a broken rule cannot hide the
  controls that fix it (cleanup round). Since the audit fixes also `/Account/*`, `/setup*`, `/notify/*`, `/Error`,
  `/not-found` and the static blocked/429/expired pages (`ThemeService.UsesAdminVariant`), so an admin whose
  session ended can still sign in; `js/theme.js` swaps the `href` of the one `<link data-theme-css>`.
- **Layout** (`Theme.Layout`, 2026-10-28): `topbar` (default, stored as no row), `sidebar` or `compact`; the shell
  `MainLayout` renders for signed-in pages (§11). Chosen in the Theme section of the Settings tab and validated like
  the other theme keys ("Layout: choose one from the list."); a stored value that fails validation reads as the
  default. Not part of `theme.css`: saving a different layout reloads the admin page into the new shell.
- **Welcome message**: `Home.WelcomeMarkdown` setting, rendered through `MarkdownRenderer`.
- **Persistent CIDR blocks**: `BlockedRange` table (Cidr, Note, CreatedAt, CreatedBy,
  ExpiresAt nullable). Checked by the abuse-block middleware before anything else; admin
  Abuse tab lists, adds and removes them. The one block mechanism for addresses (audit fixes): temporary blocks are
  rows with `ExpiresAt`; `0.0.0.0/0` and `::/0` are refused with loopback and the trusted proxies. Signed-in admins are never blocked. Country
  blocking is out of scope (do it at the proxy; see `docs/deploy/`).

## 18. Dates polls (decided 2026-10-29)

A third poll type for "when can you": the options are dates or time slots, every voter answers each with yes,
maybe or no. One ballot per identity like the other types (`responses: many` works unchanged, for a family).

### 18.1 Definition
- `type: dates`. Type-specific key: `maybe: yes | no` (default yes). The ranked and points keys are errors here, as
  they are for each other.
- Option items (§5): `YYYY-MM-DD`, `YYYY-MM-DD HH:MM`, `YYYY-MM-DD HH:MM-HH:MM`, `YYYY-MM-DD to YYYY-MM-DD` (one
  option per day, inclusive, at most `OptionsMax` in total), a range may carry a time or span that applies to each
  day. Times are 24-hour wall-clock in the poll's `timezone:`; an end before its start is a line error. Anything
  else as an item: "Line n: write a date like 2026-11-03, a time like 2026-11-03 19:00, or a range like
  2026-11-03 to 2026-11-05."
- The parser normalises labels (`2026-11-03`, `2026-11-03 19:00`, `2026-11-03 18:30–21:00` with an en dash), sorts
  options chronologically (date, start), refuses duplicates ("listed twice") and fills `PollOption.Date`,
  `Start`, `End` (nullable init properties; null for the other types and for definitions stored before this).
  Keys are the slug of the normalised label as for every option. Descriptions under items work as usual.
- Immutable like the other types (`type` cannot change). Changing the dates of a poll with ballots is an option
  change: since §26 an option change like any other (the option keeps its key, a removed date is ignored on read).
- Core: `PollType.Dates`, `PollDefinition.Maybe`, `DatesBallot(IReadOnlyDictionary<string, Availability>)` with
  `Availability { No, Maybe, Yes }`, `BallotValidator.Validate(poll, DatesBallot)` (unknown key, Maybe while
  `maybe: no`; missing keys read as No; an all-No ballot is valid: "none works" is an answer),
  `DatesResults.Compute` → rows in chronological order with `Yes`, `Maybe`, `No` (= voters − yes − maybe) and
  `Place` (1 = most yes, ties by most maybe, then date; equal counts share a place).

### 18.2 Voting
- `DatesVote` island (same ticket pattern as `RankedVote`/`PointsVote`), no JS: every cell is a button that
  cycles no → yes → maybe → no (no ↔ yes when `maybe: no`), with the state in its text/icon, not only in colour
  (`aria-pressed` carries yes; a visually-hidden label carries the word). Yes uses the success tokens, maybe the
  warning tokens, no the plain surface; never the highlight.
- One view switcher (segmented buttons "Calendar · List · Timeline", component state, default Calendar; not
  persisted) above the cells; all three views render the same answers:
  - **Calendar**: one month grid per month that holds an option, Monday first, weekday headers, days without an
    option are plain text, days with options hold one button per slot (the time as its label, or the day number
    for a whole-day option). Phones: the grid keeps seven columns; long slot lists wrap inside the cell.
  - **List**: one row per option (label, description) with the cycling button; `maybe: yes` rows show the three
    states as a segmented control instead of one cycling button (both are the same `DatesCell` component).
  - **Timeline**: a horizontally scrolling strip of columns in chronological order, month labels above, one button
    per option, for seeing a stretch of days at a glance.
- Name field, captcha, notify-on-close, confirmation summary (`VoteSummary`, heading "Your availability"),
  `responses: many` and the read-only `BallotView` after close work as for the other types; `BallotView` for dates
  is the list view, read-only.

### 18.3 Results
- `ResultsService` adds `PollResults.Dates` (the `DatesResultSet`) and, for the owner only, `DatesGrid`: rows
  (display name as entered, blank → "Anonymous", one per response) × options with each answer; null when the poll
  has `names: none` or the viewer is not the owner. Visibility rules (`CanSeeResults`) are unchanged.
- `DatesResultsView`: the same switcher and views as voting, read-only cells that show yes and maybe counts (list
  view: a stacked bar yes / maybe / no in the status tints plus the three numbers). The best date(s) (`Place` 1)
  use the highlight like the leading ranked result (§11), and a line above the views names them ("Best: Thu
  2026-11-05 19:00 · 4 yes, 1 maybe"). The owner's grid ("Who can when") is a table under the views, phones scroll
  it sideways.
- CSV: ballots export writes `yes` / `maybe` / `no` per option; results export writes `Place, Date, Yes, Maybe, No`.
- Mails and "My votes" need only the type name ("Dates").

### 18.4 Touch points
`PollDraftValidator` and `OptionListParser` (line forms, sorting; originally `PollDslParser`), `PollOption`, `PollQuestion`, `BallotValidator`, `DatesResults`,
`PollJson` (ballot and definition round-trip; `DateOnly`/`TimeOnly` serialise natively), `BallotService`
(`SubmitDatesAsync`, `MyBallot.Dates`, `ToMyBallot`), `VotePage` (island choice), `DatesVote`, `DatesCell`,
`DatesViews` (the switcher and three layouts, shared by vote and results through a cell render fragment),
`BallotView`, `VoteSummary`, `PollRules`/`PollText` (type name "Dates", rules line: "Answer yes, maybe or no per
date." / "Answer yes or no per date."), `PollPreview`, `ResultsService`, `ResultsPage`, `DatesResultsView`,
`CsvExport`, the `PollEditor` calendar (§20.2), icons `check`, `question`, `x`, `calendar`, `list`, `timeline`. Every
`switch` on `PollType` becomes exhaustive. Tests: Core (parser, validator, results, sorting, ranges, limits), Web
(round-trip, submit/edit/delete, results and grid visibility, CSV, closed poll view, owner-only grid, 404/403
unchanged), plus a headless-browser pass of the three views on desktop and phone widths.

## 19. QR code on authenticator enrolment (decided 2026-10-29)

`EnableAuthenticator` shows a QR code of the `otpauth://` URI next to the key, as inline SVG (`<rect>` modules
with `fill="currentColor"` on a white `<rect>`, `shape-rendering="crispEdges"`, width via a CSS class; no inline
style, no script, no image endpoint). The encoder is `QrCode` in Web (byte mode, error correction M, versions 1–10,
mask chosen by the standard penalty rules; the URI is ~120 bytes, version 7-M holds 122, 10-M 213; longer input
throws and the page then shows the key alone). No package: the data is a secret, and a dependency-free encoder is
~300 lines that tests pin against vectors decoded once with `zbarimg` (the matrices are stored in the test, the
tool is not needed at test time). The key stays visible for manual entry.

## 20. Form editor, no DSL, no poll passwords (decided 2026-10-31; implemented, migrations `RemovePollPasswordsAddAliases`, `FormEditorQuestions`)

The poll text DSL (§5) and poll passwords go. A poll is edited through a form whose controls fit each setting;
only the description and option details are Markdown. No release exists yet: no data migration, the debug DB is
recreated (one new migration from the current model is fine either way).

### 20.1 Model (Core) [implemented 2026-10-31]
- `PollDraft` is the form model, shaped for several questions later: `Title`, `Description`, poll-wide settings
  (`VoterMode`, `Responses`, `Names`, `ResultsVisibility`, `ClosesOn?`, `TimeZone`) and `Questions` (exactly one
  for now; validated as such). A `QuestionDraft` holds `Type`, the type settings (ranked: `Scoring`, `Bans`, `RankAll`;
  points: `Budget`, `MaxPerOption?`, `SpendAll`; dates: `Maybe`) and `Options` (label, description, and for dates the
  date / start / end).
- `PollDraftValidator.Validate(draft, PollLimits, existing?)` → `PollDefinition` or `DraftError`s keyed by field path
  (`title`, `description`, `questions[0].budget`, `questions[0].options[3].label`), not line numbers. It takes over
  what the parser checked: limits, type-specific settings, at least 2 options, duplicates, date sorting and
  normalised labels, key slugs, and the edit rules (`ValidateEdit`: type and scoring immutable).
- `PollDefinition` gets `Questions` the same way; every question has a stable `Key` (`q1` now). Ballot payloads are
  keyed by question (`{"q1": {...}}`); scoring and results work per question. Single-question pages read
  `Questions[0]`. `Poll.Type` stays as a column (the first question's type) for lists and mails.
- `OptionListParser.Parse(text, type, limits)` is the Text mode of the options editor: one option per line, a leading
  `-`, `*`, `+` or `1.` is stripped, indented lines under an option are its description; for dates the §18.1 line
  forms. `OptionListParser.Format(options)` writes the same text back, so List ↔ Text round-trips losslessly.
- Removed: `PollDslParser` (header and body), `Poll.Source`, the DSL size limit, `PollText.ExampleSource`.
  `PollParsing` becomes the Web wrapper around the validator (admin limits + IANA zone check).

### 20.2 Editor (Web, `PollEditor` component, used by new and edit) [implemented 2026-10-31]
- Basics: Title (text), Description (Markdown textarea with a character counter and rendered preview).
- Type: segmented Ranked · Points · Dates; on edit, with every other setting, read-only text (§26).
- Type settings change with the type: ranked scoring select (locked on edit), bans number (0 = off), "Rank all"
  checkbox; points budget, optional cap, "Spend all"; dates "Allow maybe".
- Options, ranked and points: List / Text switch. List: a row per option (label, "Details" disclosure with the
  Markdown description, move up, move down, remove), "Add option". Text: the `OptionListParser` textarea.
- Options, dates: Calendar / Text switch. Calendar: month grids (Monday first, previous / next month); a day click
  toggles it; a selected day is all-day until it gets time slots (`19:00` or `18:30–21:00`); "Apply to all selected
  days" copies the slots. Text: the §18.1 line forms.
- Voting: Who votes (Anyone with the link / Signed-in accounts / Invite links), Responses (One / Several), Names (None / Optional / Required). Results: see §25 (Results for, Show results, Show ballots to voters).
- Closing: "Closes on" date input or empty = automatic after the max duration (shown); Time zone: searchable
  select of the IANA zones the server knows, default the account zone (else UTC).
- Field errors sit under their control (`aria-describedby`); the preview (`PollPreview`) stays beside the form.
- Browser drafts keep the key `draft:<userId>:<pollId|new>` and store the `PollDraft` as JSON.
- Start from an existing poll: "Copy" on My polls and the owner's poll pages opens `/polls/new?from={id}` (owner
  only, through `PollAccessService.CanEdit`; others 404). Copies title, description, every setting and option;
  `closes:` only when still in the future and within the limit, else empty. Never copies ballots, invite links,
  aliases or the closed state. A copy replaces a pending "new" draft.

### 20.3 Invite links and aliases [implemented 2026-10-31, migration `RemovePollPasswordsAddAliases`]
- Poll passwords removed entirely: `PasswordHash`, `PasswordVersion`, `PollGrant`, `pg_` cookies, `PasswordGate`
  page and endpoint, `PollGrantHandoff`, `PollPasswordFailures`, the `PollPassword` rate limit and `FormGuard`
  entry, the ticket's grant fields, `PasswordChangedNotice`. `VoterIdentity` keeps `Account | Cookie | Invite`.
- Invite links stay exactly as implemented (§7, §16: one identity per link, revisable until close, claimed by the
  first signed-in write, `pi_` bound to the principal). The only change: each link gets an optional alias
  (`InviteToken.Label` → `Alias`).
- `Ballot.Alias` (nullable, voter display-name limit): set from the link's alias when an invite poll's response is
  created (never on a revision); in every poll the owner can set or clear a ballot's alias in the "Responses" list of
  the share page (`PollService.SetResponseAliasAsync`, `CanEdit`; others get not found). Owner views (share page
  list, dates "Who can when", which then also appears under `names: none` once a response has an alias) and the
  ballots CSV (`Name`, `Alias` columns) show the alias when set, else the name as entered. Voters never see aliases.
- Link aliases are cut to the voter name limit when the links are generated (a pasted list is not refused); a ballot
  alias longer than the limit is refused ("Shorten the alias to N characters."). A link's alias cannot be edited.
- Project rules: the cookie-to-account handoff rule and the password clauses are dropped; the DSL rules are replaced
  by `PollDraftValidator`/`PollParsing`.

### 20.4 Limits [implemented 2026-10-31]
- Poll description default 2 000 characters (admin setting, as before); option description 1 000; the DSL source
  limit is removed. The options Text mode has its own limit derived from the option limits (pentest AV-03).

## 21. Static voting, live results for owners (decided 2026-10-31; revised after a design security review the same day)

Voters need no live connection: a ballot is filled in and sent once. The vote page and its ballot editors become plain
static SSR (a form post plus small JS modules); a Blazor circuit is only opened where something live helps (editor,
admin, account pages). The owner's results page gets live updates over a one-way event stream instead of a circuit.
Single container, SQLite, no scale-out: the in-memory pieces below are fine and stay in memory.

### 21.1 Voting is a form post [implemented 2026-10-31]
**Implemented** as specified below, with these differences (decisions log, 2026-10-31):
- Dates layout: inside the vote form `DatesViews` renders all three panels, the shown one without `hidden`; each option's
  radio group (`o.{key}`) appears in every panel under the same name, `checked` only in the shown one, so the browser
  keeps one answer per option. A hidden `shown` field keeps the layout across a refused submit. The results page and
  the editor preview keep the CSS-only switcher (no form, no `?view=`): it already works without JS.
- Limits: `[RequestSizeLimit(65536)]`, `[RequestFormLimits(ValueCountLimit = 232, ValueLengthLimit = 16384)]` on
  `VotePage` (`VoteFormLimits`). `FormLimitMiddleware` (before antiforgery) also refuses a body over the endpoint's
  limit (since 2026-11-05: over the global `RequestLimits` default on every other request, §13) itself (Content-Length, then a counting stream), because only Kestrel applies the metadata, and pre-reads the
  form under the limits so a refusal is "Form too large" (413 / 400), not antiforgery's "Form expired".
- Stamp: missing or forged counts as too old ("The form was open too long. Submit it again."); too old records no
  abuse signal (a person, not a bot); honeypot and too fast do, as before. In-process callers without a `BallotGuard`
  (tests) skip the form checks, not the captcha.
- Ranked position fields have `max` = the number of options, so a browser refuses larger numbers before posting; the
  server compacts any gaps that arrive.
- Delete success redirects to the page without a status line; the confirm render is "Delete this response?" with
  Delete (`confirm=N`) and a Cancel link. All forms post to `p/{id}` (`action`), so `?t=`/`?ck=` never reach a POST.
- The page still carries Blazor's persisted-state comment (`<!--Blazor-Server-Component-State:`), which every SSR page
  of an app with interactive render modes gets; it is not a component and opens no circuit. The test checks for the
  server-component marker (`<!--Blazor:`).
- Ranked JS: every drag-zone item carries all its buttons and CSS shows the ones of its zone; `ranking.js` moves the
  item and rewrites the fields (`o.{key}`, `b.{key}`) after every move, so the post is always what the zones show.

- `/p/{id}` renders the ballot editor as a `<form method="post">` with no `@rendermode`. `RankedVote`, `PointsVote` and
  `DatesVote` become static components; no `/p/...` page opens a circuit (a test checks the HTML holds no
  server-component marker).
- GET keeps doing what it does (invite `?t=`, `?ck=1` cookie check, identity via `PollAccessService`). A POST has its
  own branch: no `?t=` / `?ck` handling, never mints or rotates `vk`, never redirects except the PRG redirect below. A
  POST without a voter identity (cookie gone, cookies blocked) re-renders the posted values with "Cookies are
  required to vote. Reload the page." instead of losing them.
- The POST goes to the same page (Blazor SSR form handling, `@formname="vote"` / `"delete"`, `<AntiforgeryToken />`).
  On every POST render both named forms are rendered (an error shell when the poll closed meanwhile or the response is
  gone), so the framework never answers 400 "no form named …"; such refusals show as errors next to the posted values.
- Identity comes from the request only: `PollCookies.Read` + `PollAccessService`, exactly as on GET. Nothing about the
  identity is taken from the form; the voter ticket goes (`CreateTicket`/`ReadTicket`, its protector and lifetime).
  What the ticket's user-id pin protected (a form rendered for A posted after B signed in in another tab, anonymous →
  signed in, signed in → anonymous) is now held by the antiforgery token's claim binding: such a POST fails antiforgery
  and gets the "Form expired" page, it never writes. Tests cover each of those three transitions, so nobody later swaps
  in an endpoint without antiforgery. Anonymous → same anonymous has no binding and needs none (same `vk`, same voter).
- Post/redirect/get: a successful save or delete redirects to the page; GET states are `?saved=1` (the summary with the
  `role=status` "saved" line), `?r=N&edit=1` (change response N), `?new=1` (add another, `responses: many`). Blazor's
  SSR redirect is a 302; fine for POST → GET. Reload after a save never re-posts.
- Double submit: every rendered vote form carries a single-use form id (hidden field, random). `BallotService` keeps the
  recently used ids in memory (bounded, 12 h): a second POST with an id already used by the same identity is
  answered with that response's summary instead of a new response. JS also disables the submit button on submit.
- Response numbers from the form are only a request: `BallotService` looks the response up by the request's
  `VoterIdentity`; a number that is not this identity's is "not found", same as today.
- Delete: a small form with the response number; the confirm is a second render ("Delete this response?" with
  Delete / Cancel), not a JS dialog. Delete and confirm are exempt from the minimum fill time.
- Field encoding (works without JS; JS only makes it nicer):
  - ranked: `<input type="number" name="o.{key}" min="1" max="N">` per option for the position (empty = not ranked)
    and, when bans are on, a "Ban" checkbox `b.{key}`. Ties are refused ("Give each ranked option its own
    position."), gaps are compacted (2, 5, 9 → 1, 2, 3), a ranked and banned option is refused. `ranking.js` hides the
    inputs behind the existing drag zones and buttons and writes them; it keeps the aria-live announcements and
    focus handling of today's component.
  - points: `<input type="number" name="o.{key}" min="0" max="…">` per option; `points.js` shows the remaining budget.
  - dates: a radio group per option (`yes` / `maybe` when allowed / `no`); `dates.js` lets a calendar or timeline cell
    cycle the value. Switching the calendar / list / timeline layout is client-side with JS; without JS it is a
    submit button (`name="view"`) that re-renders with the posted answers and saves nothing.
  - Keys are the option keys of the definition; unknown keys are ignored, missing keys mean "not ranked" / 0 / no
    answer, and `BallotService` validation stays the authority.
- Bot checks: the vote form renders `<FormGuardFields />`, but `/p/{id}` is not in `FormGuard.IsGuarded` (the middleware
  answers with a static page and would lose the ballot). The stamp goes into `BallotGuard` and `BallotService.CheckBotAsync`
  checks honeypot, too fast (the existing minimum) and too old (12 h, the old ticket lifetime); a refusal re-renders with
  the posted values and a fresh stamp. The captcha stays: the widget's container is inside the form and the server reads
  `cf-turnstile-response` (Turnstile) or `h-captcha-response` (hCaptcha). The captcha needs JS
  (`render=explicit`), so anonymous voters on captcha polls need JS; everyone else can vote without it.
- Enhanced navigation: links to and within `/p/*` carry `data-enhance-nav="false"` and the modules also initialise on
  Blazor's `enhancedload`, so the page always gets its scripts; `captcha.js` initialises from a data attribute.
- Rate limit: a sliding window per client key for all `/p/*` POSTs (`RateLimiting:VotePermitPerMinute`, default 120,
  sized for a class or office behind one address). Refusals of this partition are not abuse signals (the captcha and
  the per-poll limits handle bots; a shared NAT must not get blocked).
- Form limits are endpoint metadata on the vote page (every other request gets the smaller global defaults of
  `RequestLimits`, 2026-11-05; `[RequestSizeLimit(65536)]`, `[RequestFormLimits(ValueCountLimit =
  2 × OptionsMax ceiling + 32, ValueLengthLimit = …)]`), so antiforgery and FormGuard read the form under them too. An
  oversized body is a 413, too many values a 400, both as the static error page; `InvalidDataException` /
  `BadHttpRequestException` from form reading are caught there, never a 500. A test posts more than 64 kB.
- Caching: every `/p/...` response is `Cache-Control: no-store` (ballots and names are now in the HTML).
- The circuit limit (AV-09) stays as it is and now only concerns signed-in pages.

### 21.2 Live results for the owner
Status: **implemented** (2026-10-31): `PollChangeNotifier`, `LiveResultsEndpoint` (`Features/Results`),
`LiveStreamLimiter` (`Security/RateLimiting.cs`), `wwwroot/js/results-live.js`. Deviations and additions, all on the safe
side: the stream sends one `ping` at once on open (flushes the headers, so the browser's `open` fires and the "Live"
badge shows, and carries `retry: 3000`), and every event carries the retry, not only the last; the per-event check
also ends the stream for a blocked address or account (as `AbuseBlockMiddleware` would refuse a new request; admins
exempt, as there), a locked account and a security stamp changed since the stream opened (sign-out everywhere,
password reset); the endpoint is `AllowAnonymous` so an anonymous or gated visitor gets the handler's 404, not a login
or enrolment redirect; account deletion (which deletes the account's polls) ends their streams too; a cap refusal is a
`RateLimited` abuse signal, as for circuits, and the `unknown` client key is capped like any other; the module gives
up whenever `EventSource` does (`readyState` CLOSED: any non-200 answer, so also a proxy's 502; it cannot see the
status), the Refresh button stays. Results links on My polls and the share page carry `data-enhance-nav="false"`; the
vote page's links are §21.1's.
- Only the owner of the poll (`PollAccessService.CanEdit`) gets live updates; everyone else who may see results gets the
  same static page and reloads. Reason: the owner already sees every ballot, so a push that fires as someone votes
  reveals nothing new; for other viewers it would make "who just voted what" easier to read off the change.
- `PollChangeNotifier` (singleton, memory only): `Changed(pollId)` after any committed write that changes what the
  results page shows (ballot save / delete / alias change, poll edit, close / reopen, invite revoke); `End(pollId)` on
  poll delete (single and bulk), ownership transfer and retention deletion, which ends every stream of that poll at
  once. Per subscriber a bounded channel of capacity 1 (`DropWrite`), so bursts coalesce into one event.
- `GET /p/{id}/results/live` (`TypedResults.ServerSentEvents`, .NET 10, no package): `CanEdit` via `PollAccessService` and
  `AccountGate.MayActAsync` on connect (404 for anything else, like an unknown id) and again before every `changed` send
  and every heartbeat; a failed check ends the stream. Sends `changed` (no data) at most every 2 s, `ping` (empty data)
  every 25 s, `changed` once when the poll's `ClosesAt` passes while the stream is open (time-based close makes no
  write), and ends after 10 minutes with `ReconnectionInterval` 3 s. The iterator takes `[EnumeratorCancellation]`
  linked to `RequestAborted` and `ApplicationStopping`, and unsubscribes in `finally`. Headers: `Cache-Control:
  no-store`, `X-Accel-Buffering: no`.
- Caps (a concurrency limiter like `CircuitLimiter`): `RateLimiting:StreamsPerClient` (default 10) per client key,
  `RateLimiting:StreamsPerAccount` (default 10) per account, `RateLimiting:StreamsTotal` (default 1000) in all; a
  refusal is 429.
- `results-live.js` (loaded only on the owner's view): `EventSource` on that URL, closed while the tab is hidden and
  reopened when it shows (that also refreshes once; keeps HTTP/1.1's six connections per origin free). On `changed` it
  fetches the results page (same origin, at most once per 3 s) and uses it only when `response.ok`, not
  `response.redirected`, HTML, and the fetched page has `[data-live-results]`; it then replaces that element's
  children with imported nodes from `DOMParser` (never `innerHTML`). It stops on 404 / 429 from the stream. A small
  "Live" badge shows while the stream is open. No JS = the static page.
- The deploy notes say how to pass the stream through nginx (`X-Accel-Buffering` is honoured; or `proxy_buffering off`
  for `/p/*/results/live`) and that streams count against `limit_conn`.

## 22. Admin settings without mail, option help (decided 2026-11-01)

### 22.1 Auto-verify created accounts while mail is off [implemented]
- New runtime setting `AutoVerifyWithoutMail` (`SettingsService`, default **off**), a switch on the Settings tab
  next to "Open registration", label "Auto-verify created accounts". Changing it is logged like any setting.
- Effect: when a setup link of purpose `AccountSetup` (admin *Create account* / *New setup link* for an account without
  email) is completed while mail is **not configured** (`Settings.Current.Mail.IsConfigured` false) and the setting is
  on, the typed email is stored with `EmailConfirmed = true` and no verification mail is queued. Both conditions are
  read at completion time, not at link issue. In every other case the flow is unchanged (unverified, verification mail
  queued, admin *Mark verified* still available).
- Not affected: self-registration (still needs *Mark verified* with mail off, §8), email invites (already verified by
  the link), email change, existing accounts (no back-fill).
- While mail is configured the switch is shown disabled (with mail on there is a real check, and *Invite* exists).
- Accepted: the address is then only a sign-in name nobody checked. If mail is set up later, such accounts count as
  verified and receive mail at the address as typed (notify-me, admin reset links). The help text says so.

### 22.2 Option help [implemented]
- Every admin control that changes something (settings fields, switches, selects, each limit, captcha, theme fields,
  branding uploads, backup, SMTP fields, test mail, purge, user and poll row actions, per-user limit overrides, blocks
  and blocked ranges) has a description: what it does, default, side effects, privacy / security consequences and
  interactions with other settings. Read-only tabs (Status, Log) get one description per section.
- The description is hidden behind a small help toggle next to the label (`OptionHelp` component: an icon button via
  `Icon`, `aria-expanded`, `aria-controls`, accessible name "About <label>"); open, it shows a panel under the control.
  Plain text / simple markup written in the component, no Markdown, no user content.
- One button in the admin header, beside the tabs, folds or unfolds every description on the current tab:
  "Show help" / "Hide help" (`aria-pressed`). State is a cascading `AdminHelpState` (since §23.4 `HelpState`) from `AdminPage`; the fold-all
  setting sets every toggle, a single toggle afterwards only changes itself. The fold-all choice is remembered per
  browser in `localStorage` (wrapped in try/catch; works without it), applied after the first render.
- Existing explanatory `form-text` lines on admin tabs move into the help; only a line stating a hard precondition
  that disables a control (e.g. "Invites need mail settings.") stays visible.
- A source-scanning test (like `LayoutTests`) checks that each labelled control on the admin tabs has an `OptionHelp`.
- Implementation notes: per-row actions (user and poll rows) and the per-account limit fields share one description each
  ("Account actions", "Per-account limits", "Poll actions") instead of a toggle on every row; the abuse block and blocked
  range forms have one description per form. Labels covered by a shared description carry `data-help="<OptionHelp Id>"`,
  which `AdminHelpTests` accepts. Limit descriptions live in `LimitSettings.Fields` (fourth column). Inside an
  `.option-field` container CSS moves the panel under the control.

## 23. First test round: home page, landing, editor help (decided 2026-11-01)

Owner feedback from the first manual test. One failed module import (an unhandled `JSException`) killed the circuit,
and every control on the page stopped (§23.1). The product changes below stand on their own.

### 23.1 JS failures never kill a circuit [implemented]
- Every JS module import and call from an interactive component (`PollEditor` draft module, `AdminPage` theme and
  help modules, `AdminSettings`, copy buttons) catches `JSException` as well as `JSDisconnectedException`. The feature
  degrades (no browser draft, no copy); the page keeps working.
- Copy buttons run in the browser: a delegated click handler (module `js/clipboard.js`, loaded from `App.razor` like
  `timezone.js`) on `[data-copy]` elements copies the attribute's text and writes "Copied to clipboard." (or "Copy
  failed. Select the text and copy it.") into the element named by `data-copy-status`. No server round trip, so the
  click's user activation is still valid when `navigator.clipboard.writeText` runs (Firefox needs that). Used on the
  share page (poll link, invite links) and the admin Status tab.
- Implementation notes: "admin Status tab" is the `AdminStatus` message component (the one-time setup/reset link on
  the Users tab). The status line clears itself after 6 s if nothing changed it, so a later "Copied" is never stale;
  the attribute holds the text to copy (for "Copy all links", the newline-separated list). A failed `draft.js` load sets
  the editor's module to null, so no further draft calls are tried. `JsResilienceTests` scans `src/Telrang.Web` for
  every JS interop call (`Invoke(Void)Async` with a string identifier, `FocusAsync`, module `DisposeAsync`) and fails
  unless an enclosing `try` has a `catch (JSException)`.

### 23.2 Home page [implemented 2026-11-01]
**Implemented** as specified below, with these notes:
- Definitions and ballots: `ShowcasePolls` in Core builds the three drafts through `PollDraftValidator` (so the examples
  obey the real rules; `ShowcasePollsTests` checks every made-up ballot with `BallotValidator` and the hand-computed
  scores) and the 7 ballots by option index. The dates example lies in the month after today (first Friday, the
  Saturday after it with 14:00–18:00 and 19:30–23:00, the next Friday and Saturday, and the Thursday after), so it is
  always ahead and fits one calendar month. `ResultsService.Ranked` / `.Points` take ballots in hand (the vote
  page's results use the same code); dates use `DatesResults.Compute`. Names: none, so nothing personal appears.
- Fields: `RankedVote`, `PointsVote`, `DatesVote` (with `DatesViews`, `DatesCell`) take `Preview`: the same markup with
  no `name` (Blazor drops a null attribute), no `shown` field, and the dates layout switch as plain `type="button"`
  buttons rendered `hidden` and shown by `dates.js`. Every field carries `data-field="o.{key}"` / `"b.{key}"` in
  both modes, and `ranking.js`, `points.js`, `dates.js` find fields by it, and the tally / budget line inside
  `root.closest('form, [data-ballot]')`. Radios without a name do not group, so `dates.js` unchecks an option's
  other radios itself when one is checked (in the vote form this is what the shared name already did). Without JS
  the dates example's radios are not grouped; nothing is sent, so it is left at that.
- Loading: `js/showcase.js` is loaded on every page from `App.razor` (like `menus.js`) and does nothing without
  `[data-showcase]`; with it, it shows the Ranked / Points / Dates switch (rendered `hidden`, `aria-pressed`
  buttons), hides the other two examples and imports `vote.js`, which already needed no form and loads the three
  modules. Global, because a module in `HeadContent` would not run after Blazor's enhanced navigation to `/` (the
  brand link from static pages such as `/Account/Login` is enhanced); both modules also re-run on `enhancedload`.
- Layout: `.shell-public:has(.showcase)` widens the public shell to 1040 px on the home page only; from 992 px the
  ballot and its results sit side by side, and the results use their phone cards at every width inside the
  showcase (the wide tables do not fit half the page). Frame: 2 px dashed `--av-border-strong`,
  `--av-surface-subtle` background, a muted "Example" badge (`--av-text-muted` on `--av-surface-strong`), part
  titles "Your vote" and "Results · 7 voters" in muted small caps. No highlight added (the results keep theirs).
- `/` keeps `[Authorize(Policy = NotGated)]` (the account gate still sends a gated account to enrolment).
- Browser check (headless Firefox, light and dark): ranking by click, by keyboard and by drag (Sortable's pointer
  fallback, since WebDriver's synthesized native drag never fires `drop`), points line and cap, dates cells cycle and
  keep their answer across the three layouts, no request leaves the page while interacting, no console errors, no
  horizontal scroll at 390 px, and the examples work after enhanced navigation from `/Account/Login`.

- `/` is static SSR (`[ExcludeFromInteractiveRouting]`, like `/p/*`), no circuit for anyone: this also removes
  bottleneck 4 of docs/PERFORMANCE.md. Layout `PublicLayout`. Signed in, the header and the page show "My polls" in
  place of "Log in" / "Register".
- Content, top to bottom: the banner (if uploaded), the app name, the admin's welcome Markdown if set, else the lead
  "Ranked choice, points and date polls, shared by link."; Log in / Register (or My polls); the showcase; the name line.
- **Showcase.** Three examples, one per poll type, under a segmented switch (Ranked / Points / Dates; without JS all
  three stacked):
  - Ranked: "Where do we eat on Friday?" with 5 options, Borda, 1 ban, rank all off.
  - Points: "Split 100 points over next year's club trips" with 4 options, cap 60.
  - Dates: "When can you come to the board games night?" with 5 dates, one of them with two time slots, maybe allowed.
  - Each renders the real voting fields (`RankedVote`, `PointsVote`, `DatesViews`) with a definition built in code,
    never stored, enhanced by the same `ranking.js` / `points.js` / `dates.js` (the module that loads them,
    `vote.js` or a small `showcase.js`, must not need a `form`). There is **no `<form>`**, no submit button, no name,
    website, antiforgery or form-stamp field, and no field has a `name`: nothing can be posted. A layout switch that is
    a submit button in the vote form becomes a plain button (JS) or is left out.
  - Next to each example, its results view (`RankedResultsView` etc.) for 7 made-up ballots, scored by Core at render
    time.
  - It must read as a preview: a dashed frame with an "Example" badge, muted colours from the theme tokens, and as the
    poll description (Markdown, rendered like a real one) "The description supports **Markdown**. This example is
    *interactive*; nothing is sent or saved." (`ShowcasePolls.Description`; revised 2026-11-04, was a caption). Fields stay usable so a visitor can try dragging and spending points.
- ~~**Name line**~~ removed 2026-11-04: the name is explained in the README, not on an instance's home page.
- Tests: `/` renders no interactive component marker (it still loads `blazor.web.js` for enhanced navigation, like
  `/p/*`), has no `<form` inside the showcase and no `name=` on showcase fields, and renders for anonymous and
  signed-in visitors.

### 23.3 Landing after sign-in [implemented 2026-11-01]
**Implemented**: `IdentityRedirectManager.RedirectToSignedIn(returnUrl)` sends a local return URL on as before and
anything else (none, empty, off-site) to `polls` (`SignedInLanding`); `Login` (password and passkey share its
`LoginUser`), `LoginWith2fa` and `LoginWithRecoveryCode` use it. Setup link and setup key sign in and go to TOTP
enrolment as before; enrolment's Continue (`EnableAuthenticator`) now goes to My polls instead of `/`. Email
confirmation never signs anyone in (pentest R2-ACC-1): signed out, its "Log in" link carries no return URL, so the
sign-in lands on My polls; signed in as that account, it shows a My polls button. The brand already linked to `/`
(`href=""` under `<base href="/">`) in all three shells. `LandingTests` cover password (no, local, `/` and off-site
return URLs), 2FA code, recovery code, the setup link's Continue and the brand per shell; the passkey path is the
same `LoginUser` branch and has no separate test (no WebAuthn in the test host).

- Every sign-in path without a (local) return URL lands on `/polls`: password, 2FA code, recovery code, passkey,
  completing a setup link, confirming an email after registration. A given local return URL still wins.
- `/` stays reachable for signed-in users; the brand (logo and name) in every `MainLayout` shell links to `/`. Nothing
  else redirects `/`.

### 23.4 Editor help [implemented]
- The poll editor gets `OptionHelp` toggles (§22.2), same component and behaviour, with a "Show help" / "Hide help"
  button at the top of the form; the state mechanism (`AdminHelpState`) is shared and renamed `HelpState`. Its
  localStorage key is per area (admin, editor).
- Covered: Type (all three types), Scoring (Borda and Dowdall: formula, a 4-option example, when to pick which),
  Bans per voter (what a ban does to the score, 0 = off), Rank all, Budget, Cap per option, Spend all, Allow maybe,
  Who votes (each mode, what a voter needs), Responses (one vs several, per mode: several with invite links means one
  link may send several responses), Names (none / optional / required, who sees them: names are shown in the owner's
  results and the CSV, and to voters only where results show names), Results (owner only / after voting / anyone,
  with who "anyone" is per voter mode: everyone who can open the poll page, so with invite links only invite holders,
  with accounts also visitors who are not signed in), Closes on, Time zone, the options Text format.
- Title and Description get none. The help text is written from the code (Core scoring and validator), not from
  memory; numbers in examples are checked against the scoring code.
- The share page (invite links, aliases) gets help on the same rules where a control is not obvious.
- Implementation notes: `HelpState(area)` (`HelpState.Admin`, `HelpState.Editor`) and one `HelpAllButton` component
  (button, remembered choice, save on toggle) serve both areas; `js/admin-help.js` became `js/help.js` with
  `load(area)` / `save(area, open)` and keys `av:<area>-help`, so the admin key `av:admin-help` is unchanged. The
  editor's button sits at the top right of the form. Radio groups keep a visually hidden `legend` and show the same text
  (aria-hidden) beside the toggle, as the admin Palette does; the panel shows under the radios. On edit the fixed type
  line keeps the Type help (and the Scoring help for ranked polls). The options Text format `<details>` became an
  `OptionHelp` "Text format" (still the textarea's description).
  - Names: no results view shows names to voters (`ResultsService` builds the dates grid for the owner only), so the help
    says only the owner sees them: responses on the share page, the ballots CSV and the dates "who can when" grid.
  - "Anyone" for results: the poll page opens for whoever has its address (`CanView` is always true), so with invite
    links the help says invite holders and anyone they pass the address to.
  - The Scoring example (four options, two ballots A, B, C, D and one B, C, D, A: Borda B 10, A 9, C 7, D 4; Dowdall
    A 2.25, B 2, C 1.17, D 0.83, different winners) is pinned by `ScoringTests.Editor_help_scoring_example`.
  - Share page: help on "How many" and "Aliases". `EditorHelpTests` scans the editor (every `for="@Id(...)"` label but
    Title and Description, every radio group, the §23.4 list) and renders the new, edit and share pages.

### 23.5 Default time zone [implemented]
- A new poll's time zone, when the account has no time zone set, is the browser's (`Intl...timeZone`), set after
  the editor's first render if it is a known IANA id and the field still holds the server default. The account
  setting, when set, still wins; on edit the stored zone stays.
- Implementation notes: `NewPoll` passes `BrowserTimeZone` only for a blank poll (not a copy: a copy keeps the source's
  zone) whose account has no zone (`PollService.AccountTimeZoneAsync` null). After the first render the editor asks
  `js/timezone.js` (`browserZone()`, guarded per §23.1) and applies it through `PollEditor.BrowserZoneToApply` (known IANA
  id, field still equal to the server default). It is applied before the browser draft is attached and to the
  baseline too, so it is no unsaved change, and a restored draft then brings its own zone. `BrowserTimeZoneTests`.

### 23.6 Voter mode × responses
All six combinations are valid and stay allowed; nothing is locked out. The meaning goes into the help (23.4):
Anyone + Several is an honour system per browser or account; Accounts + Several is several per account; Invite +
Several is several per link (one link per household or team).

## 24. Second test round: shell, privacy in the header, owner landing, themes (decided 2026-11-01)

### 24.1 Home page keeps the app navigation [implemented]
**Implemented**: `Home` declares `MainLayout`, which already chose `PublicLayout` for a visitor who is not signed in and
the admin's shell otherwise; the shells needed no change to render statically (the sidebar's phone drawer is a CSS
checkbox, the menus are `<details>`). The compact shell widens to 1040 px on the home page like the public one, so the
examples still sit beside their results. `LayoutTests` (every page declares its layout: `/` is `MainLayout`; each shell
on `/` signed in; `PublicLayout` on `/` signed out) and `HomeTests` (no circuit in the sidebar shell).

- Signed in, `/` renders inside the signed-in shell (the admin's `Theme.Layout`: My polls, My votes, Admin…), still
  statically (no circuit, §23.2). Signed out it keeps `PublicLayout`. The shells must therefore render without a
  circuit; where a shell part needs interactivity it gets a static fallback (links, `<details>` menus as in `UserMenu`).
  `LayoutTests` learns the "public signed out, shell signed in" case for `/`.

### 24.2 No email in the header [implemented]
**Implemented**: `HeaderIdentity` (Components/Layout) gives the header text from two claims that
`AppClaimsPrincipalFactory` adds next to the gate claim: `av:display-name` (the trimmed display name, when set) and
`av:hide-identity` (when `HideIdentity` is on). No database read per render. The Profile page refreshes the sign-in
after a save, so the change shows on the next page; other sessions get it when the security stamp validator re-creates
their principal (every minute, `ValidationInterval`), a cookie from before the upgrade falls back to the part before
@. The claims are display only, never an access decision. Hidden: a generic user-icon avatar (`user-avatar-generic`),
no name next to it, "Account" as the menu header (and as the sidebar block's label). The sidebar block shows the name
next to the avatar. Migration `HideIdentity` (bool, default false). The Profile page is static, so `OptionHelp` got a
static mode: rendered with no render mode it carries `data-static-help` and `js/menus.js` toggles the panel.
`HeaderIdentityTests`.

- The header (all shells and `PublicLayout`) never shows the email address. It shows the account's **display name**
  (Account → Profile), else the part of the email before `@`. The avatar initial follows the shown text.
- New account setting **"Hide name in the header"** (`ApplicationUser.HideIdentity`, default off, on the Profile page
  with an `OptionHelp`): the header shows only a generic avatar (user icon), no name and no initial; the menu header is
  then "Account". The email stays visible only on the Account pages.
- The `title` tooltips that carried the email go too.

### 24.3 Owners land on results; close and reopen there [implemented]
**Implemented**: My polls' title links to `p/{id}/results`, the Results button became "Vote" (`p/{id}`). The results
page's owner bar ("Your poll", Edit · Share · Copy · Open poll, then the button) replaces the "Back to the poll · Share"
line for the owner; others keep "Back to the poll". Named forms `close` / `reopen` (`ResultsPage.CloseForm` /
`ReopenForm`) post to `p/{id}/results` and call `PollService.ChangeClosedAsync` (which checks `CanEdit` and the
open-polls limit and calls `PollChangeNotifier.Changed`); success redirects to the page, a refusal (the limit) shows
its message. On a POST render the form that is not shown is rendered empty and hidden, as on the vote page. A POST from
anyone but the owner (signed in or not, with a valid token) is a 404 and changes nothing; a missing, forged or foreign
token is antiforgery's 400. The bar sits inside the live-results element, so the live refresh keeps its state current.
`OwnerResultsTests`.

- In My polls the poll title links to the results page (`/p/{id}/results`); a "Vote" link opens the ballot. On the
  results page the owner bar gets Close poll now / Reopen poll, next to Edit · Share · Copy · Open poll.
- The results page is static (§21), so the actions are a named form (`close` / `reopen`) posted back to the page with
  antiforgery, checked through `PollAccessService.CanEdit` on the request, calling the same service method as the share
  page (which keeps its buttons), then post/redirect/get. `PollChangeNotifier.Changed` after the commit as today.
  A poll whose automatic close has passed shows no Reopen unless the share page offers it today (same rule there).

### 24.4 Themes: light / dark per visitor, one palette [implemented]
**Implemented**: `ThemeSwitch` (`Components/Layout`) is one line in each layout: a `<details data-menu>` toggle (sun,
moon or monitor icon) in TopBar, Compact and `PublicLayout` (which also covers `/Account/*`; admin pages use the
`MainLayout` shells), a row of three buttons above the account block in the Sidebar. The choices are buttons with
`aria-pressed`; the toggle is named "Theme: Light" etc. The markup is stateless so it works on static and interactive
pages alike: `<html>` carries `data-bs-theme` (the cookie's mode, else the admin's default, none = follow the OS),
`data-av-theme` (the visitor's choice; CSS picks the toggle's icon) and `data-av-default` (the admin's default mode),
rendered by App.razor and `StaticPages` from the `av-theme` cookie (`ThemeService.VisitorMode`, `BootstrapThemeFor`),
so a reload shows the chosen mode at once. `js/theme-switch.js` (global module) writes the cookie
(`SameSite=Lax; Path=/; Max-Age` one year, `Secure` on https) and localStorage (restores a lost cookie), sets the
attributes and re-syncs after enhanced navigation and when a switch menu opens. CSS hides the switch until the module
has run (`data-av-switch` on `<html>`), so without JS there is no dead control. `js/theme.js` (admin stylesheet swap)
now passes the admin's default and lets the visitor's choice win; the `?admin=1` variant needs nothing else, both
sheets carry both modes. Detail decided while implementing: when the admin's default is Light or Dark, the switch's
first choice reads "Default" instead of "System", because no cookie then means that mode, not the device setting.
Settings: `Theme.LightBase`, `Theme.LightHighlight`, `Theme.DarkBase`, `Theme.DarkHighlight`; the admin form shows four
colour fields (built-in value with "built-in", or the override with *Reset*) under one *Colours* help, and "Preset" is
labelled **Default mode**. The derivation reaches AA for any pair by lightness steps, so overrides are neither refused
nor warned about (as Custom before); `PaletteTests` checks AA for the built-in pair, the two former designed palettes
and extremes (white, black, pure red, mid grey, a pale base) in both modes. Upgrade: `ThemeSettings.From` reads the old
keys (`Theme.Palette`, `Theme.Primary`, `Theme.Accent`, `Theme.Radius`, now `SettingKeys.Legacy*`) as described below;
a save with a value for them is refused, and every save of the Theme section and Reset remove them. Corner radius is
`ThemeSettings.CornerRadius` (6), still emitted as `--av-radius`.
- **Visitor switch.** Every layout's header has a theme switch (System / Light / Dark, an icon button cycling or a
  small menu). The choice is stored per browser: `localStorage` for the script and a preference cookie `av-theme`
  (`light` | `dark`, no cookie for System; not essential, not a tracker) so the server renders `data-bs-theme` and the
  page does not flash. A signed-in account does not store it (per device is what people expect).
- **Admin Theme section.** The palette choice (Teal / Coral, Indigo / Amber, Plum / Gold, Custom) goes. Teal / Coral is
  the built-in palette. The admin sets the **default mode** (System / Light / Dark, the existing preset) and may
  **override base and highlight colours per mode** (light base, light highlight, dark base, dark highlight; empty = the
  built-in colour). Each mode is derived by `PaletteDerivation` from its own pair, with the same contrast checks.
- **Corner radius goes** (fixed at the current default 6 px): it changed little and doubled the visual test matrix.
  Font, font size, background image and overlay, layout and custom CSS stay.
- **Upgrade of stored settings:** a stored non-custom palette other than Teal / Coral is dropped (back to the built-in
  colours); stored custom colours become the override for both modes; the radius setting is ignored and removed on the
  next save. The admin log records nothing for this (no admin action).
- The `?admin=1` stylesheet variant follows the visitor's mode too and still never includes custom CSS.

### 24.5 Footer credit [implemented]
**Implemented** in `SiteFooter` (used by every shell and `PublicLayout`): "Telrang by LayerTrace Labs · v…", the
credit linking to `https://layertrace.eu` with `rel="noopener"`; the About link stays on the left. `LayoutTests`.

- The footer of every layout shows "Telrang by LayerTrace Labs" with a link to `https://layertrace.eu`
  (`rel="noopener"`), next to the version. Not configurable by the admin: it names the software.

### 24.6 Navigation errors in the server log [implemented]
**Cause**: with global interactive routing, Blazor's client-side router intercepts every in-app link click on an
interactive page; `data-enhance-nav="false"` is only read by enhanced navigation, which stands aside while a circuit
runs. For a target page marked `[ExcludeFromInteractiveRouting]` (all `/p/*`, `/`, account, setup, notify) the circuit's
router then asks the browser for a full load through a JS call (`navigateTo` with `forceLoad`). The page unloads, the
circuit closes, and the pending call ends in `TaskCanceledException`: `RemoteNavigationManager[4]` and
`CircuitHost[111]`. A race, so it showed only some of the time; confirmed in Firefox by watching `history.pushState`
(Blazor's path) on My polls → title / Results, share page → Results and brand → `/`. The admin's layout change used
`NavigateTo(Nav.Uri, forceLoad: true)` from the circuit, the same race ("WebSocket is not in the OPEN state" in the
console).
**Fix**: `StaticRoutes.Pattern` (route templates of every `[ExcludeFromInteractiveRouting]` page as one anchored,
case-insensitive regex) goes into `<meta name="av-static-routes">` on interactive pages only; `js/links.js` (loaded on
every page) listens for clicks on `window` in the capture phase and, for a plain left click on a same-origin link whose
path matches, stops propagation, so Blazor's document listener never sees it and the browser loads the page itself
(modifier clicks, `target`, `download` and same-page hash links are left alone). Links between interactive pages still
navigate in the circuit. The layout change calls `PageReload.SoonAsync` (`js/reload.js`: returns at once, reloads on the
next task). Not covered: `RedirectToLogin` / `RedirectToEnrolment` still force a load from the circuit when an
interactive page's authorization fails mid-session (rare; left as they are).
Browser check: no `pushState` and no server `fail` line on those links, interactive → interactive still in the
circuit, three layout changes with no console warning. `StaticRoutesTests`.

- Going from an interactive page to a static `/p/*` page logged `RemoteNavigationManager[4] Navigation failed` and
  `CircuitHost[111] Unhandled exception` (`TaskCanceledException`): the browser unloads the page before the circuit's
  JS call returns. Harmless for the user but it fills the log and hides real errors. Fix at the source (find which
  navigation goes through the circuit and make it a plain full page load before Blazor sees it), not by filtering the log.

### 24.7 Browser smoke tests in the repo [implemented]
**Implemented**: `tests/e2e/run.mjs` (harness in `tests/e2e/lib`, flows in `tests/e2e/flows`, run order in
`flows/index.mjs`), run by `scripts/e2e.sh` and the CI job `e2e`. Notes:
- What runs: `scripts/e2e.sh` publishes the app (Release, framework-dependent, like a release) and the harness starts
  `dotnet Telrang.Web.dll` in the **Production** environment on `http://localhost:<free port>` with `App__DataDir` in a
  new temp directory (static files come from the publish's endpoint manifest, so no Development environment is
  needed). `localhost`, not `127.0.0.1`: it is a secure context, so the Production auth cookie (`Secure`) works over
  plain HTTP, and WebAuthn accepts it as relying party id. Headless Firefox runs with a temp profile, BiDi on a port it
  picks itself, `TZ=Europe/Amsterdam`, light colour scheme. One app and one browser per run; the flows share them and
  run in order (setup and editor are `required`: if one stops on an error the rest is skipped).
- Flows: `setup` (setup key from the log, `/setup`, TOTP enrolment, landing on My polls), `home-showcase` (ranked by
  click and pointer drag, ban by drag, points steps / typing / cap, dates cell cycle across the three layouts, no request
  while interacting), `editor` (browser time zone default, Show / Hide help and one toggle, List ↔ Text both ways with
  details, a ranked, a points and a dates poll from calendar and text, then `draft.js` made to fail: the circuit lives
  on and the controls work), `dates-editor` (§27: mouse drag select and deselect, a touch swipe that selects nothing and a
  touch hold-then-drag that selects, Space and Shift+arrows, a weekday heading, paging, the clock with a refused end,
  month / year / decade / century and a typed year 1, a drag past the options limit refused; with `E2E_SHOTS=<dir>` it
  writes screenshots of the calendar, clock and month picker at 1280 and 390 px in light and dark), `share-copy` (`writeText` within the click, status text, clipboard content, again after
  in-app navigation), `vote-anonymous` (a separate user context: drag ranking, points, a dates vote, the results the
  voter may see), `owner-results` (My polls title / share page Results / brand are full loads, interactive →
  interactive stays in the circuit, close and reopen), `edit-after-vote` (§26: fixed settings read-only, the change
  preview, save without a confirmation, the change log on the vote and results pages), `theme-switch` (cookie and storage, server-rendered mode, reload,
  static and interactive pages, System clears it), `signin-header` (password + TOTP in a fresh context, no email in the
  header on six pages), `admin-settings` (display name and welcome text reach `/`, each layout change reloads into its
  shell, no email in any shell).
- Fails a flow on: a server log line starting `fail:` or `crit:` (the console logger runs single-line), a browser
  console error or uncaught exception / unhandled rejection (`log.entryAdded` at level error), a CSP violation (a
  preload script reports `securitypolicyviolation` over a BiDi channel), a Blazor reconnect state other than `hide`
  while the page is not unloading, or the reconnect dialog / `#blazor-error-ui` showing at the end of the flow. The
  global allow-list (`ALLOWED` in `lib/harness.mjs`) is empty; a flow that injects a fault on purpose names what it
  expects with `tolerate(pattern, reason)`, only from that point and only in that flow (the `draft.js` check).
- Drags: WebDriver's synthesized native drag never fires `drop` in Firefox, so the harness switches Sortable to its
  pointer-event fallback before a drag (same `onEnd` code). Interactive pages: the preload script counts the circuit's
  WebSocket messages; a flow waits until the circuit is open and quiet before it clicks.
- Output: a line per check, a summary with times, exit code 0 / 1; the artifacts directory (`E2E_ARTIFACTS`, else a
  temp directory) gets `app.log`, `results.json` and a screenshot of every open tab of a failed flow. CI pins the
  Firefox version (Mozilla's tarball) and uploads the artifacts on every run.
- Passkey console error: `SecurityError: The operation is insecure` from `PasskeySubmit.razor.js` appears only when the
  site is opened by IP address (`127.0.0.1`): Identity uses the request host as WebAuthn relying party id and WebAuthn
  refuses IP addresses, so the login page's conditional (autofill) passkey request fails and the template logs it.
  Not a bug in the app (passkeys cannot work on an IP host); the suite uses `localhost`, where there is no error.
- Not covered: mail, invite links and account voters, the live results stream, phone viewports, other browsers.

- Why these errors were only seen by hand: the web tests use `WebApplicationFactory` and HTTP requests, without a
  browser, so nothing runs a circuit, a JS module or a page unload. The ad-hoc Firefox scripts were never kept.
- New `tests/e2e/` (Node, no npm packages: Firefox over WebDriver BiDi with Node's built-in WebSocket): starts the app on
  a scratch data dir, sets up the admin, and walks the main flows (home showcase, create each poll type, share and copy,
  vote anonymously with drag, results, close and reopen, theme switch, admin settings save). It **fails on any server
  log line at `fail`/`crit` level, any browser console error or CSP violation**. Run by `scripts/e2e.sh` and in CI
  (Firefox and geckodriver-free: BiDi is built into Firefox). Node is a dev-only tool, noted here as a dependency.

## 25. Results settings: who, when, ballots (decided 2026-11-01) [implemented]
**Implemented**: `ResultsTiming` and `ShowBallots` on `PollDraft` / `PollDefinition`, the three refusals in
`PollDraftValidator`, `ResultsRules.CanSeeResults` / `CanSeeBallots` (`PollAccessService`, from the definition, no longer
the `Poll.ResultsVisibility` column), migration `ResultsSettings`. Notes:
- Stored invite and account polls with `Open`: the migration rewrites `results` in `DefinitionJson` (SQLite `json_set`) and
  the denormalised column; `PollJson.DeserializeDefinition` maps the same on read, so a row the migration could not parse
  still never shows results to its address alone.
- (Superseded by the §33 ballots view.) Ballot rows for voters: ranked and points get a "Ballots" table under the results (`BallotRowsView`: name, then one
  column per option; ranked the position among the voter's ranked options or "Ban", points the points); dates reuse the
  "Who can when" grid with the name as entered. Rows are in submission order (no time shown). The owner's results page
  does not show these rows: the owner's views (dates grid with aliases, ballots CSV, share page) are unchanged.
- Editor: a "Results" section (Results for · Show results · Show ballots to voters, each with `OptionHelp`). Choices that
  would leave an error a disabled control cannot fix are adjusted instead (`PollEditor.Normalise`): an invite or account
  poll moves results for anyone to voters, owner only sets while voting, names none clears show ballots.
- Wording (`PollText.ResultsLine`): "Results: owner only." / "Results: voters, after closing, with ballots." on the vote
  page (under the rules line) and the share page; the results page explains what is missing (`PollText.ResultsHidden`:
  "Results open to voters when the poll closes.", "Results open when the poll closes.", …).
- "Email me when this poll closes" is still offered on polls whose results are not owner-only; the sweep now marks every
  due poll and mails only recipients `CanSeeResults` lets in (owner-only polls: marked, no mail).


The single Results setting (Owner only / After voting / Anyone) mixed "who" and "when". It becomes three settings,
chosen at creation and fixed afterwards (§26).

- **Results for**: Owner only · Voters · Anyone with the link.
  - Voters: a visitor whose `VoterIdentity` has a ballot in the poll (the current `after-vote` check).
  - Anyone with the link: everyone who can view the poll. Offered only for `voters: public`; invite and account polls
    offer Owner only and Voters (their poll address alone must never reveal results).
  - The owner always sees everything, live, whatever the settings.
- **Show results**: While voting · After closing. While voting: a voter sees results once they voted (Anyone: right
  away). After closing: nobody but the owner until `PollState.IsClosed`. Disabled (and stored as While voting) with
  Owner only.
- **Show ballots to voters**: checkbox, off by default, offered only when the poll asks for names (`names` optional or
  required), all poll types. Same audience and timing as the results. Shows one row per response: the name as
  entered (blank → "Anonymous") and the answers (ranked order and bans, points per option, dates yes/maybe/no). Never
  the alias, the identity, the time or anything else. The owner's views are unchanged.
- Default for a new poll: Voters · While voting · no ballots.
- Model: `ResultsVisibility { Creator, AfterVote, Open }` keeps its stored values (`AfterVote` reads as Voters), plus
  `ResultsTiming { WhileOpen, AfterClose }` and `bool ShowBallots` on the definition (missing in stored JSON =
  `WhileOpen` / false). `PollDraftValidator` refuses `Open` for invite and account polls, `ShowBallots` with
  `names: none`, `AfterClose` with `Creator`. `PollAccessService.CanSeeResults` adds the timing; a new
  `CanSeeBallots` covers the ballot rows. Existing invite and account polls stored as `Open` read as `AfterVote`
  (on read and by a one-time data migration).
- Results-ready mail (§14.3a) uses `CanSeeResults` as now; the "hidden results stay unmarked" special case goes,
  because the settings no longer change after creation.
- Editor: one "Results" group with the three controls, each with `OptionHelp`. Share page and vote page show the
  settings in words ("Results: voters, after closing").

## 26. Fixed settings, option changes, change log (decided 2026-11-01) [implemented]
**Implemented**: the edit rules in `PollDraftValidator` (`FixedMessage`), `OptionDraft.Key`, `PollQuestion.RetiredKeys`
(since §31 the key counter `PollQuestion.KeysIssued`),
`OptionChanges` and `BallotFilter` in Core; `PollService.UpdatePollAsync` (no acknowledgement, no ballot or invite
changes; `PollChanges` rows in the edit's transaction), `PollJson.ReadRanked/ReadPoints/ReadDates` for every read, the
`ChangeLog` component, the editor's fixed view and change preview; migration `PollChangeLog`. Removed:
`NeedsAcknowledgement`, `ResetsBallots`, `BallotsInvalidatedBy`, `OptionKeysChanged`, `PollJson.ValidatePayload`, the
edit page's confirmation. Notes:
- Keys on edit: a row keeps the stored key it carries (each key once); a row without one takes the key of a stored option
  with exactly its label (case-sensitive, for dates the normalised date label), when no other row kept it; every other
  row gets a new slug key unique against every key the question ever had (stored and retired; since §31 the next
  numbered key `_1`, `_2`, … from `KeysIssued`). A key the poll does not
  have (a forged or stale browser draft) is ignored; on create keys are ignored, and a copy (`?from=`) clears them.
- Text mode: the lines are matched by exact label to the options the list had when Text mode opened (their keys carry
  over), then the validator matches the rest against the stored options. So a label renamed in the list keeps its key
  through Text and back, while renaming a line in the text is a removal plus an addition (by design: the text has no
  identity per line). The dates calendar re-creates slots (adding a time to an all-day date replaces it); a slot that
  comes back with the same date and times keeps its key through the label match.
- A rename and new details of one option are two entries. Order within one edit: removals, then the options in their
  new order. `BallotCount` of a removal counts the ballots that answered the option: ranked or banned, points above 0,
  yes or maybe (same in the editor preview, from `PollEditModel.AnswersPerKey`). Text: "“Pizza” removed (on 7 ballots)"
  / "(on 1 ballot)" / "(on no ballots)", "“Salad” renamed to “Green salad”", "Details of “Soup” changed", "“Curry” added".
- On edit the editor replaces the Type, Voting and Results sections and the time zone field with a read-only "Settings"
  list (lock icon, "Fixed when the poll was created.", each setting with its help); a restored browser draft gets the
  stored fixed values (`PollEditor.PinFixed`). The bans check on edit names the options ("Keep at least 3 options: voters
  may ban 2.").
- The change log sits under the description on the vote page and above the results on the results page (also on the
  403 page of hidden results: everyone who can view the poll sees it).


A poll states its rules at creation; voters must be able to trust them. After creation only these change:

| Can change | Fixed at creation |
|---|---|
| Title, description | Type, scoring, time zone |
| Options: add, remove, change label or details, reorder | Who votes, responses, names |
| Close date (within the max duration, as now), close / reopen | Results for, show results, show ballots |
| | Question rules: bans, rank all, budget, max per option, spend all, allow maybe |

- The editor shows the fixed settings as read-only text with a lock icon and one line: "Fixed when the poll was
  created." A different setup is a copy (`/polls/new?from=`), which already exists. `PollDraftValidator` refuses
  any change to a fixed field on edit ("… cannot be changed after creation.").
- The "delete all ballots" confirmation on edit goes, and with it the invite-unlinking on edit: no edit can make a
  stored ballot invalid any more.
- **Option keys are stable.** `OptionDraft` carries the existing option's `Key` (null for a new option); the
  validator keeps it, so a label or date change keeps the option and its votes. New keys never reuse a key the poll
  ever had: the question stores `RetiredKeys` (keys of removed options), so re-adding a removed label starts from
  zero instead of reviving old votes.
- **Ballots stay raw.** A removed option's key stays in stored ballots and is ignored on read: scoring, results,
  CSV and the voter's own ballot skip keys not in the definition (ranked: the options below it move up; points: the
  points on it no longer count, also under spend all). A new option reads as unranked / 0 points / no on older
  ballots, also with rank all; a voter who edits their ballot sees it.
- **Change log.** Once a poll has a ballot, every option change is recorded in a new table `PollChanges` (PollId,
  At, Kind `Added | Removed | Renamed | DetailsChanged`, Label, OldLabel, BallotCount for removals; cascade delete
  with the poll, kept on transfer, not copied). Renames that only differ in case, spacing or punctuation (letters
  and digits equal) are not recorded. Title, description, close date, reorder and close/reopen are not recorded.
  (Changed by §31: only case and whitespace are exempt, title and description changes are recorded, the log is capped
  and paged, counts show only to viewers who can see the results.)
  The vote page and the results page show it to everyone who can view the poll, newest last, under the heading
  "Changed after voting started" (times through `PollTime.Format`). It cannot be edited or hidden. Before saving,
  the editor lists the changes that will be recorded ("Voters will see: “Pizza” removed (on 7 ballots)").
- Applies to existing polls from now on; their current settings become their fixed settings.

## 27. Date editor: drag selection, paged days, pickers, full year range (decided 2026-11-01) [implemented]
**Implemented**: `PollRules.MaxDateOptions` / `PollRules.OptionsLimit` (validator and Text parser), `DateEditing` (ranges,
weekday headings, "Apply to all", refusals, paging) in Core; in Web `js/date-select.js` (drag, touch hold, Shift+arrows;
calls `PollEditor.SelectDays`), `CalendarNav` (month / year / decade / century and a typed year), `ClockPicker`, the
paged per-day list, the `clock` icon; the validator's date limit applies to `closesOn` only. Tests: Core
(`DateEditingTests`, parser and validator edges, the cap), Web (`DateEditorTests`: 0001-01-01 00:00 and 9999-12-31 23:59
through create, vote, results pages and both CSVs in Pacific/Kiritimati, Pacific/Pago_Pago and UTC with an owner viewing
in the other far zone; the rendered calendar, count and pager), e2e flow `dates-editor`. Notes:
- Option dates are never converted to instants anywhere (labels, sorting, `DatesViews`, results, CSV work on `DateOnly` /
  `TimeOnly`), so the edges needed no code change outside the validator; the tests pin it.
- A click (press and release on one day) stays Blazor's `@onclick`; the module sends a range only when the drag ended on
  another day, and then swallows the pointer click that follows (keyboard clicks always count). A touch hold released on
  the day it started does nothing. Only days of the month shown are accepted from the browser.
- Keyboard ranges extend the state of the day where Shift went down (Space first to select it, then Shift+arrows), shown
  as a preview and applied when Shift is released or focus leaves the calendar. Arrows stay within the month shown.
- Day buttons are never disabled (the keyboard must reach every day); a refused selection, weekday heading or drag shows
  "Select at most 5 more days: at most 100 options." under the calendar, "Apply to all" past the limit says how many days
  fit for that many times; adding a time past it says to remove dates or times first.
- Selecting days jumps the per-day list to the page of the first new day; pages holding a day with an error are marked
  in the pager ("Page 3, has errors").
- The clock adds the slot directly when finished (no extra "Add time"); the typed field keeps its text. Hours: 1–12 on
  the outer ring, 13–23 and 00 inside; "Skip end" keeps a start time alone; Escape closes. Ring positions are CSS classes
  using `sin()` / `cos()` of a custom property (no inline style).
- The typed year sits under the zoomed-out grids ("Year", 1–9999, its own help); Escape returns to the days.
- `LimitSettings.OptionsCeiling` rose from 100 to 128 (decided 2026-11-01), so an admin can raise the options limit to
  128 and date polls reach the cap; the default stays 100 (the e2e refusal runs at 100). The circuit message bound
  (AV-09) grows with it, from about 250 kB to about 316 kB; `VoteFormLimits.MaxValues` from 232 to 288.

The dates calendar in the editor (§20.2) gets hard to use with many dates: one click per day, and a long list of
per-day settings under it.

- **Drag selection.** Press on a day and drag to another: every day in between (reading order, across weeks) takes
  the state the first day switches to (start on an unselected day: select; on a selected day: deselect). Mouse: the
  drag starts at once. Touch: a tap toggles, a press of 200 ms then drag selects a range (as the ranked drag), so
  the page still scrolls past the calendar. Keyboard: arrows move, Space toggles, Shift+arrows extend the range.
  The drag is drawn by a JS module (`wwwroot/js/`, pointer events); only the finished range goes to the circuit.
  Without JS, clicks toggle single days as now.
- **Weekday headers.** Clicking a weekday header ("Tue") toggles every such day in the visible month (select all
  unless all are selected, then deselect all).
- **Paged day settings.** The per-day list (time slots, details) shows 10 days per page in date order, with page
  buttons and a count above ("12 days · 31 options"). "Apply to all selected days" stays.
- **Time picker.** Time inputs stay typeable; a clock button opens a clock face: hour first (24 hours on two rings),
  then minutes in 5-minute steps. For a slot range it asks the start, then the end; an end before the start is
  refused. Own component, no package; keyboard-usable, text input remains the accessible path.
- **Month / year picker.** Clicking the month title shows the 12 months of that year, clicking the year there shows
  the decade, then the century; picking zooms back in. Previous / next buttons step at each level.
- **Year range.** Option dates may be anything from 0001-01-01 to 9999-12-31 (the 100-year limit, AV-14, stays for
  close dates only, which the max duration bounds anyway). Option dates are wall-clock days and times, never
  converted to instants; any code that does convert them (formatting in a viewer zone, sorting, timeline, CSV) must
  not overflow at the edges, tested at 0001-01-01 00:00 and 9999-12-31 23:59. Dates are proleptic Gregorian;
  the help fold-out says so for dates before 1582. Past dates stay allowed. The year input and the parser accept
  4-digit years (0001–9999).
- **At most 128 dates.** Date polls use the general options limit, capped at 128 options (a day with three slots
  counts 3): `PollRules.MaxDateOptions = 128`, effective limit min(`OptionsMax`, 128). The admin's options limit
  help says so. The editor shows the count live ("97 / 128") and refuses a selection, drag or "Apply to all" that
  would go over, saying how many fit; Text mode reports it as today.

## 28. PostgreSQL (decided 2026-11-02) [implemented]

**Implemented** (2026-11-02): `DatabaseSetup` (provider, `_FILE`, exit codes 2/3), `PostgresDbContext` with its
migration set in `Data/PostgresMigrations` (baseline at today's model), `DbWrites` (writer lock, poll row lock),
`DatabaseErrors` (log redaction), `PostgresNulInterceptor`, `copy-to-postgres` (`CopyToPostgresCommand`), Status and
Backup tabs, the Web suite on both providers (`TELRANG_TEST_POSTGRES`, CI job `web-tests-postgres`), `docs/deploy/`
"PostgreSQL". Decisions in §13 (2026-11-02, §28 implemented).

SQLite stays the default (zero configuration). PostgreSQL is an alternative for instances with many concurrent votes,
or whose operator wants the data in a database other tools can query.

- **Configuration only from the environment.** `Database__Provider=Sqlite|Postgres` (default Sqlite) and
  `ConnectionStrings__Default`, or `ConnectionStrings__Default_FILE` naming a file that holds it (Docker secrets;
  the file wins when both are set, a missing or empty file is a startup error). Never in the admin panel, never in
  the database, never written by the app. An unknown provider or a Postgres provider without a connection string
  stops startup with a clear message. The connection string never reaches a log line, an error page, the Status tab
  or an exception message shown to anyone (Npgsql errors are logged by type and SQL state only).
- **Admin panel.** The Status tab shows "Database: PostgreSQL" or "Database: SQLite" and nothing else about the
  connection (no host, port, database, user). Database size: SQLite file size as now; PostgreSQL
  `pg_database_size(current_database())`. The Backup tab with PostgreSQL shows one line pointing to `pg_dump` and
  the deploy notes instead of the download.
- **Migrations.** A second EF migration set for PostgreSQL in its own assembly/folder, applied at startup like the
  SQLite set. Provider-specific raw SQL (the SQLite `strftime` auto-close backfill, the `json_set` results-settings
  rewrite, PRAGMAs) gets an equivalent in the PostgreSQL baseline, or is unnecessary there because the baseline
  starts at today's model. Both sets must produce the same model (a test compares them).
- **Locking.** Writes that SQLite serialises by its single writer (ballot submit, poll edit, the limit checks under
  `OwnerLock`, the results-ready claim) must stay correct under PostgreSQL's concurrent writers: row locks
  (`SELECT … FOR UPDATE`) or serializable transactions with retry, whichever each case needs. Concurrency tests run
  against both providers.
- **Still one app instance.** Abuse state, the live-results notifier, `OwnerLock` and the circuit limiter are
  in-memory; PostgreSQL does not make Telrang scale out. The deploy notes say so.
- **Moving data: a command, with downtime.** The app binary takes
  `copy-to-postgres --from <sqlite file>` (run with the Postgres environment set, e.g.
  `docker compose run --rm telrang copy-to-postgres --from /data/telrang.db` with the app service stopped). It
  applies the PostgreSQL migrations, refuses a target database that already holds any Telrang rows, refuses a
  SQLite file whose migrations are not current (start the old setup once first), copies every table in one
  transaction (keeping ids, Identity rows, settings, outbox, change logs), resets sequences, compares row counts
  per table, and prints a summary without any data. It never deletes or renames the SQLite file. Exit code 0 only
  on a verified copy. The Data Protection keys stay in the data directory and must move along (same volume), or
  sessions, 2FA tokens, voter keys and unsubscribe links break; the command checks the key directory is present
  and warns otherwise.
- **Package.** `Npgsql.EntityFrameworkCore.PostgreSQL` (the EF Core provider for PostgreSQL; nothing else from
  Npgsql's ecosystem).
- **Tests.** The Web test suite runs against both providers: SQLite as now; PostgreSQL when
  `TELRANG_TEST_POSTGRES` holds a connection string to a server where the tests may create and drop databases (one
  database per `AppFactory`, dropped on dispose). CI runs both, with a `postgres:17` service. Plus: the copy command
  (round-trip a populated SQLite file, refusals, row counts), configuration errors, nothing secret in logs or the
  Status tab, the model comparison between the two migration sets.
- **Documentation.** The deploy notes (`docs/deploy/`) get a PostgreSQL section: compose example with a `postgres:17`
  service, Docker secret for the connection string, health check and `depends_on`, the move from SQLite step by
  step (stop, back up, copy, switch env, start, check, keep the SQLite file until satisfied), backups with
  `pg_dump`/restore, a read-only role for reporting tools (and what it then sees: voter names and aliases), and the
  one-instance limit. `compose.yml`'s commented Postgres service is updated to match.

## 29. Three install formats: Docker, Linux service, Windows service (decided 2026-11-02) [implemented]

**Implemented** (2026-11-02): `Hosting/` (`OperatorConfig`, `HostPlatform`, `FilePermissions`, `ServiceHosting`,
`HealthCheckCommand`), `packaging/linux` and `packaging/windows`, `scripts/publish-release.sh` (self-contained archives,
`SHA256SUMS`), the multi-arch Dockerfile with a `HEALTHCHECK`, `.github/workflows/release.yml` (ci.yml reused through
`workflow_call`), e2e against a self-contained build (`--app`) and the image (`--image`), `docs/deploy/`. Verified locally:
the unit under a real systemd (container), Docker image both architectures, e2e on the tarball and the image. Not run:
the Windows scripts (parsed only) and the workflow (not yet run). Decisions in §13 (2026-11-02, §29 implemented).

Telrang ships as a Docker image, a Linux service (systemd) and a Windows service. All three run the same program
with the same command line and the same configuration rules, so instructions, commands (`copy-to-postgres`) and
support answers carry over.

- **Configuration sources**, highest first: command line, environment variables, an optional operator-written JSON
  file outside the install folder, `appsettings*.json` in the install folder (shipped defaults; replaced on upgrade).
  The file: `TELRANG_CONFIG` names it; default `/etc/telrang/telrang.json` on Linux, `%ProgramData%\Telrang\telrang.json`
  on Windows, none in Docker (environment only, as now). Same keys as the environment (`Database:Provider`,
  `ConnectionStrings:Default`, `App:DataDir`, `ASPNETCORE_URLS` as `Urls`, Kestrel certificates, reverse proxy).
  A file that exists but cannot be parsed stops startup (exit 2); a missing default file is fine. The app never
  writes it. A file readable by others than the service account and administrators/root gets a startup warning
  (it may hold the database password).
- **Service hosting.** `UseWindowsService()` (Service Control Manager start/stop, Event Log) and `UseSystemd()`
  (`Type=notify`, journal-formatted logs); both are no-ops elsewhere. New packages
  `Microsoft.Extensions.Hosting.WindowsServices` and `Microsoft.Extensions.Hosting.Systemd`. The default data
  directory follows the platform when `App:DataDir` is unset and the process runs as a service: `/var/lib/telrang`
  (systemd `StateDirectory`), `%ProgramData%\Telrang\data`; `./data` otherwise as now.
- **Keys at rest.** The Data Protection key ring (`{DataDir}/keys`) is what makes a database copy useless for 2FA
  bypass (AV-12), so it gets the platform's protection: Windows encrypts it with DPAPI (machine scope,
  `ProtectKeysWithDpapi`, no package); Linux and Docker rely on file permissions (service user only, 0700 on the
  data directory, checked at startup with a warning). With PostgreSQL on another host, the database and the key
  ring no longer share a machine; the deploy notes recommend that split and say what each part alone reveals.
- **Release contents** (built by CI on a `v*` tag; `scripts/publish-release.sh` builds the same locally):
  - Docker: multi-arch image (linux/amd64, linux/arm64) on GHCR, `ghcr.io/<owner>/telrang:<version>`, `:latest` (full
    releases) and `:next` (every release, pre-releases included);
    compose file as now.
  - Linux: `telrang-<version>-linux-x64.tar.gz` and `-linux-arm64.tar.gz`, self-contained (no .NET install
    needed), with `telrang.service` (own system user `telrang`, `StateDirectory=telrang`, `ConfigurationDirectory=telrang`,
    sandboxing: `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `NoNewPrivileges`, `CapabilityBoundingSet=`,
    `RestrictAddressFamilies`, `SystemCallFilter=@system-service`, …) and an example `telrang.json`. Install is a
    documented handful of commands (no install script required; an optional `install.sh` may do the same steps).
  - Windows: `telrang-<version>-win-x64.zip`, self-contained, with `install-service.ps1` (service under the virtual
    account `NT SERVICE\Telrang`, start Automatic (Delayed), recovery restarts, data and config folders created
    with ACLs for that account and Administrators only, optional firewall rule behind a switch) and
    `uninstall-service.ps1` (removes the service, keeps data and config). Unsigned: SmartScreen warns; signing is
    later.
  - Every release: `SHA256SUMS` and GitHub build provenance attestations for the archives and the image.
- **CI release workflow** (`.github/workflows/release.yml`, on `v*` tags, `permissions` minimal per job, actions
  pinned by SHA): Core and Web tests on SQLite and PostgreSQL, e2e on the published Linux build, build all formats,
  smoke-test each (Linux: the tarball under systemd in the runner, health check; Windows: `windows-latest` installs
  the service with the script, health check, uninstall; Docker: the image's health check and the e2e suite against
  it), then push the image and create the GitHub Release. `scripts/publish-release.sh` and the Linux/Docker smoke tests
  run the same steps locally. The workflow also checks the archive set, the version stamped in every
  `Telrang.Web.dll` and that the footer of every install format shows the tag's version and commit.
- **Other workflows**: `ci.yml` (build, format, tests on both providers, e2e), `codeql.yml` (CodeQL, skipped while the
  repository is private), Dependabot (`.github/dependabot.yml`: NuGet, Actions, the Docker base images;
  `dependabot-automerge.yml` turns on auto-merge for every update but a major version bump, so it lands once the
  checks the `main` ruleset requires pass).
- **Upgrades** are the same everywhere: stop, replace the program (folder, archive or image), start; migrations run at
  start. The deploy notes give the commands per format.
- **Documentation**: `docs/deploy/` with one page per format (`docker.md`, `linux.md`, `windows.md`) sharing the
  same headings: install, configuration (file and environment), HTTPS / reverse proxy, PostgreSQL and moving from
  SQLite, backup (SQLite file or `pg_dump`, plus the key ring), upgrade, uninstall, troubleshooting (logs: `docker
  logs`, `journalctl -u telrang`, Event Viewer). The old Docker deploy README becomes `docker.md` (moved, linked).
  A short `docs/deploy/security.md`: what the database alone, the data directory alone and the host reveal, and
  what to keep apart.

## 30. Encrypted email addresses (decided 2026-11-02) [implemented]

**Implemented** (2026-11-02): `Security/EmailProtection.cs` (encryption, keyed hash, `emailkey`), `ProtectedTokenUserStore`
(Identity's reads, writes and lookups), `Data/EmailUpgrade.cs` (startup step), outbox `ToHash` (migration
`EncryptedEmails`, both sets), admin log notes, admin search by complete address, "unreadable address". Tests:
`EncryptedEmailTests` (no '@' in any text column of a used instance, lookups, uniqueness, email change, transfer, outbox
limits, upgrade on both providers, resume, lost key ring), the copy round trip. Decisions in §13 (2026-11-02, §30
implemented).

A database copy must not hand over the list of users' email addresses (pentest round 3, "what each partial access
yields"). Emails are encrypted at rest; lookups use a keyed hash.

- **Lookup value.** `NormalizedEmail` and `NormalizedUserName` hold a keyed hash of the normalised address
  (HMAC-SHA256 with a dedicated 32-byte secret, base64url), not the address. Login, registration, forgot password,
  email change, transfer-to-email, the setup flow and every uniqueness check find accounts through it. The secret
  is generated once in `{DataDir}/keys/emailkey`, protected with Data Protection like `voterkey` (it must not rotate
  with the key ring). Identity's built-in `ProtectPersonalData` / `ILookupProtector` may implement this if it fits;
  the outcome above is what counts.
- **Readable value.** `Email` and `UserName` hold the address encrypted with Data Protection (purpose
  `Telrang.Email.v1`), decrypted only where the app shows or sends it (account pages, admin user list, mail).
- **Everywhere else an address is stored**: outbox recipients (encrypted, plus the keyed hash for the per-recipient
  limits), the admin action log (encrypted), anything else found. No address in logs (already a rule).
- **Admin search** matches the display name (substring, plain as now) or a complete email address (exact, through
  the hash). Substring search on emails goes; the search box hint says so.
- **Upgrade.** A startup step encrypts existing rows in place, idempotent and resumable; `copy-to-postgres` copies
  the encrypted values as they are (the key ring and `emailkey` move with the data directory). A database whose
  addresses cannot be decrypted (key ring lost) starts, logs the problem once and shows "unreadable address" in the
  admin list; sign-in by email then fails for those accounts. The deploy docs say: back up the key directory, or
  every address is lost.
- **Display names stay plain** (chosen by the user, needed for search). Voter names and aliases stay plain (§25
  results and the owner views need them).
- A hash-secret rotation command is later.

## 31. Pentest round 3 fixes (decided 2026-11-02)

Findings of pentest round 3 (R3-RES-, R3-VOTE-, R3-DEP-; cited here without the prefix). Decisions where the report left a choice:

- **Results page memory (RES-1).** [fixed] Ballot tables (voters' "Ballots", the dates "Who can when" grid, the owner's views)
  are paged at 50 rows; grid cells are plain markup, not a component per cell. Computed results are cached per poll
  in memory and dropped on `PollChangeNotifier.Changed`/`End` (bounded size, evicted by memory). Heavy GETs (results,
  CSV) get a per-client and a global concurrency limit; over it: 503 with `Retry-After`.
- **Editor input (RES-2).** [fixed] Every editor string is cut on the server at twice its limit when it arrives; a draft
  larger than `MaxCircuitMessageBytes` is never pushed to `draft.js`. Validation still reports the real limits.
- **Change log (VOTE-1..4, VOTE-6, RES-3).** [fixed] The rename exemption covers only case and whitespace. Labels refuse
  control, format, private-use and surrogate characters (Cc, Cf, Co, Cs) and are shown in `<bdi>`. After the first
  ballot, title changes are logged ("Title changed: old → new") and description changes as "Description changed"
  (date only), with the same case/whitespace exemption. Ballot counts on removals are shown only to viewers who can
  see the results; others see "“Pizza” removed". Entries per poll are capped (oldest beyond the cap merged into one
  "n earlier changes" line, the pages render at most the newest 50 with a count), option-changing saves are
  rate-limited per poll once it has ballots, and `RetiredKeys` becomes a key counter. Ballot rows for voters keep
  submission order (VOTE-5 accepted; the help for "Show ballots to voters" says rows appear in voting order) [fixed].
- **Edit cost under the lock (VOTE-3, RES-5, DEP-8).** [fixed] Ballots are parsed once per edit, before any write lock;
  only the write itself runs under it. Opening the editor uses the same single pass.
- **Markdown images (RES-4).** [fixed] Images render as their alt text (no `<img>` from user Markdown anywhere).
- **Home page (RES-6).** [fixed] The rendered showcase is cached (it depends only on the build and the theme), GETs on public
  pages get a generous per-client rate limit.
- **PostgreSQL lock and pool (DEP-1).** [fixed] The writer lock becomes one only Telrang's own role can take (a row lock on a
  Telrang-owned table instead of a global advisory lock); the docs revoke CONNECT from PUBLIC and give the reporting
  role no lock rights; the default pool is capped below the server's slots (`Maximum Pool Size` documented, startup
  warns when the pool exceeds `max_connections - superuser_reserved_connections`). Docs add `GSS Encryption
  Mode=Disable` for the Kerberos connect delay.
- **Server-side sessions (DEP-2).** [fixed] The authentication cookie holds only a random session id; the ticket lives in a
  `AuthSessions` table (`ITicketStore`), checked on every request, deleted on sign-out, "log out everywhere",
  password and 2FA changes, account block or delete. A key ring copy alone no longer signs anyone in. Upgrade signs
  everyone out once. The admin's user id is no longer logged at Warning.
- **Local port (DEP-3, DEP-7).** [fixed] The Linux service listens on a Unix socket (`/run/telrang/telrang.sock`, group
  `telrang-proxy`, 0660) by default; the docs show nginx and Caddy for it. Docker keeps TCP inside the container;
  Windows keeps loopback TCP with a documented note. Forwarded headers from a socket peer are trusted; loopback TCP
  is trusted only when configured.
- **Windows install (DEP-4, DEP-9, DEP-11).** [fixed; script not run on Windows] The script takes ownership of and resets ACLs on existing folders, refuses
  an existing config file it did not create unless `-KeepConfig` is given, refuses an `-InstallDir` that is not
  admin-only, and backs up to an admin-only folder. The app checks the data folder's owner on Windows. `security.md`
  corrects the DPAPI statement (any local account can decrypt machine-scope DPAPI; the folder ACL is the guard).
- **Secrets and links at rest (DEP-5, DEP-6, DEP-10).** [fixed] Database error text is redacted wherever EF formats it into
  the message; queued outbox bodies are encrypted with Data Protection until sent (then cleared as now); the
  connection-string file and `cert.pfx` get the same permission warning as the config file, and a config file the
  service itself can write gets one too.
- **Info items** (DEP-12, RES-7): [DEP-12 fixed: a, b, d, e; c documented] copy-to-postgres checks the target before migrating and that the SQLite file is
  not in use; the setup key is not written to the journal at all on systemd (file only, as on Windows); the release
  publishes the tested image digest; base images pinned by digest; the admin help says 128 [fixed]; an
  out-of-memory in a circuit records an abuse signal [fixed].

## 32. Docker behind Nginx Proxy Manager (decided 2026-11-02)

**Implemented** (2026-11-02): `docker/compose.npm.yml`, the hardening in both compose files, `scripts/npm-stack-test.sh`
(the e2e flows with the new `mail` flow and the outside checks through a real NPM 2.16 container), `docs/deploy/docker.md`
"Nginx Proxy Manager", the rewritten go-live checks. Decisions and findings in §13 (2026-11-02, §32
implemented). The upgrade fixture test is another task.

The reference setup: Docker on a Linux VM, behind an existing Nginx Proxy Manager (NPM) container that
holds the certificates, PostgreSQL, registration closed, SMTP on. This section makes
that setup a tested, documented path.

- **Compose file** `docker/compose.npm.yml`, self-contained (used alone with `-f`). [implemented] The app publishes **no port**. It sits
  on three networks: `telrang-proxy`, a user-defined bridge with a fixed small subnet that only NPM and Telrang join (created
  once with `docker network create`, `--ip-range` keeping NPM's address free; NPM's compose adds it as an external network
  with a fixed address), its own `egress` network (fixed subnet, for a host firewall rule) for outbound mail, and an
  `internal` network with the database. The app trusts forwarded headers from NPM's fixed address only
  (`ReverseProxy__KnownProxies`), never the subnet or Docker's defaults. Other containers on the VM cannot reach the app.
- **PostgreSQL by default** in this file (§28) [implemented]: `db` on `postgres:17` pinned by digest, only on the internal network,
  no published port, a named volume, `pg_isready` health check that the app waits for. Secrets as files (compose
  `secrets:`; `ConnectionStrings__Default_FILE`, `POSTGRES_PASSWORD_FILE`), generated by a documented one-liner, owned by
  the reading container's user, `0600`. A first-start init script creates Telrang's own role (no superuser), the
  database owned by it, and revokes `CONNECT`/`TEMP` from `PUBLIC`; the connection string caps the pool at 30 and sets
  `GSS Encryption Mode=Disable` (§31 DEP-1). `/data` stays mounted on the app (keys incl. `emailkey`, branding).
  Hardened like the app: runs as `postgres` (999) from the start, so `cap_drop: [ALL]` with no capability added back;
  read-only root with `tmpfs` for `/tmp`, `/var/run/postgresql` and `/docker-entrypoint-initdb.d`; `no-new-privileges`,
  memory and pids limits, log rotation. `docker/compose.yml` stays SQLite by default.
- **Hardening**, in this file and in `docker/compose.yml` [implemented]: `read_only: true` root file system with a size-capped
  `tmpfs` for `/tmp`; `cap_drop: [ALL]`; `security_opt: no-new-privileges:true`; `mem_limit` (2 GB default) and
  `pids_limit`; json-file log rotation; `DOTNET_EnableDiagnostics=0` (no debugger/profiler IPC). Whatever the app needs
  to write lives under `/data` or `/tmp`; anything else that turns out to need a writable path is a bug to fix, not a
  volume to add. (None turned up: startup, migrations, the health check, `copy-to-postgres`, the e2e flows and mail all
  run read-only.)
- **Image on the VM** [documented] without a registry: build with `scripts/publish-release.sh <v> --docker`, move with
  `docker save | ssh <vm> docker load`, or build from a source checkout on the VM. Documented both; the compose file
  names the image by tag.
- **NPM proxy host** [implemented, tested]: forward to `http://telrang:8080`, Websockets Support on (the interactive pages need it; voting does
  not), Let's Encrypt, Force SSL, HTTP/2; NPM's own HSTS off (the app sends it). NPM's admin port 81 is never published
  to the internet. The app must see the visitor's address (one hop of `X-Forwarded-For`) and `https`. Plus the
  host's Advanced line `set_real_ip_from unix:;`: NPM's nginx otherwise takes `X-Real-IP` from private and CDN
  addresses as the visitor's address, which lets such a client choose the address Telrang sees.
- **Tests** [implemented, `scripts/npm-stack-test.sh`]: the hardened container behind a real `jc21/nginx-proxy-manager` container (configured through its API, a
  self-signed certificate) runs the e2e flows and the go-live outside checks (headers, `secure` cookies, forged
  `X-Forwarded-For`, Blazor Origin check, WebSockets, mail queued to a fake SMTP server). A script reproduces it.
- **Go-live checks** [implemented]: rewritten for this setup (Docker + NPM), including:
  - Docker-published ports bypass `ufw` (only NPM publishes 80/443);
  - IPv6;
  - backup of the volume with `keys/`;
  - updates by loading a new image;
  - re-entering the SMTP password after a move (it is encrypted with the instance's key ring);
  - backups as `pg_dump -Fc` plus the `/data` volume, restore, and PostgreSQL major versions only by dump and restore.

  The systemd variant stays in `docs/deploy/linux.md`.
- **Upgrade safety** [implemented 2026-11-02]: before the first update of a live instance, `MigrationUpgradeTests` migrates a database made by
  the current schema, with accounts, polls of each type, ballots, invites, change-log entries and outbox rows, to the
  next one and checks the data is intact; the fixture is kept per release. Fixtures in
  `tests/Telrang.Web.Tests/Fixtures/upgrade/<version>/` (README there: contents, how to make one, release step), each as
  SQLite file and PostgreSQL dump with the release's key ring; both providers are checked (sign-in with password, TOTP
  and recovery code, address lookup, results equal to those recorded, invite links, voter cookie, change log, SMTP
  password, queued mail, close notification). A pre-release tag (`1.2.0-rc.1`) is checked against the fixture of the
  release it leads to (`1.2.0`). Decisions in §13 (2026-11-02, §32 upgrade safety).

## 33. Results readability: values on hover, ballots view, shell on poll pages (decided 2026-11-04)
Alpha feedback on the results page: bars need their numbers, the owner has no per-ballot view besides the CSV, and
signed-in visitors lose the navigation and width on `/p/*`.

- **Values on hover.** Every bar names its value in a `title` (SVG `<title>`): the score bar "Score 23 · 1st: 3 ·
  2 bans" (points: "Total 140 · mean 20.0 · 5 supporters"), each histogram position "2nd: 3 voters (43%)" (points
  buckets "1–15 points: 2 voters (29%)"). Each position gets a full-height transparent hit area, so empty and short
  positions show their (zero) count too. Screen-reader text stays as it is.
- **Ballots view** (`/p/{id}/results?view=ballots`, a "Ballots" link next to Refresh, only when allowed). Shown to the
  owner always and to voters when `CanSeeBallots`; nothing else changes who sees what (§25). The owner sees aliases
  (alias, else the name as entered); voters see names only (blank → "Anonymous"), as in §25. The plain results view no
  longer carries ballot tables (`BallotRowsView` and the voter "Who can when" grid move to the ballots view); the owner's
  live updates keep working on both views.
  - Ranked: one column per ballot (header: name), rows 1st, 2nd, … down to the longest ranking, each cell the option
    label; a last row "Banned" lists the ballot's bans. The position column is sticky; the table scrolls sideways.
  - Points: one column per ballot, options as rows in results order, cells the points (empty for 0); sticky option
    column, sideways scroll.
  - Dates: one row per voter, one column per date (the existing "Who can when" grid), for the owner also when the poll
    has no names (rows "Anonymous").
  - Paged by `RowWindow` like every table (§31): a page is a set of ballots (columns for ranked and points).
  - Ballots in submission order; no times, no identities.
- **Shell on poll pages.** `/p/{id}` and `/p/{id}/results` use `MainLayout` like the home page: signed in, the
  configured shell with navigation and its column width; signed out, `PublicLayout`. The pages stay static SSR with no
  circuit (the shells already render without one, §24.1). Reverses the 2026-10-28 rule that signed-in visitors of
  `/p/*` get the slim public header. Other anonymous-facing pages keep `PublicLayout`.

**Implemented (ballots view, 2026-11-04)**: `ResultsService.BallotsAsync` (null unless `CanSeeBallotTable`: owner, or
`CanSeeBallots`), `BallotsView` (ranked, points) and `DatesGridView` (dates, moved out of `DatesResultsView`), the
"Ballots" / "Summary" link next to Refresh (Refresh keeps the view and page). Details the spec left open:
- A `?view=ballots` from a viewer it refuses renders the plain summary (no error status): the link is simply not offered.
- Ranked: the "Banned" row is shown when the poll allows bans or a ballot on the page has one; bans are listed in the
  option order (the cached cells keep the position per option, not the order bans were picked in).
- Points: rows in results order (total, then label, as the summary). Option labels and names are in `<bdi>` (§31).
- The pager counts ballots ("Ballots 51–100 of N") on all three types; `?view=ballots&rows=N`.
- The ballots view replaces the summary below the toolbar (counts line, change log and owner bar stay); live updates
  refetch `location.href`, so the query survives (e2e `owner-results` checks a new column arriving live).
