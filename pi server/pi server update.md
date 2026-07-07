# RemSound Pi Server — Handover

**Status: server side COMPLETE.** Built, released, deployed, verified — 15 May 2026.

This document records the upgrade of the RemSound relay server from the original
two-peer reflector to a dual-protocol (v1 pairwise + v2 lobby) relay with a
GitHub-based auto-updater. It is written for the RemSound thread, for Andre, and
for any future maintainer.

The work was done from the **Pi thread** (the one that manages Ed's Raspberry Pi).
The design it was built from is the companion file `remsound server update.md`.

---

## 1. What was built

A single relay binary, `remsound-relay.py`, that handles **two protocol versions
concurrently** on the same UDP listener (port 47830):

- **v1 (pairwise)** — the original two-slot reflector, **unchanged**. First two
  UDP endpoints to send a valid RemSound v1 packet claim the slots; their
  traffic is mirrored to each other. Existing RemSound clients (v1.x) keep
  working against the new server with no changes.
- **v2 (lobby)** — a multi-peer lobby, up to 10 peers (configurable), keyed on a
  per-instance `CLIENT_ID` (UUID). Every packet from one client is fanned out to
  every other client in the lobby. NAT rebinds and "two clients behind one NAT"
  stop being special cases because identity is the CLIENT_ID, not the endpoint.
  Periodic `LobbyRoster` packets keep clients informed of membership.

The server inspects byte 4 of each packet (the version byte) and routes v1 vs v2
accordingly. A v1 client and a v2 client **cannot** share a lobby in this release
— that is a deliberate scope cut (see the design doc).

Also built:

- **Auto-updater** — `remsound-relay-update.sh`, run by a systemd timer. It polls
  the GitHub Releases API hourly for tags beginning `server-`, and if a newer one
  exists, downloads it, backs up the current install, swaps the files, restarts
  the service, health-checks it, and **rolls back automatically** if the service
  fails to come up.
- **systemd units** — `remsound-relay-update.service` (one-shot) +
  `remsound-relay-update.timer` (boot + 2 min, then hourly with 10-min jitter).
- **Installer / uninstaller / smoke-test** — `install.sh`, `uninstall.sh`,
  `smoke-test.sh`, updated to wire in the auto-updater.
- **`VERSION`** — the tag the bundle represents.

---

## 2. Where everything is

| Thing | Location |
| --- | --- |
| GitHub releases (what the auto-updater pulls) | `github.com/Ednunp/RemSound/releases` — tags `server-v2.0` … `server-v2.3` |
| GitHub source | `github.com/Ednunp/RemSound` → `server/` folder |
| Deployed and running | Ed's Raspberry Pi (`Pi5`), `server-v2.3`, UDP 47830 |
| Deployable bundle (this archive) | the files alongside this document |
| Design / spec | `remsound server update.md` (alongside this document) |

**Note for the RemSound thread:** the repo's `server/` folder was updated by the
Pi thread on 15 May 2026 directly via the GitHub API. The **local checkout** at
`D:\proj\remsound\server\` may therefore be behind the remote — do a `git pull`
to sync before doing any work there, and do **not** overwrite `server/` with
older server code.

---

## 3. The releases

| Tag | What it is |
| --- | --- |
| `server-v2.0` | Initial dual-protocol release |
| `server-v2.1` | No-op test release (used to validate the auto-updater's upgrade path) |
| `server-v2.2` | Bug fix — see section 6 |
| `server-v2.3` | No-op test release (validated the fixed updater) |

The Pi runs `server-v2.3`. The auto-updater always picks the **highest** version,
so the test releases don't interfere. **Future real releases should be `server-v2.4`
and upward.**

---

## 4. IMPORTANT — what is still left, and whose job it is

The **server is finished**. What remains is **client-side work in the RemSound
application**, and that is the **RemSound thread's job**, not the Pi thread's.

Per the design doc, sections 5 and 9, the client work is:

1. `Profile` / `AppConfig`: a new `ClientId` (Guid) field, generated once and
   persisted in `remsound.config.json`.
2. `RemPacket`: header read/write learns the v2 format (28-byte header with the
   16-byte CLIENT_ID). v1 read/write stays.
3. `AudioSender`: send one stream to the lobby server; drop the per-peer fan-out.
4. `AudioReceiver` / `StreamSession`: re-key sessions on `(CLIENT_ID, streamId)`
   instead of `(IPEndPoint, streamId)`.
5. `Connectivity` tab: a "Lobby" section showing the current `LobbyRoster`.

None of this blocks anything: v1 clients keep working against the new server, so
the client work can land in its own release whenever convenient (the design doc
suggests `v1.5` or `v2.0` of the client).

---

## 5. Wire format (v1 vs v2)

**v1 header — 12 bytes, unchanged:**

```
0   4   MAGIC = 'RMND'
4   1   VERSION = 1
5   1   TYPE       (Format=1, Audio=2, KeepAlive=3, Heartbeat=4, Control=5)
6   2   STREAM_ID  (LE)
8   4   SEQUENCE   (LE)
12      payload...
```

**v2 header — 28 bytes:**

```
0   4   MAGIC = 'RMND'
4   1   VERSION = 2
5   1   TYPE       (1-5 as v1, plus LobbyHello=6, LobbyRoster=7,
                    LobbyFull=8, LobbyBye=9)
6   2   STREAM_ID  (LE)
8   4   SEQUENCE   (LE)
12  16  CLIENT_ID  (UUID, RFC 4122 binary form)
28      payload...
```

v2 packet types the server originates use a zero CLIENT_ID
(`00000000-0000-0000-0000-000000000000`) so clients can recognise "from server".

---

## 6. The bug we hit (for the record)

During release validation, the auto-updater (`remsound-relay-update.sh`) was
found to exit with status 1 **after a successful upgrade**. Cause: the EXIT trap
referenced a variable (`work`) that had been declared `local` inside a function;
by the time bash fired the EXIT trap (after the function returned) the variable
was out of scope, so `set -u` raised `unbound variable` and bash exited 1. systemd
then marked the one-shot service as failed even though the upgrade had completed
correctly.

Fixed in `server-v2.2`: the working-directory variable was promoted to script
scope (`WORK_DIR`) and the cleanup trap registered at script-global level. The
clean upgrade path was re-verified on `server-v2.2` → `server-v2.3`.

---

## 7. Cutting a future server release

```bash
# 1. Edit the bundle source. Bump VERSION to the new tag, e.g. "server-v2.4".
# 2. Tar it with the correct internal directory name:
tar -czf /tmp/remsound-server-v2.4.tar.gz \
    --transform 's,^<srcdir>,remsound-server-v2.4,' <srcdir>
# 3. Publish the release:
gh release create server-v2.4 /tmp/remsound-server-v2.4.tar.gz \
    --repo Ednunp/RemSound --title "Server v2.4 — ..." --notes "..."
# 4. Within ~1 hour every running relay's auto-updater picks it up,
#    installs it, restarts, and rolls back automatically if it fails.
```

The asset must be named `remsound-server-*.tar.gz` and must contain a single
top-level folder. The updater finds the highest `server-*` tag, so version
numbers must keep climbing.

---

## 8. Installing on a fresh box (e.g. Andre's Linux server)

The deployable files sit alongside this document. On the target machine:

```bash
sudo ./install.sh      # sets up the relay AND the auto-updater
sudo ./smoke-test.sh   # confirms it's alive
# then open UDP 47830 in the firewall / router toward this host
```

After that the box auto-updates from GitHub — no manual intervention ever again.
To pin a version: `sudo systemctl disable --now remsound-relay-update.timer`.
To remove everything: `sudo ./uninstall.sh`.

Full operational detail is in the bundle's own `README.md`.

---

## 9. Verification done

- Initial install on the Pi via `install.sh` — smoke test all green.
- v1 synthetic packet accepted (logged `peer_joined`); v2 synthetic packet
  accepted (logged `client_joined`).
- Auto-updater "up to date" path — clean exit.
- Auto-updater upgrade path — `server-v2.0` → `v2.1` → `v2.2` → `v2.3`, each
  step verified, post-fix runs exit `0/SUCCESS`, version stamp updates, rollback
  snapshot in place.
- The relay survived a whole-house power cut on 15 May 2026 and came back on
  `server-v2.3` automatically.
