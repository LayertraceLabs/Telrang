#!/usr/bin/env sh
# Run the web app in Debug with the Development environment.
#
#   scripts/run-debug.sh            # http://localhost:5099
#   scripts/run-debug.sh --https    # https://localhost:7045 (dev cert) + http
#   scripts/run-debug.sh --fresh    # wipe ./src/Telrang.Web/data first (new setup key)
#
# Any other arguments are passed to `dotnet run` unchanged.
# The admin setup key is printed in the log on first start.
set -eu
cd "$(dirname "$0")/.."

profile=http
for arg in "$@"; do
  case "$arg" in
    --https) profile=https; shift ;;
    --fresh) rm -rf src/Telrang.Web/data; shift ;;
  esac
done

exec dotnet run --project src/Telrang.Web -c Debug --launch-profile "$profile" "$@"
