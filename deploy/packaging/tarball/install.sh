#!/bin/sh
# Michka installer for general Linux (systemd) hosts.
#
# Hub (web dashboard):
#   sudo ./install.sh                      headless hub (prompts for the run-user, default michka)
#   sudo ./install.sh --kiosk              hub + touchscreen kiosk (cage + Chromium)
#   sudo ./install.sh --port 8080          serve on a custom port (default 5000)
#   sudo ./install.sh --user NAME          run the hub as NAME (skip the prompt; "root" = run as root)
#
# Agent (push this machine's metrics to a hub, no web UI):
#   sudo ./install.sh --agent --hub http://HUB:5000 [--name LABEL] [--interval MS]
#
# Run from the unpacked tarball directory. Re-running upgrades the binary in place.
set -e

MODE=hub
KIOSK=0
PORT=""
HUB=""
NAME=""
INTERVAL=""
RUN_USER=""   # hub run-user; empty => prompt (interactive) or default to "michka"
while [ $# -gt 0 ]; do
  case "$1" in
    --agent) MODE=agent ;;
    --kiosk) KIOSK=1 ;;
    --user) RUN_USER="$2"; shift ;;
    --user=*) RUN_USER="${1#*=}" ;;
    --port) PORT="$2"; shift ;;
    --port=*) PORT="${1#*=}" ;;
    --hub) HUB="$2"; shift ;;
    --hub=*) HUB="${1#*=}" ;;
    --name) NAME="$2"; shift ;;
    --name=*) NAME="${1#*=}" ;;
    --interval) INTERVAL="$2"; shift ;;
    --interval=*) INTERVAL="${1#*=}" ;;
    -h|--help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

[ "$(id -u)" = "0" ] || { echo "install.sh must run as root (use sudo)." >&2; exit 1; }
[ -d /run/systemd/system ] || { echo "this installer targets systemd hosts." >&2; exit 1; }

DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# The agent runs unprivileged via systemd DynamicUser=yes — no account is created or needed. The hub
# needs a STATIC system user instead (the reference dbus-daemon refuses DynamicUser bus connections,
# which would break its systemctl-based kiosk/display actions). The run-user is chosen below.

# Create/select the hub's run-user, add it to the "michka" group (the polkit grant key), and write a
# systemd drop-in for any non-default/existing/root choice. Records a user WE create in
# /etc/michka/created-user so uninstall can remove only that. Sets RUN_USER to the final name.
setup_run_user() {
  # Resolve the name: --user wins; else prompt on a TTY; else default to michka.
  if [ -z "$RUN_USER" ]; then
    if [ -t 0 ]; then
      printf 'The hub runs unprivileged as a dedicated system user.\n'
      printf 'Run-user name [michka] (enter an existing name to reuse it, or "root" to run as root): '
      read -r ans || ans=""
      RUN_USER=$(printf '%s' "$ans" | tr -d '[:space:]')
    fi
    [ -n "$RUN_USER" ] || RUN_USER=michka
  fi

  # The "michka" group authorises the hub's kiosk/display actions via polkit; always present.
  if ! getent group michka >/dev/null 2>&1; then
    addgroup --system michka >/dev/null 2>&1 || groupadd -r michka >/dev/null 2>&1 || true
  fi

  if [ "$RUN_USER" = "root" ]; then
    echo "==> Hub will run as root (no dedicated user created)."
  elif getent passwd "$RUN_USER" >/dev/null 2>&1; then
    echo "==> Hub will run as existing user '$RUN_USER' (added to group 'michka', otherwise untouched)."
    adduser --quiet "$RUN_USER" michka >/dev/null 2>&1 || usermod -aG michka "$RUN_USER" >/dev/null 2>&1 || true
  else
    echo "==> Creating locked system user '$RUN_USER' to run the hub."
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

# ======================================================================= AGENT
if [ "$MODE" = "agent" ]; then
  [ -f "$DIR/michka_c" ] || { echo "michka_c not found next to install.sh" >&2; exit 1; }

  echo "==> Installing the Michka agent to /opt/michka"
  install -d -m 0755 /opt/michka
  install -m 0755 "$DIR/michka_c" /opt/michka/michka_c

  # The agent extracts its bundled native libs to an EXEC dir; the DynamicUser StateDirectory is noexec,
  # so the unit uses /var/lib/michka-agent-bundle (1777). Create it (tmpfiles rule + a mkdir fallback) or
  # the service crash-loops at the systemd NAMESPACE step.
  if [ -f "$DIR/tmpfiles/michka.conf" ]; then
    install -d -m 0755 /usr/lib/tmpfiles.d
    install -m 0644 "$DIR/tmpfiles/michka.conf" /usr/lib/tmpfiles.d/michka.conf
    systemd-tmpfiles --create /usr/lib/tmpfiles.d/michka.conf >/dev/null 2>&1 || true
  fi
  [ -d /var/lib/michka-agent-bundle ] || { mkdir -p /var/lib/michka-agent-bundle && chmod 1777 /var/lib/michka-agent-bundle; }

  if [ -n "$HUB" ]; then
    ARGS="--hub $HUB"
    [ -n "$NAME" ] && ARGS="$ARGS --name $NAME"
    [ -n "$INTERVAL" ] && ARGS="$ARGS --interval $INTERVAL"
    printf 'MICHKA_ARGS="%s"\n' "$ARGS" > /etc/default/michka-agent
  elif [ ! -f /etc/default/michka-agent ]; then
    install -m 0644 "$DIR/agent/michka-agent.env" /etc/default/michka-agent
  fi

  install -m 0644 "$DIR/systemd/michka-agent.service" /etc/systemd/system/michka-agent.service
  systemctl daemon-reload
  systemctl enable michka-agent.service >/dev/null 2>&1 || true

  if grep -q 'HUB-HOST' /etc/default/michka-agent 2>/dev/null; then
    echo ""
    echo "Set the hub in /etc/default/michka-agent (MICHKA_ARGS), then:"
    echo "  sudo systemctl start michka-agent"
  else
    systemctl restart michka-agent.service
    echo ""
    echo "Done. Agent is pushing to the hub. Logs: journalctl -u michka-agent -f"
  fi
  exit 0
fi

# ========================================================================= HUB
[ -f "$DIR/michka_s" ] || { echo "michka_s not found next to install.sh" >&2; exit 1; }

echo "==> Installing the Michka hub binary to /opt/michka"
install -d -m 0755 /opt/michka
install -m 0755 "$DIR/michka_s" /opt/michka/michka_s

setup_run_user

echo "==> Preparing /var/lib/michka"
install -d -m 0755 /var/lib/michka

# Seed the on-disk templates + widgets bundled with the tarball into the data dir the hub reads from.
# Per-folder, never clobbering a drop-in the user changed or added, so re-running keeps customisations.
for kind in templates widgets; do
  [ -d "$DIR/$kind" ] || continue
  install -d -m 0755 "/var/lib/michka/$kind"
  for d in "$DIR/$kind"/*/; do
    [ -d "$d" ] || continue
    name=$(basename "$d")
    [ -e "/var/lib/michka/$kind/$name" ] || cp -r "$d" "/var/lib/michka/$kind/$name"
  done
done

# Own the data dir as the run-user (systemd also re-applies this for StateDirectory on each start).
OWN_GROUP=$(id -gn "$RUN_USER" 2>/dev/null || echo "$RUN_USER")
chown -R "$RUN_USER":"$OWN_GROUP" /var/lib/michka 2>/dev/null || true

echo "==> Installing the michka-hub systemd service"
install -m 0644 "$DIR/systemd/michka-hub.service" /etc/systemd/system/michka-hub.service
if [ -n "$PORT" ]; then
  sed -i "s#^ExecStart=.*#ExecStart=/opt/michka/michka_s --db /var/lib/michka/michka.db --port $PORT#" \
      /etc/systemd/system/michka-hub.service
fi

systemctl daemon-reload
systemctl enable michka-hub.service >/dev/null 2>&1 || true
systemctl restart michka-hub.service

if [ "$KIOSK" = "1" ]; then
  echo "==> Setting up the kiosk display add-on"
  install -m 0755 "$DIR/kiosk/kiosk.sh" /opt/michka/kiosk.sh

  if command -v apt-get >/dev/null 2>&1; then
    echo "    Installing the display stack via apt..."
    DEBIAN_FRONTEND=noninteractive apt-get update -qq || true
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      cage chromium libegl1 libgles2 libgl1-mesa-dri grim wlr-randr curl fonts-noto-cjk polkitd || \
      echo "    (apt reported errors — install the packages above manually if the kiosk fails)"
  else
    echo "    No apt detected. Install these with your package manager before starting the kiosk:"
    echo "      cage chromium libegl1 libgles2 libgl1-mesa-dri grim wlr-randr curl fonts-noto-cjk polkitd"
  fi

  # The hub stays unprivileged. Install the helper units + polkit rule that let it drive the panel
  # (screen power, kiosk restart, exit-to-login) through systemctl without running as root.
  install -m 0644 "$DIR/systemd/michka-kiosk.service"      /etc/systemd/system/michka-kiosk.service
  install -m 0644 "$DIR/systemd/michka-screen-on.service"           /etc/systemd/system/michka-screen-on.service
  install -m 0644 "$DIR/systemd/michka-screen-off.service"          /etc/systemd/system/michka-screen-off.service
  install -m 0644 "$DIR/systemd/michka-kiosk-autostart-on.service"  /etc/systemd/system/michka-kiosk-autostart-on.service
  install -m 0644 "$DIR/systemd/michka-kiosk-autostart-off.service" /etc/systemd/system/michka-kiosk-autostart-off.service
  install -d -m 0755 /etc/polkit-1/rules.d
  install -m 0644 "$DIR/polkit/49-michka.rules" /etc/polkit-1/rules.d/49-michka.rules

  systemctl daemon-reload
  systemctl reload polkit.service >/dev/null 2>&1 || systemctl restart polkit.service >/dev/null 2>&1 || true
  systemctl enable michka-kiosk.service >/dev/null 2>&1 || true
  systemctl start michka-kiosk.service || \
    echo "    Kiosk did not start — check 'journalctl -u michka-kiosk' (display stack / tty1 in use)."
fi

PORT_SHOWN=${PORT:-5000}
IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo ""
echo "Done. Michka hub is running."
echo "  Local:   http://localhost:${PORT_SHOWN}/"
[ -n "$IP" ] && echo "  Network: http://${IP}:${PORT_SHOWN}/"
echo "  Runs as: ${RUN_USER}  (unprivileged; in group 'michka')"
echo "  Status:  systemctl status michka-hub"
echo "  Data:    /var/lib/michka  (michka.conf, michka.db, templates/, widgets/)"
