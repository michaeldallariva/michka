#!/bin/sh
# Shared install-time helpers, sourced by BOTH the hub .deb's postinst
# (packaging/deb/michka-hub/DEBIAN/postinst) and the tarball installer (packaging/tarball/install.sh).
# These two used to carry byte-for-byte duplicate copies of this logic; this file is the one place it
# lives now, shipped as a regular package file by build.sh (deb: /opt/michka/lib/install-helpers.sh,
# tarball: <unpack-dir>/lib/install-helpers.sh) so each installer can `.` it at install time.
# Not meant to be run directly.

# --- setup_run_user ----------------------------------------------------------------------------
# Create/select the hub's run-user, add it to the "michka" group (the polkit grant key), and write a
# systemd drop-in for any non-default/existing/root choice. The caller must set $RUN_USER first (the
# .deb resolves it via debconf; the tarball installer via --user or a TTY prompt — the two differ only
# in HOW they pick the name, not in what happens once it's picked, which is what this function covers).
# Records a user this install CREATED (as opposed to an existing account it just joined to the group)
# in /etc/michka/created-user, so uninstall/postrm --purge can remove only that one, never a
# pre-existing account. The base unit (michka-hub.service) defaults to User=michka Group=michka, so
# choosing the default "michka" needs no drop-in at all.
setup_run_user() {
  if ! getent group michka >/dev/null 2>&1; then
    addgroup --system michka >/dev/null 2>&1 || groupadd -r michka >/dev/null 2>&1 || true
  fi

  if [ "$RUN_USER" = "root" ]; then
    :
  elif getent passwd "$RUN_USER" >/dev/null 2>&1; then
    # Existing account: add to the michka group, leave everything else untouched.
    adduser --quiet "$RUN_USER" michka >/dev/null 2>&1 || usermod -aG michka "$RUN_USER" >/dev/null 2>&1 || true
  else
    adduser --system --group --home /var/lib/michka --no-create-home \
      --shell /usr/sbin/nologin "$RUN_USER" >/dev/null 2>&1 || \
    useradd -r -M -d /var/lib/michka -s /usr/sbin/nologin "$RUN_USER" >/dev/null 2>&1 || true
    adduser --quiet "$RUN_USER" michka >/dev/null 2>&1 || usermod -aG michka "$RUN_USER" >/dev/null 2>&1 || true
    mkdir -p /etc/michka && echo "$RUN_USER" > /etc/michka/created-user
  fi

  # Apply a non-default run-user via a drop-in (the base unit defaults to User=michka Group=michka).
  DROPIN=/etc/systemd/system/michka-hub.service.d/10-run-user.conf
  if [ "$RUN_USER" != "michka" ]; then
    mkdir -p /etc/systemd/system/michka-hub.service.d
    if [ "$RUN_USER" = "root" ]; then
      printf '[Service]\nUser=root\nGroup=root\n' > "$DROPIN"
    else
      PG=$(id -gn "$RUN_USER" 2>/dev/null || echo "$RUN_USER")
      printf '[Service]\nUser=%s\nGroup=%s\nSupplementaryGroups=michka\n' "$RUN_USER" "$PG" > "$DROPIN"
    fi
  else
    rm -f "$DROPIN" 2>/dev/null || true
  fi
}

# --- setup_kiosk_user ---------------------------------------------------------------------------
# Create the dedicated, locked, no-login "kiosk" system user the display stack (cage + Chromium) runs
# as — see michka-kiosk.service. Unlike the hub's run-user this name is fixed (not admin-chosen): it's
# an internal technical account, not something reached over the network. Membership in video/render
# (DRM/KMS) + input (evdev) + audio (ALSA fallback) is a defence-in-depth belt-and-suspenders grant —
# the primary mechanism is systemd-logind, which hands the session device ACLs automatically because
# the unit opens a real PAM/logind session (PAMName=michka-kiosk, a dedicated PAM service shipped as
# /etc/pam.d/michka-kiosk that explicitly classes the session "user"/"wayland" on seat0 — the stock
# /etc/pam.d/login's pam_systemd line has no class= argument and leaves a locked system account's
# session with no seat, see CLAUDE.md "Done — Batch 5"), the same mechanism any unprivileged Wayland
# compositor (GDM/sddm greeters, etc.) relies on. Running cage/Chromium as root with --no-sandbox (the
# old approach) meant a compromised renderer got unsandboxed root; this is what replaces it. Also owns
# $HOME (/var/lib/michka-kiosk — adduser's --no-create-home only registers the path, it doesn't create
# the directory, and Chromium/WirePlumber/cage all crash without a real writable $HOME) and the
# Chromium profile dir (<dataDir>/chrome-profile) so the kiosk can read/write both without needing
# hub-group membership — the parent data dir stays owned by the hub's run-user.
# Records whether THIS install created the account in /etc/michka/created-kiosk-user so purge removes
# only that, never a pre-existing "kiosk" account some other package/admin set up.
setup_kiosk_user() {
  DATA_DIR="${1:-/var/lib/michka}"

  if ! getent group kiosk >/dev/null 2>&1; then
    addgroup --system kiosk >/dev/null 2>&1 || groupadd -r kiosk >/dev/null 2>&1 || true
  fi

  if ! getent passwd kiosk >/dev/null 2>&1; then
    adduser --system --ingroup kiosk --home /var/lib/michka-kiosk --no-create-home \
      --shell /usr/sbin/nologin kiosk >/dev/null 2>&1 || \
    useradd -r -g kiosk -M -d /var/lib/michka-kiosk -s /usr/sbin/nologin kiosk >/dev/null 2>&1 || true
    mkdir -p /etc/michka && echo kiosk > /etc/michka/created-kiosk-user
  fi
  for g in video render input audio; do
    getent group "$g" >/dev/null 2>&1 || continue
    adduser --quiet kiosk "$g" >/dev/null 2>&1 || usermod -aG "$g" kiosk >/dev/null 2>&1 || true
  done

  # --no-create-home/-M means the $HOME path is only registered in /etc/passwd, never created on
  # disk. Chromium, WirePlumber, and cage all want a real, writable $HOME for cache/state dirs
  # (XDG_CACHE_HOME/XDG_STATE_HOME default under it); without this Chromium crashes on launch.
  mkdir -p /var/lib/michka-kiosk
  chown kiosk:kiosk /var/lib/michka-kiosk
  chmod 0750 /var/lib/michka-kiosk

  mkdir -p "$DATA_DIR/chrome-profile"
  chown -R kiosk:kiosk "$DATA_DIR/chrome-profile" 2>/dev/null || true
  chmod 0700 "$DATA_DIR/chrome-profile" 2>/dev/null || true
}

# --- seed_installed_dropins ---------------------------------------------------------------------
# Seed on-disk templates/widgets from a shipped source directory into the hub's data dir, per-folder,
# never clobbering a drop-in the user changed or added — so upgrades/re-runs keep customisations.
# (Distinct from build.sh's seed_dropins(), which runs at PACKAGE-BUILD time, on the build host, to
# bundle the canonical templates/widgets into the .deb/tarball in the first place. This one runs at
# INSTALL time, on the target host, against whatever's already in /var/lib/michka.)
seed_installed_dropins() {  # seed_installed_dropins <src-dir> <dst-dir>
  src="$1"; dst="$2"
  [ -d "$src" ] || return 0
  mkdir -p "$dst"
  for d in "$src"/*/; do
    [ -d "$d" ] || continue
    name=$(basename "$d")
    [ -e "$dst/$name" ] || cp -r "$d" "$dst/$name"
  done
}
