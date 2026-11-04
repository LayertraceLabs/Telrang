#!/usr/bin/env sh
# Build the release files (DESIGN.md §29): the same ones CI's release workflow (.github/workflows/release.yml) publishes
# on a v* tag.
#
#   scripts/publish-release.sh 1.2.0                       # every archive + SHA256SUMS into dist/1.2.0/
#   scripts/publish-release.sh 1.2.0 --only linux-x64      # one or more of linux-x64,linux-arm64,win-x64
#   scripts/publish-release.sh 1.2.0 --docker              # also the image telrang:1.2.0 (this machine's architecture)
#   scripts/publish-release.sh 1.2.0 --tag                 # also create git tag v1.2.0 (clean tree only)
#   scripts/publish-release.sh 1.2.0 --skip-tests
#
# dist/<version>/:
#   telrang-<version>-linux-x64.tar.gz, -linux-arm64.tar.gz   self-contained (no .NET needed), plus telrang.service and
#                                                             an example telrang.json (docs/deploy/linux.md)
#   telrang-<version>-win-x64.zip                             self-contained, plus install-service.ps1,
#                                                             uninstall-service.ps1, telrang.json (docs/deploy/windows.md)
#   SHA256SUMS
# The version is stamped into the assemblies (page footer). Builds are deterministic (ContinuousIntegrationBuild: no local
# paths in the binaries); archive entries carry the commit time, root ownership and fixed modes.
# Needs: .NET 10 SDK, GNU tar, gzip, python3 (zip), sha256sum.
set -eu
cd "$(dirname "$0")/.."

version=${1:-}
case "$version" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "usage: $0 <major.minor.patch[-suffix]> [--only <rid,...>] [--docker] [--tag] [--skip-tests]" >&2; exit 2 ;;
esac
shift

all_rids="linux-x64 linux-arm64 win-x64"
rids=$all_rids; docker_build=no; tag=no; run_tests=yes
while [ $# -gt 0 ]; do
  case "$1" in
    --only) [ $# -ge 2 ] || { echo "--only needs a list" >&2; exit 2; }; rids=$(echo "$2" | tr ',' ' '); shift ;;
    --docker) docker_build=yes ;;
    --tag) tag=yes ;;
    --skip-tests) run_tests=no ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
for rid in $rids; do
  case " $all_rids " in *" $rid "*) ;; *) echo "unknown runtime: $rid (one of: $all_rids)" >&2; exit 2 ;; esac
done

commit=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)
# Archive timestamps: the commit's time, so two builds of one commit give the same archives.
epoch=${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct 2>/dev/null || date +%s)}
out="dist/$version"
stage="dist/.stage-$version"

if [ "$tag" = yes ] && [ -n "$(git status --porcelain)" ]; then
  echo "refusing to tag: working tree is not clean" >&2; exit 1
fi

if [ "$run_tests" = yes ]; then
  echo "== tests"
  dotnet test -c Release --nologo -v q
fi

rm -rf "$out" "$stage"
mkdir -p "$out" "$stage"

for rid in $rids; do
  name="telrang-$version-$rid"
  dir="$stage/$name"
  echo "== publish $version ($commit) $rid"
  dotnet publish src/Telrang.Web/Telrang.Web.csproj -c Release -r "$rid" --self-contained -o "$dir" --nologo -v q \
    -p:Version="$version" -p:InformationalVersion="$version+$commit" -p:ContinuousIntegrationBuild=true
  case "$rid" in
    linux-*)
      install -m 644 packaging/linux/telrang.service packaging/linux/telrang.json "$dir/"
      # Owned by root, read-only for everyone else, the program executable: as installed under /opt/telrang.
      tar -C "$stage" --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" \
        --mode='u+rwX,go+rX,go-w' -cf - "$name" | gzip -n -9 > "$out/$name.tar.gz"
      echo "   $out/$name.tar.gz"
      ;;
    win-*)
      install -m 644 packaging/windows/install-service.ps1 packaging/windows/uninstall-service.ps1 \
        packaging/windows/telrang.json "$dir/"
      python3 -I - "$stage" "$name" "$out/$name.zip" "$epoch" <<'PY'
import os, sys, time, zipfile
root, name, target, epoch = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
stamp = time.gmtime(max(epoch, 315532800))[:6]  # zip cannot store times before 1980
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for base, dirs, files in os.walk(os.path.join(root, name)):
        dirs.sort()
        for f in sorted(files):
            path = os.path.join(base, f)
            info = zipfile.ZipInfo(os.path.relpath(path, root).replace(os.sep, "/"), stamp)
            info.external_attr = 0o644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as src:
                z.writestr(info, src.read())
PY
      echo "   $out/$name.zip"
      ;;
  esac
done
rm -rf "$stage"

(cd "$out" && sha256sum -- *.tar.gz *.zip 2>/dev/null > SHA256SUMS || true)
echo "   $out/SHA256SUMS"

if [ "$docker_build" = yes ]; then
  echo "== docker build telrang:$version"
  docker build -f docker/Dockerfile --build-arg VERSION="$version" --build-arg COMMIT="$commit" \
    -t "telrang:$version" -t telrang:latest .
fi

if [ "$tag" = yes ]; then
  git tag -a "v$version" -m "Release $version"
  echo "== tagged v$version (push with: git push origin v$version)"
fi

echo "== done"
