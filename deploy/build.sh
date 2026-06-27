#!/usr/bin/env bash
# Build the Michka deployment artifacts:
#   dist/michka-hub_<ver>_amd64.deb        headless hub package
#   dist/michka-kiosk_<ver>_amd64.deb      optional kiosk display add-on
#   dist/michka-agent_<ver>_amd64.deb      Linux client (pushes metrics to a hub)
#   dist/michka-<ver>-linux-x64.tar.gz     generic tarball (install.sh: hub / kiosk / agent)
#   dist/SHA256SUMS
#
# Run on a Debian/Ubuntu host (needs dpkg-deb + tar). Provide the linux-x64 binaries by placing
# michka_s + michka_c next to build.sh, passing BINARY_HUB=/path and BINARY_AGENT=/path, or having
# the .NET SDK available so this script can publish them from ../src/Monitor.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PKG="$ROOT/packaging"
DIST="$ROOT/dist"
BUILD="$DIST/build"
VERSION=$(tr -d ' \t\r\n' < "$ROOT/VERSION")
ARCH=amd64

echo "Michka deploy build  version=$VERSION  arch=$ARCH"

command -v dpkg-deb >/dev/null 2>&1 || { echo "ERROR: dpkg-deb not found (run on Debian/Ubuntu)"; exit 1; }
rm -rf "$BUILD"; mkdir -p "$BUILD" "$DIST"

# --- resolve the two binaries ----------------------------------------------
# publish <assembly-name> -> echoes the produced binary path
publish() {
  echo "==> Publishing $1 (linux-x64) with dotnet" >&2
  dotnet publish "$ROOT/../src/Monitor/Monitor.csproj" -c Release -r linux-x64 \
    -p:AssemblyName="$1" -o "$BUILD/publish-$1" >/dev/null
  echo "$BUILD/publish-$1/$1"
}
HAVE_DOTNET=0; command -v dotnet >/dev/null 2>&1 && [ -f "$ROOT/../src/Monitor/Monitor.csproj" ] && HAVE_DOTNET=1

BIN="${BINARY_HUB:-}"
[ -z "$BIN" ] && [ -f "$ROOT/michka_s" ] && BIN="$ROOT/michka_s"
[ -z "$BIN" ] && [ "$HAVE_DOTNET" = 1 ] && BIN=$(publish michka_s)
[ -n "$BIN" ] && [ -f "$BIN" ] || { echo "ERROR: michka_s not found (set BINARY_HUB=...)"; exit 1; }

AGENTBIN="${BINARY_AGENT:-}"
[ -z "$AGENTBIN" ] && [ -f "$ROOT/michka_c" ] && AGENTBIN="$ROOT/michka_c"
[ -z "$AGENTBIN" ] && [ "$HAVE_DOTNET" = 1 ] && AGENTBIN=$(publish michka_c)
[ -n "$AGENTBIN" ] && [ -f "$AGENTBIN" ] || { echo "ERROR: michka_c not found (set BINARY_AGENT=...)"; exit 1; }
echo "    hub:   $BIN ($(du -h "$BIN" | cut -f1))"
echo "    agent: $AGENTBIN ($(du -h "$AGENTBIN" | cut -f1))"

# strip CR so files authored on Windows are valid on Linux
fixnl() { for f in "$@"; do [ -f "$f" ] && sed -i 's/\r$//' "$f"; done; }

# ============================================================ michka-hub .deb
HUB="$BUILD/michka-hub"
mkdir -p "$HUB/DEBIAN" "$HUB/opt/michka" "$HUB/lib/systemd/system"
install -m 0755 "$BIN" "$HUB/opt/michka/michka_s"
install -m 0644 "$PKG/systemd/michka-hub.service" "$HUB/lib/systemd/system/michka-hub.service"
# Ship the canonical on-disk templates + widgets read-only under /opt; postinst seeds them into the
# data dir (/var/lib/michka/{templates,widgets}) on install. (_screenshots is dev-only, not shipped.)
seed_dropins() {  # seed_dropins <src-dir> <dest-dir>
  [ -d "$1" ] || return 0
  mkdir -p "$2"
  for d in "$1"/*/; do
    [ -d "$d" ] || continue
    case "$(basename "$d")" in _*) continue ;; esac   # skip _screenshots and other underscore dirs
    cp -r "$d" "$2/"
  done
}
seed_dropins "$ROOT/../templates" "$HUB/opt/michka/templates"
seed_dropins "$ROOT/../widgets"   "$HUB/opt/michka/widgets"
for f in control postinst prerm postrm; do cp "$PKG/deb/michka-hub/DEBIAN/$f" "$HUB/DEBIAN/$f"; done
sed -i "s/__VERSION__/$VERSION/g" "$HUB/DEBIAN/control"
fixnl "$HUB/DEBIAN/"* "$HUB/lib/systemd/system/michka-hub.service"
chmod 0644 "$HUB/DEBIAN/control"; chmod 0755 "$HUB/DEBIAN/postinst" "$HUB/DEBIAN/prerm" "$HUB/DEBIAN/postrm"
dpkg-deb --root-owner-group --build "$HUB" "$DIST/michka-hub_${VERSION}_${ARCH}.deb" >/dev/null
echo "==> built michka-hub_${VERSION}_${ARCH}.deb"

# ========================================================== michka-kiosk .deb
KIO="$BUILD/michka-kiosk"
mkdir -p "$KIO/DEBIAN" "$KIO/opt/michka" "$KIO/lib/systemd/system" "$KIO/etc/systemd/system/michka-hub.service.d"
install -m 0755 "$PKG/kiosk/kiosk.sh" "$KIO/opt/michka/kiosk.sh"
install -m 0644 "$PKG/systemd/michka-kiosk.service" "$KIO/lib/systemd/system/michka-kiosk.service"
install -m 0644 "$PKG/systemd/michka-hub.kiosk-override.conf" "$KIO/etc/systemd/system/michka-hub.service.d/kiosk.conf"
for f in control postinst prerm postrm; do cp "$PKG/deb/michka-kiosk/DEBIAN/$f" "$KIO/DEBIAN/$f"; done
sed -i "s/__VERSION__/$VERSION/g" "$KIO/DEBIAN/control"
fixnl "$KIO/DEBIAN/"* "$KIO/opt/michka/kiosk.sh" "$KIO/lib/systemd/system/michka-kiosk.service" "$KIO/etc/systemd/system/michka-hub.service.d/kiosk.conf"
chmod 0644 "$KIO/DEBIAN/control"; chmod 0755 "$KIO/DEBIAN/postinst" "$KIO/DEBIAN/prerm" "$KIO/DEBIAN/postrm"
dpkg-deb --root-owner-group --build "$KIO" "$DIST/michka-kiosk_${VERSION}_${ARCH}.deb" >/dev/null
echo "==> built michka-kiosk_${VERSION}_${ARCH}.deb"

# ========================================================== michka-agent .deb
AGT="$BUILD/michka-agent"
mkdir -p "$AGT/DEBIAN" "$AGT/opt/michka" "$AGT/lib/systemd/system" "$AGT/etc/default"
install -m 0755 "$AGENTBIN" "$AGT/opt/michka/michka_c"
install -m 0644 "$PKG/systemd/michka-agent.service" "$AGT/lib/systemd/system/michka-agent.service"
install -m 0644 "$PKG/agent/michka-agent.env" "$AGT/etc/default/michka-agent"
for f in control conffiles postinst prerm postrm; do cp "$PKG/deb/michka-agent/DEBIAN/$f" "$AGT/DEBIAN/$f"; done
sed -i "s/__VERSION__/$VERSION/g" "$AGT/DEBIAN/control"
fixnl "$AGT/DEBIAN/"* "$AGT/lib/systemd/system/michka-agent.service" "$AGT/etc/default/michka-agent"
chmod 0644 "$AGT/DEBIAN/control" "$AGT/DEBIAN/conffiles"; chmod 0755 "$AGT/DEBIAN/postinst" "$AGT/DEBIAN/prerm" "$AGT/DEBIAN/postrm"
dpkg-deb --root-owner-group --build "$AGT" "$DIST/michka-agent_${VERSION}_${ARCH}.deb" >/dev/null
echo "==> built michka-agent_${VERSION}_${ARCH}.deb"

# ================================================================== tar.gz
TARNAME="michka-${VERSION}-linux-x64"
TAR="$BUILD/tar/$TARNAME"
mkdir -p "$TAR/systemd" "$TAR/kiosk" "$TAR/agent"
install -m 0755 "$BIN"      "$TAR/michka_s"
install -m 0755 "$AGENTBIN" "$TAR/michka_c"
install -m 0755 "$PKG/tarball/install.sh"   "$TAR/install.sh"
install -m 0755 "$PKG/tarball/uninstall.sh" "$TAR/uninstall.sh"
install -m 0644 "$PKG/systemd/michka-hub.service"              "$TAR/systemd/michka-hub.service"
install -m 0644 "$PKG/systemd/michka-kiosk.service"            "$TAR/systemd/michka-kiosk.service"
install -m 0644 "$PKG/systemd/michka-agent.service"            "$TAR/systemd/michka-agent.service"
install -m 0644 "$PKG/systemd/michka-hub.kiosk-override.conf"  "$TAR/systemd/michka-hub.kiosk-override.conf"
install -m 0755 "$PKG/kiosk/kiosk.sh"                          "$TAR/kiosk/kiosk.sh"
install -m 0644 "$PKG/agent/michka-agent.env"                 "$TAR/agent/michka-agent.env"
seed_dropins "$ROOT/../templates" "$TAR/templates"
seed_dropins "$ROOT/../widgets"   "$TAR/widgets"
[ -f "$ROOT/README.md" ] && install -m 0644 "$ROOT/README.md" "$TAR/README.md"
fixnl "$TAR/install.sh" "$TAR/uninstall.sh" "$TAR/systemd/"* "$TAR/kiosk/kiosk.sh" "$TAR/agent/michka-agent.env" "$TAR/README.md" 2>/dev/null || true
tar -C "$BUILD/tar" -czf "$DIST/${TARNAME}.tar.gz" "$TARNAME"
echo "==> built ${TARNAME}.tar.gz"

# ================================================================== checksums
( cd "$DIST" && sha256sum michka-hub_${VERSION}_${ARCH}.deb michka-kiosk_${VERSION}_${ARCH}.deb \
    michka-agent_${VERSION}_${ARCH}.deb ${TARNAME}.tar.gz > SHA256SUMS )
rm -rf "$BUILD"

echo ""
echo "Artifacts in $DIST:"
( cd "$DIST" && ls -lh michka-hub_${VERSION}_${ARCH}.deb michka-kiosk_${VERSION}_${ARCH}.deb \
    michka-agent_${VERSION}_${ARCH}.deb ${TARNAME}.tar.gz SHA256SUMS | awk '{print "  "$9"  "$5}' )
