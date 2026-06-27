#!/bin/sh
# Michka kiosk launcher: read the hub port from michka.conf, wait for the hub to answer, then run
# Chromium full-screen against it. Launched by michka-kiosk.service inside a cage Wayland session.
CONF=/var/lib/michka/michka.conf
PROFILE=/var/lib/michka/chrome-profile

port=5000
if [ -f "$CONF" ]; then
  p=$(grep -oE '"port"[[:space:]]*:[[:space:]]*[0-9]+' "$CONF" | grep -oE '[0-9]+' | head -1)
  [ -n "$p" ] && port=$p
fi

# Audio: start a per-session PipeWire/PulseAudio server so Chromium has sound. WirePlumber
# auto-routes to the machine's default output, so this works on any hardware (no device hardcoding).
# Idempotent — skipped if pipewire is already running for this session.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
if command -v pipewire >/dev/null 2>&1 && ! pgrep -x pipewire >/dev/null 2>&1; then
  pipewire >/dev/null 2>&1 &
  wireplumber >/dev/null 2>&1 &
  pipewire-pulse >/dev/null 2>&1 &
  sleep 1
fi

# Wait for the hub to come up (up to ~30s) before pointing the browser at it.
for i in $(seq 1 30); do
  curl -sf "http://localhost:$port/" >/dev/null 2>&1 && break
  sleep 1
done

# Some distros ship the binary as `chromium`, others as `chromium-browser`.
BROWSER=$(command -v chromium || command -v chromium-browser)
[ -z "$BROWSER" ] && { echo "kiosk.sh: chromium not found" >&2; exit 1; }

exec "$BROWSER" \
  --kiosk --no-sandbox --ozone-platform=wayland --enable-features=UseOzonePlatform \
  --no-first-run --fast --fast-start --disable-translate --disable-infobars --noerrdialogs \
  --disable-session-crashed-bubble --disable-pinch --overscroll-history-navigation=0 \
  --check-for-update-interval=31536000 --user-data-dir="$PROFILE" \
  "http://localhost:$port/"
