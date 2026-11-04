# Performance

Measured on 2026-11-02 at commit `7b4de58`, with SQLite and with PostgreSQL 17 on the same machine, the same seeded data
and the same build. The numbers are from one machine. Treat them as an order of magnitude for sizing a server, not as
a guarantee.

Three setups were measured:

- **SQLite**: the default, the database file on the app's disk.
- **PostgreSQL, own cores**: PostgreSQL pinned to four other cores, as with a separate database host.
- **PostgreSQL, same cores**: PostgreSQL sharing the app's cores, as on one small server.

## Summary

App on 4 performance cores of a desktop-class 8-P-core machine; throughput is the plateau, in requests per second.

| | SQLite | PostgreSQL, own cores | PostgreSQL, same cores | Estimate per 3 GHz server core (app) |
|---|---|---|---|---|
| Vote page views | ~4,800/s | ~5,250/s | ~3,300/s | ~650/s |
| Results page, 100 ballots | ~2,850/s | ~3,100/s | ~2,500/s | ~400/s |
| Results page, 10,000 ballots | ~50/s (48 ms each) | ~57/s (38 ms each) | ~55/s | ~9/s (0.1 s each) |
| Results page, 100,000 ballots | ~4/s (0.55 s each) | ~5/s (0.38 s each) | ~5/s | ~0.5/s (2–2.5 s each) |
| Complete votes, different polls | **50–125/s** whatever the core count (disk sync) | **750–1,000/s** | **570–620/s** | SQLite ~50–100/s in all; PostgreSQL ~100/s per app core |
| Complete votes, all on one poll | 30–120/s (same limit) | ~250/s (one poll's row lock) | ~230/s | |
| App memory, idle | ~190–200 MB | same | same | same |
| App memory, under load | 220–550 MB; results of large polls and the home page far more (see "Memory") | same | same | same |
| Database server memory | none (in the app) | 150 MB idle, ~330 MB at 100 connections | same | same |
| Storage per account / poll / ballot | ~0.5 KB / ~1.7 KB / ~0.6 KB | ~0.6 KB / ~1.7 KB / ~0.5 KB | same | same |

- **The difference is votes.** SQLite writes one vote at a time, and every vote waits for the disk to confirm it:
  50–125 votes/s here, on 1 core as on 4. PostgreSQL commits votes on different polls in parallel and groups their
  disk syncs, so votes become CPU work: about 1,000 votes/s on 4 app cores, scaling with cores. Votes on one poll
  still go one at a time (a row lock per poll, DESIGN.md §28), at about 250/s.
- **Reads are about the same.** Page views and results pages cost the app's CPU on both. With PostgreSQL on its own
  cores the app has a little more CPU left (+5–10 %); sharing the cores costs page views a third (4 cores) to a half
  (1 core), since PostgreSQL then takes about one core of the app's at full load.
- **Results are computed on every view** (ballots are stored raw, never as totals). The cost is linear in the number
  of ballots: about 10 µs per ballot on one core here, 4–5 µs on four. PostgreSQL loads the rows faster than SQLite,
  so large results pages are 15–30 % faster with it; the parsing and scoring in the app is the same.
- **Multi-threading:** yes. Page views scale almost linearly: 4 cores serve 3.6× as many as 1 (table below).

## Throughput and scaling with cores

App pinned to 1–4 physical performance cores (one thread each). Plateau reached with 16–256 simultaneous clients,
in requests per second (complete votes per second for the vote columns). "own" = PostgreSQL on its own 4 cores,
"same" = PostgreSQL on the app's cores.

| App cores | Vote page SQLite / own / same | Results, 100 ballots | Results, 10k ballots | Results, 100k ballots | Home (any database) |
|---|---|---|---|---|---|
| 1 | 1,320 / 1,190 / 630 | 775 / 820 / 535 | 17 / 22 / 18 | 0.7–1 / 0.9–1.6 / 1–1.2 | 720–780 |
| 2 | 2,490 / 2,820 / 1,660 | 1,480 / 1,650 / 1,240 | 31 / 39 / 30 | <1 / 2 / 2 | 1,330 |
| 3 | 3,670 / 4,040 / 2,420 | 2,200 / 2,370 / 1,750 | 43 / 52 / 49 | 3 / 3 / 2.5 | 1,800 |
| 4 | 4,840 / 5,250 / 3,300 | 2,850 / 3,100 / 2,520 | 50–58 / 56–58 / 55 | 4 / 5 / 5.5 | 3,100–3,200 |

| App cores | Votes, different polls SQLite / own / same | Votes, one poll SQLite / own / same |
|---|---|---|
| 1 | 65–125 / 180–230 / 96–108 | 65–120 / 175–190 / 130–137 |
| 2 | 60–90 / 480–540 / 290–300 | 75–95 / 240 / 215 |
| 3 | 22–120 / 690–775 / 440–460 | 30–125 / 245 / 230 |
| 4 | 50–125 / 750–1,010 / 570–620 | 30–120 / 250–260 / 225–240 |

- **SQLite votes do not grow with cores.** Every vote waits for a disk sync (5–7 ms per vote with one voter at a
  time), and a second writer sleeps 150 ms when it finds the database busy (see "Known bottlenecks"). The spread
  between runs is the disk: the same run gave 22 or 120 votes/s depending on the desktop's other disk traffic. With
  the database on tmpfs (RAM) SQLite reached ~550 votes/s on 4 cores, so the disk, not the CPU, is the limit.
- **PostgreSQL votes grow with cores.** With PostgreSQL on its own cores, the app was the busy side (3.4 of 4 cores
  at ~1,000 votes/s, PostgreSQL 1.0–1.5 cores). On shared cores the two divide them: on 1 core PostgreSQL gives the
  same ~100 votes/s as SQLite and halves page views, so on one core SQLite is the better choice.
- **One poll at a time.** Votes on one poll wait for that poll's row lock, which is held for the whole write
  transaction including the commit's disk sync: ~4 ms each, so ~250/s whatever the core count.
- **One core is worse than half of two.** On one core the app serves its requests, garbage collection and hundreds of
  connections on the same thread, and latency spreads out.
- **Compared with 2026-11-01** (commit `146bc17`, SQLite, same machine): vote pages ~10 % fewer (the page grew),
  votes the same. Results pages are 2–3× slower (results with 100 ballots 2,850/s instead of 6,800/s; 10,000 ballots
  48 ms instead of 20 ms): the results page grew with the results settings and ballot table (§25), the change log
  (§26), and every stored ballot is now filtered against the current options (§26), which rebuilds the option set for
  every ballot (see "Known bottlenecks"). The home page went from 20,500/s to ~3,200/s: it now carries the static
  showcase of the three poll types (117 KB of HTML per view).

## Response times

One visitor at a time (server time, no network), app on 4 cores; in brackets on 1 core:

| Request | SQLite | PostgreSQL |
|---|---|---|
| Vote page | 0.8–1 ms | 1.1–1.3 ms |
| Results, 100 ballots | 2 ms (3.8 ms) | 1.5 ms (3.6 ms) |
| Results, 10,000 ballots | 48 ms (130 ms) | 38 ms (106 ms) |
| Results, 100,000 ballots | 0.55 s (1.3 s) | 0.38–0.40 s (1.1–1.2 s) |
| Saving a vote (POST) | 5.6–6.8 ms, mostly the disk sync | 6.7–7 ms, mostly the disk sync |
| Complete vote (5 requests) | 10–15 ms | 12–14 ms (19 ms on 1 shared core) |

Under load, response time grows with the queue. Percentiles with 4 app cores (PostgreSQL on its own cores), in ms:

| Load | SQLite p50 / p95 / p99 | PostgreSQL p50 / p95 / p99 |
|---|---|---|
| Vote page, 64 clients | 13 / 16 / 21 | 12 / 18 / 21 |
| Vote page, 256 clients | 54 / 64 / 73 | 55 / 72 / 81 |
| Results 100 ballots, 64 clients | 22 / 27 / 34 | 20 / 36 / 45 |
| Complete vote, 16 voters at once | 75–100 / 810–960 / 1,250–1,600 | 18–62 / 22–78 / 24–100 |
| Complete vote, 64 voters at once | 500–900 / 1,800–3,500 / 3,100–4,400 | 63–86 / 74–107 / 82–123 |
| Complete vote, 256 voters at once | 1,900–2,400 / 3,700–4,400 / 4,700–5,200 | 285 / 315–325 / 330–345 |
| Complete vote on one poll, 64 voters | 690–770 / 2,100–2,400 / 3,100–3,300 | 245–250 / 250–260 / 255–270 |

With SQLite even a few simultaneous voters wait: the median vote stays short, but one in twenty takes about a second.
With PostgreSQL vote times stay even and grow with the queue only.

In a real browser (Firefox, same machine, measured 2026-11-01 with SQLite; the database makes no visible difference at
one visitor):

| Page | First visit | Repeat visit |
|---|---|---|
| Vote page | ~190 ms (one redirect for the voter cookie, ~20 KB HTML, ~125 KB of CSS/JS) | ~50 ms |
| Results, 100 ballots | — | ~40 ms |
| Results, 10,000 ballots | — | ~85 ms |
| Home | — | ~20 ms |

Over the internet, add a few round trips. On a typical 30–50 ms connection a first visit to a vote page shows in
about half a second.

## What "concurrent users" means here

Voters hold no connection and no server memory between requests: the vote page is plain HTML and a form post.
A voter costs only CPU for the few requests they make. A typical voter makes about six requests over a minute or two
(open the link, the page, submit, the page after, the results), one of them a write.

At half load, so pages stay fast, one 3 GHz core handles the page views of a few thousand people voting at the same
moment. Saving votes:

- **SQLite:** shared by the whole server, ~50–100 votes per second, **about 3,000–6,000 votes per minute** on a fast
  SSD, far fewer on an SD card or a network disk.
- **PostgreSQL:** ~100 votes per second per 3 GHz app core with PostgreSQL on its own cores (half that when they share
  the cores), **about 20,000–25,000 votes per minute on 4 cores**, but ~10,000–15,000 per minute into any one poll.

Memory is not what limits voters.

Poll owners and admins are different. Their pages (editor, My polls, admin) hold a live connection (a Blazor
circuit) for as long as the tab is open. Each one takes about 0.5 MB of server memory, so **each extra GB of RAM
holds about 1,500–2,000 open owner or admin tabs**. The app limits these to 20 per account and 60 per address by
default.

## Memory

Resident memory of the app process (peak during each 8 s run) and of PostgreSQL (anonymous plus shared memory of its
container, so shared buffers count once):

| Situation | App | PostgreSQL |
|---|---|---|
| Idle after start | 190–200 MB (same on both) | ~150 MB (128 MB of it the default shared buffers) |
| Vote pages, results of small polls, 1–4 cores, up to 256 clients | 210–300 MB (SQLite), 210–450 MB (PostgreSQL) | 150–300 MB |
| Votes, 256 at once | 270–370 MB (SQLite), 400–600 MB (PostgreSQL) | ~330 MB (up to 100 connections) |
| Results of a 10,000-ballot poll, 16–256 at once | 450 MB–1 GB (once 1.9 GB) | ~170–300 MB |
| Results of a 100,000-ballot poll: 1 / 4 / 16 at once | ~0.7 / ~1.4 / 2.3–3.3 GB | ~150–170 MB |
| Home page, 256 at once | 1–3 GB | — |

- Each view of a 100,000-ballot poll holds all its ballots in memory while it computes, roughly 150–200 MB. Many
  simultaneous views of a very large poll are the one thing that needs a lot of RAM.
- The home page's 117 KB of HTML is built per request; at 256 simultaneous home page views the managed heap grew to
  1–3 GB before the garbage collector caught up (on a 64 GB machine the .NET server GC collects late). Under a
  container memory limit the GC collects earlier: on 2026-11-01 the app ran with a 64 MB heap limit
  (`DOTNET_GCHeapHardLimit`) and with workstation GC, both at ~70–80 % of the throughput.
- Memory grows with cores (one GC heap per core).
- PostgreSQL's memory grows with its connections (about 2 MB each here) and with `shared_buffers`; the operating
  system's file cache comes on top for both databases and keeps reads fast.

## Recommended hardware

| | CPU | RAM | Disk | Database |
|---|---|---|---|---|
| Family, a club, ~10 people | 1 core | 512 MB | 1 GB | SQLite. A small VPS or a Raspberry Pi 4/5 class board. An SD card makes votes slower (slow sync) but fine at this size |
| School or company, ~1,000 people | 2 cores | 1 GB | 5 GB SSD | SQLite. Plenty of headroom; voting a whole school in one break is ~1,000 votes in minutes |
| Many people voting at the same moment (more than ~50 votes per second, e.g. 10,000 people within a few minutes) | 4 cores for the app, 2 for PostgreSQL | 2 GB + 1 GB for PostgreSQL | 20 GB SSD, more for PostgreSQL's WAL (up to ~1 GB) | PostgreSQL, ideally on its own cores or host |
| ~1,000,000 accounts or ballots | 4–8 cores | 4–8 GB | 20 GB NVMe | Either. Storage and accounts fit (about 0.5–2 GB of database). PostgreSQL if votes come in bursts. A single poll with hundreds of thousands of ballots has slow results pages (about 1 s per view per 100,000 ballots on a 3 GHz core, 150–200 MB of memory per simultaneous view) whatever the database. One instance only; there is no clustering |

**When PostgreSQL is worth it:** when many votes arrive at once. SQLite saves ~50–125 votes per second on a good SSD
whatever the hardware, and under contention some voters wait a second or more. PostgreSQL turns that into CPU work
(~1,000 votes/s on 4 cores here, ~250/s into one poll) with even response times. Also when the data should live in a
database that other tools can query, or on another host.

**When SQLite is enough:** nearly everywhere else. It needs no second service and no extra memory, page views and
results nearly as fast, and on a 1–2 core server where PostgreSQL would share the cores, SQLite serves a third to
half more page views (on 1 core at the same vote rate; on 2 cores PostgreSQL still saves ~300 votes/s against ~60–90).

## Storage

Measured from the seeded database (1,000 accounts, 1,003 polls, 110,100 ballots): SQLite's `dbstat` per table, and
PostgreSQL's `pg_total_relation_size` (table, indexes and TOAST) after `VACUUM ANALYZE`:

| Item | SQLite | PostgreSQL | 1 million of them |
|---|---|---|---|
| Account (email, password hash, no passkey) | ~0.50 KB | ~0.63 KB | ~500–630 MB |
| Poll (5 options, a short description; with its creation record) | ~1.7 KB | ~1.7 KB | ~1.7 GB |
| Ballot (ranked, 10 options, a voter name) | ~0.62 KB (10,000 → 6 MB, 100,000 → 62 MB) | ~0.53 KB (10,000 → 5.3 MB, 100,000 → 53 MB) | ~530–620 MB |
| Whole seeded database | 70 MB file | 66 MB (`pg_database_size`) | |

Accounts with 2FA and passkeys, polls with many options or long descriptions, and ballots for larger polls take
more. SQLite adds its write-ahead log (a few MB while running); PostgreSQL its WAL directory (128 MB after these runs,
up to `max_wal_size`, 1 GB by default, plus dead rows until autovacuum reclaims them). Add the mail outbox, branding
images and backups. Retention ("Delete closed polls after") keeps the database from growing forever.

## Moving to PostgreSQL: `copy-to-postgres` at scale

`copy-to-postgres --from app.db` on the seeded 70 MB file (1,000 accounts, 1,003 polls, 110,100 ballots) took
**6.4–6.7 s** in three runs: 0.4 s for the migrations, 4.7–5.0 s for the ballots (~23,000 per second), the rest
startup, the other tables and the row-count check. The app process used about 6.5 s of CPU, PostgreSQL about 2 s.
At that rate a million ballots copy in about a minute.

On this machine every new PostgreSQL connection first took 7 s (see "Known bottlenecks"), which made the same copy
take 42 s; `GSS Encryption Mode=Disable` in the connection string removed the delay.

## How it was measured

- **Hardware.** A desktop-class machine with 8 performance and 12 efficiency cores (performance cores up to 5.5 GHz,
  64 GB RAM), PCIe 4 NVMe SSD, Linux. The machine had its usual desktop workload running alongside; its disk traffic
  is visible in the SQLite vote numbers. A 4 KB synchronous append on the data disk took 9–18 ms during the runs
  (`dd oflag=dsync`); PostgreSQL's `pg_test_fsync` measured 3.3 ms per `fdatasync` on its volume.
- **CPU split** (from `lscpu --extended`: P-cores are CPUs 0–15, two threads each; E-cores 16–27). The app was pinned
  with `taskset` to 4, 3, 2 and then 1 performance core (CPUs 0,2,4,6, one thread per core). PostgreSQL on its own
  cores: four more performance cores (CPUs 8,10,12,14, `docker update --cpuset-cpus`); on shared cores: the app's
  CPUs. The load generator ran as eight processes on eight efficiency cores (CPUs 16–23).
- **Load generator headroom.** It sustained ~26,700 requests/s against the trivial `/healthz` endpoint (4 app cores).
  The app's pages peaked far below that, so the numbers measure the app, not the client.
- **Build.** Release, `ASPNETCORE_ENVIRONMENT=Production`, plain HTTP on loopback, no reverse proxy, `TELRANG_CONFIG=none`.
- **Settings.** Rate limits and abuse thresholds were raised through configuration so they did not interfere
  (`RateLimiting__VotePermitPerMinute`, `Abuse__Threshold`, `Abuse__FormMinSeconds=0`), and the per-poll ballot limit
  was set to 0. The app's real defaults (120 vote posts per minute per address) cap any single address far below
  these numbers.
- **Databases.** Both on the same NVMe. SQLite: the app's data directory as shipped (WAL mode, default sync).
  PostgreSQL 17.11 in Docker (`postgres:17`, data on a Docker volume, defaults: `synchronous_commit=on`, `fsync=on`,
  `shared_buffers=128MB`, `max_connections=100`), reached over the Docker bridge at the container's address as from
  another container; Npgsql's default pool (up to 100 connections). Every configuration started from a fresh copy
  of the same seeded data (SQLite: a copy of the file; PostgreSQL: `CREATE DATABASE … TEMPLATE` of the copied
  database, then `VACUUM ANALYZE`), and the app was restarted for every scenario.
- **Seed data.** 1,000 accounts and 1,000 polls (ranked, 5 options, a short description) created through the app's
  own services (`UserManager`, `PollService`), plus ranked polls with 10 options and 100, 10,000 and 100,000 ballots
  (full rankings with a voter name; rows of the exact shape `BallotService` writes, inserted in bulk). The PostgreSQL
  database is a `copy-to-postgres` of that SQLite file.
- **Load generator.** Node with keep-alive connections and no packages, 8 s per run after a 3 s warm-up, 1, 4, 16, 64
  and 256 simultaneous clients (16–256 for 2 and 3 cores; at most 16 for the 100,000-ballot results page).
  - A "vote" is a fresh visitor doing what a browser does: the first visit and the cookie check (two redirects that
    set the voter cookie), the form, the POST with a full ranking and a name, and the page after the redirect.
    "Different polls" picks one of the 1,000 polls at random per vote; "one poll" always the same one.
  - Vote page views carry a voter cookie (as after the first visit) and pick a poll at random.
  - Latency is per request (per complete vote for votes); throughput is the plateau.
  - Noisy runs (all SQLite vote runs, PostgreSQL votes on 4 cores) were repeated; ranges show the spread.
- **Memory.** Resident memory (peak) of the app process; PostgreSQL as anonymous plus shared memory of its container's
  cgroup. Per-tab memory (0.5 MB) is from 2026-11-01: 10–200 home-page tabs in headless Firefox.
- **Results cost.** Split on one core with the app's own code: for 10,000 ballots, loading the rows takes 20 ms with
  SQLite and 13 ms with PostgreSQL, parsing them 51–67 ms (deserialising 32–34 ms, filtering against the current
  options the rest), scoring 16 ms.
- **"3 GHz core."** The estimate column assumes a dedicated 3 GHz server core does about half the work of the test
  core. A shared VPS vCPU (one hyperthread) can be slower still.

## Known bottlenecks (possible improvements)

1. **Waiting SQLite writers sleep 150 ms.** When two votes are written at the same moment, the SQLite driver makes the
   second one sleep a fixed 150 ms before retrying. This shows as p95 vote times of 0.8–1.3 s with only 16 voters at
   once (still so on 2026-11-02). Setting SQLite's own `busy_timeout`, or queueing writes in the app, would remove the
   stall.
2. **Every vote waits for a disk sync.** With SQLite that caps votes at 50–125/s here, whatever the cores, and lower
   on slow disks. SQLite's `synchronous=NORMAL` in WAL mode would multiply write throughput. The trade-off: a power
   cut (not an app crash) could lose the last votes that were already confirmed to voters. Not changed; it is a
   durability decision. PostgreSQL (DESIGN.md §28) avoids both, measured: votes on different polls are written in
   parallel and their syncs grouped (750–1,000 votes/s on 4 app cores, scaling with cores); votes on one poll go one
   at a time under the poll's row lock (~250/s, each holding the lock through its commit's sync); every other write
   transaction runs alone under the writer lock.
3. **Fixed (2026-11-02, DESIGN.md §31): results were recomputed on every view.** About 10 µs per ballot on one core,
   and 150–200 MB of memory per simultaneous view of a 100,000-ballot poll. The computed results are now cached per
   poll in memory (`ResultsCache`, dropped by `PollChangeNotifier`), ballot tables are paged at 50 rows, and
   `BallotFilter.For` builds the option key set once per view instead of once per ballot. Not re-measured with the
   tables above; a re-test during the third pentest round (1,000 ballots × 100 options, 16 clients) confirmed the fix.
4. **Fixed (2026-11-02, DESIGN.md §31): the home page was 117 KB of HTML built per request.** ~3,200 views/s on 4
   cores, and at 256 simultaneous views the app's memory grew to 1–3 GB. The examples are now rendered once a day
   (`ShowcaseCache`) and page GETs have a per-client limit (600/min). Re-test, 256 clients from one address with the
   limit lifted, 2 GB heap: 11,200 views/s, memory peak 480 MB.
5. **PostgreSQL connections: pool size and slow connects.** Npgsql's default pool (100 connections) equals
   PostgreSQL's default `max_connections` (100, of which 3 are reserved for superusers). With 256 simultaneous voters
   the pool fills up; vote throughput then fell 10–30 % against 64 voters (waiting for a pooled connection on top of
   the row locks). A role that is not a superuser, or a second client such as `pg_dump`, can then be refused
   ("too many clients"). Fixed (DESIGN.md §31): the pool defaults to 30 unless the connection string sets `Maximum Pool
   Size`, and the app warns at startup when the pool exceeds the server's free connections. Separately, on this machine (Kerberos libraries installed) every new
   connection first took 7 s, because Npgsql tries GSS encryption before a plain connection; `GSS Encryption
   Mode=Disable` in the connection string removed it. The GSS delay is connection-string advice (docs/deploy, "PostgreSQL").
6. **Fixed (2026-11-01, DESIGN.md §23.2):** the home page opened a live connection (Blazor circuit) per visitor,
   although it had nothing interactive for them. It is now static for everyone (no circuit, no WebSocket); its
   examples run in the browser.
