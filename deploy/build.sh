#!/usr/bin/env bash
# Build the Michka deployment artifacts:
#   dist/michka-hub_<ver>_<arch>.deb        headless hub package
#   dist/michka-kiosk_<ver>_<arch>.deb      optional kiosk display add-on
#   dist/michka-agent_<ver>_<arch>.deb      Linux client (pushes metrics to a hub)
#   dist/michka-<ver>-<rid>.tar.gz          generic tarball (install.sh: hub / kiosk / agent)
#   dist/SHA256SUMS
#
# Run on a Debian/Ubuntu host (needs dpkg-deb + tar). Provide the linux-x64 binaries by placing
# michka_s + michka_c next to build.sh, passing BINARY_HUB=/path and BINARY_AGENT=/path, or having
# the .NET SDK available so this script can publish them from ../src/Monitor.
#
# OPTIONAL ARM64 (Raspberry Pi etc.) — UNTESTED, off by default. Set BUILD_ARM=1 to ALSO produce
# arm64 .debs + a linux-arm64 tarball. The arm64 binaries come from BINARY_HUB_ARM64/BINARY_AGENT_ARM64,
# loose ./michka_s-arm64 + ./michka_c-arm64, or a `dotnet publish -r linux-arm64` (cross-compiles from
# any host with the .NET SDK). NOTE: these arm64 artifacts have NOT been tested on real ARM hardware —
# publish them as "untested/experimental". See the Raspberry Pi notes when validating.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PKG="$ROOT/packaging"
DIST="$ROOT/dist"
BUILD="$DIST/build"
VERSION=$(tr -d ' \t\r\n' < "$ROOT/VERSION")

BUILD_ARM="${BUILD_ARM:-0}"
echo "Michka deploy build  version=$VERSION  arm=$([ "$BUILD_ARM" = 1 ] && echo on || echo off)"

command -v dpkg-deb >/dev/null 2>&1 || { echo "ERROR: dpkg-deb not found (run on Debian/Ubuntu)"; exit 1; }
rm -rf "$BUILD"; mkdir -p "$BUILD" "$DIST"
HAVE_DOTNET=0; command -v dotnet >/dev/null 2>&1 && [ -f "$ROOT/../src/Monitor/Monitor.csproj" ] && HAVE_DOTNET=1

# strip CR so files authored on Windows are valid on Linux
fixnl() { for f in "$@"; do [ -f "$f" ] && sed -i 's/\r$//' "$f"; done; }

# publish <assembly-name> <rid> -> echoes the produced binary path
publish() {
  echo "==> Publishing $1 ($2) with dotnet" >&2
  dotnet publish "$ROOT/../src/Monitor/Monitor.csproj" -c Release -r "$2" \
    -p:AssemblyName="$1" -o "$BUILD/publish-$1-$2" >/dev/null
  echo "$BUILD/publish-$1-$2/$1"
}

# resolve <assembly> <rid> <env-value> <loose-name> -> echoes a binary path or exits
resolve_bin() {
  local asm="$1" rid="$2" envval="$3" loose="$4" bin=""
  bin="$envval"
  [ -z "$bin" ] && [ -f "$ROOT/$loose" ] && bin="$ROOT/$loose"
  [ -z "$bin" ] && [ "$HAVE_DOTNET" = 1 ] && bin=$(publish "$asm" "$rid")
  [ -n "$bin" ] && [ -f "$bin" ] || { echo "ERROR: $asm not found for $rid (set the matching BINARY_* env or provide $loose)" >&2; exit 1; }
  echo "$bin"
}

# Ship the canonical on-disk templates + widgets read-only; postinst seeds them into the data dir
# (/var/lib/michka/{templates,widgets}) on install. (_screenshots is dev-only, not shipped.)
seed_dropins() {  # seed_dropins <src-dir> <dest-dir>
  [ -d "$1" ] || return 0
  mkdir -p "$2"
  for d in "$1"/*/; do
    [ -d "$d" ] || continue
    case "$(basename "$d")" in _*) continue ;; esac   # skip _screenshots and other underscore dirs
    cp -r "$d" "$2/"
  done
}

ARTIFACTS=""   # accumulates produced filenames (for the checksum + listing steps)

# build_arch <deb-arch> <rid> <hub-bin> <agent-bin> — packages all artifacts for one architecture
build_arch() {
  local ARCH="$1" RID="$2" BIN="$3" AGENTBIN="$4"
  echo ""
  echo "=== arch=$ARCH  rid=$RID"
  echo "    hub:   $BIN ($(du -h "$BIN" | cut -f1))"
  echo "    agent: $AGENTBIN ($(du -h "$AGENTBIN" | cut -f1))"

  # set the Debian control Architecture field for this pass (files are authored as amd64)
  set_arch() { sed -i "s/^Architecture: .*/Architecture: $ARCH/" "$1"; }

  # ============================================================ michka-hub .deb
  local HUB="$BUILD/michka-hub-$ARCH"
  mkdir -p "$HUB/DEBIAN" "$HUB/opt/michka" "$HUB/lib/systemd/system"
  install -m 0755 "$BIN" "$HUB/opt/michka/michka_s"
  install -m 0644 "$PKG/systemd/michka-hub.service" "$HUB/lib/systemd/system/michka-hub.service"
  seed_dropins "$ROOT/../templates" "$HUB/opt/michka/templates"
  seed_dropins "$ROOT/../widgets"   "$HUB/opt/michka/widgets"
  for f in control config templates postinst prerm postrm; do cp "$PKG/deb/michka-hub/DEBIAN/$f" "$HUB/DEBIAN/$f"; done
  sed -i "s/__VERSION__/$VERSION/g" "$HUB/DEBIAN/control"; set_arch "$HUB/DEBIAN/control"
  fixnl "$HUB/DEBIAN/"* "$HUB/lib/systemd/system/michka-hub.service"
  chmod 0644 "$HUB/DEBIAN/control" "$HUB/DEBIAN/templates"; chmod 0755 "$HUB/DEBIAN/config" "$HUB/DEBIAN/postinst" "$HUB/DEBIAN/prerm" "$HUB/DEBIAN/postrm"
  dpkg-deb --root-owner-group --build "$HUB" "$DIST/michka-hub_${VERSION}_${ARCH}.deb" >/dev/null
  echo "==> built michka-hub_${VERSION}_${ARCH}.deb"

  # ========================================================== michka-kiosk .deb
  local KIO="$BUILD/michka-kiosk-$ARCH"
  mkdir -p "$KIO/DEBIAN" "$KIO/opt/michka" "$KIO/lib/systemd/system" "$KIO/etc/polkit-1/rules.d"
  install -m 0755 "$PKG/kiosk/kiosk.sh" "$KIO/opt/michka/kiosk.sh"
  install -m 0644 "$PKG/systemd/michka-kiosk.service"     "$KIO/lib/systemd/system/michka-kiosk.service"
  # Helper units + polkit rule that let the unprivileged hub (static "michka" user) drive the display without root.
  install -m 0644 "$PKG/systemd/michka-screen-on.service"            "$KIO/lib/systemd/system/michka-screen-on.service"
  install -m 0644 "$PKG/systemd/michka-screen-off.service"           "$KIO/lib/systemd/system/michka-screen-off.service"
  install -m 0644 "$PKG/systemd/michka-kiosk-autostart-on.service"   "$KIO/lib/systemd/system/michka-kiosk-autostart-on.service"
  install -m 0644 "$PKG/systemd/michka-kiosk-autostart-off.service"  "$KIO/lib/systemd/system/michka-kiosk-autostart-off.service"
  install -m 0644 "$PKG/polkit/49-michka.rules"            "$KIO/etc/polkit-1/rules.d/49-michka.rules"
  for f in control postinst prerm postrm; do cp "$PKG/deb/michka-kiosk/DEBIAN/$f" "$KIO/DEBIAN/$f"; done
  sed -i "s/__VERSION__/$VERSION/g" "$KIO/DEBIAN/control"; set_arch "$KIO/DEBIAN/control"
  fixnl "$KIO/DEBIAN/"* "$KIO/opt/michka/kiosk.sh" "$KIO/lib/systemd/system/"*.service "$KIO/etc/polkit-1/rules.d/49-michka.rules"
  chmod 0644 "$KIO/DEBIAN/control"; chmod 0755 "$KIO/DEBIAN/postinst" "$KIO/DEBIAN/prerm" "$KIO/DEBIAN/postrm"
  dpkg-deb --root-owner-group --build "$KIO" "$DIST/michka-kiosk_${VERSION}_${ARCH}.deb" >/dev/null
  echo "==> built michka-kiosk_${VERSION}_${ARCH}.deb"

  # ========================================================== michka-agent .deb
  local AGT="$BUILD/michka-agent-$ARCH"
  mkdir -p "$AGT/DEBIAN" "$AGT/opt/michka" "$AGT/lib/systemd/system" "$AGT/etc/default" "$AGT/usr/lib/tmpfiles.d"
  install -m 0755 "$AGENTBIN" "$AGT/opt/michka/michka_c"
  install -m 0644 "$PKG/systemd/michka-agent.service" "$AGT/lib/systemd/system/michka-agent.service"
  install -m 0644 "$PKG/agent/michka-agent.env" "$AGT/etc/default/michka-agent"
  # The agent unit extracts its bundled native libs to /var/lib/michka-agent-bundle (1777) — the noexec
  # DynamicUser StateDirectory can't hold them. Ship the tmpfiles.d rule that creates it (postinst applies it).
  install -m 0644 "$PKG/tmpfiles/michka.conf" "$AGT/usr/lib/tmpfiles.d/michka.conf"
  for f in control conffiles postinst prerm postrm; do cp "$PKG/deb/michka-agent/DEBIAN/$f" "$AGT/DEBIAN/$f"; done
  sed -i "s/__VERSION__/$VERSION/g" "$AGT/DEBIAN/control"; set_arch "$AGT/DEBIAN/control"
  fixnl "$AGT/DEBIAN/"* "$AGT/lib/systemd/system/michka-agent.service" "$AGT/etc/default/michka-agent" "$AGT/usr/lib/tmpfiles.d/michka.conf"
  chmod 0644 "$AGT/DEBIAN/control" "$AGT/DEBIAN/conffiles"; chmod 0755 "$AGT/DEBIAN/postinst" "$AGT/DEBIAN/prerm" "$AGT/DEBIAN/postrm"
  dpkg-deb --root-owner-group --build "$AGT" "$DIST/michka-agent_${VERSION}_${ARCH}.deb" >/dev/null
  echo "==> built michka-agent_${VERSION}_${ARCH}.deb"

  # ================================================================== tar.gz
  local TARNAME="michka-${VERSION}-${RID}"
  local TAR="$BUILD/tar-$ARCH/$TARNAME"
  mkdir -p "$TAR/systemd" "$TAR/kiosk" "$TAR/agent" "$TAR/polkit" "$TAR/tmpfiles"
  install -m 0755 "$BIN"      "$TAR/michka_s"
  install -m 0755 "$AGENTBIN" "$TAR/michka_c"
  install -m 0755 "$PKG/tarball/install.sh"   "$TAR/install.sh"
  install -m 0755 "$PKG/tarball/uninstall.sh" "$TAR/uninstall.sh"
  install -m 0644 "$PKG/systemd/michka-hub.service"             "$TAR/systemd/michka-hub.service"
  install -m 0644 "$PKG/systemd/michka-kiosk.service"           "$TAR/systemd/michka-kiosk.service"
  install -m 0644 "$PKG/systemd/michka-agent.service"           "$TAR/systemd/michka-agent.service"
  install -m 0644 "$PKG/systemd/michka-screen-on.service"           "$TAR/systemd/michka-screen-on.service"
  install -m 0644 "$PKG/systemd/michka-screen-off.service"          "$TAR/systemd/michka-screen-off.service"
  install -m 0644 "$PKG/systemd/michka-kiosk-autostart-on.service"  "$TAR/systemd/michka-kiosk-autostart-on.service"
  install -m 0644 "$PKG/systemd/michka-kiosk-autostart-off.service" "$TAR/systemd/michka-kiosk-autostart-off.service"
  install -m 0644 "$PKG/polkit/49-michka.rules"                 "$TAR/polkit/49-michka.rules"
  install -m 0755 "$PKG/kiosk/kiosk.sh"                          "$TAR/kiosk/kiosk.sh"
  install -m 0644 "$PKG/agent/michka-agent.env"                 "$TAR/agent/michka-agent.env"
  install -m 0644 "$PKG/tmpfiles/michka.conf"                   "$TAR/tmpfiles/michka.conf"
  seed_dropins "$ROOT/../templates" "$TAR/templates"
  seed_dropins "$ROOT/../widgets"   "$TAR/widgets"
  [ -f "$ROOT/README.md" ] && install -m 0644 "$ROOT/README.md" "$TAR/README.md"
  fixnl "$TAR/install.sh" "$TAR/uninstall.sh" "$TAR/systemd/"* "$TAR/kiosk/kiosk.sh" "$TAR/agent/michka-agent.env" "$TAR/tmpfiles/michka.conf" "$TAR/README.md" 2>/dev/null || true
  tar -C "$BUILD/tar-$ARCH" -czf "$DIST/${TARNAME}.tar.gz" "$TARNAME"
  echo "==> built ${TARNAME}.tar.gz"

  ARTIFACTS="$ARTIFACTS michka-hub_${VERSION}_${ARCH}.deb michka-kiosk_${VERSION}_${ARCH}.deb michka-agent_${VERSION}_${ARCH}.deb ${TARNAME}.tar.gz"
}

# --- amd64 (always) --------------------------------------------------------
HUB_X64=$(resolve_bin michka_s linux-x64 "${BINARY_HUB:-}"   michka_s)
AGT_X64=$(resolve_bin michka_c linux-x64 "${BINARY_AGENT:-}" michka_c)
build_arch amd64 linux-x64 "$HUB_X64" "$AGT_X64"

# --- arm64 (optional, UNTESTED) -------------------------------------------
if [ "$BUILD_ARM" = 1 ]; then
  HUB_ARM=$(resolve_bin michka_s linux-arm64 "${BINARY_HUB_ARM64:-}"   michka_s-arm64)
  AGT_ARM=$(resolve_bin michka_c linux-arm64 "${BINARY_AGENT_ARM64:-}" michka_c-arm64)
  build_arch arm64 linux-arm64 "$HUB_ARM" "$AGT_ARM"
  echo ""
  echo "NOTE: arm64 artifacts are UNTESTED on real ARM hardware — publish them as experimental."
fi

# ================================================================== checksums
( cd "$DIST" && sha256sum $ARTIFACTS > SHA256SUMS )
rm -rf "$BUILD"

echo ""
echo "Artifacts in $DIST:"
( cd "$DIST" && ls -lh $ARTIFACTS SHA256SUMS | awk '{print "  "$9"  "$5}' )
