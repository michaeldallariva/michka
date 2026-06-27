#!/bin/sh
# Michka installer for general Linux (systemd) hosts.
#
# Hub (web dashboard):
#   sudo ./install.sh                      headless hub
#   sudo ./install.sh --kiosk              hub + touchscreen kiosk (cage + Chromium)
#   sudo ./install.sh --port 8080          serve on a custom port (default 5000)
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
while [ $# -gt 0 ]; do
  case "$1" in
    --agent) MODE=agent ;;
    --kiosk) KIOSK=1 ;;
    --port) PORT="$2"; shift ;;
    --port=*) PORT="${1#*=}" ;;
    --hub) HUB="$2"; shift ;;
    --hub=*) HUB="${1#*=}" ;;
    --name) NAME="$2"; shift ;;
    --name=*) NAME="${1#*=}" ;;
    --interval) INTERVAL="$2"; shift ;;
    --interval=*) INTERVAL="${1#*=}" ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

[ "$(id -u)" = "0" ] || { echo "install.sh must run as root (use sudo)." >&2; exit 1; }
[ -d /run/systemd/system ] || { echo "this installer targets systemd hosts." >&2; exit 1; }

DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# Shared: the unprivileged service user.
ensure_user() {
  if ! getent passwd michka >/dev/null 2>&1; then
    adduser --system --group --no-create-home --home /var/lib/michka \
            --shell /usr/sbin/nologin --quiet michka 2>/dev/null || \
    useradd --system --home-dir /var/lib/michka --shell /usr/sbin/nologin michka || true
  fi
}

# ======================================================================= AGENT
if [ "$MODE" = "agent" ]; then
  [ -f "$DIR/michka_c" ] || { echo "michka_c not found next to install.sh" >&2; exit 1; }

  echo "==> Installing the Michka agent to /opt/michka"
  install -d -m 0755 /opt/michka
  install -m 0755 "$DIR/michka_c" /opt/michka/michka_c
  ensure_user

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

echo "==> Creating the michka service user + /var/lib/michka"
ensure_user
install -d -m 0755 -o michka -g michka /var/lib/michka

# Seed the on-disk templates + widgets bundled with the tarball into the data dir the hub reads from.
# Per-folder, never clobbering a drop-in the user changed or added, so re-running keeps customisations.
for kind in templates widgets; do
  [ -d "$DIR/$kind" ] || continue
  install -d -m 0755 -o michka -g michka "/var/lib/michka/$kind"
  for d in "$DIR/$kind"/*/; do
    [ -d "$d" ] || continue
    name=$(basename "$d")
    [ -e "/var/lib/michka/$kind/$name" ] || cp -r "$d" "/var/lib/michka/$kind/$name"
  done
  chown -R michka:michka "/var/lib/michka/$kind" 2>/dev/null || true
done

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
      cage chromium libegl1 libgles2 libgl1-mesa-dri grim wlr-randr curl fonts-noto-cjk || \
      echo "    (apt reported errors — install the packages above manually if the kiosk fails)"
  else
    echo "    No apt detected. Install these with your package manager before starting the kiosk:"
    echo "      cage chromium libegl1 libgles2 libgl1-mesa-dri grim wlr-randr curl fonts-noto-cjk"
  fi

  install -d -m 0755 /etc/systemd/system/michka-hub.service.d
  install -m 0644 "$DIR/systemd/michka-hub.kiosk-override.conf" \
          /etc/systemd/system/michka-hub.service.d/kiosk.conf
  install -m 0644 "$DIR/systemd/michka-kiosk.service" /etc/systemd/system/michka-kiosk.service

  systemctl daemon-reload
  systemctl restart michka-hub.service
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
echo "  Status:  systemctl status michka-hub"
echo "  Data:    /var/lib/michka  (michka.conf, michka.db, templates/, widgets/)"
