# Telrang as a Windows service

A self-contained build (no .NET install needed) in `C:\Program Files\Telrang`, run by the Service Control Manager as
the virtual account `NT SERVICE\Telrang`, data in `C:\ProgramData\Telrang\data`, configuration in
`C:\ProgramData\Telrang\telrang.json`. Shared reference (configuration keys, first start, mail, reverse proxy configs,
PostgreSQL details): [README.md](README.md). Security notes: [security.md](security.md).

The scripts are written for Windows PowerShell 5.1 and PowerShell 7; the release workflow installs, checks and removes
the service on `windows-latest` with them.

## Install

Windows 10 / Server 2016 or later, x64. In an elevated PowerShell:

```powershell
$v = '1.2.0'
Invoke-WebRequest "https://github.com/<owner>/telrang/releases/download/v$v/telrang-$v-win-x64.zip" -OutFile "telrang-$v-win-x64.zip"
Invoke-WebRequest "https://github.com/<owner>/telrang/releases/download/v$v/SHA256SUMS" -OutFile SHA256SUMS
(Get-FileHash "telrang-$v-win-x64.zip" -Algorithm SHA256).Hash.ToLower()   # compare with the line in SHA256SUMS
gh attestation verify "telrang-$v-win-x64.zip" --owner <owner>             # optional: built by the release workflow
Unblock-File "telrang-$v-win-x64.zip"
Expand-Archive "telrang-$v-win-x64.zip" -DestinationPath .
& ".\telrang-$v-win-x64\install-service.ps1"
```

The binaries are not code-signed yet: SmartScreen may warn about `Telrang.Web.exe`. If the execution policy refuses
the script: `powershell -ExecutionPolicy Bypass -File .\telrang-$v-win-x64\install-service.ps1`.

`install-service.ps1` (run it again from a newer folder to upgrade):

- copies the program to `C:\Program Files\Telrang` (`-InstallDir` for another folder);
- creates the service `Telrang` under `NT SERVICE\Telrang` (a virtual account: no password, no rights beyond what is
  granted to it), start type Automatic (Delayed Start), restart after 5 s, 30 s, then every 60 s on failure, with
  `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_EnableDiagnostics=0` (no debugger or profiler attach);
- creates `C:\ProgramData\Telrang` (service may read) and `C:\ProgramData\Telrang\data` (service may change), both with
  inheritance off and access for SYSTEM, Administrators and `NT SERVICE\Telrang` only (`C:\ProgramData` itself lets
  every user read and create folders), and `C:\ProgramData\Telrang\backups` (SYSTEM and Administrators only). Folders
  that exist already are taken over: owner Administrators, ACLs reset for everything in them, so an account that
  created `C:\ProgramData\Telrang` before the install keeps no access;
- writes the example `telrang.json` there unless one exists. An existing one not owned by Administrators or SYSTEM
  stops the script (another account wrote it; it could point the data folder elsewhere): check it, then remove it or
  pass `-KeepConfig`. Registers the Event Log source `Telrang`;
- refuses an `-InstallDir` that accounts other than SYSTEM and Administrators may change (they could replace the
  program); a new folder gets SYSTEM and Administrators full control, Users read and run;
- on an upgrade, backs up `data` to `backups` before starting the new version;
- with `-OpenFirewall [-Port 8080]`, allows inbound TCP on that port for `Telrang.Web.exe` (rule `Telrang-TCP`);
- starts the service and prints the one-time admin setup key.

The setup key is also in `C:\ProgramData\Telrang\data\admin-key.txt` (Administrators only), never in the Event Log,
which every local user can read. The file is deleted at the next start. Then put a reverse proxy in front (below) and
open `https://<your host>/setup` with the key.

## Configuration

`C:\ProgramData\Telrang\telrang.json`; every key: [README.md, "Configuration"](README.md#configuration). The app warns
at startup when the file or the data folder is open to other accounts. Edit as Administrator, then
`Restart-Service Telrang`.

```json
{
  "Urls": "http://127.0.0.1:8080",
  "App": { "DataDir": "C:\\ProgramData\\Telrang\\data" }
}
```

Backslashes in JSON strings are doubled. Save as UTF-8 (a UTF-16 file is refused with a message saying so).

Environment variables beat the file. The service's own variables are a multi-string value in the registry:

```powershell
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Telrang'
$vars = @((Get-ItemProperty $key).Environment) + 'App__RecoveryKey=1'
Set-ItemProperty $key -Name Environment -Value $vars -Type MultiString
Restart-Service Telrang
```

A start with an unreadable or malformed file, or an unusable database configuration, stops with exit code 2 and an
Error event (source `Telrang`) saying why. Commands from a console read the same file:
`& 'C:\Program Files\Telrang\Telrang.Web.exe' <command>` in an elevated PowerShell.

## HTTPS / reverse proxy

Recommended: the app on `http://127.0.0.1:8080` behind a reverse proxy on the same machine (Caddy for Windows, nginx,
or IIS with URL Rewrite + Application Request Routing and WebSockets enabled): [README.md, "Reverse
proxy"](README.md#reverse-proxy). The example `telrang.json` trusts `X-Forwarded-*` from loopback
(`"ReverseProxy": { "KnownProxies": "127.0.0.1" }`); without that line the app ignores them.

**Every local account counts as the proxy.** Windows has no Unix-socket setup like the Linux service: any program on
this machine can connect to `127.0.0.1:8080`, send any `X-Forwarded-For` (dodging per-address limits, getting another
address blocked), and while the service is stopped or restarting, bind the port itself and receive what the proxy
forwards, passwords included. On a machine other people can sign in to, run the proxy and Telrang on a machine of their
own, or remove the `ReverseProxy` line and let Kestrel serve HTTPS itself (below).

Kestrel serving HTTPS itself: copy the certificate to `C:\ProgramData\Telrang\cert.pfx` (inherits the folder's ACL),
then in `telrang.json`:

```json
{
  "Urls": "https://0.0.0.0:443",
  "App": { "DataDir": "C:\\ProgramData\\Telrang\\data" },
  "Kestrel": { "Certificates": { "Default": { "Path": "C:\\ProgramData\\Telrang\\cert.pfx", "Password": "..." } } }
}
```

and open the port: run `install-service.ps1 -OpenFirewall -Port 443` again from the release folder (or
`New-NetFirewallRule`). Kestrel reloads a certificate file that changes in place.

## PostgreSQL and moving from SQLite

Configuration and what the copy guarantees: [README.md, "PostgreSQL"](README.md#postgresql). In `telrang.json`:

```json
{
  "Urls": "http://127.0.0.1:8080",
  "App": { "DataDir": "C:\\ProgramData\\Telrang\\data" },
  "Database": { "Provider": "Postgres" },
  "ConnectionStrings": { "Default": "Host=localhost;Database=telrang;Username=telrang;Password=..." }
}
```

Move an existing instance (the site is down during the copy), in an elevated PowerShell:

```powershell
Stop-Service Telrang
Compress-Archive C:\ProgramData\Telrang\data\* "C:\ProgramData\Telrang\backups\telrang-data-$(Get-Date -Format yyyy-MM-dd).zip"   # database and keys
# create an empty database and its role on the server (do not start Telrang on it first), then edit telrang.json
& 'C:\Program Files\Telrang\Telrang.Web.exe' copy-to-postgres --from C:\ProgramData\Telrang\data\app.db
Start-Service Telrang
```

The copy exits 0 only after a verified copy (`$LASTEXITCODE`). `app.db` stays where it is; going back is removing the
two settings again.

## Backup

SQLite: Admin → Settings → "Download backup" (a consistent copy while the app runs), plus `data\keys` and
`data\branding`. Or everything, with the service stopped:

```powershell
Stop-Service Telrang
Compress-Archive C:\ProgramData\Telrang\data\* "C:\ProgramData\Telrang\backups\telrang-data-$(Get-Date -Format yyyy-MM-dd).zip"
Start-Service Telrang
```

`C:\ProgramData\Telrang\backups` is for SYSTEM and Administrators only (`install-service.ps1` makes it so; an upgrade
also leaves a copy of `data` there). Do not write backups to the current folder or the desktop: a copy takes the ACL of
where it lands, and on this machine any account can decrypt the key ring inside (below). Move backups off the machine
for keeping.

PostgreSQL: `pg_dump -Fc` plus `data\keys`. Store the key backup apart from the database backup
([security.md](security.md)).

**The key ring is DPAPI-protected with this machine's key.** Machine-scope DPAPI keeps a copy from being used on
another machine; on this machine **every** account can decrypt it, so the folder's ACL is what keeps other local users
out. A backup of `data\keys` restores on this machine (also
after reinstalling Telrang), not on another one. Moving to a new machine therefore loses the key ring: everyone signs
in again, 2FA (authenticator apps) must be set up again (an admin resets it, or the recovery key for the admin), link
voters cannot revise earlier ballots, and the SMTP password and captcha secret must be entered again. Keep the old
machine until the new one runs, and plan the move like that.

## Upgrade

Download and verify the new zip as in Install, then from the new folder, elevated:

```powershell
& ".\telrang-$v-win-x64\install-service.ps1"
```

It stops the service, backs up `data` to `C:\ProgramData\Telrang\backups`, replaces `C:\Program Files\Telrang`, keeps
configuration and data, and starts it. Migrations
run at start: back up first.

## Uninstall

```powershell
& ".\telrang-$v-win-x64\uninstall-service.ps1" -RemoveProgram
# data and configuration stay; to remove them as well:
Remove-Item -Recurse -Force C:\ProgramData\Telrang
```

The Event Log source `Telrang` stays so earlier events remain readable; remove it with
`[System.Diagnostics.EventLog]::DeleteEventSource('Telrang')`.

## Troubleshooting

- Logs: Event Viewer → Windows Logs → Application, source `Telrang` (warnings and errors), or
  `Get-EventLog -LogName Application -Source Telrang -Newest 20 | Format-List`.
- More detail: stop the service and run `& 'C:\Program Files\Telrang\Telrang.Web.exe'` in an elevated console; it reads
  the same configuration and logs everything to the console.
- Service stops right after start: an Error event `Telrang cannot start: ...` names the cause (exit code 2:
  configuration, 3: database).
- Health: `Invoke-WebRequest http://127.0.0.1:8080/healthz` or `& 'C:\Program Files\Telrang\Telrang.Web.exe' healthcheck`.
- `The data directory ... is open to ...`: run `install-service.ps1` again; it resets the ACLs.
- Everyone signed out and 2FA gone after a move or restore: the key ring came from another machine (see Backup).
