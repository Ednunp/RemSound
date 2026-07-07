#!/bin/bash
# uninstall.sh — Remove the RemSound UDP relay and its auto-updater cleanly.
#
# Run with sudo:
#   sudo ./uninstall.sh
#
# Stops and disables both the relay and the updater timer, removes their
# scripts and unit files, and removes the /etc/remsound-relay/ state dir
# (version file + rollback backups). Leaves log files in place — rename or
# delete them yourself if you don't want them kept. Does NOT touch your
# router port-forward — that's separate.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "This uninstaller must run as root. Try: sudo ./uninstall.sh" >&2
  exit 1
fi

# Stop the updater timer first so it can't fire mid-uninstall.
if systemctl list-unit-files remsound-relay-update.timer >/dev/null 2>&1; then
  echo "Stopping remsound-relay-update.timer ..."
  systemctl stop remsound-relay-update.timer 2>/dev/null || true
  systemctl disable remsound-relay-update.timer 2>/dev/null || true
fi

if systemctl list-unit-files remsound-relay-update.service >/dev/null 2>&1; then
  systemctl stop remsound-relay-update.service 2>/dev/null || true
fi

if systemctl list-unit-files remsound-relay.service >/dev/null 2>&1; then
  echo "Stopping remsound-relay.service ..."
  systemctl stop remsound-relay.service 2>/dev/null || true
  echo "Disabling remsound-relay.service ..."
  systemctl disable remsound-relay.service 2>/dev/null || true
fi

for f in \
  /etc/systemd/system/remsound-relay.service \
  /etc/systemd/system/remsound-relay-update.service \
  /etc/systemd/system/remsound-relay-update.timer \
  /usr/local/sbin/remsound-relay.py \
  /usr/local/sbin/remsound-relay-update.sh
do
  if [[ -f "$f" ]]; then
    echo "Removing $f ..."
    rm -f "$f"
  fi
done

if [[ -d /etc/remsound-relay ]]; then
  echo "Removing /etc/remsound-relay/ (version stamp + backup) ..."
  rm -rf /etc/remsound-relay
fi

systemctl daemon-reload

echo
echo "Uninstall complete."
echo "Note: log files left in place — delete them yourself if you don't want them:"
echo "  /var/log/remsound-relay.log"
echo "  /var/log/remsound-relay-update.log"
echo "Note: any router port-forward you added for UDP 47830 is unchanged — remove that yourself if you no longer need it."
