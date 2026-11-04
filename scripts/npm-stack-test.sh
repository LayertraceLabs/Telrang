#!/usr/bin/env bash
# The Docker + Nginx Proxy Manager setup end to end (DESIGN.md §32): the hardened image from docker/compose.npm.yml
# (PostgreSQL), behind a real jc21/nginx-proxy-manager configured through its API with a self-signed certificate, then
#   1. docker/compose.yml (SQLite, hardened) starts, gets healthy and stops; copy-to-postgres moves its database
#      (docs/deploy/docker.md, "Moving an existing instance from SQLite") into compose.npm.yml's db service;
#   2. the e2e flows through NPM's HTTPS, plus the mail flow against a fake SMTP server on the app's
#      outbound network;
#   3. the go-live outside checks and the container hardening.
#
#   scripts/npm-stack-test.sh                     # build the image from this checkout, run everything
#   scripts/npm-stack-test.sh --image telrang:1.2.0   # an existing image (not removed afterwards)
#   scripts/npm-stack-test.sh --fresh             # skip step 1: start compose.npm.yml on an empty database
#   scripts/npm-stack-test.sh --only setup,vote   # e2e flows to run (plus the ones they need); --no-e2e for none
#
# Needs Docker (compose v2.23+), curl, openssl, certutil (NSS tools: the certificate for Firefox), Node 22+ and Firefox
# (as scripts/e2e.sh). NPM listens on 127.0.0.1:80 and
# 127.0.0.1:443, which must be free (the app builds links from the Host header without a port, so 443 it is). The site is
# https://vote.telrang.test (NPM's own default server owns "localhost"); curl, Node and Firefox are told it is 127.0.0.1.
# Every container, network, volume and image tag it creates is named telrang-npmtest-<random> and removed at the end, also
# on failure (NPM_STACK_HOLD=<seconds> keeps it up that long first, for debugging). Artifacts (app and NPM logs, e2e screenshots, results) stay in E2E_ARTIFACTS or the printed directory.
# Exit code 0 when every check passed.
set -euo pipefail
cd "$(dirname "$0")/.."
repo=$PWD

image=
fresh=no
e2e_args=()
run_e2e=yes
while [ $# -gt 0 ]; do
  case "$1" in
    --image) [ $# -ge 2 ] || { echo "npm-stack-test: --image needs a name" >&2; exit 2; }; image=$2; shift 2 ;;
    --fresh) fresh=yes; shift ;;
    --no-e2e) run_e2e=no; shift ;;
    --only) [ $# -ge 2 ] || { echo "npm-stack-test: --only needs flows" >&2; exit 2; }; e2e_args+=(--only "$2"); shift 2 ;;
    *) echo "npm-stack-test: unknown argument $1" >&2; exit 2 ;;
  esac
done

# Pinned like the base images (DESIGN.md §31, DEP-12e).
NPM_IMAGE=jc21/nginx-proxy-manager:2.16.0@sha256:4393e642e233e5efd5adeb7f10918d4aaea71c9ed875b94190273fc8e87f9594
SMTP_IMAGE=axllent/mailpit:v1.31.6@sha256:23fe39e81bde596a918000b3135c0f85dc8bb4c9ea6be274b051143f3e300387
# The postgres image of compose.npm.yml, used here only to chown the secret files as root.
PG_IMAGE=$(sed -n 's/^ *image: \(postgres:.*\)$/\1/p' docker/compose.npm.yml | head -n 1)

id=telrang-npmtest-$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')
work=$(mktemp -d "${TMPDIR:-/tmp}/telrang-npmtest-XXXXXX")
artifacts=${E2E_ARTIFACTS:-$work/artifacts}
mkdir -p "$artifacts" "$work/proj/secrets"
octet=$(( $(od -An -N1 -tu1 /dev/urandom) % 200 + 20 ))
export TELRANG_PROXY_NETWORK=$id-proxy
export NPM_ADDRESS=10.254.$octet.2
export TELRANG_EGRESS_SUBNET=10.254.$octet.16/28   # room for the fake SMTP server next to the app
proxy_subnet=10.254.$octet.0/29
built=no
if [ -z "$image" ]; then image=$id:dev; built=yes; fi
export TELRANG_IMAGE=$image
export TELRANG_PORT=0
name=vote.telrang.test
H=https://$name
resolve=(--resolve "$name:443:127.0.0.1" --resolve "$name:80:127.0.0.1")
cert=$work/cert.pem   # the site's certificate, uploaded to NPM
ca=$work/ca.pem       # the test CA that signed it: what curl, Node and Firefox trust

failures=0
checks=0
check() { # check <name> <ok:0|1> [detail]
  checks=$((checks + 1))
  if [ "$2" = 0 ]; then echo "  ok   $1"; else failures=$((failures + 1)); echo "  FAIL $1${3:+: $3}"; fi
}
is() { [ "$1" = "$2" ]; }
has() { grep -qiE -- "$2" <<<"$1"; }
lacks() { ! grep -qiE -- "$2" <<<"$1"; }
t() { if "$@"; then echo 0; else echo 1; fi; } # a test's status as check's 0/1

compose_sqlite() { docker compose -p "$id" --project-directory "$work/proj" -f "$repo/docker/compose.yml" -f "$work/proj/override.yml" "$@"; }
compose_npm() { docker compose -p "$id" --project-directory "$work/proj" -f "$repo/docker/compose.npm.yml" -f "$work/proj/override.yml" "$@"; }

cleanup() {
  local status=$?
  set +e
  # Debugging: NPM_STACK_HOLD=<seconds> keeps the stack up that long before removing it (https://$name, --resolve).
  if [ -n "${NPM_STACK_HOLD:-}" ]; then echo "== holding the stack for $NPM_STACK_HOLD s"; sleep "$NPM_STACK_HOLD"; fi
  echo "== cleanup"
  docker logs "$id-app-1" >"$artifacts/app-container.log" 2>&1
  docker logs "$id-npm" >"$artifacts/npm.log" 2>&1
  docker exec "$id-npm" sh -c 'cat /data/nginx/proxy_host/*.conf' >"$artifacts/npm-proxy-host.conf" 2>/dev/null
  docker exec "$id-npm" sh -c 'cat /data/logs/proxy-host-*_access.log' >"$artifacts/npm-access.log" 2>/dev/null
  docker exec "$id-npm" sh -c 'cat /data/logs/proxy-host-*_error.log' >"$artifacts/npm-error.log" 2>/dev/null
  # The test's own containers first: the fake SMTP server sits on a compose network.
  docker rm -f "$id-npm" "$id-smtp" >/dev/null 2>&1
  compose_npm down -v --remove-orphans >/dev/null 2>&1
  compose_sqlite down -v --remove-orphans >/dev/null 2>&1
  docker volume rm "$id-npm-data" "$id-npm-le" >/dev/null 2>&1
  docker network rm "$id-proxy" >/dev/null 2>&1
  # The secret files belong to the containers' users now: give them back before deleting.
  [ -n "$PG_IMAGE" ] && docker run --rm -u 0 -v "$work/proj/secrets:/s" --entrypoint chown "$PG_IMAGE" -R "$(id -u):$(id -g)" /s >/dev/null 2>&1
  rm -rf "$work/proj" "$work"/*.pem "$work"/*.srl
  [ "$built" = yes ] && docker rmi "$image" >/dev/null 2>&1
  local left
  left=$(docker ps -a --filter "name=$id" -q; docker volume ls -q --filter "name=$id"; docker network ls -q --filter "name=$id")
  [ -n "$left" ] && echo "  leftovers: $left" >&2
  echo "== artifacts in $artifacts"
  exit $status
}
trap cleanup EXIT
trap 'exit 130' INT TERM

echo "== $id (proxy network $proxy_subnet, NPM $NPM_ADDRESS, work $work)"
if [ "$built" = yes ]; then
  echo "== build $image"
  docker build -q -f docker/Dockerfile -t "$image" . >/dev/null
fi

# ---- files ---------------------------------------------------------------------------------------------------------------
# A throwaway CA and a certificate it signs (Firefox does not take a self-signed server certificate as a trust anchor).
openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=Telrang npm-stack-test CA" \
  -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign \
  -keyout "$work/ca-key.pem" -out "$ca" 2>/dev/null
openssl req -new -newkey rsa:2048 -nodes -subj "/CN=$name" -keyout "$work/key.pem" -out "$work/req.pem" 2>/dev/null
printf 'subjectAltName=DNS:%s\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\n' "$name" >"$work/ext.cnf"
openssl x509 -req -in "$work/req.pem" -CA "$ca" -CAkey "$work/ca-key.pem" -CAcreateserial -days 2 \
  -extfile "$work/ext.cnf" -out "$cert" 2>/dev/null
rm -f "$work/req.pem" "$work/ext.cnf"
cat >"$work/proj/override.yml" <<'EOF'
# Test only: one log line per entry, so the e2e harness finds "fail:"/"crit:" lines and the setup key.
services:
  app:
    environment:
      Logging__Console__FormatterName: simple
      Logging__Console__FormatterOptions__SingleLine: "true"
EOF
# The secrets exactly as compose.npm.yml's header says (owner and mode included).
(
  cd "$work/proj"
  umask 077
  pw=$(openssl rand -hex 32)
  printf '%s' "$pw" >secrets/db_app_password
  printf 'Host=db;Database=telrang;Username=telrang;Password=%s;Maximum Pool Size=30;GSS Encryption Mode=Disable' "$pw" >secrets/telrang_db
  openssl rand -hex 32 | tr -d '\n' >secrets/db_superuser_password
)
docker run --rm -u 0 -v "$work/proj/secrets:/s" --entrypoint sh "$PG_IMAGE" \
  -c 'chown 1654:1654 /s/telrang_db && chown 999:999 /s/db_app_password /s/db_superuser_password' >/dev/null

docker network create --subnet "$proxy_subnet" --ip-range "10.254.$octet.4/30" "$id-proxy" >/dev/null

wait_healthy() { # wait_healthy <container> <seconds>
  local t=0 s
  while [ $t -lt "$2" ]; do
    s=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2>/dev/null || true)
    [ "$s" = healthy ] && return 0
    sleep 2; t=$((t + 2))
  done
  echo "$1: health '$s' after $2 s" >&2
  return 1
}

# ---- 1. SQLite instance (compose.yml), then copy-to-postgres ---------------------------------------------------------------
if [ "$fresh" = no ]; then
  echo "== compose.yml (SQLite, hardened): start, health check, stop"
  compose_sqlite up -d --no-build app >/dev/null 2>&1
  wait_healthy "$id-app-1" 150 && check "compose.yml: HEALTHCHECK healthy under read_only/cap_drop" 0 || check "compose.yml: HEALTHCHECK healthy" 1
  check "compose.yml: root file system read-only" "$(docker exec "$id-app-1" sh -c 'touch /app/x' >/dev/null 2>&1 && echo 1 || echo 0)"
  compose_sqlite stop app >/dev/null 2>&1

  echo "== compose.npm.yml: db, copy-to-postgres from the same /data volume"
  compose_npm up -d --wait db >/dev/null 2>&1 || { docker logs "$id-db-1" | tail -20; check "db healthy" 1; exit 1; }
  copy_out=$(compose_npm run --rm -T app copy-to-postgres --from /data/app.db 2>&1) && copy=0 || copy=$?
  echo "$copy_out" >"$artifacts/copy-to-postgres.log"
  check "copy-to-postgres exit 0" "$(t is "$copy" 0)" "exit $copy, see $artifacts/copy-to-postgres.log"
fi

# ---- 2. the stack ---------------------------------------------------------------------------------------------------------
echo "== compose.npm.yml up"
compose_npm up -d --wait >/dev/null 2>&1 || { docker logs "$id-app-1" 2>&1 | tail -30; check "compose.npm.yml up --wait" 1; exit 1; }
check "compose.npm.yml: app healthy (HEALTHCHECK)" "$(t is "$(docker inspect -f '{{.State.Health.Status}}' "$id-app-1")" healthy)"

echo "== fake SMTP on the app's outbound network, NPM on the proxy network"
docker run -d --name "$id-smtp" --network "${id}_egress" -p 127.0.0.1::8025 "$SMTP_IMAGE" >/dev/null
smtp_api=http://$(docker port "$id-smtp" 8025/tcp | head -n 1)
npm_pw=$(openssl rand -hex 16)
docker run -d --name "$id-npm" --network "$id-proxy" --ip "$NPM_ADDRESS" \
  -p 127.0.0.1:80:80 -p 127.0.0.1:443:443 -p 127.0.0.1::81 \
  -v "$id-npm-data:/data" -v "$id-npm-le:/etc/letsencrypt" \
  -e INITIAL_ADMIN_EMAIL=admin@example.com -e INITIAL_ADMIN_PASSWORD="$npm_pw" "$NPM_IMAGE" >/dev/null
api=http://$(docker port "$id-npm" 81/tcp | head -n 1)/api
for _ in $(seq 1 90); do curl -sf "$api/" >/dev/null 2>&1 && break; sleep 2; done
token=$(curl -sf -X POST "$api/tokens" -H 'Content-Type: application/json' \
  -d "{\"identity\":\"admin@example.com\",\"secret\":\"$npm_pw\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -n "$token" ] || { echo "NPM API: no token" >&2; exit 1; }
auth=(-H "Authorization: Bearer $token")
cert_id=$(curl -sf -X POST "$api/nginx/certificates" "${auth[@]}" -H 'Content-Type: application/json' \
  -d '{"provider":"other","nice_name":"telrang-test"}' | sed -n 's/.*"id":\([0-9]*\)}$/\1/p')
curl -sf -X POST "$api/nginx/certificates/$cert_id/upload" "${auth[@]}" -F "certificate=@$cert" -F "certificate_key=@$work/key.pem" >/dev/null
# The proxy host as docs/deploy/docker.md, "Nginx Proxy Manager", tells the owner to set it up.
host_id=$(curl -sf -X POST "$api/nginx/proxy-hosts" "${auth[@]}" -H 'Content-Type: application/json' -d "{
  \"domain_names\": [\"$name\"], \"forward_scheme\": \"http\", \"forward_host\": \"telrang\", \"forward_port\": 8080,
  \"certificate_id\": $cert_id, \"ssl_forced\": true, \"http2_support\": true, \"hsts_enabled\": false, \"hsts_subdomains\": false,
  \"allow_websocket_upgrade\": true, \"block_exploits\": true, \"caching_enabled\": false, \"trust_forwarded_proto\": false,
  \"access_list_id\": 0, \"advanced_config\": \"set_real_ip_from unix:;\", \"locations\": [], \"meta\": {}}" |
  sed -n 's/^{"id":\([0-9]*\),.*/\1/p')
[ -n "$host_id" ] || { echo "NPM API: proxy host not created" >&2; exit 1; }
c() { curl -s --cacert "$ca" "${resolve[@]}" "$@"; }
code() { c -o /dev/null -w '%{http_code}' "$@"; }
for _ in $(seq 1 30); do [ "$(code "$H/healthz")" = 200 ] && break; sleep 1; done
check "healthz through NPM over HTTPS" "$(t is "$(c "$H/healthz")" Healthy)"

# ---- e2e through NPM ------------------------------------------------------------------------------------------------------
set +e   # from here on a failing command is a failed check, not the end of the run
if [ "$run_e2e" = yes ]; then
  echo "== e2e through $H"
  e2e=0
  NODE_EXTRA_CA_CERTS=$ca E2E_SMTP_HOST=$id-smtp E2E_SMTP_PORT=1025 \
    node tests/e2e/run.mjs --base "$H" --resolve 127.0.0.1 --container "$id-app-1" --artifacts "$artifacts/e2e" "${e2e_args[@]}" || e2e=$?
  check "e2e flows through NPM" "$(t is "$e2e" 0)" "exit $e2e, see $artifacts/e2e"
  if [ -z "${e2e_args[*]}" ]; then
    msgs=$(curl -s "$smtp_api/api/v1/messages")
    check "mail: the fake SMTP server received the test mail for the admin" "$(t has "$msgs" '"Address":"admin@example.com"')" "${msgs:0:300}"
  fi
fi

# ---- 3. outside checks -----------------------------------------------------------------------
echo "== outside checks"
r=$(curl -sI "${resolve[@]}" "http://$name/")
check "6.1 http -> 301 to https" "$( (has "$r" '^HTTP/1.1 301' && has "$r" "^location: $H/") && echo 0 || echo 1)" "$(head -n 1 <<<"$r")"
hdr=$(c -D - -o /dev/null "$H/")
for h in 'strict-transport-security: max-age=' "content-security-policy:.*frame-ancestors 'none'" 'x-frame-options: DENY' \
  'x-content-type-options: nosniff' 'referrer-policy:' 'permissions-policy:' 'cross-origin-opener-policy:'; do
  check "6.2 header $h" "$(t has "$hdr" "^$h")"
done
check "6.2 exactly one Strict-Transport-Security (NPM's HSTS off)" "$(t is "$(grep -ci '^strict-transport-security' <<<"$hdr")" 1)"
check "6.2 no Server version from the app (Kestrel)" "$(t lacks "$hdr" '^server: kestrel')"
ck=$(c -D - -o /dev/null "$H/Account/Login" | grep -i '^set-cookie')
check "6.3 antiforgery cookie secure; samesite=strict; httponly (https recognised)" \
  "$( (has "$ck" 'secure' && has "$ck" 'samesite=strict' && has "$ck" 'httponly') && echo 0 || echo 1)" "$ck"
check "6.4 /Account/Register 404 (registration closed)" "$(t is "$(code "$H/Account/Register")" 404)"
if [ "$run_e2e" = yes ]; then
  check "6.5 /setup 404 (admin exists)" "$(t is "$(code "$H/setup")" 404)"
fi
check "6.5 /setup/recover 404" "$(t is "$(code "$H/setup/recover")" 404)"
r=$(c -o /dev/null -w '%{http_code} %{redirect_url}' "$H/admin")
check "6.6 /admin 302 to the login page, on https" "$(t has "$r" "^302 $H/Account/Login")" "$r"
check "6.7 POST /admin/backup not 200" "$(t lacks "$(code -X POST "$H/admin/backup")" '^200$')"
for p in appsettings.json app.db data/app.db keys keys/emailkey .git/config Telrang.Web.dll Telrang.Web.deps.json web.config; do
  check "6.8 /$p not served" "$(t lacks "$(code "$H/$p")" '^200$')"
done
check "6.9 unknown poll 404" "$(t is "$(code "$H/p/AAAAAAAAAAAAAAAAAAAAAA")" 404)"
check "6.11 Blazor negotiate from another Origin 403" \
  "$(t is "$(code -X POST -H 'Origin: https://evil.example' "$H/_blazor/negotiate?negotiateVersion=1")" 403)"
check "6.11 Blazor negotiate from the site's Origin 200" \
  "$(t is "$(code -X POST -H "Origin: $H" "$H/_blazor/negotiate?negotiateVersion=1")" 200)"
ws=$(c --http1.1 -o /dev/null -w '%{http_code}' -m 5 -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
  -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' -H "Origin: $H" "$H/_blazor?id=x" || true)
check "WebSocket upgrade reaches the app (not 400/502 from NPM)" "$(t lacks "$ws" '^(502|504|000)$')" "$ws"
check "6.12 healthz" "$(t is "$(c "$H/healthz")" Healthy)"

echo "== containers and networks"
app_hc=$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.HostConfig.SecurityOpt}} {{.HostConfig.Memory}} {{.HostConfig.PidsLimit}} {{.HostConfig.LogConfig.Type}} {{.HostConfig.LogConfig.Config}}' "$id-app-1")
check "app: read_only, cap_drop ALL, no-new-privileges, 2 GB, pids, rotated json-file log" \
  "$(t is "$app_hc" 'true [ALL] [no-new-privileges:true] 2147483648 512 json-file map[max-file:5 max-size:10m]')" "$app_hc"
check "app: no published port" "$(t is "$(docker inspect -f '{{json .HostConfig.PortBindings}}' "$id-app-1")" '{}')"
check "app: nothing writable outside /data and /tmp" \
  "$(docker exec "$id-app-1" sh -c 'touch /app/x || touch /home/app/x || touch /x' >/dev/null 2>&1 && echo 1 || echo 0)"
check "app: runs as UID 1654" "$(t is "$(docker exec "$id-app-1" id -u)" 1654)"
check "app: no diagnostics IPC socket in /tmp" "$(t is "$(docker exec "$id-app-1" sh -c 'ls /tmp | grep -c dotnet-diagnostic' || true)" 0)"
db_hc=$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.HostConfig.SecurityOpt}} {{.Config.User}} {{json .HostConfig.PortBindings}}' "$id-db-1")
check "db: read_only, cap_drop ALL, no-new-privileges, user 999, no published port" \
  "$(t is "$db_hc" 'true [ALL] [no-new-privileges:true] 999:999 {}')" "$db_hc"
psql() { docker exec "$id-db-1" psql -U postgres -d telrang -tAc "$1"; }
check "db: the app's role is no superuser and owns the database" \
  "$(t is "$(psql "select r.rolsuper::text || ' ' || pg_get_userbyid(d.datdba) from pg_roles r, pg_database d where r.rolname = 'telrang' and d.datname = 'telrang'")" 'false telrang')"
check "db: PUBLIC has no CONNECT on the database" "$(t lacks "$(psql "select datacl::text from pg_database where datname = 'telrang'")" '[{,]=[A-Za-z]*c')"
check "db: no route out (internal network)" \
  "$(docker exec "$id-db-1" sh -c 'getent hosts example.com' >/dev/null 2>&1 && echo 1 || echo 0)"
check "NPM does not see the db" "$(docker exec "$id-npm" sh -c 'getent hosts db' >/dev/null 2>&1 && echo 1 || echo 0)"
proxy_ip=$(docker inspect -f "{{(index .NetworkSettings.Networks \"$id-proxy\").IPAddress}}" "$id-app-1")
egress_ip=$(docker inspect -f "{{(index .NetworkSettings.Networks \"${id}_egress\").IPAddress}}" "$id-app-1")
other=$(docker run --rm --entrypoint sh "$NPM_IMAGE" -c \
  "curl -s -m 5 -o /dev/null -w '%{http_code}' http://$proxy_ip:8080/healthz; echo; curl -s -m 5 -o /dev/null -w '%{http_code}' http://$egress_ip:8080/healthz" 2>/dev/null || true)
check "a container on another network cannot reach the app ($proxy_ip, $egress_ip)" "$(t lacks "$other" '200')" "$other"

# Last: these posts count as abuse signals and may block this client for a few minutes.
echo "== forwarded headers (6.10)"
codes=
for i in $(seq 1 14); do
  codes+=$(code -X POST -H "X-Forwarded-For: 203.0.113.$i" -H "X-Real-IP: 198.51.100.$i" "$H/Account/Login")" "
done
check "6.10 forged X-Forwarded-For / X-Real-IP share one bucket: 400s, then 429 (or 403 once blocked)" \
  "$(t has "$codes" '^(400 ){1,10}(429|403)')" "$codes"
# Another client (NPM's own loopback, a different address than the host's) is not limited: the app keys on the hop
# NPM adds, not on NPM's address.
second=$(docker exec "$id-npm" curl -sk -o /dev/null -w '%{http_code}' --resolve "$name:443:127.0.0.1" -X POST "$H/Account/Login")
check "6.10 a second client keeps its own bucket (400, not 429)" "$(t is "$second" 400)" "$second"
# Control: without the Advanced line NPM takes X-Real-IP from this (private) client, and each forged value is a fresh
# client to the app, although this client is limited or blocked by now. It shows the line is what stops it.
curl -sf -X PUT "$api/nginx/proxy-hosts/$host_id" "${auth[@]}" -H 'Content-Type: application/json' -d '{"advanced_config": ""}' >/dev/null
sleep 3
codes=
for i in $(seq 1 5); do codes+=$(code -X POST -H "X-Real-IP: 192.0.2.$i" "$H/Account/Login")" "; done
check "6.10 control: without set_real_ip_from in the host, a forged X-Real-IP is a new client (400s)" \
  "$(t is "$codes" '400 400 400 400 400 ')" "$codes"

echo
echo "== $((checks - failures))/$checks checks passed"
[ "$failures" = 0 ]
