# Telrang with Docker

One container, SQLite on a volume (or PostgreSQL, see [below](#postgresql-and-moving-from-sqlite)). Shared reference
(configuration keys, first start, mail, reverse proxy configs, PostgreSQL details): [README.md](README.md).
Security notes: [security.md](security.md). Behind Nginx Proxy Manager with PostgreSQL (the tested internet setup):
[Nginx Proxy Manager](#nginx-proxy-manager).

## Install

Release image (linux/amd64 and linux/arm64): `ghcr.io/<owner>/telrang:<version>`, also `:latest`. Verify it:
`gh attestation verify oci://ghcr.io/<owner>/telrang:<version> --owner <owner>`.

```sh
docker run -d --name telrang --restart unless-stopped -p 127.0.0.1:8080:8080 -v telrang-data:/data \
  ghcr.io/<owner>/telrang:<version>
docker logs telrang 2>&1 | grep "setup key"
```

Or with compose, built from a checkout of the repository:

```sh
docker compose -f docker/compose.yml up -d --build
docker compose -f docker/compose.yml logs app | grep "setup key"
```

(`docker build -f docker/Dockerfile -t telrang .` builds the image alone; add
`--platform linux/amd64,linux/arm64` with buildx for both architectures.)

Both compose files harden the container (DESIGN.md §32): read-only root file system with a 64 MB `tmpfs` on `/tmp`,
`cap_drop: [ALL]`, `no-new-privileges`, `mem_limit: 2g`, `pids_limit: 512`, json-file logs rotated at 5 × 10 MB. The
app writes only to `/data` and `/tmp`. With `docker run`, the same:
`--read-only --tmpfs /tmp:size=64m --cap-drop ALL --security-opt no-new-privileges:true --memory 2g --pids-limit 512
--log-opt max-size=10m --log-opt max-file=5`.

The image runs as the non-root `app` user (UID/GID 1654), listens on HTTP 8080
(`ASPNETCORE_URLS=http://+:8080`), and keeps everything stateful in `/data`:

| Path | What |
|---|---|
| `/data/app.db` | SQLite database (plus `-wal`/`-shm` files while running) |
| `/data/keys/` | ASP.NET Core Data Protection key ring |
| `/data/cert.pfx` | optional, cert-file mode only |

Migrations are applied automatically at startup. Health: `GET /healthz` returns `200 Healthy`; the image's
`HEALTHCHECK` asks it (see Configuration).

Auth cookies, antiforgery tokens, form stamps and voter cookies are encrypted with the key
ring in `/data/keys`. The same directory holds `voterkey`, the (key-ring encrypted) secret that
turns a browser's voter cookie into a per-poll ballot key; without it link-mode voters can no
longer revise ballots they cast earlier. If that directory is lost or not on a persistent volume, **every restart
logs everyone out** and invalidates every outstanding form. Always mount `/data` as a
volume and include it in backups.

The keys are stored unencrypted at rest (the log says "No XML encryptor configured"); the volume is `0700` for UID
1654 and nothing else. Protect it like you protect the database, and back it up apart from the database ([security.md](security.md)). Running more than one container against the same
data is not supported (SQLite, single key ring writer; with PostgreSQL still one container, see "PostgreSQL").

If you bind-mount a host directory instead of a named volume, make it writable for UID 1654:
`sudo chown -R 1654:1654 ./data`.

## Configuration

Environment variables only; the image reads no operator file unless you set `TELRANG_CONFIG` and mount one
(e.g. `-v /srv/telrang.json:/etc/telrang/telrang.json:ro -e TELRANG_CONFIG=/etc/telrang/telrang.json`). Every key:
[README.md, "Configuration"](README.md#configuration). The image sets `ASPNETCORE_ENVIRONMENT=Production`,
`ASPNETCORE_URLS=http://+:8080`, `App__DataDir=/data` and `DOTNET_EnableDiagnostics=0` (no debugger or profiler
attach; set `DOTNET_EnableDiagnostics=1` to use `dotnet-trace`/`dotnet-dump`).

A start that cannot use its configuration ends with exit code 2 (`restart: unless-stopped` then restarts it in a loop:
read `docker logs`), one that cannot open its database with 3.

The image's `HEALTHCHECK` runs `dotnet Telrang.Web.dll healthcheck` (GET `/healthz` on the first `http` address of
`ASPNETCORE_URLS`; `--url http://127.0.0.1:8080` to choose): `docker ps` shows `healthy`. `/healthz` stays reachable over
plain HTTP when HTTPS redirection is on, for probes.

## HTTPS / reverse proxy

Reverse proxy (recommended): publish `127.0.0.1:8080:8080` (as above) and put Caddy, nginx or Traefik in front:
[README.md, "Reverse proxy"](README.md#reverse-proxy), including which proxy addresses the app trusts by default in a
container.

### Kestrel serves HTTPS from a certificate file

Copy a PKCS#12 certificate into the volume as `/data/cert.pfx` (readable by UID 1654), then set:

```yaml
environment:
  ASPNETCORE_URLS: "http://+:8080;https://+:8443"
  ASPNETCORE_Kestrel__Certificates__Default__Path: "/data/cert.pfx"
  ASPNETCORE_Kestrel__Certificates__Default__Password: "the-pfx-password"
  ASPNETCORE_HTTPS_PORT: "443"     # the *public* port, used for HTTP->HTTPS redirects
  # No proxy in front: honour X-Forwarded-* from nobody but the container itself.
  ReverseProxy__KnownProxies: "127.0.0.1"
ports:
  - "443:8443"
  - "80:8080"                      # optional, only redirects to HTTPS
```

In this mode the app faces the internet directly, so no forwarded headers should be trusted. Set
`ReverseProxy__KnownProxies` to `127.0.0.1` as above: with Docker's userland proxy (rootless Docker,
some IPv6 setups) every client appears to come from the bridge gateway (e.g. `172.17.0.1`), which
the default trust of the container's own network would accept. Publishing 8080 publicly is fine here: it only
redirects to HTTPS, and nobody on it can set their IP.

When an `https://` URL is configured the app enables HTTP→HTTPS redirection (except
`/healthz`) and HSTS. Without `ASPNETCORE_HTTPS_PORT` the redirect would point at 8443, which is
wrong if you published the container port as 443. Convert PEM files with
`openssl pkcs12 -export -in fullchain.pem -inkey privkey.pem -out cert.pfx`. Kestrel watches a
certificate configured by path (since .NET 8) and reloads the endpoint when the file changes, so
renewing in place normally needs no restart. Some volume and bind-mount setups do not deliver
file-change events into the container; if the old certificate is still served after a renewal,
restart the container.

## Nginx Proxy Manager

One host with Docker, an existing [Nginx Proxy Manager](https://nginxproxymanager.com/) (NPM) container that holds the
certificates, and Telrang with PostgreSQL: [`docker/compose.npm.yml`](../../docker/compose.npm.yml), used alone. Tested by
`scripts/npm-stack-test.sh` (a real NPM 2.16 container configured as below; DESIGN.md §32).

| Container | Networks | Published ports |
|---|---|---|
| NPM | its own, `telrang-proxy` (fixed address `172.31.255.2`) | 80, 443; 81 (admin) on loopback only |
| `app` (Telrang) | `telrang-proxy` (alias `telrang`), `egress` (outbound mail, DNS), `db` | none |
| `db` (PostgreSQL 17) | `db` (`internal`: no way out, only `app` reaches it) | none |

The app trusts `X-Forwarded-For`/`-Proto` from NPM's fixed address only (`ReverseProxy__KnownProxies`), never the
subnet. Other containers on the host share no network with it and cannot reach it.

### Set up

Put `compose.npm.yml` in a directory of its own on the host (e.g. `/srv/telrang`, owned by root, `0700`); the commands
below run there.

1. **The shared network**, once per host. Pick a subnet nothing else uses (the command refuses one that overlaps
   another Docker network; check `ip route` for your LAN and VPNs):
   ```sh
   docker network create --subnet 172.31.255.0/29 --ip-range 172.31.255.4/30 telrang-proxy
   ```
   `--ip-range` hands out `.4`–`.7` to containers without a fixed address (Telrang), so `.2` stays free for NPM even
   when Telrang starts first. With another subnet, also change `NPM_ADDRESS` (the default `172.31.255.2` in
   `compose.npm.yml`) and the address below.
2. **NPM joins it with the fixed address.** In NPM's own compose file (the service may have another name):
   ```yaml
   services:
     app:                              # the jc21/nginx-proxy-manager service
       ports:
         - "80:80"
         - "443:443"
         - "127.0.0.1:81:81"           # admin UI: never on the internet (SSH tunnel: ssh -L 8181:127.0.0.1:81 <vm>)
       networks:
         default: {}                   # keep the network it had
         telrang-proxy:
           ipv4_address: 172.31.255.2

   networks:
     telrang-proxy:
       external: true
   ```
   then `docker compose up -d` in NPM's directory (recreates the container; certificates and hosts stay in its volumes).
   Listing `default` matters: a service with a `networks:` key joins only the networks listed.
3. **The image**, with no registry: on the build machine
   ```sh
   scripts/publish-release.sh 1.2.0 --only linux-x64 --docker     # or: docker build -f docker/Dockerfile -t telrang:1.2.0 .
   docker save telrang:1.2.0 | ssh <vm> docker load
   ssh <vm> docker tag telrang:1.2.0 telrang:latest
   ```
   or build from a checkout on the host: `docker build -f docker/Dockerfile -t telrang:latest .`
   (`--platform linux/arm64` builds for an ARM host elsewhere). `compose.npm.yml` runs `telrang:latest`.
   Both compose files set `pull_policy: never` for the app, so `docker compose pull` (and a stack manager's "Re-pull image", e.g. Portainer's,
   on a stack update) skips this local image instead of failing with "pull access denied". With the registry image
   (`TELRANG_IMAGE=ghcr.io/<owner>/telrang:<version>`) set `TELRANG_PULL_POLICY=always`.
4. **The secrets**: random passwords in files next to the compose file, each readable only by the container user that
   reads it (the app is UID 1654, PostgreSQL 999). Never in the compose file or the environment.
   ```sh
   mkdir -p secrets && (umask 077; pw=$(openssl rand -hex 32)
     printf '%s' "$pw" > secrets/db_app_password
     printf 'Host=db;Database=telrang;Username=telrang;Password=%s;Maximum Pool Size=30;GSS Encryption Mode=Disable' "$pw" > secrets/telrang_db
     openssl rand -hex 32 | tr -d '\n' > secrets/db_superuser_password)
   sudo chown 1654:1654 secrets/telrang_db && sudo chown 999:999 secrets/db_app_password secrets/db_superuser_password
   ```
   On the first start (empty `db-data` volume) the `db` service creates the role `telrang` (no superuser), the database
   `telrang` owned by it, and revokes `CONNECT` and `TEMP` from `PUBLIC` ([README.md, "PostgreSQL"](README.md#postgresql)).
   The pool cap (30) stays far below the server's 100 connections; the app's role cannot take more. Changing the
   password later: `ALTER ROLE` in `psql`, then both files.
5. **Start**: `docker compose -f compose.npm.yml up -d`, then `docker compose -f compose.npm.yml ps` (both `healthy`)
   and `docker compose -f compose.npm.yml logs app | grep "setup key"`.

### The NPM proxy host

Hosts → Proxy Hosts → Add Proxy Host:

| Tab | Field | Value |
|---|---|---|
| Details | Domain Names | `vote.example.com` |
| | Scheme / Forward Hostname / Forward Port | `http` / `telrang` / `8080` |
| | Cache Assets | off |
| | Block Common Exploits | on |
| | Websockets Support | **on** (the interactive pages need it; voting does not) |
| | Access List | Publicly Accessible |
| SSL | SSL Certificate | Request a new certificate (Let's Encrypt) |
| | Force SSL | **on** |
| | HTTP/2 Support | on |
| | HSTS Enabled / HSTS Subdomains | **off** (the app sends HSTS itself; two headers confuse scanners) |
| | Trust Upstream Forwarded Proto Headers | off |
| Advanced | Custom Nginx Configuration | `set_real_ip_from unix:;` (below) |

**Why the Advanced line.** NPM's nginx takes the visitor address from the `X-Real-IP` header for connections from
private addresses (10/8, 172.16/12, 192.168/16) and from the CDN ranges it downloads (Cloudflare, CloudFront).
Anyone who reaches NPM from such an address, for example through a Cloudflare Worker, or over IPv6 through Docker's
proxy (below), could then pick the address Telrang sees and step around its rate limits and blocks. A
`set_real_ip_from` in the host's own block replaces that list for this host; `unix:` matches no TCP client, so the
address is always the real peer. `scripts/npm-stack-test.sh` checks that a forged `X-Real-IP` or `X-Forwarded-For`
does not change the client the app sees.

Use Let's Encrypt. NPM 2.16 writes an uploaded custom certificate, private key included, into its container log
(seen in the stack test), so a custom certificate's key ends up in `docker logs` of NPM.

**What the app then sees.** NPM sends `X-Forwarded-For: <what the client sent>, <client address>` and
`X-Forwarded-Proto: https`. The app takes exactly one hop (the right-most entry, added by NPM) from NPM's address, so a
forged `X-Forwarded-For` stays a string nobody reads, and `https` gives secure cookies and HSTS without a redirect
loop (NPM redirects plain HTTP; the app never does behind a proxy).

**Port 81** (NPM's admin UI) is never published to the internet: `127.0.0.1:81:81` as above and an SSH tunnel. Docker
publishes ports past `ufw` (it writes its own iptables rules), so a `"81:81"` would be open whatever `ufw` says. Give
NPM's admin a long password: the Telrang container shares a network with NPM and could reach port 81 there if it were
ever taken over.

### Outbound traffic (optional)

The `egress` network has a fixed subnet (`172.31.255.16/29`) so a host firewall rule can limit what the app may reach.
The app needs DNS and the SMTP server only; this keeps a taken-over app from scanning your LAN (not tested by the
stack test, as it changes the host's firewall):

```sh
sudo iptables -I DOCKER-USER -s 172.31.255.16/29 -j DROP
sudo iptables -I DOCKER-USER -s 172.31.255.16/29 -p tcp --dport 587 -j RETURN    # SMTP submission (STARTTLS)
sudo iptables -I DOCKER-USER -s 172.31.255.16/29 -p udp --dport 53 -j RETURN
sudo iptables -I DOCKER-USER -s 172.31.255.16/29 -p tcp --dport 53 -j RETURN
```

(`-I` inserts at the top, so the `RETURN` lines end up above the `DROP`.) Persist them with `iptables-persistent` or
your firewall's own mechanism. The rule does not cover the host itself (that is the `INPUT` chain: `ufw`).

### Mail

Admin → Mail with your provider's SMTP host (e.g. `smtp.example.com`), port 587, STARTTLS, the SMTP token as password ([README.md, "Mail"](README.md#mail)),
after Admin → Settings → Public URL. The password is encrypted with this instance's key ring (`/data/keys`): a new
instance (new volume) cannot read it, so enter it again there.

### Backup and restore

Two parts, both needed: the database dump and the `app-data` volume (`keys/` with the Data Protection key ring and
`emailkey`; without them every email address in the dump is unreadable, every session and 2FA key lost, and the SMTP
password gone; `branding/`).

```sh
cd /srv/telrang
umask 077     # both files 0600: the archive holds the key ring
docker compose -f compose.npm.yml exec -T db pg_dump -U telrang -d telrang -Fc > telrang-$(date +%F).dump
docker run --rm -v telrang_app-data:/data:ro -v "$PWD":/backup debian:stable-slim \
  sh -c 'umask 077 && tar czf /backup/telrang-data-$(date +%F).tgz -C /data .'
```

The dump is consistent while the app runs. Keep the two apart and as private as the server (the dump holds password
hashes and voter names; the volume the keys that decrypt the rest).

Restore (new host or after a loss): set up as above with the same secrets files, then

```sh
docker compose -f compose.npm.yml up -d db
docker run --rm -v telrang_app-data:/data -v "$PWD":/backup debian:stable-slim \
  sh -c 'tar xzf /backup/telrang-data-2026-11-02.tgz -C /data && chown -R 1654:1654 /data && chmod 700 /data'
docker compose -f compose.npm.yml exec -T db pg_restore -U telrang -d telrang --no-owner < telrang-2026-11-02.dump
docker compose -f compose.npm.yml up -d
```

(`pg_restore` into the database the first start created, which is still empty because the app has not run.)

**PostgreSQL major versions** (17 → 18) do not read each other's data directory: never only change the image tag.
Dump, stop, remove the `db-data` volume, start the new version (the init creates role and database again), restore the
dump. Minor updates (a new digest of 17) need nothing.

### Update

```sh
docker save telrang:1.3.0 | ssh <vm> docker load      # or build on the host
# on the host, in /srv/telrang: back up first (above): migrations run at start
docker tag telrang:1.3.0 telrang:latest
docker compose -f compose.npm.yml up -d
docker compose -f compose.npm.yml ps                  # healthy; logs: no "fail:" lines
```

In a stack manager such as Portainer: load and tag the image on the host as above, then *Update the stack*. "Re-pull image" can stay on (the
app is skipped, PostgreSQL is pulled); the new `latest` is used because the container is recreated when its image
changes.

Going back to the old version after a migration needs the backup: tag the old image `latest` again, restore the dump.

### From an existing SQLite instance

`compose.npm.yml` uses the same project name (`telrang`) and volume (`app-data`) as `docker/compose.yml`, so the volume
with `app.db` and `keys/` is the one it mounts. With the old instance stopped (`docker compose -f compose.yml stop app`)
and the secrets in place, follow [Moving an existing instance from SQLite](#moving-an-existing-instance-from-sqlite)
with `-f compose.npm.yml`: `up -d db`, then
`docker compose -f compose.npm.yml run --rm app copy-to-postgres --from /data/app.db`, then `up -d`
(`scripts/npm-stack-test.sh` does exactly this).

## PostgreSQL and moving from SQLite

Configuration and the copy command's guarantees: [README.md, "PostgreSQL"](README.md#postgresql).

### Compose example

```yaml
services:
  app:
    image: telrang:latest
    restart: unless-stopped
    ports:
      - "127.0.0.1:8080:8080"
    volumes:
      - app-data:/data                     # keys and branding, still required
    environment:
      App__DataDir: "/data"
      Database__Provider: "Postgres"
      ConnectionStrings__Default_FILE: "/run/secrets/telrang_db"
    secrets:
      - telrang_db
    depends_on:
      db:
        condition: service_healthy         # the app migrates at startup: wait for the server

  db:
    image: postgres:17
    restart: unless-stopped
    environment:
      POSTGRES_DB: telrang
      POSTGRES_USER: telrang
      POSTGRES_PASSWORD_FILE: /run/secrets/telrang_db_password
    secrets:
      - telrang_db_password
    volumes:
      - db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U telrang -d telrang"]
      interval: 5s
      timeout: 5s
      retries: 10
    # No "ports": the server is reachable only on the compose network.

secrets:
  telrang_db:
    file: ./secrets/telrang_db               # one line: Host=db;Database=telrang;Username=telrang;Password=...
  telrang_db_password:
    file: ./secrets/telrang_db_password      # the same password, alone

volumes:
  app-data:
  db-data:
```

Use a long random password of letters and digits (`openssl rand -hex 32`), so it needs no quoting in the
connection string. Compose mounts file secrets with the host file's owner and mode: make each file readable only by
the user that reads it, `sudo chown 1654 secrets/telrang_db` (the app) and `sudo chown 999 secrets/telrang_db_password`
(the postgres image), then `chmod 0400` both. `docker/compose.yml` has the same services commented out.

### Moving an existing instance from SQLite

The move is a command with the app stopped; the site is down for the copy (seconds for a small instance, minutes for
millions of ballots). Nothing changes in the SQLite file, so going back is switching the variables back.

1. **Stop the app**: `docker compose stop app`. The copy cannot tell whether the app still runs, and votes a
   running app writes to the SQLite file during or after the copy are not moved.
2. **Back up** the whole `/data` volume (database and keys), e.g.
   `docker run --rm -v telrang_app-data:/data -v "$PWD":/backup alpine sh -c 'umask 077 && tar czf /backup/telrang-data.tgz -C /data .'`
3. **Start only the database** (`docker compose up -d db`) with an **empty** database. Do not start the app on it
   first: its first start seeds rows, and the copy refuses a database that holds any Telrang rows (then drop and
   create the database again).
4. **Copy**, with the PostgreSQL variables set and the old volume mounted:
   ```sh
   docker compose run --rm app copy-to-postgres --from /data/app.db
   ```
   The command applies the PostgreSQL migrations and copies every table in one transaction (ids, accounts, 2FA,
   passkeys, settings, the mail outbox, change logs and the admin log as they are), continues the id sequences,
   compares the row count of every table and only then commits. It prints table names and row counts, never data.
   It refuses (exit code 3, nothing written) a SQLite file that is not at this version's schema (start the old setup
   once with this version, stop it, then copy), a file from a newer version, and a non-empty target. Exit code 0
   means a verified copy; 1 means it failed and nothing was committed; 2 is a usage error. It warns when
   `/data/keys` is missing: the key ring must move along (same volume), or sign-ins, 2FA, voter cookies, unsubscribe
   links and the stored SMTP and captcha secrets stop working.
5. **Switch**: set `Database__Provider` and the connection string on the app service permanently (as in the example
   above), keep the same `/data` volume.
6. **Start** the app (`docker compose up -d`) and **check**: sign in, open a poll and its results, look at the admin
   Status tab ("Database: PostgreSQL"), send a test mail if mail is configured.
7. **Keep `app.db`** in `/data` until you are satisfied. It is never deleted or renamed; delete it yourself later.

Linux and Windows installs: [linux.md](linux.md#postgresql-and-moving-from-sqlite),
[windows.md](windows.md#postgresql-and-moving-from-sqlite).

### Backups and restore

The admin Backup section has no download on PostgreSQL; use `pg_dump`. The dump holds password hashes, encrypted email
addresses and voter names: store it like the server itself. The key ring in `/data/keys` is still separate and still
needed for a restore (back it up apart from the dump).

```sh
# backup (custom format, consistent while the app runs)
(umask 077; docker compose exec -T db pg_dump -U telrang -d telrang -Fc > telrang-$(date +%F).dump)

# restore into an empty database, with the app stopped
docker compose stop app
docker compose exec -T db dropdb -U telrang telrang
docker compose exec -T db createdb -U telrang telrang
docker compose exec -T db pg_restore -U telrang -d telrang --no-owner < telrang-2026-11-02.dump
docker compose start app
```

Back up before upgrading: migrations run at startup.

## Backup

PostgreSQL: see [PostgreSQL and moving from SQLite](#postgresql-and-moving-from-sqlite) (`pg_dump`). SQLite: Admin → Settings → "Download backup" streams a consistent copy of the SQLite database (`VACUUM INTO`,
one per minute; the copy is made in `/data/tmp`, owner-only, and deleted when the download ends or at the
next start). The file holds password hashes and voter names (email addresses only encrypted): store it like the server itself.
Authenticator keys and recovery codes in it are encrypted with the key ring, so keep the key backup
apart from the database backup. It does **not** contain `/data/keys`: back that directory up too, or after a restore
every login and form is invalid, link-mode voters cannot revise their ballots, and
the stored SMTP password cannot be decrypted. Branding images live in `/data/branding` and are not in the
database backup either. Simplest full backup: stop the container and copy the
whole `/data` volume. Migrations run at startup, so back up before upgrading.

## Upgrade

```sh
docker compose -f docker/compose.yml pull     # or: git pull && docker compose -f docker/compose.yml build
# back up /data first (see Backup): migrations run at start
docker compose -f docker/compose.yml up -d
```

With `docker run`: `docker pull ghcr.io/<owner>/telrang:<new>`, `docker rm -f telrang`, then the same `docker run` with
the new tag and the same volume. Going back to an older version after a migration needs the backup.

## Uninstall

```sh
docker compose -f docker/compose.yml down        # keeps the volumes
docker compose -f docker/compose.yml down -v     # also deletes app-data (database, keys, branding)
```

## Troubleshooting

- Logs: `docker logs telrang` / `docker compose -f docker/compose.yml logs app`. Startup errors start with
  `Telrang cannot start:`.
- `unhealthy`: `docker inspect --format '{{json .State.Health}}' telrang`.
- Permission denied on `/data` with a bind mount: `sudo chown -R 1654:1654 ./data && sudo chmod 700 ./data`.
- "The data directory /data is open to other users": `docker exec -u 0 telrang chmod 700 /data /data/keys`.
- Everyone logged out after every restart: `/data` is not a persistent volume.
- Pages render but buttons do nothing: the proxy does not pass WebSocket upgrades.
