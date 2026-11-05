# Deploying Telrang

Telrang ships in three install formats. All three run the same program with the same command line and the same
configuration rules (DESIGN.md §29); only installing, logs and file locations differ.

| Format | Page | Program | Data | Configuration | Logs |
|---|---|---|---|---|---|
| Docker image (linux/amd64, linux/arm64) | [docker.md](docker.md) | `ghcr.io/<owner>/telrang:<version>` | volume `/data` | environment | `docker logs` |
| Linux, systemd service (x64, arm64) | [linux.md](linux.md) | `/opt/telrang` | `/var/lib/telrang` | `/etc/telrang/telrang.json` | `journalctl -u telrang` |
| Windows service (x64) | [windows.md](windows.md) | `C:\Program Files\Telrang` | `C:\ProgramData\Telrang\data` | `C:\ProgramData\Telrang\telrang.json` | Event Viewer, source `Telrang` |

[security.md](security.md): what the database, the data directory and the host each reveal, and what to keep apart.

This page is the reference all three share: configuration keys, first start, mail, reverse proxy, PostgreSQL.

## Data directory

| Path | What |
|---|---|
| `app.db` | SQLite database (plus `-wal`/`-shm` files while running) |
| `keys/` | ASP.NET Core Data Protection key ring, `voterkey` and `emailkey` |
| `branding/` | uploaded logo, banner, background |
| `tmp/` | backup scratch space, emptied at start |
| `admin-key.txt` | Linux and Windows services: the one-time admin setup or recovery key, deleted at the next start |
| `cert.pfx` | optional, if Kestrel serves HTTPS from a file you put there |

The app creates the directory and `keys/` owner-only (`0700`; on Windows an ACL for SYSTEM, Administrators and the
app's account) and warns at startup when either is open to other accounts.

**The key ring must persist.** Auth cookies, antiforgery tokens, form stamps and voter cookies are encrypted with it;
`voterkey` (encrypted with the ring) turns a browser's voter cookie into a per-poll ballot key; authenticator keys,
recovery codes, every email address (accounts, queued mail, the admin log; DESIGN.md §30) and the stored SMTP and
captcha secrets are encrypted with it, and `emailkey` (encrypted with the ring) keys the hashes that sign-in looks
addresses up by. If `keys/` is lost, every session ends, every outstanding form is invalid, link-mode voters can no
longer revise their ballots, 2FA must be set up again, the mail password must be entered again, and **every stored
email address is lost**: the admin page shows "unreadable address" for those accounts and they cannot sign in by email. Back it up with the database, but store the two backups apart
([security.md](security.md)). On Windows the ring is also encrypted with DPAPI (machine scope): a copy works only on
the same machine. Running two app instances on one data directory or database is not supported.

## Configuration

Every key below can be set in three places, highest first: a command-line argument (`--App:DataDir=/srv/telrang`), an
environment variable (`App__DataDir`, `__` = section separator) or the operator file (`"App": { "DataDir": "..." }`),
over the shipped `appsettings.json` in the program folder (replaced on upgrade; do not edit it).

The operator file is JSON (comments and trailing commas allowed), written by you, never by the app:

| Format | Default file | Created by |
|---|---|---|
| Docker | none: environment only (set `TELRANG_CONFIG` and mount a file to use one) | |
| Linux | `/etc/telrang/telrang.json` | you, from the example in the archive, `root:telrang 0640` |
| Windows | `C:\ProgramData\Telrang\telrang.json` | `install-service.ps1`, from the example in the zip |

`TELRANG_CONFIG=/path/file.json` names another file (it must exist), `TELRANG_CONFIG=none` reads none. A missing default
file is fine. A file that exists but cannot be read or parsed stops the start with exit code 2 and a message giving
the line, never the content. A file readable by other accounts than the service account and root/Administrators gets a
startup warning: it may hold the database password. So does a file the service account itself can change (it should
only read it), and the same check runs on the `ConnectionStrings__Default_FILE` file and Kestrel's certificate files.
The startup log names the file it read
(`Configuration file: /etc/telrang/telrang.json`). Commands run from a shell (`copy-to-postgres`) read the same file
as the service, so run them as the service account.

| Variable | Default | Meaning |
|---|---|---|
| `App__Name` | `Telrang` | Fallback name, used only while the admin Settings tab's display name is empty (set the name there) |
| `TELRANG_CONFIG` | per format, see above | Operator file; `none` = no file. Environment or command line only |
| `App__DataDir` | `/data` (image), `/var/lib/telrang` (Linux service), `C:\ProgramData\Telrang\data` (Windows service), `./data` otherwise | Database, key ring, branding |
| `ASPNETCORE_URLS` / `Urls` (file) | `http://+:8080` (image), `http://127.0.0.1:8080` (example files), else `http://localhost:5000` | Addresses Kestrel listens on, `;`-separated |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__Password` (file: `Kestrel:Certificates:Default:Path`) | unset | PKCS#12 certificate when Kestrel serves HTTPS itself (see the format page, "HTTPS / reverse proxy") |
| `ASPNETCORE_HTTPS_PORT` | unset | Public HTTPS port for HTTP→HTTPS redirects |
| `Database__Provider` | `Sqlite` | `Sqlite` or `Postgres`; anything else stops startup (exit code 2). See "PostgreSQL" |
| `ConnectionStrings__Default` | `Data Source={DataDir}/app.db` | SQLite: override the connection string. PostgreSQL: required (or the `_FILE` variant) |
| `ConnectionStrings__Default_FILE` | unset | Path of a file holding the connection string (Docker secret); wins over `ConnectionStrings__Default`. A missing or empty file stops startup (exit code 2) |
| `ReverseProxy__KnownProxies` / `ReverseProxy__KnownNetworks` | empty (= Unix socket peers only; in a container also the container's own networks; loopback only when listed) | Comma-separated IPs / CIDRs allowed to send `X-Forwarded-*`; replaces the default list. See "Forwarded headers trust" |
| `ReverseProxy__TrustAll` | `false` | `true` = accept `X-Forwarded-*` from any address (only if your proxy has a public address and the app is unreachable except through it) |
| `RateLimiting__AuthPermitPerMinute` | `10` | Login/register/2FA/reset/setup-link/passkey-options POSTs per client IP per minute |
| `RateLimiting__SetupPermitPerMinute` | `5` | `/setup` (admin bootstrap) POSTs per client IP per minute |
| `RateLimiting__VotePermitPerMinute` | `120` | Vote page (`/p/...`) POSTs per client IP (IPv6: per /48) per minute, sized for a class or office behind one address; refusals are not abuse signals |
| `RateLimiting__OtherPostsPerMinute` | `120` | Every other POST (account pages, sign-out, posts to unknown paths; not the Blazor connection) per client IP (IPv6: per /48) per minute; `0` = off; refusals are not abuse signals |
| `RateLimiting__CircuitsPerAccount` | `20` | Open Blazor connections (browser tabs) per signed-in account; one more is refused |
| `RateLimiting__CircuitsPerClient` | `60` | Open Blazor connections per client IP (IPv6: per /48), all accounts and anonymous visitors behind it together; one more is refused |
| `RateLimiting__StreamsPerClient` | `10` | Open live results streams (owner's results tabs) per client IP (IPv6: per /48); one more gets 429 |
| `RateLimiting__StreamsPerAccount` | `10` | Open live results streams per account |
| `RateLimiting__StreamsTotal` | `1000` | Open live results streams in all |
| `Security__AllowInsecureCookies` | `false` | Plain-HTTP testing only; see below |
| `App__RecoveryKey` | unset | `1` = issue an admin recovery key at startup; see "Lost admin access". Remove after use |
| `Abuse__Threshold` / `Abuse__Window` | `20` / `00:10:00` | Weighted abuse signals per client address or account before a temporary block (1, 10, then 60 min) |
| `Abuse__FormMinSeconds` / `Abuse__FormMaxMinutes` | `2` / `120` | Public forms submitted faster or later than this are refused |
| `Abuse__ClearBlockedRangesOnStart` | `false` | `true` = delete every blocked range at startup (way out of a lock-out; see "Blocking networks"); remove it again afterwards |

`ASPNETCORE_ENVIRONMENT` comes from the environment only (the service files set `Production`).

Everything else (registration, mail, display name, public URL, the about/privacy page, limits, captcha) is
set on the admin page and stored in the database, not in the configuration. The database itself is the
opposite: configured only here (environment, operator file, command line), never on the admin page.

A start that cannot use its database ends instead of serving: exit code 2 for an unusable database configuration
(the message names the variable), 3 for a database that cannot be opened or migrated (logged at Critical with the
error type and SQL state only, never the connection string).

## First start: the setup key

There is no default admin and nobody becomes admin by registering. On a start with no admin account
the app logs, once, at Warning level:

```
Admin setup key: 3q2-…-Xw (valid 1 h). Open https://vote.example.org/setup and enter it.
```

Where to read it: `docker compose -f docker/compose.yml logs app | grep "setup key"` (Docker),
`sudo cat /var/lib/telrang/admin-key.txt` (Linux: never the journal, which keeps it for its whole retention and members
of `adm` read), `C:\ProgramData\Telrang\data\admin-key.txt` (Windows: the key never goes to the Event Log, which every
local user may read; `install-service.ps1` prints it). Open
`https://your-host/setup`, enter the key, your email and a password, then enrol an authenticator app
(TOTP); the account is usable only after that. The key is kept in memory only (as a hash), works once,
and expires after an hour; restart the app for a new one. Once an admin exists `/setup` is 404.

## Lost admin access: the recovery key

If the only admin lost their authenticator app and recovery codes (or the password while mail is off),
restart once with `App__RecoveryKey=1` (or `"App": { "RecoveryKey": "1" }` in the operator file). The log then shows

```
Admin recovery key: Qx…bY (valid 1 h). Open https://vote.example.org/setup/recover and enter it; remove App__RecoveryKey and restart afterwards.
```

Open `https://your-host/setup/recover`, enter the key, pick the admin account and set a new password.
Its 2FA is cleared, all its sessions end, and it must enrol an authenticator app at the next sign-in.
The key works once. Remove `App__RecoveryKey` and restart: while it is set, every start logs a warning.

Registration starts **closed**. Either open it (Admin → Settings → Open registration) or create
accounts yourself: Admin → Users → "Create account" shows a one-time setup link (7 days) to hand over;
"Invite by email" mails it (needs mail settings). The person sets email, password and an
authenticator app through the link.

## Mail

Mail is optional but needed for email verification, password recovery and invites. Configure SMTP at
Admin → Mail (host, port, STARTTLS / implicit TLS / none, username, password or token, from address and
name) and use "Send test mail to me". The password is stored encrypted with the Data Protection key
ring. Example: `smtp.example.com`, port 587, STARTTLS, an SMTP submission token or app password as
password. Your domain needs SPF, DKIM and DMARC records or mail lands in spam.

All mail goes through an outbox in the database, sent every 10 seconds: at most 100 per hour (setting),
one message of a kind per recipient per 15 minutes, three per recipient per day; failures retry after
1, 5, 15 and 60 minutes, then the message is dead-lettered (visible on the Mail tab with the last error).

Links in mails and on share pages use Admin → Settings → Public URL. Set it to your public `https://`
address: **mail is not sent without it** (a reset or verification link is never built from the
request's `Host` header, which a client chooses). Once it is set, requests for any other host name get
a bare 400, except loopback names (`localhost`, `127.0.0.1`, `::1`) and `/healthz`. Set it from a
browser that has the page open on that host (or on loopback, e.g. through an SSH tunnel); the Settings
tab refuses a host it is not open on, so a typo cannot lock you out. While it is empty, share pages use
the address the request came in on (the Mail and Status tabs warn about that).

Without mail: users cannot verify their email (an admin can, Users → "Mark verified"), which they need
to create polls; "forgot password" sends nothing, so an admin issues a reset link (Users → "Reset
password", valid 24 hours) and hands it over.

## Captcha (optional)

Admin → Settings → Captcha: Cloudflare Turnstile or hCaptcha with your site key and secret. It is only
asked of anonymous voters in `voters: public` polls. While it is on, voters' browsers resolve and
contact the provider (`challenges.cloudflare.com`, or `hcaptcha.com` / `*.hcaptcha.com`), which sees
their IP address and may set cookies; say so on your about/privacy page. The server calls the
provider's verify endpoint, so the app needs outbound HTTPS to it. Blocks from the built-in abuse
tracker (Admin → Abuse) need no third party and are kept in memory only.

## Branding

Admin → Settings → Branding uploads a logo (page header), a banner (home page) and a background image. They
are stored in `/data/branding/` as `logo.<ext>`, `banner.<ext>` and `background.<ext>`, where `<ext>` is
`png`, `jpg` or `webp`. Caps: logo and banner 1 MB, background 2 MB. The format is decided by the file
header, not the name; SVG and anything else is refused.

To replace a file by hand, copy it into `{DataDir}/branding` under the fixed name with the extension that matches
its real format, and delete the same name's other extensions (the newest valid one wins otherwise). Docker:

```sh
docker cp ./logo.webp telrang:/data/branding/logo.webp
docker exec telrang rm -f /data/branding/logo.png /data/branding/logo.jpg
```

A file whose header does not match its extension is not served. URLs carry the file's time and size as a
version, so browsers fetch the new file at once. The folder is part of the data directory: upgrades
of the app never touch it; include it in backups. Theme settings, the welcome text and custom CSS live in
the database.

## Blocking networks

Admin → Abuse → "Blocked ranges" blocks IPv4/IPv6 CIDRs persistently (stored in the database, optional
expiry in days). Blocked clients get a 403 page "Access from your network is blocked."; signed-in admins
are never blocked. Ranges that include loopback, a trusted reverse proxy or the admin's own current
address are refused, as are ranges with host bits set (`198.51.100.7/24`: enter `198.51.100.0/24`) and
prefixes shorter than `/16` (IPv4) or `/32` (IPv6). The client address is the one after the trusted
`X-Forwarded-For` (see HTTPS below), so set the proxy trust right first.

**Locked out by a range** (e.g. the admin's own network, signed out): start the app once with
`Abuse__ClearBlockedRangesOnStart=true`. It deletes every blocked range at startup and logs a warning
saying how many; remove the variable again afterwards, or every restart clears the list.

Country blocking is not built in (it needs a GeoIP database). Do it at the proxy, which sees the real
address before the app does. Caddy, built with the `github.com/porech/caddy-maxmind-geolocation` plugin (`xcaddy build --with ...`), and a MaxMind
GeoLite2-Country database:

```caddyfile
vote.example.com {
    @allowed_countries {
        maxmind_geolocation {
            db_path "/etc/caddy/GeoLite2-Country.mmdb"
            allow_countries US CA DE FR
        }
    }
    # Allow-list: visitors from these countries reach the app; everyone else gets 403.
    handle @allowed_countries {
        reverse_proxy 127.0.0.1:8080
    }
    handle {
        respond "Access from your country is blocked." 403
    }
}
```

nginx with the `ngx_http_geoip2_module` and the same database:

```nginx
geoip2 /etc/nginx/GeoLite2-Country.mmdb {
    $geoip2_country_code country iso_code;
}
map $geoip2_country_code $blocked_country {
    default 0;
    XX      1;   # list ISO codes to block, one per line
    YY      1;
}

server {
    # ... the server block from "Reverse proxy" below ...
    if ($blocked_country) { return 403; }
}
```

Keep the GeoIP database updated (MaxMind's `geoipupdate`); the app needs no change.

## HTTPS and the reverse proxy

The app must be reached over HTTPS in production: the auth cookie is `Secure`, so a browser on
plain HTTP (other than `localhost`) will not keep you logged in.

Choose one of two modes: a reverse proxy in front (recommended, below), or Kestrel serving HTTPS itself from a
certificate file (see your format's page, "HTTPS / reverse proxy").

### Reverse proxy

The app listens on HTTP 8080; the proxy forwards `X-Forwarded-For` and `X-Forwarded-Proto`, and
**must pass WebSocket upgrades** (Blazor Server keeps a WebSocket open per tab; without it pages
render but buttons do nothing). Keep the app on plain HTTP (the image's `http://+:8080`, the Linux service's Unix
socket with the nginx and Caddy blocks in [linux.md](linux.md#https--reverse-proxy), the Windows example's
`http://127.0.0.1:8080`); the proxy does the
HTTP→HTTPS redirect and the app emits HSTS on requests that arrived via HTTPS.

Caddy (WebSockets and `X-Forwarded-*` are handled automatically):

```caddyfile
vote.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    http2 on;
    server_name vote.example.com;
    ssl_certificate     /etc/letsencrypt/live/vote.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/vote.example.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;   # long-lived Blazor circuits
        proxy_buffering    off;
    }
}

server {
    listen 80;
    server_name vote.example.com;
    return 301 https://$host$request_uri;
}

# Catch-all: requests for any other host name (or none) never reach the app.
server {
    listen 443 ssl default_server;
    listen 80 default_server;
    server_name _;
    ssl_reject_handshake on;
    return 444;
}
```

Keep the catch-all: without it, the first `server` block is the default and forwards whatever `Host`
a client sends. The app refuses foreign hosts itself once the Public URL is set, but the proxy should
not depend on that.

Traefik works the same way (WebSockets are forwarded by default).

Nginx Proxy Manager (a container with a web UI): [docker.md, "Nginx Proxy Manager"](docker.md#nginx-proxy-manager),
with the settings that matter (Websockets on, Force SSL, its HSTS off, and an Advanced line that stops NPM from
taking `X-Real-IP` from private and CDN addresses as the visitor's address).

**Live results stream.** A poll owner's results page keeps one server-sent event stream open
(`GET /p/{id}/results/live`, DESIGN.md §21.2): a heartbeat every 25 s, reconnected by the browser every
10 minutes. The app sends `X-Accel-Buffering: no` on it, which nginx honours, so the config above needs
nothing more (`proxy_buffering off` already covers it; with buffering on elsewhere, add
`location ~ ^/p/[^/]+/results/live$ { proxy_buffering off; ... }` with the same proxy settings).
`proxy_read_timeout` must stay above 25 s. Caddy and Traefik flush event streams by themselves.

**Connection limit.** The app refuses more than 20 open Blazor connections per account and 60 per client address
(`RateLimiting__CircuitsPerAccount`, `RateLimiting__CircuitsPerClient`), but each refused attempt still costs a WebSocket handshake. Cap
connections per address at the proxy too, generously (a person may have many tabs open), e.g. for nginx:

```nginx
# http {} block
limit_conn_zone $binary_remote_addr zone=peraddr:10m;
# in the server {} block above
limit_conn peraddr 100;
```

Open live results streams count against `limit_conn` too (one per owner's results tab, closed while the tab is
hidden); the app caps them itself with `RateLimiting__StreamsPerClient` and friends.

Caddy has no built-in per-address connection limit; use a firewall rule (e.g. `nft ... ct count over 50`)
or the `caddy-ratelimit` plugin.

**Forwarded headers trust.** The app uses `X-Forwarded-For` (client IP, which every per-IP rate
limit is keyed on) and `X-Forwarded-Proto` only when the request comes from a trusted address, and
then only the right-most `X-Forwarded-For` entry (the one your proxy appends). Three settings:

1. **Default (nothing set):** trust the peer of a Unix socket (the Linux service's default
   `http://unix:/run/telrang/telrang.sock`, which only the proxy's group can open), and over TCP nobody,
   not even loopback: every local account and process can reach a loopback port, so trusting it would
   let them claim any client address (pentest R3-DEP-7). In a container (the image sets
   `DOTNET_RUNNING_IN_CONTAINER=true`) also the networks of the container's own interfaces, i.e. its
   Docker network (e.g. `172.18.0.0/16`): the bridge gateway that a published port like
   `127.0.0.1:8080:8080` connects from, and a proxy container on the same network. Other
   private ranges (your LAN) are not trusted: a LAN client that reaches the app directly cannot fake
   its IP. A proxy on loopback TCP (the Windows service's example file), on another machine, or a
   container started with `network_mode: host` (where the container's networks are the host's LAN),
   needs option 2.
2. **`ReverseProxy__KnownProxies` / `ReverseProxy__KnownNetworks`:** trust exactly these addresses /
   CIDRs instead of the default list, e.g. `127.0.0.1` for a proxy on loopback. Use this when you know
   the proxy's address, e.g. `ReverseProxy__KnownProxies: "192.168.1.10"` for a proxy on another LAN
   host, or `ReverseProxy__KnownNetworks: "172.30.0.0/24"` for a dedicated Docker network. Check it
   by signing in from two machines: the admin Abuse tab and the rate limits must tell them apart, so
   if every request seems to come from the proxy's address, the proxy is not trusted yet.
3. **`ReverseProxy__TrustAll=true`:** trust any source. Only for a proxy with a public address that
   you cannot list, and only if the app is reachable exclusively through that proxy; otherwise
   anyone can fake their IP (and with it dodge the login rate limits and abuse blocks) and claim HTTPS.

## PostgreSQL

SQLite is the default and needs nothing. PostgreSQL (17 is what the tests and CI use) is an alternative for instances
with many votes at the same moment, or whose operator wants the data in a database other tools can read. The app
behaves the same on both; the Status tab says which one runs ("Database: PostgreSQL" and its size, nothing about the
connection).

**Still one app instance.** Abuse blocks, rate limits, the live results feed, the per-owner limit locks and the
circuit limits are kept in the app's memory. PostgreSQL does not make Telrang scale out: run exactly one app instance
against a database, never two.

### Configuration

From the configuration (environment, operator file, command line), never the admin page and never the database:

- `Database__Provider=Postgres`
- the connection string in `ConnectionStrings__Default`, or better in a file named by `ConnectionStrings__Default_FILE`
  (a Docker secret, or a file only the service account can read; the file wins when both are set). Example:
  `Host=db;Port=5432;Database=telrang;Username=telrang;Password=...`

The connection string never appears in the log, on error pages or on the Status tab; database errors are logged by
type and SQL state only (`28P01` wrong password, `3D000` no such database, `08001` server unreachable). The app
applies its PostgreSQL migrations at startup, as on SQLite; a missing database is created when the role may
(`CREATEDB`), otherwise create it first. The data directory is still needed: it holds the Data Protection keys and the branding
images.

Create the role and the database so that only that role (and the superuser) can connect: PostgreSQL grants `CONNECT`
and `TEMP` on a new database to every role on the server (`PUBLIC`).

```sql
CREATE ROLE telrang LOGIN PASSWORD '...';
CREATE DATABASE telrang OWNER telrang;
REVOKE CONNECT, TEMP ON DATABASE telrang FROM PUBLIC;
```

**Connection pool.** The app opens at most `Maximum Pool Size` connections (default 30 when the connection string sets
none; Npgsql's own default of 100 is above the 97 connections a default server leaves for non-superusers). Writes
that wait for each other hold a connection each, so the pool must stay below the server's `max_connections` minus its
reserved slots, with room for backups and other clients; the app warns at startup when it does not. Set it in the
connection string: `...;Maximum Pool Size=30`.

**Slow first connection.** When the client machine has Kerberos configured (or a broken `krb5.conf`), Npgsql may try
GSS encryption first and every new connection waits seconds for it. Without Kerberos, add
`GSS Encryption Mode=Disable` to the connection string.

**The writer lock.** On PostgreSQL the app serialises its multi-step writes with row locks on its own `WriterLocks`
table (one row) and on the poll being voted on. Taking them needs the `UPDATE` right on those tables, which only the
app's role has: a read-only role cannot hold up the app's writes.

### Moving from SQLite: `copy-to-postgres`

The move is a command run with the app stopped and the PostgreSQL configuration set; each format's page gives the exact
commands. What it does, everywhere:

- applies the PostgreSQL migrations and copies every table in one transaction (ids, accounts, 2FA, passkeys,
  settings, the mail outbox, change logs and the admin log as they are), continues the id sequences, compares the row
  count of every table and only then commits. It prints table names and row counts, never data;
- refuses (exit code 3, nothing written) a SQLite file that is not at this version's schema (start the old setup once
  with this version, stop it, then copy), a file from a newer version, and a target database holding any Telrang rows
  (do not start the app on the new database first: its first start seeds rows);
- exit code 0 means a verified copy; 1 means it failed and nothing was committed; 2 is a usage or configuration error;
- warns when `{DataDir}/keys` is missing: the key ring must stay with the app (same data directory), or sign-ins,
  2FA, voter cookies, unsubscribe links and the stored SMTP and captcha secrets stop working;
- never deletes or renames the SQLite file. Going back is switching the configuration back. Delete `app.db` yourself
  once satisfied.

After the switch, check: sign in, open a poll and its results, the admin Status tab ("Database: PostgreSQL"), a test
mail if mail is configured.

### Backups

The admin Backup section has no download on PostgreSQL; use `pg_dump -Fc` (consistent while the app runs) and
`pg_restore` into an empty database with the app stopped. The dump holds password hashes, encrypted email addresses and voter
names: store it like the server itself. The key ring is still separate and still needed for a restore. Back up
before upgrading: migrations run at startup.

### A read-only role for reporting tools

Grafana, Metabase or `psql` sessions should not use the app's role. A read-only role:

```sql
CREATE ROLE telrang_report LOGIN PASSWORD '...' CONNECTION LIMIT 3;
ALTER ROLE telrang_report SET statement_timeout = '60s';
ALTER ROLE telrang_report SET idle_in_transaction_session_timeout = '60s';
GRANT CONNECT ON DATABASE telrang TO telrang_report;
GRANT USAGE ON SCHEMA public TO telrang_report;
GRANT SELECT ON "Polls", "Ballots", "PollChanges" TO telrang_report;
```

Grant `SELECT` only, never `UPDATE`, `DELETE` or `TRUNCATE`: with those a role can lock rows and tables the app writes
to. The connection limit keeps a reporting tool from using the server's connection slots, the timeouts end forgotten
sessions.

What it then sees: every poll's definition (`DefinitionJson`: title, options, settings) and owner id, every ballot as
stored (`PayloadJson`: raw rankings, points or availability, not scores; scores are computed by the app), with
**voter display names and the owner's private aliases** (`DisplayName`, `Alias`), the voting account ids and the
timestamps. That is personal data: give the role only to people who may see it. Do not grant `AspNetUsers` (emails,
password hashes), `AspNetUserTokens` (encrypted 2FA keys), `InviteTokens`, `SetupLinks`, `OutboxMessages` (mail
addresses and bodies) or `AppSettings` (encrypted secrets). `GRANT SELECT ON ALL TABLES` would include them; new
tables added by later versions are not granted automatically, which is the safe default.

## Plain HTTP (testing only)

`Security__AllowInsecureCookies=true` drops the `Secure` requirement on the auth cookie so you can
log in over plain HTTP on a LAN. Passwords and session cookies then travel in clear text. Do not
use this on the internet.
