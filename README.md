# Telrang

Telrang (Dutch *tel*, count, and *rang*, rank) is a self-hosted voting app for friends, family, clubs and schools: an
independent alternative to ad-funded poll services. One container, one SQLite file, no tracking.

- **Poll types:** ranked choice (with optional bans), point distribution, and date finding (who can when).
- **Voters need no account.** Polls are reached by an unguessable link, or by personal invite links.
- **Results:** shown to the owner only, to voters after they vote, or to everyone. The owner's results page updates
  live.
- **Works without JavaScript** for voters; the vote page is a plain form.
- **Admin page:** users, polls, limits, mail (optional), branding and theme, abuse blocks, status, backups.
- **Security:** 2FA for accounts, passkeys, rate limits, a strict Content Security Policy, and no client IP
  addresses written to disk.

Built with .NET 10 (Blazor), EF Core and SQLite, or PostgreSQL for busy instances (DESIGN.md §28, deploy notes "PostgreSQL").

## Running it

Three install formats, the same program and configuration in each: Docker image, Linux (systemd) service, Windows
service. See [docs/deploy/](docs/deploy/README.md) for installing ([Docker](docs/deploy/docker.md),
[Linux](docs/deploy/linux.md), [Windows](docs/deploy/windows.md)), reverse proxy (Caddy, nginx), configuration
and [what each part reveals](docs/deploy/security.md).

## Performance at a glance

Measured on one machine and scaled to a 3 GHz server core. Details, method and caveats are in
[docs/PERFORMANCE.md](docs/PERFORMANCE.md).

| Per 3 GHz server core | SQLite (default) | PostgreSQL |
|---|---|---|
| Vote page views | ~650/s | about the same (fewer if it shares the app's cores) |
| Votes saved | ~50–100/s for the whole server (limited by the disk) | ~100/s per app core (~1,000/s on 4 fast cores measured); ~250/s into one poll |
| People voting at the same moment | a few thousand | tens of thousands |
| Results page, 10,000-ballot poll | ~0.1 s (grows linearly with ballots) | 15–30 % faster |

- **Multi-core:** page views scale almost linearly (4 cores: 3.6× one core). With SQLite votes stay at the disk's
  sync rate whatever the cores; with PostgreSQL they scale with cores.
- **Which database:** SQLite unless many votes arrive at once (more than ~50 per second); PostgreSQL then, ideally on
  its own cores. On a server with 1–2 cores shared with PostgreSQL, SQLite serves a third to half more page views.
- **Memory:** about 200 MB idle and 250–550 MB under load, plus ~150–350 MB for PostgreSQL. Results views of very
  large polls (100,000 ballots) take 150–200 MB each while they run. Voters cost no memory between requests. Each
  open owner or admin tab costs ~0.5 MB.
- **Storage:** about 0.5 KB per account, 1.7 KB per poll, 0.5–0.6 KB per ballot on either database. A million ballots
  is about 550 MB.

| Size | CPU | RAM | Disk |
|---|---|---|---|
| ~10 people | 1 core | 512 MB | 1 GB |
| ~1,000 people | 2 cores | 1 GB | 5 GB SSD |
| Many votes at once (>50/s) | 4 cores + 2 for PostgreSQL | 2 GB + 1 GB | 20 GB SSD |
| ~1,000,000 accounts or ballots | 4–8 cores | 4–8 GB | 20 GB NVMe (see the limits in PERFORMANCE.md) |

## Development

```sh
dotnet build
dotnet test tests/Telrang.Core.Tests
dotnet test tests/Telrang.Web.Tests
scripts/run-debug.sh          # http://localhost:5099
scripts/e2e.sh                # browser smoke tests: publishes, runs tests/e2e in headless Firefox
```

The browser smoke tests need Node 22+ and Firefox (set `FIREFOX` if it is not on the PATH); both are only needed
for development and CI. They fail on any server error in the log, browser console error, CSP violation or dropped
circuit, and leave the app log and failure screenshots in a temp directory (`E2E_ARTIFACTS` to choose one).

The specification and decision log is [DESIGN.md](DESIGN.md).
