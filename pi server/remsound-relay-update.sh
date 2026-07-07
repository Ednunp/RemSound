#!/bin/bash
# remsound-relay-update.sh — periodic GitHub-release-based updater for the
# RemSound relay. Designed to run from a systemd .timer; safe to run by hand
# too. Idempotent: if there is no newer release, exits 0 quickly with no
# system changes.
#
# What it does:
#   1. Read the installed tag from /etc/remsound-relay/version.
#   2. Query the GitHub Releases API for tags starting with "server-".
#   3. If the latest is newer than the installed tag:
#      a. Download the matching tarball asset.
#      b. Snapshot current installed files to /etc/remsound-relay/backup/.
#      c. Stop the relay service.
#      d. Replace the relay files with the new tarball contents.
#      e. systemctl daemon-reload + start.
#      f. Wait 3s and check the service is active.
#         If yes: write the new tag, log success, exit 0.
#         If no:  restore from backup, restart, log failure, exit 1.
#   4. Log everything to /var/log/remsound-relay-update.log.
#
# Failure modes are defensive — a broken updater run leaves the previously
# installed version running, never less.

set -euo pipefail

# -------- configuration ------------------------------------------------------

REPO="${REMSOUND_UPDATE_REPO:-Ednunp/RemSound}"
TAG_PREFIX="${REMSOUND_UPDATE_TAG_PREFIX:-server-}"
ASSET_PATTERN="${REMSOUND_UPDATE_ASSET_PATTERN:-remsound-server-.*\.tar\.gz$}"

INSTALL_BIN="/usr/local/sbin/remsound-relay.py"
INSTALL_UPDATER="/usr/local/sbin/remsound-relay-update.sh"
INSTALL_SERVICE="/etc/systemd/system/remsound-relay.service"
INSTALL_UPDATE_SERVICE="/etc/systemd/system/remsound-relay-update.service"
INSTALL_UPDATE_TIMER="/etc/systemd/system/remsound-relay-update.timer"

STATE_DIR="/etc/remsound-relay"
VERSION_FILE="$STATE_DIR/version"
BACKUP_DIR="$STATE_DIR/backup"
LOG_FILE="/var/log/remsound-relay-update.log"

SERVICE_NAME="remsound-relay.service"
HEALTH_WAIT_SECONDS=3

# Script-global mktemp working directory. Set by main(); cleaned up by the
# EXIT trap below. Kept at script scope (not function-local) so the trap can
# still see it under `set -u` after main returns.
WORK_DIR=""

cleanup_work_dir() {
    if [[ -n "$WORK_DIR" && -d "$WORK_DIR" ]]; then
        rm -rf "$WORK_DIR"
    fi
}
trap cleanup_work_dir EXIT

# -------- helpers ------------------------------------------------------------

log() {
    local ts msg
    ts="$(date '+%Y-%m-%d %H:%M:%S')"
    msg="$ts $*"
    printf '%s\n' "$msg" | tee -a "$LOG_FILE" >&2
}

require_root() {
    if [[ $EUID -ne 0 ]]; then
        log "ERROR: this script must run as root"
        exit 1
    fi
}

ensure_dirs() {
    mkdir -p "$STATE_DIR" "$BACKUP_DIR" "$(dirname "$LOG_FILE")"
    touch "$LOG_FILE"
}

read_current_version() {
    if [[ -f "$VERSION_FILE" ]]; then
        local v
        v="$(tr -d '[:space:]' < "$VERSION_FILE")"
        if [[ -n "$v" ]]; then
            printf '%s' "$v"
            return
        fi
    fi
    printf '%s' "${TAG_PREFIX}v0"
}

# Parse a tag like "server-v2.10" into a comparable numeric form.
# Outputs MAJOR.MINOR; both default to 0 if the tag is unparseable.
tag_to_version() {
    local tag="$1"
    # strip the prefix
    tag="${tag#"$TAG_PREFIX"}"
    # strip a leading "v" if present
    tag="${tag#v}"
    local major minor
    major="${tag%%.*}"
    minor="${tag#*.}"
    # if there's no dot, minor==major. Treat as MAJOR.0.
    if [[ "$minor" == "$tag" ]]; then
        minor="0"
    fi
    # keep only digits — survive things like "v2.0-rc1" by ignoring the suffix.
    major="${major//[^0-9]/}"
    minor="${minor//[^0-9]/}"
    : "${major:=0}"
    : "${minor:=0}"
    printf '%s.%s' "$major" "$minor"
}

# Returns 0 if $1 > $2 (i.e. left tag is newer), 1 otherwise.
tag_newer_than() {
    local left right lv rv
    left="$(tag_to_version "$1")"
    right="$(tag_to_version "$2")"
    # Numeric compare major then minor.
    lv="${left%.*}"; rv="${right%.*}"
    if (( lv > rv )); then return 0; fi
    if (( lv < rv )); then return 1; fi
    lv="${left#*.}"; rv="${right#*.}"
    if (( lv > rv )); then return 0; fi
    return 1
}

# -------- GitHub releases query ---------------------------------------------

# Fetch the releases list and pick the latest server-tag release.
# Outputs three lines: tag, asset_url, asset_name. Exits non-zero on no match.
get_latest_release() {
    local json
    if ! json="$(curl --fail --silent --show-error --max-time 30 \
        -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/${REPO}/releases?per_page=30" 2>&1)"; then
        log "ERROR: failed to query GitHub releases: $json"
        return 2
    fi

    REPO="$REPO" TAG_PREFIX="$TAG_PREFIX" ASSET_PATTERN="$ASSET_PATTERN" \
    python3 - "$json" <<'PY'
import json, os, re, sys

raw = sys.argv[1] if len(sys.argv) > 1 else ""
prefix = os.environ.get("TAG_PREFIX", "server-")
asset_re = re.compile(os.environ.get("ASSET_PATTERN", r"remsound-server-.*\.tar\.gz$"))

try:
    releases = json.loads(raw)
except Exception as exc:
    sys.stderr.write(f"json parse failed: {exc}\n")
    sys.exit(3)

if not isinstance(releases, list):
    sys.stderr.write(f"unexpected releases response shape\n")
    sys.exit(3)

def parse_version(tag):
    # strip prefix
    if tag.startswith(prefix):
        tag = tag[len(prefix):]
    if tag.startswith("v"):
        tag = tag[1:]
    parts = tag.split(".")
    out = []
    for p in parts:
        digits = "".join(c for c in p if c.isdigit())
        out.append(int(digits) if digits else 0)
    if len(out) < 2:
        out.append(0)
    return tuple(out)

candidates = []
for r in releases:
    if not isinstance(r, dict):
        continue
    tag = r.get("tag_name") or ""
    if not tag.startswith(prefix):
        continue
    if r.get("draft") or r.get("prerelease"):
        continue
    asset = None
    for a in r.get("assets") or []:
        name = (a or {}).get("name") or ""
        if asset_re.search(name):
            asset = a
            break
    if asset is None:
        continue
    url = asset.get("browser_download_url") or ""
    name = asset.get("name") or ""
    if not url:
        continue
    candidates.append((parse_version(tag), tag, url, name))

if not candidates:
    sys.stderr.write("no eligible server-* releases found\n")
    sys.exit(4)

candidates.sort(reverse=True)
_, tag, url, name = candidates[0]
print(tag)
print(url)
print(name)
PY
}

# -------- backup + install ---------------------------------------------------

snapshot_backup() {
    log "snapshotting current install to $BACKUP_DIR"
    rm -rf "$BACKUP_DIR"
    mkdir -p "$BACKUP_DIR"
    for f in \
        "$INSTALL_BIN" "$INSTALL_UPDATER" \
        "$INSTALL_SERVICE" "$INSTALL_UPDATE_SERVICE" "$INSTALL_UPDATE_TIMER" \
        "$VERSION_FILE"
    do
        if [[ -f "$f" ]]; then
            cp -a "$f" "$BACKUP_DIR/$(basename "$f")"
        fi
    done
}

restore_backup() {
    log "rolling back from $BACKUP_DIR"
    for f in \
        "$INSTALL_BIN" "$INSTALL_UPDATER" \
        "$INSTALL_SERVICE" "$INSTALL_UPDATE_SERVICE" "$INSTALL_UPDATE_TIMER" \
        "$VERSION_FILE"
    do
        local backup="$BACKUP_DIR/$(basename "$f")"
        if [[ -f "$backup" ]]; then
            cp -a "$backup" "$f"
        fi
    done
    systemctl daemon-reload
    systemctl start "$SERVICE_NAME" || true
}

install_from_staging() {
    local staging="$1"
    # Required files: remsound-relay.py + remsound-relay.service.
    if [[ ! -f "$staging/remsound-relay.py" ]]; then
        log "ERROR: staging missing remsound-relay.py"
        return 1
    fi
    install -o root -g root -m 755 "$staging/remsound-relay.py" "$INSTALL_BIN"
    python3 -m py_compile "$INSTALL_BIN"
    if [[ -f "$staging/remsound-relay.service" ]]; then
        install -o root -g root -m 644 "$staging/remsound-relay.service" "$INSTALL_SERVICE"
    fi
    if [[ -f "$staging/remsound-relay-update.sh" ]]; then
        install -o root -g root -m 755 "$staging/remsound-relay-update.sh" "$INSTALL_UPDATER"
    fi
    if [[ -f "$staging/remsound-relay-update.service" ]]; then
        install -o root -g root -m 644 "$staging/remsound-relay-update.service" "$INSTALL_UPDATE_SERVICE"
    fi
    if [[ -f "$staging/remsound-relay-update.timer" ]]; then
        install -o root -g root -m 644 "$staging/remsound-relay-update.timer" "$INSTALL_UPDATE_TIMER"
    fi
    return 0
}

# -------- main flow ----------------------------------------------------------

main() {
    require_root
    ensure_dirs

    log "update check starting (repo=$REPO prefix=$TAG_PREFIX)"

    local current latest_tag asset_url asset_name
    current="$(read_current_version)"
    log "currently installed: $current"

    local release_info
    if ! release_info="$(get_latest_release)"; then
        log "no upgrade attempted (could not query releases or no eligible release)"
        return 0
    fi
    latest_tag="$(printf '%s\n' "$release_info" | sed -n '1p')"
    asset_url="$(printf '%s\n' "$release_info" | sed -n '2p')"
    asset_name="$(printf '%s\n' "$release_info" | sed -n '3p')"
    log "latest available: $latest_tag asset=$asset_name"

    if ! tag_newer_than "$latest_tag" "$current"; then
        log "up to date (installed $current >= available $latest_tag)"
        return 0
    fi

    log "newer release found: $latest_tag -> upgrading from $current"

    # Working area in /tmp. Use the script-global $WORK_DIR (not a function
    # local) so the EXIT trap can still see the variable after main returns.
    # The trap is also script-global, registered just below.
    WORK_DIR="$(mktemp -d -t remsound-relay-update.XXXXXXXX)"

    local tarball="$WORK_DIR/$asset_name"
    log "downloading $asset_url"
    if ! curl --fail --silent --show-error --max-time 120 \
            --location -o "$tarball" "$asset_url"; then
        log "ERROR: download failed"
        return 1
    fi

    log "extracting $asset_name"
    if ! tar -xzf "$tarball" -C "$WORK_DIR"; then
        log "ERROR: tarball extraction failed"
        return 1
    fi
    # Find the staging root — first directory inside the work dir.
    local staging
    staging="$(find "$WORK_DIR" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
    if [[ -z "$staging" ]]; then
        log "ERROR: tarball did not contain a top-level folder"
        return 1
    fi
    log "staging at $staging"

    snapshot_backup

    log "stopping $SERVICE_NAME"
    systemctl stop "$SERVICE_NAME" || true

    if ! install_from_staging "$staging"; then
        log "ERROR: install step failed — rolling back"
        restore_backup
        return 1
    fi

    # Persist the new version BEFORE starting, so a crash after start still
    # leaves the version file consistent with what's on disk.
    printf '%s\n' "$latest_tag" > "$VERSION_FILE"

    log "reloading systemd + starting $SERVICE_NAME"
    systemctl daemon-reload
    systemctl start "$SERVICE_NAME" || true

    sleep "$HEALTH_WAIT_SECONDS"

    if systemctl is-active --quiet "$SERVICE_NAME"; then
        log "post-install check: $SERVICE_NAME is active — upgrade to $latest_tag complete"
        return 0
    fi

    log "post-install check FAILED: $SERVICE_NAME not active — rolling back"
    restore_backup
    if systemctl is-active --quiet "$SERVICE_NAME"; then
        log "rollback succeeded — back on $current"
    else
        log "ERROR: rollback also did not restore service — manual intervention required"
    fi
    return 1
}

main "$@"
