# What each part reveals

Telrang keeps its state in two places: the **database** (SQLite `app.db` in the data directory, or PostgreSQL) and the
**data directory** (`keys/` key ring, `branding/`, and with SQLite the database). The key ring is what makes a copy of
the database alone of limited use. Keep the two apart where you can: separate backups, and with PostgreSQL on another
host, separate machines.

## The database alone

A copy of `app.db`, a `pg_dump`, a PostgreSQL replica or a read-only role sees:

- every account's display name and password hash (PBKDF2-HMAC-SHA512, 600 000 iterations: offline guessing is slow,
  not impossible for weak passwords). **Not** the email addresses: they are encrypted with the key ring, and the lookup
  columns hold a keyed hash (secret `keys/emailkey`), so the list of addresses cannot be read and a known address cannot
  be confirmed without the data directory;
- every poll's definition and every ballot as cast, with voter display names and the owners' private aliases;
- the mail outbox's subjects and times (recipients, and queued bodies that can hold a live reset or invite link, are
  encrypted until sent; sent bodies are deleted) and the admin action log's actions (its notes are encrypted);
- **not** usable: authenticator keys and recovery codes, the stored SMTP password and captcha secret (encrypted with
  the key ring), sessions (only the hash of each session's key is stored, its ticket encrypted with the ring), voter
  cookies (derived with `keys/voterkey`, encrypted with the ring).
  Setup and invite links are stored as hashes; password reset tokens are not stored (they are key-ring protected).

So the database alone does not let anyone sign in past 2FA or impersonate a voter, but it is personal data in full.

## The data directory alone

`keys/` holds the key ring and `voterkey`. On Linux and Docker the files are plain XML: whoever reads them can forge
antiforgery tokens and voter cookies, decrypt the 2FA secrets **once they also have the database**, and recompute voter
keys. They cannot sign in as anyone: the sign-in cookie holds only a random session key, and the session itself (its
key's hash and the encrypted ticket) is a row in the database, looked up and checked against the account's security
stamp on every request. A forged cookie names no existing session. On Windows the ring
is encrypted with DPAPI (machine scope): a copy is useless on another machine, but **any account on the same machine**
can decrypt it (machine scope means "this machine", not "administrators"). What keeps other local users out is the
folder's ACL (SYSTEM, Administrators and the service account, owner Administrators; `install-service.ps1` takes over
folders created before the install, and the app warns at startup about other owners or entries). A copy elsewhere on
the machine, such as a backup zip, has the ACL of where it lands: keep backups in `C:\ProgramData\Telrang\backups`
(administrators only) or off the machine.

With SQLite the data directory also holds the database, so it alone reveals everything above.

## The host

Root or Administrator on the host can read both, attach to the process (unless blocked; see below) and change the
program. Nothing protects against that; the install formats limit what *other* accounts on the host get:

| | Docker | Linux service | Windows service |
|---|---|---|---|
| Program | image layers | `/opt/telrang`, root-owned, read-only for the service | `C:\Program Files\Telrang`, Administrators |
| Data directory | volume `/data`, `0700` UID 1654 | `/var/lib/telrang`, `0700` `telrang` | `C:\ProgramData\Telrang\data`, SYSTEM + Administrators + `NT SERVICE\Telrang` |
| Configuration (may hold the database password) | environment, Docker secrets | `/etc/telrang/telrang.json`, `root:telrang 0640` | `C:\ProgramData\Telrang\telrang.json`, same ACL, service read-only |
| Key ring at rest | file permissions | file permissions | ACL (DPAPI machine scope only stops use on other machines) |
| Admin setup/recovery key | `docker logs` (who can run `docker` is root-equivalent anyway) | `/var/lib/telrang/admin-key.txt` (`0600` `telrang`; root); never the journal | `data\admin-key.txt` (Administrators); never the Event Log, which all users read |
| HTTP backend | inside the container (published port: your choice) | Unix socket `/run/telrang/telrang.sock`, `0660`, group `telrang-proxy`: only the proxy connects, nobody can take it during a restart | `127.0.0.1:8080`: every local account can connect, send `X-Forwarded-*` (trusted from loopback in the example file) and, while the service restarts, bind the port itself |
| Debugger/diagnostics attach | off (`DOTNET_EnableDiagnostics=0`) | off | off |
| Process sandbox | container, non-root | systemd sandbox (no capabilities, read-only system, private /tmp, syscall filter) | virtual account, no rights beyond its folders |

The app warns at startup when the configuration file or the data directory is open to other accounts. It never writes
the configuration file. Client IP addresses are never written to disk or the log.

## What to keep apart

- **Backups**: the database backup and the key ring backup in different places (or encrypt the archive). Either alone is
  of limited use; together they are the whole instance.
- **PostgreSQL on another host**: the database server then never holds the key ring, and the app host holds no
  database files. Give the app role only its own database; reporting tools get a read-only role
  ([README.md](README.md#a-read-only-role-for-reporting-tools)) that excludes accounts, tokens and the outbox.
- **The connection string**: in a Docker secret, or in the operator file readable only by root/Administrators and the
  service. Never in a shell history or a world-readable unit file (`systemctl show` prints `Environment=` lines to
  every user; a drop-in `Environment=` with a password is readable that way, the operator file is not). Never on the
  command line either: every local user sees a process's arguments (`ps`), whatever the service's sandbox.
- **Other services on the host**: they run as other accounts and cannot read the data directory or the configuration.
  Do not add the service account to other groups or give other accounts its group.
- **Windows moves**: the DPAPI-protected ring cannot follow to another machine (see windows.md, "Backup").
