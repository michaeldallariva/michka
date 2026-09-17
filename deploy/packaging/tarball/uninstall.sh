#!/bin/sh
# Michka hub uninstaller (tarball install).
#
#   sudo ./uninstall.sh           stop + remove the hub and kiosk (keeps /var/lib/michka data)
#   sudo ./uninstall.sh --purge   also delete /var/lib/michka and remove the hub run-user/group that
#                                 the installer created (an existing account you reused is left alone)
set -e

PURGE=0
[ "$1" = "--purge" ] && PURGE=1

[ "$(id -u)" = "0" ] || { echo "uninstall.sh must run as root (use sudo)." >&2; exit 1; }

echo "==> Stopping services"
for svc in michka-kiosk michka-agent michka-hub; do
  systemctl stop "$svc.service" >/dev/null 2>&1 || true
  systemctl disable "$svc.service" >/dev/null 2>&1 || true
done

echo "==> Removing files"
rm -f /etc/systemd/system/michka-kiosk.service
rm -f /etc/systemd/system/michka-agent.service
rm -f /etc/systemd/system/michka-hub.service
rm -f /etc/systemd/system/michka-screen-on.service
rm -f /etc/systemd/system/michka-screen-off.service
rm -f /etc/polkit-1/rules.d/49-michka.rules
# Legacy path from older versions that ran the hub as root under the kiosk.
rm -f /etc/systemd/system/michka-hub.service.d/kiosk.conf
rm -f /etc/systemd/system/michka-hub.service.d/10-run-user.conf
rmdir /etc/systemd/system/michka-hub.service.d 2>/dev/null || true
rm -rf /opt/michka
systemctl daemon-reload >/dev/null 2>&1 || true
systemctl reload polkit.service >/dev/null 2>&1 || true

if [ "$PURGE" = "1" ]; then
  echo "==> Purging data + config"
  rm -rf /var/lib/michka /var/lib/michka-agent
  rm -f /etc/default/michka-agent
  # Remove only the run-user this installer created (recorded in /etc/michka/created-user); an existing
  # account the admin reused is left alone. Then drop the 'michka' group if it is now empty.
  if [ -f /etc/michka/created-user ]; then
    cu=$(cat /etc/michka/created-user 2>/dev/null | tr -d '[:space:]')
    if [ -n "$cu" ] && getent passwd "$cu" >/dev/null 2>&1; then
      deluser --quiet --system "$cu" >/dev/null 2>&1 || userdel "$cu" >/dev/null 2>&1 || true
    fi
  fi
  if getent group michka >/dev/null 2>&1; then
    delgroup --quiet --only-if-empty michka >/dev/null 2>&1 || groupdel michka >/dev/null 2>&1 || true
  fi
  rm -f /etc/michka/created-user 2>/dev/null || true
  rmdir /etc/michka 2>/dev/null || true
fi

echo "Done."
