# RemSound UDP Relay

A small UDP reflector that lets RemSound peers reach each other across the
internet without Tailscale in the audio path. It never decodes audio: it
checks each packet's RemSound header and forwards or drops it. Two modes run
in one process, on one UDP port:

- **v1 (pairwise)** — a two-slot reflector. The first two endpoints to send a
  valid RemSound v1 packet claim the slots, and their traffic is mirrored to
  each other. The Windows RemSound app sends v1 packets (a 12-byte header).
- **v2 (lobby)** — a multi-peer lobby (default cap: 10) keyed on a
  per-instance CLIENT_ID, for clients that send the 28-byte v2 header.
  Periodic LobbyRoster packets tell each client who is in.

A v1 client and a v2 client cannot hear each other through the relay.

## What's in this bundle

| File                                  | Purpose                                                                                            |
| ------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `remsound-relay.py`                   | the relay itself (dual-protocol)                                                                   |
| `remsound-relay.service`              | systemd unit for the relay                                                                         |
| `remsound-relay-update.sh`            | auto-updater. Polls GitHub Releases hourly for newer `server-*` tags and installs them in place    |
| `remsound-relay-update.service`       | systemd one-shot unit fired by the timer                                                           |
| `remsound-relay-update.timer`         | the schedule (boot + every hour, with random jitter)                                               |
| `install.sh`                          | sets up the relay AND the auto-updater                                                             |
| `uninstall.sh`                        | removes everything this bundle installed                                                           |
| `smoke-test.sh`                       | install-time check (v1 + v2 paths + updater scaffolding). Install time only: see below             |
| `VERSION`                             | the release tag this bundle represents (`server-vX.Y`)                                             |
| `README.md`                           | this file                                                                                          |

The `server/` folder in the repository also holds `test_relay.py` (unit tests
for the relay's logic, run by the Windows build gate) and two historical
design documents. A relay doesn't need any of them.

## Installing on a fresh host

Tested on Raspberry Pi OS Bookworm and Debian / Ubuntu. Requires `python3`
and `curl` (both usually preinstalled).

```bash
# 1. Download and extract a server bundle. Server releases are tagged
#    server-vX.Y on https://github.com/Ednunp/RemSound/releases (pick the
#    highest). They are never marked "Latest": that label belongs to the
#    Windows app. Any server bundle will do, because the auto-updater moves
#    to the newest server release within about an hour.
TAG=server-vX.Y    # replace with the real tag
curl -L -o /tmp/remsound-server.tar.gz \
  "https://github.com/Ednunp/RemSound/releases/download/$TAG/remsound-$TAG.tar.gz"
tar xzf /tmp/remsound-server.tar.gz -C /tmp
cd "/tmp/remsound-$TAG"

# 2. Install — sets up the relay AND auto-updates.
sudo ./install.sh

# 3. Open UDP 47830 in the firewall and / or router port-forward.
#    On UFW: sudo ufw allow 47830/udp
#    On UDM / pfSense / etc.: WAN UDP 47830 -> this host's LAN IP.

# 4. Confirm it's alive. Do this now, before anyone uses the relay.
sudo ./smoke-test.sh
```

After install, the auto-updater is enabled. Future releases roll out
automatically — no manual SCP, no manual edit. To pin to the current
version:

```bash
sudo systemctl disable --now remsound-relay-update.timer
```

To check for updates manually:

```bash
sudo systemctl start remsound-relay-update.service
```

To uninstall everything cleanly:

```bash
cd "/tmp/remsound-$TAG"   # or wherever the bundle is
sudo ./uninstall.sh
```

## The smoke test is for install time only

`smoke-test.sh` pretends to be two clients: it sends one v1 and one v2 packet
from 127.0.0.1. The relay treats those like real peers, so each one holds a
slot until it has been idle for 60 seconds. A v1 pair has only two slots. If
one real person is already connected and waiting for their partner, the smoke
test takes the second slot, and the partner is locked out for up to a minute.

Run it straight after installing, before the relay is in use. Don't run it on
a relay people are connected to.

## Files on disk after install

| Path                                                | Purpose                                |
| --------------------------------------------------- | -------------------------------------- |
| `/usr/local/sbin/remsound-relay.py`                 | the relay                              |
| `/usr/local/sbin/remsound-relay-update.sh`          | the auto-updater                       |
| `/etc/systemd/system/remsound-relay.service`        | relay unit                             |
| `/etc/systemd/system/remsound-relay-update.service` | updater unit                           |
| `/etc/systemd/system/remsound-relay-update.timer`   | updater schedule                       |
| `/etc/remsound-relay/version`                       | currently installed tag                |
| `/etc/remsound-relay/backup/`                       | snapshot for the updater's rollback    |
| `/var/log/remsound-relay.log`                       | relay event log (`event=...` per line) |
| `/var/log/remsound-relay-update.log`                | update-check history                   |

## Networking

- Listens on UDP `47830`, the same port the RemSound app uses for its audio,
  so people can type the relay's address without adding a port number.
- Bound to `0.0.0.0`, so the kernel routes via the default interface.
  Tailscale must NOT carry this traffic — the whole point of the relay is
  to remove Tailscale's hops from the audio path.
- The relay process itself never decodes audio. It validates the
  RemSound header (magic `RMND`, version 1 or 2) and forwards or drops.

## Address proof and the per-IP cap

- **Address proof.** The first time the relay sees an address, it sends that
  address a random cookie (packet type 10). A real client echoes it back,
  which proves the address really receives traffic; a forged source address
  never can. RemSound 5.6 and later echo it. By default the relay only
  *watches*: it still forwards to unverified addresses, and logs each one that
  would have been blocked. With `--require-addr-check` (or
  `REMSOUND_REQUIRE_ADDR_CHECK=1`) it withholds all forwarded traffic, the
  lobby roster included, from any address that has not echoed its cookie. A
  lobby client that turns up at a new address has to prove the new one.
- **Per-IP cap.** One source IP may hold at most 4 pair or lobby entries at
  once, counted across v1 and v2 together. This is always on.

## Log format

Structured key=value lines. Notable events:

```
event=startup version_supported=v1,v2 listen=0.0.0.0:47830 max_clients=10 addr_check=watch-only

# v1 (pairwise)
event=peer_joined addr=1.2.3.4:5555 slots_filled=1
event=peer_paired a=1.2.3.4:5555 b=5.6.7.8:9999
event=peer_dropped reason=idle addr=1.2.3.4:5555 remaining=1
event=peer_replaced old=1.2.3.4:5555 new=9.8.7.6:4444

# v2 (lobby)
event=client_joined client_id=<uuid> addr=1.2.3.4:5555 count=2
event=client_endpoint_update client_id=<uuid> old=1.2.3.4:5555 new=1.2.3.4:6666
event=client_named client_id=<uuid> name='Andre'
event=client_left client_id=<uuid> addr=... reason=bye
event=client_idle_expired client_id=<uuid> addr=...
event=lobby_full attempted_client_id=<uuid> addr=... count=10 max=10

# address proof and the per-IP cap
event=addr_verified addr=1.2.3.4:5555
event=would_block_unverified proto=v1 addr=1.2.3.4:5555 (watch-only; enforcement would withhold traffic)
event=join_rejected reason=ip_cap client_id=<uuid> addr=1.2.3.4:5555
event=bye_rejected reason=endpoint_mismatch client_id=<uuid> from=5.6.7.8:9999

# once a minute: two lines, counting since the previous pair
event=stats forwarded=N dropped_unpaired=N dropped_lobby_full=N
            rejected_bad_header=N pair_changes=N lobby_changes=N
            client_count=N v1_peers=[...] v2_clients=[...]
event=addr_check_stats addr_check=watch-only addr_checks_verified=N
            blocked_unverified=N would_block_unverified=N rejected_ip_cap=N
```

`would_block_unverified` is logged once per client. A v1 peer refused by the
per-IP cap is not logged on its own line; it is only counted in
`rejected_ip_cap`.

What the `addr_check_stats` counters mean:

- `addr_check` — `watch-only` or `ENFORCED`, the same as the startup line.
- `addr_checks_verified` — addresses that echoed their cookie.
- `would_block_unverified` — forwarded packets sent to an address that has
  not proved itself (watch-only mode). While this stays above zero, clients
  that cannot echo are still in use, and turning enforcement on would cut
  them off.
- `blocked_unverified` — forwarded packets withheld (enforcement on).
- `rejected_ip_cap` — joins refused by the per-IP cap.

Never logs CLIENT_ID payload bytes beyond the UUID itself. Never logs audio
payload.

## Tunables

| Setting                  | How to override                                                          |
| ------------------------ | ------------------------------------------------------------------------ |
| Listen port (47830)      | `ExecStart=` `--port=N` in the service unit                              |
| Listen address           | `ExecStart=` `--host=X` in the service unit                              |
| Log file                 | `ExecStart=` `--log-path=PATH` in the service unit                       |
| Lobby capacity (10)      | `--max-clients=N` or env var `REMSOUND_MAX_CLIENTS=N` in the service unit |
| Address proof (watch-only) | `--require-addr-check` or env var `REMSOUND_REQUIRE_ADDR_CHECK=1` in the service unit |
| Per-IP entry cap (4)     | edit `MAX_ENTRIES_PER_IP` in `remsound-relay.py`                         |
| Idle timeout (60 s)      | edit `IDLE_TIMEOUT_SECONDS` in `remsound-relay.py`                       |
| Stats interval (60 s)    | edit `STATS_INTERVAL_SECONDS` in `remsound-relay.py`                     |
| Update check (hourly)    | edit `remsound-relay-update.timer`                                       |
| Update repo (Ednunp/RemSound) | env var `REMSOUND_UPDATE_REPO` in the updater service unit          |

## Why an auto-updater

The relay is small and doesn't change often, but when it does we'd rather
not chase every operator to re-SCP. The updater polls GitHub Releases for
tags starting with `server-`, finds the highest version, downloads it, swaps
the files, restarts the service, and falls back to the prior version if
startup fails. Logs everything to `/var/log/remsound-relay-update.log`.

It only triggers on `server-*` tags, so RemSound app releases (tags like
`vX.Y`, without the `server-` prefix) don't affect the relay. It also skips
drafts and pre-releases.

## Publishing a server release

1. Change the relay files and set `VERSION` to the new tag. Tags must keep
   climbing (`server-vX.Y`), because the updater installs the highest one.
2. Make the tarball. It must be named `remsound-<tag>.tar.gz` and hold a
   single top-level folder named `remsound-<tag>`, containing the bundle
   files listed at the top of this README:

   ```bash
   TAG=server-vX.Y
   tar -czf "/tmp/remsound-$TAG.tar.gz" \
       --transform "s,^<srcdir>,remsound-$TAG," <srcdir>
   ```

3. Publish it, marked NOT latest:

   ```bash
   gh release create "$TAG" "/tmp/remsound-$TAG.tar.gz" \
       --repo Ednunp/RemSound --latest=false \
       --title "Server $TAG" --notes "..."
   ```

   - `--latest=false` is required. GitHub's "Latest" label must stay on the
     Windows app: the app's README download link and its install-by-hand
     message both send people to `/releases/latest`.
   - Do **not** use `--prerelease`. The relay updater skips pre-releases, so
     a server release marked that way never reaches any relay.

Within about an hour every relay's updater installs it, restarts the relay,
and rolls back if the service does not come up.

## Disabling auto-updates

Either disable the timer:

```bash
sudo systemctl disable --now remsound-relay-update.timer
```

…or run `uninstall.sh` (removes the updater scaffolding entirely along
with the relay).
