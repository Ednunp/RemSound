# RemSound UDP Relay — `server-v2.0`

A small UDP reflector that lets RemSound peers reach each other across the
internet without Tailscale in the audio path. Two modes in one binary:

- **v1 (pairwise)** — the original two-slot reflector. First two endpoints
  to send a valid RemSound v1 packet claim the slots; their traffic gets
  mirrored to each other. Used by RemSound clients up to v1.x.
- **v2 (lobby)** — a multi-peer lobby (default cap: 10) keyed on a
  per-instance CLIENT_ID. Used by RemSound clients that emit v2 packets.
  Periodic LobbyRoster packets keep clients informed of who's in.

A single relay instance handles both protocols concurrently on the same UDP
port. v1 clients keep working unchanged; v2 clients get the lobby model.
(A v1 client and a v2 client cannot hear each other in the same session;
that's a deliberate scope cut for this release.)

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
| `smoke-test.sh`                       | post-install sanity check (v1 + v2 paths + updater scaffolding)                                    |
| `VERSION`                             | the tag this bundle represents (`server-v2.0`)                                                     |
| `README.md`                           | this file                                                                                          |

## Installing on a fresh host

Tested on Raspberry Pi OS Bookworm and Debian / Ubuntu. Requires `python3`
and `curl` (both usually preinstalled).

```bash
# 1. Download and extract the latest server tarball.
curl -L -o /tmp/remsound-server.tar.gz \
  https://github.com/Ednunp/RemSound/releases/download/server-v2.0/remsound-server-v2.0.tar.gz
tar xzf /tmp/remsound-server.tar.gz -C /tmp
cd /tmp/remsound-server-v2.0

# 2. Install — sets up the relay AND auto-updates.
sudo ./install.sh

# 3. Open UDP 47830 in the firewall and / or router port-forward.
#    On UFW: sudo ufw allow 47830/udp
#    On UDM / pfSense / etc.: WAN UDP 47830 -> this host's LAN IP.

# 4. Confirm it's alive.
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
cd /tmp/remsound-server-v2.0   # or wherever the bundle is
sudo ./uninstall.sh
```

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

- Listens on UDP `47830` (chosen to avoid clashes with RemSound's own
  defaults — 47820/47821/47822 — plus NetFlow 2055 and NUT 3493).
- Bound to `0.0.0.0`, so the kernel routes via the default interface.
  Tailscale must NOT carry this traffic — the whole point of the relay is
  to remove Tailscale's hops from the audio path.
- The relay process itself never decodes audio. It validates the
  RemSound header (magic `RMND`, version 1 or 2) and forwards or drops.

## Log format

Structured key=value lines. Notable events:

```
event=startup version_supported=v1,v2 listen=0.0.0.0:47830 max_clients=10

# v1 (pairwise)
event=peer_joined addr=1.2.3.4:5555 slots_filled=1
event=peer_paired a=1.2.3.4:5555 b=5.6.7.8:9999
event=peer_dropped reason=idle addr=1.2.3.4:5555 remaining=1
event=peer_replaced old=1.2.3.4:5555 new=9.8.7.6:4444

# v2 (lobby)
event=client_joined client_id=<uuid> addr=1.2.3.4:5555 count=2
event=client_endpoint_update client_id=<uuid> old=1.2.3.4:5555 new=1.2.3.4:6666
event=client_named client_id=<uuid> name=Andre
event=client_left client_id=<uuid> addr=... reason=bye
event=client_idle_expired client_id=<uuid> addr=...
event=lobby_full attempted_client_id=<uuid> addr=... count=10 max=10

# once a minute
event=stats forwarded=N dropped_unpaired=N dropped_lobby_full=N
            rejected_bad_header=N pair_changes=N lobby_changes=N
            client_count=N v1_peers=[...] v2_clients=[...]
```

Never logs CLIENT_ID payload bytes beyond the UUID itself. Never logs audio
payload.

## Tunables

| Setting                  | How to override                                                          |
| ------------------------ | ------------------------------------------------------------------------ |
| Listen port (47830)      | `ExecStart=` `--port=N` in the service unit                              |
| Listen address           | `ExecStart=` `--host=X` in the service unit                              |
| Lobby capacity (10)      | `--max-clients=N` or env var `REMSOUND_MAX_CLIENTS=N` in the service unit |
| Idle timeout (60 s)      | edit `IDLE_TIMEOUT_SECONDS` in `remsound-relay.py`                       |
| Stats interval (60 s)    | edit `STATS_INTERVAL_SECONDS` in `remsound-relay.py`                     |
| Update check (hourly)    | edit `remsound-relay-update.timer`                                       |
| Update repo (Ednunp/RemSound) | env var `REMSOUND_UPDATE_REPO` in the updater service unit          |

## Why an auto-updater

The relay is small and (after this release) doesn't change often, but when
it does we'd rather not chase every operator to re-SCP. The updater polls
GitHub Releases for tags starting with `server-`, finds the highest version,
downloads it, swaps the files, restarts the service, and falls back to the
prior version if startup fails. Logs everything to
`/var/log/remsound-relay-update.log`.

It only triggers on `server-*` tags, so RemSound client releases (`v1.x`,
`v2.x` without the `server-` prefix) don't affect the relay.

## Disabling auto-updates

Either disable the timer:

```bash
sudo systemctl disable --now remsound-relay-update.timer
```

…or run `uninstall.sh` (removes the updater scaffolding entirely along
with the relay).
