#!/bin/sh
# Michka hub uninstaller (tarball install).
#
#   sudo ./uninstall.sh           stop + remove the hub and kiosk (keeps /var/lib/michka data)
#   sudo ./uninstall.sh --purge   also delete /var/lib/michka and the michka user
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
rm -f /etc/systemd/system/michka-hub.service.d/kiosk.conf
rmdir /etc/systemd/system/michka-hub.service.d 2>/dev/null || true
rm -rf /opt/michka
systemctl daemon-reload >/dev/null 2>&1 || true

if [ "$PURGE" = "1" ]; then
  echo "==> Purging data + config + service user"
  rm -rf /var/lib/michka
  rm -f /etc/default/michka-agent
  if getent passwd michka >/dev/null 2>&1; then
    deluser --quiet --system michka >/dev/null 2>&1 || userdel michka >/dev/null 2>&1 || true
  fi
fi

echo "Done."
