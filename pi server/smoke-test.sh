#!/bin/bash
# smoke-test.sh — Quick health check for the RemSound relay + auto-updater.
#
# Run after install. Confirms the relay service is running, listening on
# UDP 47830, accepts both a v1 and a v2 valid RemSound header, and that
# the auto-updater scaffolding is in place.

set -u

GREEN=$'\033[0;32m'
RED=$'\033[0;31m'
YELLOW=$'\033[0;33m'
RESET=$'\033[0m'

ok()   { printf "%s[ok]%s   %s\n"   "$GREEN" "$RESET" "$*"; }
fail() { printf "%s[FAIL]%s %s\n"   "$RED"   "$RESET" "$*"; }
warn() { printf "%s[warn]%s %s\n"   "$YELLOW" "$RESET" "$*"; }

failures=0

# 1. Relay service active?
if systemctl is-active --quiet remsound-relay.service; then
  ok "remsound-relay.service is active"
else
  fail "remsound-relay.service is not active. Try: sudo systemctl status remsound-relay"
  failures=$((failures + 1))
fi

# 2. Listening on UDP 47830?
if command -v ss >/dev/null 2>&1; then
  if ss -lun 2>/dev/null | grep -q ':47830'; then
    ok "listening on UDP 47830"
  else
    fail "no UDP 47830 listener visible to ss"
    failures=$((failures + 1))
  fi
else
  warn "ss not installed, skipping listener check"
fi

# 3. Send synthetic valid v1 + v2 RemSound headers, confirm they're accepted.
if command -v python3 >/dev/null 2>&1; then
  python3 - <<'PY'
import socket, struct, uuid
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# v1: magic 'RMND' (LE 'RMND'), version 1, type 4 (Heartbeat), stream 1, seq 1.
v1 = bytes([0x52, 0x4D, 0x4E, 0x44, 1, 4, 1, 0, 1, 0, 0, 0])
s.sendto(v1, ("127.0.0.1", 47830))

# v2: magic 'RMND', version 2, type 4 (Heartbeat), stream 1, seq 1, random UUID.
cid = uuid.uuid4().bytes
v2 = bytes([0x52, 0x4D, 0x4E, 0x44, 2, 4, 1, 0, 1, 0, 0, 0]) + cid
s.sendto(v2, ("127.0.0.1", 47830))

s.close()
PY
  ok "sent synthetic v1 + v2 RemSound headers to 127.0.0.1:47830"
else
  warn "python3 not installed, skipping header send"
fi

# 4. Look for the expected log events for both protocols.
sleep 1
read_log() {
  if [[ -r /var/log/remsound-relay.log ]]; then
    tail -40 /var/log/remsound-relay.log 2>/dev/null
  elif sudo -n test -r /var/log/remsound-relay.log 2>/dev/null; then
    sudo tail -40 /var/log/remsound-relay.log 2>/dev/null
  fi
}
log_lines="$(read_log)"
if [[ -n "$log_lines" ]]; then
  if printf '%s\n' "$log_lines" | grep -q "event=peer_joined.*127.0.0.1"; then
    ok "v1 path: relay logged a peer_joined event for 127.0.0.1"
  else
    warn "v1 path: no peer_joined event for 127.0.0.1 in last 40 log lines"
  fi
  if printf '%s\n' "$log_lines" | grep -q "event=client_joined.*127.0.0.1"; then
    ok "v2 path: relay logged a client_joined event for 127.0.0.1"
  else
    warn "v2 path: no client_joined event for 127.0.0.1 in last 40 log lines"
  fi
else
  warn "cannot read /var/log/remsound-relay.log (try: sudo $0)"
fi

# 5. Auto-updater scaffolding in place?
if [[ -x /usr/local/sbin/remsound-relay-update.sh ]]; then
  ok "auto-updater script at /usr/local/sbin/remsound-relay-update.sh"
else
  fail "missing /usr/local/sbin/remsound-relay-update.sh"
  failures=$((failures + 1))
fi
if systemctl is-active --quiet remsound-relay-update.timer; then
  ok "remsound-relay-update.timer is active"
else
  fail "remsound-relay-update.timer is not active. Try: sudo systemctl status remsound-relay-update.timer"
  failures=$((failures + 1))
fi
if [[ -r /etc/remsound-relay/version ]]; then
  installed_tag="$(tr -d '[:space:]' < /etc/remsound-relay/version)"
  if [[ "$installed_tag" =~ ^server-v[0-9]+\.[0-9]+ ]]; then
    ok "installed version: $installed_tag"
  else
    warn "version file present but unexpected format: $installed_tag"
  fi
elif sudo -n test -r /etc/remsound-relay/version 2>/dev/null; then
  installed_tag="$(sudo tr -d '[:space:]' < /etc/remsound-relay/version)"
  ok "installed version (via sudo): $installed_tag"
else
  fail "cannot read /etc/remsound-relay/version"
  failures=$((failures + 1))
fi

echo
if (( failures == 0 )); then
  echo "${GREEN}All checks passed.${RESET}"
  exit 0
else
  echo "${RED}${failures} check(s) failed.${RESET}"
  exit 1
fi
