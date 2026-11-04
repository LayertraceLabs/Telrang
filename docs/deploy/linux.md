# Telrang as a Linux service (systemd)

A self-contained build (no .NET install needed) under `/opt/telrang`, run by systemd as its own user `telrang`, data
in `/var/lib/telrang`, configuration in `/etc/telrang/telrang.json`. Shared reference (configuration keys, first start,
mail, reverse proxy configs, PostgreSQL details): [README.md](README.md). Security notes: [security.md](security.md).

## Install

Needs: x86-64 or arm64, glibc, systemd, ICU and time zone data (`libicu` + `tzdata` on Debian/Ubuntu, `libicu` +
`tzdata` on Fedora, `icu` + `tzdata` on Arch). Not for musl (Alpine): use Docker there.

```sh
v=1.2.0; arch=x64                       # or arm64
curl -fLO https://github.com/<owner>/telrang/releases/download/v$v/telrang-$v-linux-$arch.tar.gz
curl -fLO https://github.com/<owner>/telrang/releases/download/v$v/SHA256SUMS
sha256sum -c --ignore-missing SHA256SUMS
gh attestation verify telrang-$v-linux-$arch.tar.gz --owner <owner>    # optional: built by the release workflow

sudo mkdir -p /opt/telrang
sudo tar -xzf telrang-$v-linux-$arch.tar.gz -C /opt/telrang --strip-components=1 --no-same-owner
sudo useradd --system --user-group --no-create-home --home-dir /var/lib/telrang --shell /usr/sbin/nologin telrang
sudo groupadd --system telrang-proxy                                    # may connect to the app's socket
sudo usermod -aG telrang-proxy www-data                                 # the proxy's account: www-data (nginx), caddy, ...
sudo install -d -m 750 -o root -g telrang /etc/telrang
sudo install -m 640 -o root -g telrang /opt/telrang/telrang.json /etc/telrang/telrang.json
sudo install -m 644 /opt/telrang/telrang.service /etc/systemd/system/telrang.service
sudo systemctl daemon-reload
sudo systemctl enable --now telrang
sudo cat /var/lib/telrang/admin-key.txt      # the one-time setup key; never in the journal, deleted at the next start
```

Then put a reverse proxy in front (below) and open `https://<your host>/setup` with the key.

What the unit does (`/etc/systemd/system/telrang.service`):

- `Type=notify`: `systemctl start` returns once the app listens; logs go to the journal with priorities.
- `User=telrang`; `/opt/telrang` stays root-owned, so the app cannot change its own program.
- `StateDirectory=telrang` (`/var/lib/telrang`, mode `0700`, the default data directory of the service),
  `ConfigurationDirectory=telrang` (`/etc/telrang`, `0750`), `UMask=0077`.
- `RuntimeDirectory=telrang` with `Group=telrang-proxy`: `/run/telrang` (`telrang:telrang-proxy 0750`), where the app
  creates its socket `telrang.sock` (mode `0660`). Only the proxy's account (a member of `telrang-proxy`) connects; no
  other local account can, nor take the address while the service restarts (as it could a TCP port above 1024).
  `SupplementaryGroups=telrang` keeps `/etc/telrang` readable; everything else the service writes is owner-only.
- `Restart=on-failure`, except exit code 2 (unusable configuration: restarting does not help).
- Sandbox: no capabilities, `NoNewPrivileges`, `ProtectSystem=strict` (everything read-only but the state directory),
  `ProtectHome`, `PrivateTmp`, `PrivateDevices`, `ProtectProc=invisible`, `RestrictAddressFamilies`,
  `SystemCallFilter=@system-service ~@privileged`, and more. `systemd-analyze security telrang` rates it 1.4 (OK).
  `MemoryDenyWriteExecute` and `SystemCallFilter=~@resources` are left out on purpose: the .NET runtime does not start
  with them.
- `DOTNET_EnableDiagnostics=0`: no debugger, profiler or `dotnet-dump` attach to the running app.

Change the unit with `sudo systemctl edit telrang` (a drop-in that survives upgrades), not by editing the file.

## Configuration

`/etc/telrang/telrang.json` (example in the archive), owner `root`, group `telrang`, mode `0640`: the service reads
it, cannot change it, and other accounts cannot read it (the app warns at startup otherwise). Every key:
[README.md, "Configuration"](README.md#configuration). After a change: `sudo systemctl restart telrang`.

```json
{
  "Urls": "http://unix:/run/telrang/telrang.sock",
  "App": { "DataDir": "/var/lib/telrang" }
}
```

Environment variables beat the file; set them in a drop-in:

```sh
sudo systemctl edit telrang
# [Service]
# Environment=App__RecoveryKey=1
```

A start with an unreadable or malformed file, or an unusable database configuration, ends with exit code 2:
`systemctl status telrang` shows `status=2/INVALIDARGUMENT` and the journal the reason.

Commands run from a shell read the same file, so run them as the service account:
`sudo -u telrang /opt/telrang/Telrang.Web <command>`. As another user the start stops with "The configuration
folder /etc/telrang cannot be read by this account" rather than running with other settings.

## HTTPS / reverse proxy

Recommended: the app on its Unix socket (the example file) behind Caddy or nginx on the same host, whose account is in
the group `telrang-proxy` (Install). The app trusts `X-Forwarded-For` and `X-Forwarded-Proto` from the socket: only
that group can connect. The rest of the proxy configuration (catch-all, connection limits, the live results stream):
[README.md, "Reverse proxy"](README.md#reverse-proxy).

Caddy (`/etc/caddy/Caddyfile`; the package runs as `caddy`: `sudo usermod -aG telrang-proxy caddy`):

```caddyfile
vote.example.com {
    reverse_proxy unix//run/telrang/telrang.sock
}
```

nginx (`www-data` on Debian/Ubuntu, `nginx` on Fedora/RHEL):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

upstream telrang {
    server unix:/run/telrang/telrang.sock;
}

server {
    listen 443 ssl;
    http2 on;
    server_name vote.example.com;
    ssl_certificate     /etc/letsencrypt/live/vote.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/vote.example.com/privkey.pem;

    location / {
        proxy_pass         http://telrang;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $remote_addr;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;
        proxy_buffering    off;
    }
}
```

Restart the proxy after adding its account to the group. A proxy on another machine cannot reach a socket: listen on
TCP there (`"Urls": "http://0.0.0.0:8080"`) and set `ReverseProxy:KnownProxies` to the proxy's address. A proxy on
this host over loopback TCP works too (`"Urls": "http://127.0.0.1:8080"`, `"ReverseProxy": { "KnownProxies":
"127.0.0.1" }`), but then every local account can reach the app directly, claim any client address, and take the
port while the service restarts ([security.md](security.md)).

Kestrel serving HTTPS itself: copy the certificate to `/etc/telrang/cert.pfx` (`root:telrang 0640`), then in
`telrang.json`:

```json
{
  "Urls": "http://0.0.0.0:8080;https://0.0.0.0:8443",
  "App": { "DataDir": "/var/lib/telrang" },
  "Kestrel": { "Certificates": { "Default": { "Path": "/etc/telrang/cert.pfx", "Password": "..." } } }
}
```

Ports below 1024 (443) need a capability the unit drops: `sudo systemctl edit telrang` with
`AmbientCapabilities=CAP_NET_BIND_SERVICE` and `CapabilityBoundingSet=CAP_NET_BIND_SERVICE`, or keep 8443 and redirect
with the firewall and set `Environment=ASPNETCORE_HTTPS_PORT=443` (the public port, for HTTP→HTTPS redirects). Kestrel reloads a certificate file that changes in place; convert PEM
files with `openssl pkcs12 -export -in fullchain.pem -inkey privkey.pem -out cert.pfx`.

## PostgreSQL and moving from SQLite

Configuration and what the copy guarantees: [README.md, "PostgreSQL"](README.md#postgresql). In `telrang.json`:

```json
{
  "Urls": "http://unix:/run/telrang/telrang.sock",
  "App": { "DataDir": "/var/lib/telrang" },
  "Database": { "Provider": "Postgres" },
  "ConnectionStrings": { "Default": "Host=localhost;Database=telrang;Username=telrang;Password=..." }
}
```

or keep the connection string in its own file: `"ConnectionStrings": { "Default_FILE": "/etc/telrang/db" }` with
`/etc/telrang/db` as `root:telrang 0640`. With PostgreSQL on this host, add it to the unit's ordering:
`sudo systemctl edit telrang` with `[Unit]`, `After=postgresql.service`, `Wants=postgresql.service`.

Move an existing instance (the site is down during the copy):

```sh
sudo systemctl stop telrang
sudo tar -C /var/lib -czf telrang-data-$(date +%F).tgz telrang          # back up database and keys
sudo -u postgres createuser --pwprompt telrang
sudo -u postgres createdb -O telrang telrang                            # empty: do not start the app on it first
sudo -u postgres psql -c 'REVOKE CONNECT, TEMP ON DATABASE telrang FROM PUBLIC'   # only telrang may connect
sudoedit /etc/telrang/telrang.json                                      # Database + ConnectionStrings as above
sudo -u telrang /opt/telrang/Telrang.Web copy-to-postgres --from /var/lib/telrang/app.db
sudo systemctl start telrang
```

The copy exits 0 only after a verified copy. `/var/lib/telrang/app.db` stays where it is; going back is removing the
two settings again.

## Backup

SQLite: Admin → Settings → "Download backup" (a consistent copy while the app runs), plus `/var/lib/telrang/keys` and
`/var/lib/telrang/branding`. Or everything, with the app stopped:

```sh
sudo systemctl stop telrang
sudo tar -C /var/lib -czf telrang-data-$(date +%F).tgz telrang
sudo systemctl start telrang
```

PostgreSQL: `sudo -u postgres pg_dump -Fc telrang > telrang-$(date +%F).dump`, plus the key ring:
`sudo tar -C /var/lib/telrang -czf telrang-keys-$(date +%F).tgz keys`. Store the key backup apart from the database
backup ([security.md](security.md)). Restore: stop, put the files back (owned by `telrang`, mode `0700` for the
folders), start.

## Upgrade

```sh
v=1.3.0; arch=x64
# download and verify as in Install, then:
sudo systemctl stop telrang
sudo mv /opt/telrang /opt/telrang.previous
sudo mkdir /opt/telrang
sudo tar -xzf telrang-$v-linux-$arch.tar.gz -C /opt/telrang --strip-components=1 --no-same-owner
sudo install -m 644 /opt/telrang/telrang.service /etc/systemd/system/telrang.service   # the unit may have changed
sudo systemctl daemon-reload
sudo systemctl start telrang
sudo rm -rf /opt/telrang.previous      # once it runs
```

Migrations run at start: back up first. Going back to the previous program after a migration needs that backup.

## Uninstall

```sh
sudo systemctl disable --now telrang
sudo rm /etc/systemd/system/telrang.service && sudo rm -rf /etc/systemd/system/telrang.service.d
sudo systemctl daemon-reload
sudo rm -rf /opt/telrang
# data and configuration stay; to remove them as well:
sudo rm -rf /var/lib/telrang /etc/telrang && sudo userdel telrang
```

## Troubleshooting

- Logs: `journalctl -u telrang -e` (`-f` to follow). Startup errors start with `Telrang cannot start:`.
- `status=2/INVALIDARGUMENT`: the configuration (file, database settings); not restarted automatically. Fix,
  then `sudo systemctl restart telrang`.
- `status=3`: the database cannot be opened or migrated (logged by error type and SQL state only).
- `Couldn't find a valid ICU package`: install the ICU package (see Install).
- `Failed to create CoreCLR`: a drop-in added `MemoryDenyWriteExecute=yes` or `SystemCallFilter=~@resources`.
- `status=203/EXEC` on SELinux systems (Fedora, RHEL): `sudo restorecon -R /opt/telrang`.
- Health from the host: `sudo -u telrang /opt/telrang/Telrang.Web healthcheck --url http://unix:/run/telrang/telrang.sock`
  or `sudo curl --unix-socket /run/telrang/telrang.sock http://localhost/healthz`.
- `status=216/GROUP` in `systemctl status telrang` ("Failed to determine group credentials"): create the group (Install). `502 Bad Gateway`
  from the proxy: its account is not in `telrang-proxy` (`id www-data`), or the proxy has not been restarted since.
- Upgrading from a version that listened on `127.0.0.1:8080`: create the group, add the proxy's account, change `Urls`
  in `telrang.json` and the proxy's upstream as above, install the new unit, restart both.
- `The data directory ... is open to other users`: `sudo chmod 700 /var/lib/telrang /var/lib/telrang/keys`.
- Sandbox review: `systemd-analyze security telrang`.
