# RemSound relay upgrade — lobby model with auto-updates

Design + ops handover for upgrading the existing `remsound-relay.py` from a two-slot pairing reflector to a small lobby-style multi-peer relay. Same bundle works on a Raspberry Pi (the existing deployment) and on a full Linux host (Andre's box). Includes a self-update mechanism so once a host is on the new bundle, future releases roll out without anyone manually copying files.

The existing two-slot relay (`server/remsound-relay.py` on GitHub) stays usable. The lobby relay is a parallel script — same package, additional file — so existing hosts that just want a private two-peer relay keep working unmodified.

---

## 1. What's changing and why

### Current state

`remsound-relay.py` has two peer slots. The first two UDP endpoints to send a valid RemSound packet claim the slots; everything from one slot is reflected to the other. Third endpoint is dropped silently. Slots get reclaimed after 60 s idle.

This means:

- Hard cap of two connected peers per relay.
- Two clients behind the same NAT can both register (different ephemeral ports), but they get paired with each other instead of with the intended remote peer.
- A third RemSound instance joining an active pair gets ignored with no feedback.

### What we want

- Any number of RemSound instances (up to a configurable cap, default 10) all able to be in the same conversation through one server.
- Each instance is identified by a stable client ID, not by its network endpoint. NAT rebinds, network switches, and same-NAT-multiple-clients all stop being special cases.
- The relay forwards each packet to every other registered client. No mixing on the server. Clients use `PlayoutEngine`'s existing per-session mixing exactly as they do today.
- The relay logs joins, leaves, and per-minute stats. It never decodes audio and never logs payload bytes.
- The relay auto-updates: when a new release is published on GitHub, every running relay picks it up within an hour and restarts itself.

### What we're not adding (yet)

- Rooms / channels. One server = one lobby. Want a second lobby? Run a second instance on a different port.
- Server-side mixing. Forwarding is simpler, faster, and matches RemSound's existing client-side mix path.
- Voice activation / push-to-talk gating at the server.
- Authentication or lobby passwords.

All of those are future work and don't need to block this change.

---

## 2. Lobby model

### Capacity

- Cap = 10 clients per lobby. Configurable on the server (`--max-clients`).
- 11th client gets a `LobbyFull` control packet back with the current member count. Client surfaces a "Lobby is full (10 / 10)" message; no queue.
- At PCM-stereo (~1.5 Mbps/stream) a 10-peer lobby is ~13.5 Mbps downlink per client. Opus 192 kbps brings that down to ~1.7 Mbps. Both fine on consumer broadband.

### Identity

Each RemSound instance generates a UUID once and persists it in `remsound.config.json` (machine-local, not per-profile, not part of the profile JSON). Survives reboots, profile switches, network changes. Two instances on the same machine have distinct IDs — that's exactly how the "DT + LT both behind same NAT" case stops being special.

### Forwarding rule

- Every packet from a registered client gets forwarded to every other registered client in the same lobby. Unchanged bytes, including the original sender's CLIENT_ID, so receivers know who the audio came from.
- Sender does not need a destination peer list any more. It sends one stream to the server; the server fan-outs.
- Receiver routes incoming packets to the matching `SessionPlayout` keyed by CLIENT_ID (instead of by `(endpoint, streamId)` as today).

### Idle handling

- Per-client idle timeout, 60 s (same as the current relay). Goes silent → expire that client. Doesn't affect anyone else in the lobby.
- Roster broadcast (see §4) fires whenever lobby membership changes, so other clients learn about joins / leaves promptly.

---

## 3. Wire format v2

The relay must distinguish v1 packets (legacy `remsound-relay.py` clients) from v2 (lobby-aware clients) and route accordingly.

### v1 header (existing, unchanged)

```
offset  size  field
0       4     MAGIC = 'RMND'
4       1     VERSION = 1
5       1     TYPE        (Format=1, Audio=2, KeepAlive=3, Heartbeat=4, Control=5)
6       2     STREAM_ID   (LE)
8       4     SEQUENCE    (LE)
12            payload...
```

Total 12 bytes.

### v2 header (new)

```
offset  size  field
0       4     MAGIC = 'RMND'
4       1     VERSION = 2
5       1     TYPE        (Format=1, Audio=2, KeepAlive=3, Heartbeat=4, Control=5,
                            LobbyHello=6, LobbyRoster=7, LobbyFull=8, LobbyBye=9)
6       2     STREAM_ID   (LE)
8       4     SEQUENCE    (LE)
12     16     CLIENT_ID   (UUID, RFC 4122 binary form, big-endian by convention)
28            payload...
```

Total 28 bytes. CLIENT_ID slot is the only structural addition.

### Backward compatibility

Server inspects byte 4 (VERSION):

- `0x01` → v1 client. Apply the existing two-slot pairing logic, identical to today. Lets unmodified legacy clients keep working against the new server.
- `0x02` → v2 client. Apply lobby logic.

A single relay instance handles both protocols concurrently. A v1 client and a v2 client cannot share a lobby in this release — that's a deliberate limitation. Practical answer: anyone running a fresh server runs the new bundle, and any client wanting to use a lobby upgrades to a v2-aware build. v1 clients keep working against the same server for pairwise use.

### New packet types (v2 only)

- **LobbyHello (6)** — client → server. Sent immediately after the first audio/format packet, but redundant if those were the first thing seen. Carries the client's display name (UTF-8, max 32 bytes, padded with null). Server uses this for roster announcements.
- **LobbyRoster (7)** — server → client. Sent on every membership change (someone joins, someone leaves, someone updates display name) and periodically at ~1 Hz heartbeat. Carries `count` followed by `count` × `{CLIENT_ID(16), display_name(32 bytes, null-padded UTF-8)}`. Max realistic size: 10 × 48 = 480 bytes + header = well under MTU.
- **LobbyFull (8)** — server → would-be client. Sent in response to a registration attempt when the lobby is at capacity. Carries `current_count(1) max_count(1)`.
- **LobbyBye (9)** — client → server, or server → client. Carries no payload (or just a reason code). Sent on graceful disconnect (client closing) or eviction (server pruning).

These are all small, low-rate, never on the audio hot path.

---

## 4. Server design

### State

```python
# Per-client entry. Identifies who, where, when last seen.
ClientEntry = (
    endpoint: (host, port),
    display_name: str,
    last_seen_monotonic: float,
    rx_packets: int,
    tx_packets: int,
)

# The entire lobby.
clients: dict[uuid.UUID, ClientEntry] = {}
max_clients: int = 10  # configurable
```

Single flat dict, keyed by CLIENT_ID. No nested rooms.

### Packet flow

For an incoming UDP packet with `(data, addr)`:

1. Parse header. If `MAGIC != 'RMND'`: drop (`rejected_bad_header++`).
2. If `VERSION == 1`: route through legacy two-slot logic (unchanged from today).
3. If `VERSION == 2`:
   a. Read `CLIENT_ID` from bytes 12..28.
   b. If `CLIENT_ID not in clients`:
      - If `len(clients) >= max_clients`: send `LobbyFull` to `addr`, drop the original packet (`dropped_lobby_full++`).
      - Else: insert new `ClientEntry(addr, display_name="", monotonic_now, 0, 0)`. Log `event=client_joined`. Schedule a roster broadcast.
   c. Existing entry: refresh `endpoint` (handles NAT rebinding) and `last_seen_monotonic`. Bump `rx_packets`.
   d. By packet type:
      - `LobbyHello`: extract display name, store. Schedule a roster broadcast.
      - `LobbyBye`: remove the entry. Log `event=client_left`. Schedule a roster broadcast.
      - `Audio` / `Format` / `KeepAlive` / `Heartbeat` / `Control`: forward to every OTHER client in `clients`. Bump each recipient's `tx_packets`.
      - Anything else: drop.

### Roster broadcast

- Sent whenever a join / leave / name update happens.
- Also sent periodically (every 1 s) as a heartbeat, so clients quietly detect disconnects when their roster goes empty.
- Built once per cycle, sent unmodified to every connected client.

### Idle expiry

- Once per main loop iteration: walk `clients`, remove any entry whose `last_seen` is older than `IDLE_TIMEOUT_SECONDS` (60). Same timeout as today.
- On removal, log `event=client_idle_expired` and schedule a roster broadcast.

### Logging

Same format as the current relay: structured key=value lines to `/var/log/remsound-relay.log` and stderr. New events:

- `event=client_joined client_id=<uuid> addr=<ip:port> name=<display>`
- `event=client_left client_id=<uuid>`
- `event=client_idle_expired client_id=<uuid>`
- `event=lobby_full attempted_client_id=<uuid> addr=<ip:port>`
- `event=stats forwarded=N dropped_lobby_full=N rejected_bad_header=N client_count=N peers=[...]`

Never logs CLIENT_ID payload bytes beyond the UUID itself. Never logs audio payload.

### Capacity check

```python
def can_admit(client_id: uuid.UUID) -> bool:
    return client_id in clients or len(clients) < max_clients
```

That's it. The cap is a soft limit at admit time; existing clients can never get evicted by a newcomer.

### Approximate complexity

For an active lobby of N peers, each audio packet from one client triggers (N - 1) `sock.sendto` calls. At 10 peers and PCM rates (~1500 packets/sec/client → 10 × 1500 = 15,000 inbound/sec → 10 × 9 × 1500 ≈ 135,000 outbound sendto's/sec). Comfortable on any modern Linux. Even a Pi 4 will handle it; a real server is bored.

---

## 5. Client changes (high-level — actual implementation is a separate task)

These are listed so Andre can see the full picture, not because the server bundle has to contain client code.

- `Profile` / `AppConfig`: new `ClientId` field (Guid), persisted in `remsound.config.json`. Generated on first launch.
- `RemPacket`: header reading / writing learns v2 format. Old v1 reads/writes still supported.
- `AudioSender`: sends one stream to the lobby server. Drops the per-peer fan-out loop.
- `AudioReceiver` / `StreamSession`: dictionary key changes from `(IPEndPoint, ushort)` to `(Guid clientId, ushort streamId)`. Endpoint becomes a routing detail.
- `HeartbeatService`: peer-health tracking keyed by `ClientId`.
- `Connectivity` tab: "Lobby" section showing connected lobby members from the latest `LobbyRoster`. Replaces the per-pair "Discovered peers" model for lobby connections. LAN discovery still works for non-lobby pair sessions.
- `Selected peers` semantics unchanged: still a tick-to-accept allow-list, but keyed on CLIENT_ID rather than IP.

A migration phase where v1 packets are still emitted lets older clients keep talking to the new server for pair use.

---

## 6. Auto-update on the server

### Goal

Push a new server release to GitHub → every relay running this bundle picks it up within an hour and restarts itself onto the new version. Andre never has to re-SCP, never has to remember to update.

### Mechanism

Three new pieces ship in the bundle alongside `remsound-relay.py`:

1. **`remsound-relay-update.sh`** — the updater script. Bash. Talks to GitHub.
2. **`remsound-relay-update.service`** — systemd unit that runs the updater once.
3. **`remsound-relay-update.timer`** — systemd timer firing the updater on a schedule.

The relay binary itself doesn't reach out. Separation of concerns: the relay just relays; the updater just updates. If the updater is broken, the relay keeps running. If the relay crashes, the updater keeps trying to install fixes.

### Updater script behaviour

```
remsound-relay-update.sh:

1. Read current version from /etc/remsound-relay/version (file contains a single line like "server-v2.0").
   If the file is missing, treat the installed version as "server-v0".
2. Call GitHub API: https://api.github.com/repos/Ednunp/RemSound/releases
   Filter releases whose tag starts with "server-". Take the highest by tag semver.
3. If latest <= current: log "up to date" and exit 0.
4. If latest > current:
   a. Look in the release's assets for `remsound-server-<tag>.tar.gz`. If missing, log warning, exit 1.
   b. curl the asset to /tmp/remsound-server-<tag>.tar.gz.
   c. (Optional v2) Verify SHA256 against a second asset `remsound-server-<tag>.tar.gz.sha256`.
   d. Extract to /tmp/remsound-server-<tag>/.
   e. Stop the relay: systemctl stop remsound-relay.
   f. Copy the new files into place (replacing /usr/local/sbin/remsound-relay.py and
      /etc/systemd/system/remsound-relay.service if present in the asset).
   g. systemctl daemon-reload, systemctl start remsound-relay.
   h. Wait 3 seconds. Check the service is active. If yes: write the new tag to
      /etc/remsound-relay/version, log success, clean up /tmp staging.
      If no: roll back from a backup of the old files (kept at /etc/remsound-relay/backup/),
      restart the service, log failure. Exit 1.
5. Log to /var/log/remsound-relay-update.log.
```

The updater self-update case (a new updater script is itself shipped in a release) is handled by the same copy step in 4f — the running updater finishes its current cycle, exits, and next cycle the new updater script is what runs.

### Systemd timer

```
# remsound-relay-update.timer
[Unit]
Description=Periodic update check for remsound-relay

[Timer]
OnBootSec=2min
OnUnitActiveSec=1h
RandomizedDelaySec=10min
Persistent=true

[Install]
WantedBy=timers.target
```

- Fires 2 minutes after boot, then every hour thereafter.
- `RandomizedDelaySec=10min` so multiple relays don't all hit the GitHub API at the same exact instant if you ever run many.
- `Persistent=true` ensures missed runs (host was off) catch up on next boot.

### Systemd service for the updater

```
# remsound-relay-update.service
[Unit]
Description=Check for and apply remsound-relay updates from GitHub
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/remsound-relay-update.sh
Restart=no
```

One-shot. Triggered only by the timer. Failure is logged but not retried — the next hourly tick has another go.

### Release tag convention

Server releases use the tag pattern `server-vMAJOR.MINOR` (e.g. `server-v2.0`, `server-v2.1`). Client releases keep their existing `vMAJOR.MINOR` pattern (`v1.4`, `v1.5`). The updater filter (`tag startswith "server-"`) makes the two streams orthogonal: client releases don't trigger relay updates, and vice versa.

A release asset name is `remsound-server-<tag>.tar.gz`. Tarball contents:

```
remsound-server-v2.0/
├── remsound-relay.py
├── remsound-relay.service
├── remsound-relay-update.sh
├── remsound-relay-update.service
├── remsound-relay-update.timer
├── install.sh
├── uninstall.sh
├── smoke-test.sh
├── VERSION                 # contains "server-v2.0\n"
└── README.md
```

### Rollback path

On failed update (service won't come up after install), the updater restores from `/etc/remsound-relay/backup/`, which `install.sh` populates at install time (and which the updater itself refreshes before every replacement). If even that fails, manual recovery is the same as today: SSH in, `apt install python3` (already there), and copy the bundle from a working host or `git clone` the repo.

### Disabling auto-update

Anyone who wants the relay to stay pinned at a known version simply disables the timer:

```
sudo systemctl disable --now remsound-relay-update.timer
```

Service keeps running. No further updates. Re-enabling resumes the schedule.

---

## 7. Installation on a fresh host

### Raspberry Pi (or any Debian/Ubuntu/Pi OS)

1. Download the latest server tarball from the GitHub release page (or have the updater do it after manual bootstrap).
2. Extract, `cd` into the folder, `sudo ./install.sh`.
3. Open UDP 47830 in the host's firewall and / or router.

### Full Linux server (Andre's box)

Same script. The differences are operational, not structural:

- Andre will likely want a non-default port (e.g. 47830 is fine for one lobby; second instance on 47831 if needed). Add `--port` flag to `remsound-relay.service` `ExecStart=` line.
- He may want the log file under `/var/log/journal/` and a smaller log retention; that's a `journald.conf` concern, not the relay's.
- `max-clients` configurable via env var `REMSOUND_MAX_CLIENTS` so it can be set in the systemd unit without editing the script.

### What `install.sh` does in the new bundle

Adds three things to the existing five steps:

6. Install `remsound-relay-update.sh` to `/usr/local/sbin/`.
7. Install `remsound-relay-update.service` and `remsound-relay-update.timer` to `/etc/systemd/system/`.
8. `systemctl enable --now remsound-relay-update.timer`.
9. Write the current bundle's version to `/etc/remsound-relay/version`.
10. Snapshot current files to `/etc/remsound-relay/backup/` for the updater's rollback path.

`uninstall.sh` gets the matching teardown.

### Smoke test

`smoke-test.sh` checks:

- Service is active.
- UDP 47830 has a listener.
- Log file is being written to.
- (New) Updater service exists and the timer is active.
- (New) `/etc/remsound-relay/version` is readable and matches expected format.

---

## 8. GitHub release process

When we cut a new server release:

1. Bump version in `VERSION` and in `remsound-relay.py`'s startup-log line.
2. Tag: `git tag server-v2.1 && git push origin server-v2.1`.
3. `tar czf remsound-server-v2.1.tar.gz remsound-server-v2.1/` (where the folder is a staging area with the bundle contents).
4. `gh release create server-v2.1 remsound-server-v2.1.tar.gz --title "server v2.1" --notes "…"`.
5. Within an hour, every running relay's updater catches the release, downloads it, restarts the service.

We can additionally publish a `.sha256` alongside if we want signature-style integrity checks; not strictly required because GitHub serves the tarball over HTTPS.

### Release notes content

Keep them short and operational: what changed, what the operator needs to know, anything they need to do manually (usually nothing — that's the whole point of auto-update).

---

## 9. Migration phases

Ordered so each step is independently testable and rollback-able. Server work is steps 1–3; client work is 4–6.

1. **v2 wire format added to client side, still emits v1 by default.** No behaviour change. Just makes the new header reading / writing code paths exist. Backward-compatible with existing relays.
2. **New `remsound-lobby.py` ships as `server-v2.0`.** Handles v1 packets exactly like today's relay (two-slot pairing); handles v2 packets via the lobby logic. Initial deployment: install on onj.me alongside / replacing the current relay. Existing v1 clients keep working pairwise.
3. **Auto-updater bundle goes live.** Once steps 1 and 2 are in place, future server changes roll out without manual deployment.
4. **Client adds CLIENT_ID generation + persistence.** New UUID stamped on every outbound v2 packet. Server now sees the same client by ID even if endpoint changes.
5. **Client receiver re-keys sessions on CLIENT_ID.** Internal change. Lobby connections become a real first-class thing in the UI.
6. **Client UI: lobby tab / lobby roster integration.** Replace per-pair manual peer entry with a "connect to lobby" affordance. LAN discovery still works in parallel for non-lobby setups.

Each phase is a separate commit (and release where appropriate). Steps 1–3 are server-side and can ship without any RemSound client release. Steps 4–6 are client-side; they should ship in a single client release (probably `v1.5` or `v2.0` depending on how disruptive the wire change feels).

---

## 10. Testing

### Server-side

- **`smoke-test.sh`** runs the basic install-check (listener bound, service active, log present, updater timer enabled).
- **2-client v1 pair test**: existing test from the current bundle. Should still pass — proves backward compat.
- **3-client v2 lobby test**: bring up three RemSound instances (or three test scripts that mimic v2 packets), all dial the relay, confirm each receives the other two's audio.
- **Lobby-full test**: 11th client receives `LobbyFull`. Server doesn't crash.
- **Idle eviction test**: stop one client, wait 60 s, confirm the other clients receive an updated roster.
- **Auto-update dry-run**: tag a no-op server release, watch the timer fire, confirm the relay picks it up, restarts, and the new version shows in `/etc/remsound-relay/version`.

### Client-side (once steps 4–6 land)

- Two instances behind the same NAT both connect to the same lobby: each sees the other and the remote peer in the lobby roster.
- Network rebinding (Wi-Fi → Ethernet): client session stays alive; endpoint update is picked up by the server.
- Lobby-full: clean UI message.
- Mixed lobby of v2 clients only (initially we won't support mixing v1 and v2 in one lobby — it's a separate work stream if ever needed).

### Production canary

When `server-v2.0` is ready, ship it first to one of the relays (the Pi or onj.me, your call), watch logs for a day or two, then deploy to the other. Auto-update + atomic rollback means even a bad release doesn't take both hosts offline simultaneously.

---

## 11. Open questions

These should be answered before code starts but aren't blocking for design review.

1. **Display names**: do we want them at all in v2.0, or defer to v2.1? They're nice for UI roster display but not load-bearing for routing. **Suggestion: defer.** Client sends `LobbyHello` with display name = profile name; server treats missing / empty display names as "Unknown" in the roster.
2. **Authentication**: do we need a shared secret to join a lobby on onj.me? **Suggestion: not for v2.0.** Anyone who knows the address can connect. If unwanted-strangers becomes a problem, add a `LobbyAuth(secret)` packet type in a later release.
3. **IPv6**: the existing relay binds IPv4 only. For Andre's full server it'd be reasonable to bind both. **Suggestion: optional dual-stack via `--bind6`. Default IPv4 to match existing behaviour.**
4. **Metrics endpoint**: would a Prometheus-style `/metrics` HTTP endpoint be useful for Andre? **Suggestion: not in v2.0. The per-minute stats line in the log file is enough until someone asks.**
5. **Recording at the server**: a server-side recording feature would be powerful (capture all lobby audio for later playback / archive) but adds the kind of complexity the rest of this design carefully avoids. **Suggestion: defer indefinitely.**
6. **Renegotiating CLIENT_ID**: if someone wants to wipe their identity (privacy / fresh start), the simplest answer is "delete the line from `remsound.config.json` and restart". No protocol-level renegotiation needed.

---

## 12. Quick reference for Andre

If you're reading this to set up your Linux box once the v2.0 release is out:

```
# 1. Download and extract the latest server bundle.
curl -L -o /tmp/remsound-server.tar.gz \
  https://github.com/Ednunp/RemSound/releases/download/server-v2.0/remsound-server-v2.0.tar.gz
tar xzf /tmp/remsound-server.tar.gz -C /tmp
cd /tmp/remsound-server-v2.0

# 2. Install. This sets up the relay AND the auto-updater.
sudo ./install.sh

# 3. Open UDP 47830 in the firewall.
sudo ufw allow 47830/udp
# (or whatever your firewall is)

# 4. Confirm it's alive.
sudo ./smoke-test.sh
```

After that you can forget about it. Future updates roll out automatically. If you want to pin a version, `sudo systemctl disable --now remsound-relay-update.timer`. If you want to remove it entirely, `sudo ./uninstall.sh`.

Log file is `/var/log/remsound-relay.log` for the relay itself, `/var/log/remsound-relay-update.log` for the update history. Both rotate via the system's logrotate defaults.

---

## 13. Status

This is a design + handover document, not the implementation. None of the v2 code exists yet. When ready to build:

- `remsound-lobby.py` (~250 lines Python) — the new lobby relay.
- `remsound-relay-update.sh` (~150 lines Bash) — the updater.
- Two systemd units (~30 lines total) — service + timer for the updater.
- `install.sh` / `uninstall.sh` / `smoke-test.sh` updates.
- Wire format v2 in `RemSound.Core.RemPacket` (~100 lines C# delta).
- Per-client UUID persistence + heartbeat / session refactor in the client (~few hundred lines).
- Client UI for the lobby tab (later phase, optional in v2.0 release).

Total scope: a couple of focused days for the server bundle, several days for the client refactor + UI. Server-side ships first and runs alongside the existing two-slot relay without breaking it.
