#!/usr/bin/env bash
# Builds the feeder Pi package (daemon + touch UI + systemd units):
#   packaging/feeder/build-deb.sh [version] [rid]
# Output: artifacts/3ctool-feeder_<version>_<arch>.deb (rid linux-arm64 -> arm64, linux-x64 -> amd64).
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
version=${1:-0.1.0}
rid=${2:-linux-arm64}
out=${OUT:-$root/artifacts}
maintainer=${MAINTAINER:-"$(git -C "$root" config user.name 2>/dev/null || echo 3C Tool) <$(git -C "$root" config user.email 2>/dev/null || echo root@localhost)>"}

case "$rid" in
  linux-arm64) arch=arm64 ;;
  linux-x64) arch=amd64 ;;
  linux-arm) arch=armhf ;;
  *) echo "Unsupported runtime identifier '$rid'." >&2; exit 2 ;;
esac

if ! [[ "$version" =~ ^[0-9][A-Za-z0-9.+~-]*$ ]]; then
  echo "Invalid Debian version '$version'." >&2
  exit 2
fi

command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required." >&2; exit 2; }

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
app="$stage/opt/3ctool-feeder"

# Invariant globalization: all formatting is culture-invariant, and it drops the libicu dependency
# (whose package name changes with every Debian release).
publish() {
  dotnet publish "$root/$1" -c Release -r "$rid" --self-contained \
    -p:InvariantGlobalization=true -p:Version="${version%%[~+-]*}" -p:InformationalVersion="$version" \
    -o "$2" --nologo -v quiet
}
publish src/ThreeCTool.Feeder.Service/ThreeCTool.Feeder.Service.csproj "$app/feederd"
publish src/ThreeCTool.Feeder/ThreeCTool.Feeder.csproj "$app/ui"
find "$app" -name '*.pdb' -delete

install -d "$stage/usr/bin"
ln -s /opt/3ctool-feeder/feederd/3ctool-feederd "$stage/usr/bin/3ctool-feederd"
ln -s /opt/3ctool-feeder/ui/3ctool-feeder-ui "$stage/usr/bin/3ctool-feeder-ui"
install -D -m 644 "$here/systemd/3ctool-feederd.service" "$stage/lib/systemd/system/3ctool-feederd.service"
install -D -m 644 "$here/systemd/3ctool-feeder-ui.service" "$stage/lib/systemd/system/3ctool-feeder-ui.service"
install -D -m 644 "$here/feeder.json" "$stage/etc/3ctool-feeder/feeder.json"

install -d "$stage/DEBIAN"
sed -e "s|@VERSION@|$version|" -e "s|@ARCH@|$arch|" -e "s|@MAINTAINER@|$maintainer|" \
  -e "s|@SIZE@|$(du -sk --exclude=DEBIAN "$stage" | cut -f1)|" \
  "$here/debian/control" > "$stage/DEBIAN/control"
install -m 644 "$here/debian/conffiles" "$stage/DEBIAN/conffiles"
install -m 755 "$here/debian/postinst" "$here/debian/prerm" "$here/debian/postrm" "$stage/DEBIAN/"

mkdir -p "$out"
package="$out/3ctool-feeder_${version}_${arch}.deb"
dpkg-deb --build --root-owner-group -Zxz "$stage" "$package"
echo "$package"
