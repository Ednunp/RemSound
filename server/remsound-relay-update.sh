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
#      a. Download the matching tarball asset and its "<tarball>.sig" signature.
#         Refuse to go any further unless the signature is valid for the
#         RemSound release key built into this script (2026-09-13: this used
#         to install whatever appeared, as root, unchecked).
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

# The RemSound release-signing PUBLIC key: the same key the Windows app checks its own updates with
# (RemSound.Core.UpdateSignature.PublicKeyPem; the app's self-test pins that the two copies match). A
# server release is installed only if its "<tarball>.sig" asset is a valid signature by this key, as
# RemSound.exe --sign-server-release writes it. Only the holder of the private key can publish one.
RELEASE_PUBLIC_KEY='-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENrzZmey3cvNxNyd6t55QQThTb3Zj
xR34nJr7egPq4f1Ff1IL5qA46nstniKZ3Zl6k+vcLWRr1oXzzdHvbIidcw==
-----END PUBLIC KEY-----'

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

# Compare two release tags by their dotted numeric version, parsed the SAME way as the Python
# release selector in get_latest_release() — split on every '.', keep the digits of each
# component, then pad to equal length before comparing. Delegated to python3 (already a hard
# dependency of this script) so the upgrade GATE and the release SELECTOR can never disagree, and
# so multi-component tags (server-v2.3.1), pre-release suffixes (server-v2.3-rc1) and missing
# components are all handled correctly.
#
# This replaces a bash-only parser that collapsed everything after the FIRST dot into the "minor"
# field and then stripped the dot — so server-v2.3.1 read as "2.31" and was wrongly judged NEWER
# than server-v2.3. The moment any patch-style tag existed, the hourly update check would STOP and
# RESTART the live relay (a real multi-second outage for every connected client), and it could even
# "upgrade" to an OLDER build (server-v2.9.1 -> "2.91" > server-v2.10 -> "2.10"). 2026-06-12.

# Returns 0 if $1 is a strictly newer version than $2, 1 otherwise (equal counts as NOT newer).
tag_newer_than() {
    TAG_PREFIX="$TAG_PREFIX" python3 - "$1" "$2" <<'PY'
import os, sys

prefix = os.environ.get("TAG_PREFIX", "server-")

def parse(tag):
    if tag.startswith(prefix):
        tag = tag[len(prefix):]
    if tag.startswith("v"):
        tag = tag[1:]
    out = []
    for part in tag.split("."):
        digits = "".join(c for c in part if c.isdigit())
        out.append(int(digits) if digits else 0)
    return out

left = parse(sys.argv[1])
right = parse(sys.argv[2])
# Pad to equal length so 2.3 and 2.3.0 compare EQUAL — a shorter tuple must not read as older,
# or a re-tagged same-version release would trigger a needless stop/restart of the relay.
n = max(len(left), len(right))
left += [0] * (n - len(left))
right += [0] * (n - len(right))
sys.exit(0 if left > right else 1)
PY
}

# Returns 0 only if file $2 holds a valid signature over file $1 by the public key in file $3: base64 of
# a DER-encoded ECDSA P-256 / SHA-256 signature, as RemSound.exe --sign-server-release writes it.
# Anything else - no signature, garbage, another key, a changed file - returns non-zero. Logs nothing,
# so the app's self-test can drive it on its own.
verify_release_signature() {
    local file="$1" signature="$2" pubkey="$3"
    [[ -s "$file" && -s "$signature" && -s "$pubkey" ]] || return 1
    local der ok=1
    der="$(mktemp)" || return 1
    if tr -d ' \r\n' < "$signature" | base64 -d > "$der" 2>/dev/null \
        && openssl dgst -sha256 -verify "$pubkey" -signature "$der" "$file" >/dev/null 2>&1; then
        ok=0
    fi
    rm -f "$der"
    return "$ok"
}

# -------- GitHub releases query ---------------------------------------------

# Fetch the releases list and pick the latest server-tag release.
# Outputs four lines: tag, asset_url, asset_name, signature_url (empty when the
# release has no "<asset_name>.sig"). Exits non-zero on no match.
#
# The list goes through a FILE. It was handed to python as one command-line argument, and Linux refuses any single
# argument over 32 memory pages: 128 KB on most machines (a Pi 4, an ordinary server), 512 KB on a Pi 5. Thirty releases
# were already 197 KB in September 2026, so on most machines the check failed every hour and said only "no upgrade
# attempted"; a Pi 5 still had room (found 2026-09-25). A hundred releases a page, not thirty, so a server release is
# not missed behind the app's own releases in the same repository.
get_latest_release() {
    local list rc
    if ! list="$(mktemp)"; then
        log "ERROR: could not make a temporary file for the release list"
        return 2
    fi
    if ! curl --fail --silent --show-error --max-time 30 \
        -H "Accept: application/vnd.github+json" \
        -o "$list" \
        "https://api.github.com/repos/${REPO}/releases?per_page=100" 2>"$list.err"; then
        log "ERROR: failed to query GitHub releases: $(cat "$list.err" 2>/dev/null)"
        rm -f "$list" "$list.err"
        return 2
    fi
    rm -f "$list.err"

    REPO="$REPO" TAG_PREFIX="$TAG_PREFIX" ASSET_PATTERN="$ASSET_PATTERN" \
    python3 - "$list" <<'PY'
import json, os, re, sys

path = sys.argv[1] if len(sys.argv) > 1 else ""
prefix = os.environ.get("TAG_PREFIX", "server-")
asset_re = re.compile(os.environ.get("ASSET_PATTERN", r"remsound-server-.*\.tar\.gz$"))

try:
    with open(path, encoding="utf-8") as f:
        releases = json.load(f)
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
    sig_url = ""
    for s in r.get("assets") or []:
        if ((s or {}).get("name") or "") == name + ".sig":
            sig_url = (s or {}).get("browser_download_url") or ""
            break
    candidates.append((parse_version(tag), tag, url, name, sig_url))

if not candidates:
    sys.stderr.write("no eligible server-* releases found\n")
    sys.exit(4)

candidates.sort(reverse=True)
_, tag, url, name, sig_url = candidates[0]
print(tag)
print(url)
print(name)
print(sig_url)
PY
    rc=$?
    rm -f "$list"
    return $rc
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

    local current latest_tag asset_url asset_name sig_url
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
    sig_url="$(printf '%s\n' "$release_info" | sed -n '4p')"
    log "latest available: $latest_tag asset=$asset_name"

    if ! tag_newer_than "$latest_tag" "$current"; then
        log "up to date (installed $current >= available $latest_tag)"
        return 0
    fi

    log "newer release found: $latest_tag -> upgrading from $current"

    # Refuse anything that cannot prove where it came from, before touching the running relay.
    if [[ -z "$sig_url" ]]; then
        log "ERROR: $latest_tag has no signature ($asset_name.sig) - refusing to install an unsigned release"
        return 1
    fi
    if ! command -v openssl >/dev/null 2>&1; then
        log "ERROR: openssl is needed to check the release signature - refusing to update (sudo apt-get install -y openssl)"
        return 1
    fi

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

    log "downloading $sig_url"
    if ! curl --fail --silent --show-error --max-time 30 \
            --location -o "$tarball.sig" "$sig_url"; then
        log "ERROR: signature download failed - refusing to install"
        return 1
    fi
    printf '%s\n' "$RELEASE_PUBLIC_KEY" > "$WORK_DIR/release-public-key.pem"
    if ! verify_release_signature "$tarball" "$tarball.sig" "$WORK_DIR/release-public-key.pem"; then
        log "ERROR: $asset_name is not signed by the RemSound release key - refusing to install"
        return 1
    fi
    log "signature checked: $asset_name is signed by the RemSound release key"

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

# Run only when executed (the systemd service does). Sourcing the script - as the app's self-test does,
# to drive verify_release_signature on its own - defines the functions and runs nothing.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
