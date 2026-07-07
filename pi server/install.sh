#!/bin/bash
# install.sh — RemSound UDP relay installer for a stock Raspberry Pi (or any
# systemd Linux). Idempotent: re-running it just refreshes the files and
# restarts the service.
#
# Run with sudo from inside this folder:
#   sudo ./install.sh
#
# What it does:
#   1. Sanity checks: systemd present, python3 present, curl present.
#   2. Copies remsound-relay.py to /usr/local/sbin/.
#   3. Copies remsound-relay.service to /etc/systemd/system/.
#   4. Copies the auto-updater script + service + timer.
#   5. Creates empty log files.
#   6. Writes /etc/remsound-relay/version with the bundled tag.
#   7. Snapshots the current install to /etc/remsound-relay/backup/ so the
#      auto-updater has somewhere to roll back to on a bad future release.
#   8. systemctl daemon-reload, enable + start the relay + updater timer.
#   9. Prints status and the last few log lines.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "This installer must run as root. Try: sudo ./install.sh" >&2
  exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"

REQUIRED=(
  remsound-relay.py
  remsound-relay.service
  remsound-relay-update.sh
  remsound-relay-update.service
  remsound-relay-update.timer
  VERSION
)
for f in "${REQUIRED[@]}"; do
  if [[ ! -f "$SCRIPT_DIR/$f" ]]; then
    echo "Bundle is missing $f. Run the installer from inside the unzipped bundle folder." >&2
    exit 1
  fi
done

if ! command -v systemctl >/dev/null 2>&1; then
  echo "systemctl not found. This bundle expects a systemd-based Linux." >&2
  exit 1
fi
if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 not found. Install it first: sudo apt-get install -y python3" >&2
  exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "curl not found. Install it first: sudo apt-get install -y curl" >&2
  exit 1
fi

VERSION_TAG="$(tr -d '[:space:]' < "$SCRIPT_DIR/VERSION")"
echo "Installing RemSound relay bundle $VERSION_TAG ..."

echo "Installing relay script to /usr/local/sbin/remsound-relay.py ..."
install -o root -g root -m 755 "$SCRIPT_DIR/remsound-relay.py" /usr/local/sbin/remsound-relay.py
python3 -m py_compile /usr/local/sbin/remsound-relay.py

echo "Installing systemd unit to /etc/systemd/system/remsound-relay.service ..."
install -o root -g root -m 644 "$SCRIPT_DIR/remsound-relay.service" /etc/systemd/system/remsound-relay.service

echo "Installing auto-updater script to /usr/local/sbin/remsound-relay-update.sh ..."
install -o root -g root -m 755 "$SCRIPT_DIR/remsound-relay-update.sh" /usr/local/sbin/remsound-relay-update.sh

echo "Installing auto-updater systemd units ..."
install -o root -g root -m 644 "$SCRIPT_DIR/remsound-relay-update.service" /etc/systemd/system/remsound-relay-update.service
install -o root -g root -m 644 "$SCRIPT_DIR/remsound-relay-update.timer" /etc/systemd/system/remsound-relay-update.timer

echo "Ensuring log files exist ..."
touch /var/log/remsound-relay.log /var/log/remsound-relay-update.log
chown root:root /var/log/remsound-relay.log /var/log/remsound-relay-update.log
chmod 0644 /var/log/remsound-relay.log /var/log/remsound-relay-update.log

echo "Writing version stamp ..."
mkdir -p /etc/remsound-relay /etc/remsound-relay/backup
printf '%s\n' "$VERSION_TAG" > /etc/remsound-relay/version

echo "Snapshotting installed files to /etc/remsound-relay/backup/ for rollback ..."
rm -rf /etc/remsound-relay/backup
mkdir -p /etc/remsound-relay/backup
for f in \
  /usr/local/sbin/remsound-relay.py \
  /usr/local/sbin/remsound-relay-update.sh \
  /etc/systemd/system/remsound-relay.service \
  /etc/systemd/system/remsound-relay-update.service \
  /etc/systemd/system/remsound-relay-update.timer \
  /etc/remsound-relay/version
do
  if [[ -f "$f" ]]; then
    cp -a "$f" "/etc/remsound-relay/backup/$(basename "$f")"
  fi
done

echo "Reloading systemd ..."
systemctl daemon-reload

echo "Enabling and starting remsound-relay.service ..."
systemctl enable remsound-relay.service >/dev/null
systemctl restart remsound-relay.service

echo "Enabling and starting remsound-relay-update.timer ..."
systemctl enable remsound-relay-update.timer >/dev/null
systemctl restart remsound-relay-update.timer

sleep 1

echo
echo "=== relay service status ==="
systemctl --no-pager --full status remsound-relay.service || true

echo
echo "=== updater timer status ==="
systemctl --no-pager status remsound-relay-update.timer || true

echo
echo "=== last 10 relay log lines ==="
tail -n 10 /var/log/remsound-relay.log 2>/dev/null || echo "(log empty so far)"

echo
echo "=== listening socket check ==="
if command -v ss >/dev/null 2>&1; then
  ss -lunp 2>/dev/null | grep ':47830' || echo "WARNING: no listener on UDP 47830 — see service status above."
else
  echo "(ss not installed, skipping)"
fi

echo
echo "Install complete. The relay is running on UDP 47830."
echo "Installed version: $VERSION_TAG"
echo "Auto-updates: enabled (hourly via remsound-relay-update.timer)."
echo
echo "To disable auto-updates: sudo systemctl disable --now remsound-relay-update.timer"
echo "To check for updates manually: sudo systemctl start remsound-relay-update.service"
echo "Update history log: /var/log/remsound-relay-update.log"
echo "See README.md for full operational notes."
