# Upgrade fixtures

One directory per release (DESIGN.md §32, "Upgrade safety"): the data a live instance of that release holds, made by
that release's code. `MigrationUpgradeTests` starts the current app on each of them, on SQLite and (with
`TELRANG_TEST_POSTGRES`) on PostgreSQL, lets migrations and the startup steps run, and checks that accounts sign in
(password, authenticator, recovery code), addresses decrypt and are found, polls open with the results the release
computed, invite links and an anonymous voter's cookie still find their ballots, the change log renders, the SMTP
password decrypts, queued mail goes out and a closed poll mails its subscriber.

| File | What |
| --- | --- |
| `app.db` | SQLite database (`App:DataDir/app.db`) |
| `keys/` | the instance's Data Protection key ring, `voterkey`, `emailkey` (addresses, 2FA secrets, the SMTP password and voter cookies are encrypted with them) |
| `branding/` | uploaded branding images |
| `postgres.sql` | the same data after `copy-to-postgres`, as `pg_dump --inserts` plain SQL (restores into an empty PostgreSQL 17+ database with any user) |
| `manifest.json` | what is in it: accounts with passwords, 2FA keys and recovery codes, polls with expected results and change logs, invite tokens, a voter cookie, row counts |

Everything is test data: `example.com` addresses, the test password, an SMTP password made up for the fixture.

## Making the fixture of a release

Part of the release checklist, before tagging: on the release commit, with a PostgreSQL 17 server for the
dump (the dump comes from the server's own `pg_dump`, so it restores on PostgreSQL 17):

```
docker run -d --rm --name telrang-fixture-pg -e POSTGRES_PASSWORD=fixture -p 55440:5432 postgres:17
dotnet build
TELRANG_MAKE_UPGRADE_FIXTURE=1.2.0 \
TELRANG_TEST_POSTGRES="Host=localhost;Port=55440;Username=postgres;Password=fixture" \
TELRANG_PG_DUMP="docker exec telrang-fixture-pg pg_dump -U postgres" \
  dotnet test tests/Telrang.Web.Tests --no-build --filter "FullyQualifiedName~UpgradeFixtureGenerator"
docker stop telrang-fixture-pg
dotnet test tests/Telrang.Web.Tests --filter "FullyQualifiedName~MigrationUpgradeTests"
git add tests/Telrang.Web.Tests/Fixtures/upgrade/1.2.0
```

`TELRANG_PG_DUMP` defaults to `pg_dump` on the PATH (connected with the host, port, user and password of
`TELRANG_TEST_POSTGRES`); its major version must not be newer than 17, or the dump may not restore on 17. Without
`TELRANG_TEST_POSTGRES` the generator writes no `postgres.sql`, and the PostgreSQL test fails for that fixture.

The generator (`UpgradeFixtureGenerator` in `UpgradeFixtures.cs`) builds the data through the app's services and pages, so
it changes with the code; once committed, a fixture is never regenerated or edited (it stands for what that release
wrote). Ids, keys and timestamps are random on every run; the manifest's structure, results and change logs are the same,
so a regenerated manifest diffs readably.

A tagged CI build (`GITHUB_REF_TYPE=tag`) fails when the tag's version has no fixture, or when that fixture was made with
an older schema than the code.

## When a check fails

A fixture failing after a change means an instance of that release would lose or break that data on upgrade. Fix the
migration or the startup step, not the fixture. Only a change that removes data on purpose (documented in DESIGN.md)
changes the expectation, and then in `MigrationUpgradeTests`, per fixture version, never in the fixture.
