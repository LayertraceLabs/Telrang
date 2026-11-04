#!/usr/bin/env sh
# Browser smoke tests (DESIGN.md §24.7): publishes the app (Release) and runs tests/e2e against it in headless Firefox.
#
#   scripts/e2e.sh                  # publish, run every flow
#   scripts/e2e.sh --no-build       # reuse the last publish
#   scripts/e2e.sh --only editor,vote-anonymous   # these flows (plus the ones they need)
#   scripts/e2e.sh --app dist/x/Telrang.Web       # a built app instead: a release folder's executable or a .dll
#   scripts/e2e.sh --image telrang:1.2.0          # the Docker image (docker run, DESIGN.md §29)
#
# Needs Node 22+ and Firefox (dev-only tools; FIREFOX=/path/to/firefox if it is not on PATH).
# E2E_ARTIFACTS sets where app.log, failure screenshots and results.json go (default: a new temp directory).
# The app gets this environment: Database__Provider=Postgres ConnectionStrings__Default=<empty database> runs the flows
# on PostgreSQL (DESIGN.md §28).
# Exit code: 0 when every flow passed.
set -eu
cd "$(dirname "$0")/.."

build=yes
target=
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) build=no; shift ;;
    --app) [ $# -ge 2 ] || { echo "e2e: --app needs a path" >&2; exit 2; }; build=no; target="--app $2"; shift 2 ;;
    --image) [ $# -ge 2 ] || { echo "e2e: --image needs a name" >&2; exit 2; }; build=no; target="--image $2"; shift 2 ;;
    *) break ;;
  esac
done

work=${E2E_WORK:-${TMPDIR:-/tmp}/telrang-e2e-build}
artifacts=${E2E_ARTIFACTS:-$(mktemp -d "${TMPDIR:-/tmp}/telrang-e2e-XXXXXX")}
mkdir -p "$artifacts"

command -v node >/dev/null || { echo "e2e: node not found (Node 22+ is needed)" >&2; exit 2; }
"${FIREFOX:-firefox}" --version >/dev/null 2>&1 || { echo "e2e: firefox not found (set FIREFOX)" >&2; exit 2; }

if [ "$build" = yes ]; then
  echo "== publish (Release) -> $work/app"
  rm -rf "$work/app"
  dotnet publish src/Telrang.Web/Telrang.Web.csproj -c Release -o "$work/app" --nologo -v q -p:UseAppHost=false
fi
if [ -z "$target" ]; then
  [ -f "$work/app/Telrang.Web.dll" ] || { echo "e2e: no build in $work/app; run without --no-build" >&2; exit 2; }
  target="--app $work/app/Telrang.Web.dll"
fi

echo "== e2e ($("${FIREFOX:-firefox}" --version 2>/dev/null), node $(node --version))"
start=$(date +%s)
status=0
# shellcheck disable=SC2086 # $target is an option and its value (paths without spaces)
node tests/e2e/run.mjs $target --artifacts "$artifacts" "$@" || status=$?
echo "== e2e took $(( $(date +%s) - start )) s, exit $status; artifacts in $artifacts"
exit $status
