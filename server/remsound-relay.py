#!/usr/bin/env python3
"""
RemSound UDP relay, dual-protocol.

Listens on a single UDP port and handles two protocol versions concurrently:

- v1 ("pairwise"): 12-byte header, two-slot reflector. First two distinct
  UDP endpoints to send a valid RemSound v1 packet claim the slots; subsequent
  v1 packets from one slot's endpoint are reflected to the other. Slots idle
  for IDLE_TIMEOUT_SECONDS are eligible for replacement. This is the original
  remsound-relay.py behaviour, preserved here unchanged so legacy clients keep
  working against the new server.

- v2 ("lobby"): 28-byte header with embedded CLIENT_ID (UUID). Up to
  REMSOUND_MAX_CLIENTS instances (default 10) form a single lobby. Each
  incoming packet is forwarded unmodified to every OTHER registered client.
  Identity is the CLIENT_ID, not the network endpoint — NAT rebinds and
  same-NAT-multiple-clients are no longer special cases. Periodic LobbyRoster
  packets keep clients informed of the current membership.

The two protocols share state only via the listening socket and the stats
counters. They never interact otherwise: a v1 client and a v2 client cannot
hear each other in this release (deliberate — see the design doc).

Owner: Pi thread. Spec: D:\\Dropbox\\proj\\pi\\remsound server update.md.
"""

from __future__ import annotations

import argparse
import logging
import logging.handlers
import os
import select
import signal
import socket
import struct
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional

LISTEN_HOST = "0.0.0.0"
DEFAULT_PORT = 47830
RECV_BUFFER_BYTES = 2048
IDLE_TIMEOUT_SECONDS = 60
STATS_INTERVAL_SECONDS = 60
ROSTER_HEARTBEAT_SECONDS = 1.0  # v2 only — periodic roster broadcast
SOCKET_POLL_TIMEOUT_SECONDS = 1.0
DEFAULT_LOG_PATH = "/var/log/remsound-relay.log"
DEFAULT_MAX_CLIENTS = 10
LOBBY_NAME_BYTES = 32  # bytes reserved for a display name on the wire

# Wire format constants.
MAGIC = b"RMND"
V1_VERSION = 1
V2_VERSION = 2
V1_HEADER_LEN = 12
V2_HEADER_LEN = 28
V2_CLIENT_ID_OFFSET = 12
V2_CLIENT_ID_LEN = 16

# Packet types (v1 + v2 shared range; v2-only types are 6+).
TYPE_FORMAT = 1
TYPE_AUDIO = 2
TYPE_KEEPALIVE = 3
TYPE_HEARTBEAT = 4
TYPE_CONTROL = 5
TYPE_LOBBY_HELLO = 6
TYPE_LOBBY_ROSTER = 7
TYPE_LOBBY_FULL = 8
TYPE_LOBBY_BYE = 9
# Address-proof challenge (2026-07-27): a random cookie sent to every newly seen client address;
# the client echoes the packet back verbatim, proving the address actually RECEIVES — a forged
# (spoofed) source address can never echo. This is what stops the reflection attack (register a
# victim's spoofed address, then have the relay bounce audio at them). 5.6+ clients echo it;
# older clients drop it as an unknown type, so enforcement (--require-addr-check) stays OFF
# until the fleet has updated — watch-only mode logs who WOULD have been blocked meanwhile.
TYPE_ADDR_CHECK = 10
ADDR_CHECK_COOKIE_LEN = 16
ADDR_CHECK_RESEND_SECONDS = 2.0
V2_FORWARDABLE_TYPES = {
    TYPE_FORMAT, TYPE_AUDIO, TYPE_KEEPALIVE, TYPE_HEARTBEAT, TYPE_CONTROL,
}
# Cap on how many lobby/pair entries one source IP may hold at once. Legitimate households behind
# one NAT show a handful of machines (distinct ports, same IP); a lobby-occupation attacker shows
# ten. Enforced immediately — it breaks no working setup.
MAX_ENTRIES_PER_IP = 4

# A zero UUID identifies the server in outbound v2 packets that we originate
# (LobbyRoster, LobbyFull, LobbyBye-from-server). Clients can recognise this
# as "from server" rather than from another peer.
SERVER_CLIENT_ID_BYTES = b"\x00" * V2_CLIENT_ID_LEN


@dataclass
class PeerSlot:
    """v1 protocol — one of (up to) two peer endpoints in a pair."""
    addr: tuple[str, int]
    last_seen: float
    rx_packets: int = 0
    tx_packets: int = 0
    # Address-proof state (see TYPE_ADDR_CHECK).
    verified: bool = False
    cookie: bytes = b""
    cookie_sent: float = 0.0
    would_block_logged: bool = False


@dataclass
class ClientEntry:
    """v2 protocol — one client in the lobby, keyed by CLIENT_ID."""
    addr: tuple[str, int]
    display_name: str
    last_seen: float
    rx_packets: int = 0
    tx_packets: int = 0
    # Address-proof state (see TYPE_ADDR_CHECK).
    verified: bool = False
    cookie: bytes = b""
    cookie_sent: float = 0.0
    would_block_logged: bool = False


@dataclass
class RelayStats:
    forwarded: int = 0
    dropped_unpaired: int = 0          # v1: third endpoint while pair active
    dropped_lobby_full: int = 0        # v2: 11th client when at cap
    rejected_bad_header: int = 0
    pair_changes: int = 0              # v1 slot joins/leaves/replacements
    lobby_changes: int = 0             # v2 joins/leaves/expiries
    addr_checks_verified: int = 0      # cookies echoed back correctly
    blocked_unverified: int = 0        # forwards withheld (enforce mode only)
    would_block_unverified: int = 0    # forwards that WOULD be withheld (watch-only)
    rejected_ip_cap: int = 0           # admissions refused by MAX_ENTRIES_PER_IP


def setup_logger(log_path: str) -> logging.Logger:
    logger = logging.getLogger("remsound-relay")
    logger.setLevel(logging.INFO)
    fmt = logging.Formatter(
        fmt="%(asctime)s level=%(levelname)s %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    try:
        fh = logging.handlers.WatchedFileHandler(log_path, encoding="utf-8")
        fh.setFormatter(fmt)
        logger.addHandler(fh)
    except OSError as e:
        sys.stderr.write(f"remsound-relay: could not open {log_path}: {e}\n")
    sh = logging.StreamHandler(sys.stderr)
    sh.setFormatter(fmt)
    logger.addHandler(sh)
    return logger


def parse_header_v1(data: bytes) -> Optional[tuple[int, int, int]]:
    """Validate a v1 header. Returns (type, stream_id, sequence) or None."""
    if len(data) < V1_HEADER_LEN:
        return None
    pkt_type = data[5]
    stream_id = struct.unpack_from("<H", data, 6)[0]
    sequence = struct.unpack_from("<I", data, 8)[0]
    return pkt_type, stream_id, sequence


def parse_header_v2(data: bytes) -> Optional[tuple[int, int, int, bytes]]:
    """Validate a v2 header. Returns (type, stream_id, sequence, client_id_bytes) or None."""
    if len(data) < V2_HEADER_LEN:
        return None
    pkt_type = data[5]
    stream_id = struct.unpack_from("<H", data, 6)[0]
    sequence = struct.unpack_from("<I", data, 8)[0]
    client_id_bytes = bytes(data[V2_CLIENT_ID_OFFSET:V2_CLIENT_ID_OFFSET + V2_CLIENT_ID_LEN])
    return pkt_type, stream_id, sequence, client_id_bytes


def _fmt_addr(addr: tuple[str, int]) -> str:
    return f"{addr[0]}:{addr[1]}"


def _decode_lobby_name(raw: bytes) -> str:
    """Decode the 32-byte null-padded UTF-8 display-name field. Tolerant of garbage."""
    end = raw.find(b"\x00")
    if end >= 0:
        raw = raw[:end]
    try:
        return raw.decode("utf-8", errors="replace").strip()
    except Exception:
        return ""


def _encode_lobby_name(name: str) -> bytes:
    """Encode a display name into LOBBY_NAME_BYTES, null-padded."""
    encoded = (name or "").encode("utf-8", errors="replace")[:LOBBY_NAME_BYTES]
    return encoded + b"\x00" * (LOBBY_NAME_BYTES - len(encoded))


class Relay:
    """Dispatcher that owns both the v1 pair state and the v2 lobby state."""

    def __init__(self, sock: socket.socket, log: logging.Logger, max_clients: int,
                 require_addr_check: bool = False):
        self.sock = sock
        self.log = log
        self.max_clients = max_clients
        # Enforcement switch for the address-proof: False = watch-only (log who WOULD be blocked,
        # forward anyway — safe while pre-5.6 clients that can't echo are still around); True =
        # withhold all forwarded traffic from unverified addresses. Flipped by --require-addr-check
        # in a later server release once the 5.6 auto-update has rolled through.
        self.require_addr_check = require_addr_check
        # v1 state
        self.v1_peers: list[PeerSlot] = []
        # v2 state
        self.v2_clients: dict[uuid.UUID, ClientEntry] = {}
        self.v2_roster_dirty = False  # set when membership changes
        self.v2_last_roster_broadcast = 0.0
        # shared
        self.stats = RelayStats()
        self.last_stats_log = time.monotonic()

    # ------- address-proof (shared by v1 + v2) -----------------------------

    def _addr_check_packet(self, cookie: bytes) -> bytes:
        """A v1-framed AddrCheck: 12-byte RemSound header + the cookie. v1 framing on purpose —
        every client (v1 pair or v2 lobby) parses it, and the echo comes back the same way."""
        header = bytearray(V1_HEADER_LEN)
        header[0:4] = MAGIC
        header[4] = V1_VERSION
        header[5] = TYPE_ADDR_CHECK
        return bytes(header) + cookie

    def _send_addr_check(self, entry, addr: tuple[str, int], now: float) -> None:
        """Issue (or re-issue) the cookie challenge for an entry. Throttled; keeps the same cookie
        until verified so a slow echo still matches."""
        if entry.verified or (now - entry.cookie_sent) < ADDR_CHECK_RESEND_SECONDS:
            return
        if not entry.cookie:
            entry.cookie = os.urandom(ADDR_CHECK_COOKIE_LEN)
        entry.cookie_sent = now
        try:
            self.sock.sendto(self._addr_check_packet(entry.cookie), addr)
        except OSError as e:
            self.log.warning("event=addr_check_send_failed to=%s err=%s", _fmt_addr(addr), e)

    def _try_verify(self, entry, data: bytes, addr: tuple[str, int], header_len: int) -> None:
        """An AddrCheck came back from a registered endpoint — verify its cookie. The echo may
        arrive v1-framed (as sent) even from a v2 client, so callers pass their header length."""
        cookie = data[header_len:header_len + ADDR_CHECK_COOKIE_LEN]
        if entry.cookie and cookie == entry.cookie and not entry.verified:
            entry.verified = True
            self.stats.addr_checks_verified += 1
            self.log.info("event=addr_verified addr=%s", _fmt_addr(addr))

    def _ip_at_cap(self, ip: str) -> bool:
        """True when this source IP already holds MAX_ENTRIES_PER_IP lobby/pair entries."""
        count = sum(1 for p in self.v1_peers if p.addr[0] == ip)
        count += sum(1 for e in self.v2_clients.values() if e.addr[0] == ip)
        return count >= MAX_ENTRIES_PER_IP

    def _may_forward_to(self, entry, proto: str) -> bool:
        """The enforcement point: may forwarded traffic be delivered to this entry's address?
        Watch-only mode always says yes but logs (once per entry) who WOULD have been blocked."""
        if entry.verified:
            return True
        if self.require_addr_check:
            self.stats.blocked_unverified += 1
            return False
        self.stats.would_block_unverified += 1
        if not entry.would_block_logged:
            entry.would_block_logged = True
            self.log.info(
                "event=would_block_unverified proto=%s addr=%s (watch-only; enforcement would withhold traffic)",
                proto, _fmt_addr(entry.addr),
            )
        return True

    # ------- v1 (pairwise) -------------------------------------------------

    def _v1_find_slot(self, addr: tuple[str, int]) -> Optional[int]:
        for i, p in enumerate(self.v1_peers):
            if p.addr == addr:
                return i
        return None

    def _v1_expire_idle(self, now: float) -> None:
        if not self.v1_peers:
            return
        kept: list[PeerSlot] = []
        dropped: list[tuple[str, int]] = []
        for p in self.v1_peers:
            if (now - p.last_seen) <= IDLE_TIMEOUT_SECONDS:
                kept.append(p)
            else:
                dropped.append(p.addr)
        if dropped:
            self.v1_peers = kept
            for addr in dropped:
                self.log.info(
                    "event=peer_dropped reason=idle addr=%s remaining=%d",
                    _fmt_addr(addr), len(self.v1_peers),
                )
                self.stats.pair_changes += 1

    def _v1_admit_or_replace(self, addr: tuple[str, int], now: float) -> int:
        if len(self.v1_peers) < 2:
            self.v1_peers.append(PeerSlot(addr=addr, last_seen=now))
            self.log.info(
                "event=peer_joined addr=%s slots_filled=%d",
                _fmt_addr(addr), len(self.v1_peers),
            )
            self.stats.pair_changes += 1
            if len(self.v1_peers) == 2:
                self.log.info(
                    "event=peer_paired a=%s b=%s",
                    _fmt_addr(self.v1_peers[0].addr),
                    _fmt_addr(self.v1_peers[1].addr),
                )
            return len(self.v1_peers) - 1
        oldest = 0 if self.v1_peers[0].last_seen <= self.v1_peers[1].last_seen else 1
        if (now - self.v1_peers[oldest].last_seen) > IDLE_TIMEOUT_SECONDS:
            old_addr = self.v1_peers[oldest].addr
            self.v1_peers[oldest] = PeerSlot(addr=addr, last_seen=now)
            self.log.info(
                "event=peer_replaced old=%s new=%s",
                _fmt_addr(old_addr), _fmt_addr(addr),
            )
            self.stats.pair_changes += 1
            return oldest
        return -1

    def _handle_v1(self, data: bytes, addr: tuple[str, int]) -> None:
        parsed = parse_header_v1(data)
        if parsed is None:
            self.stats.rejected_bad_header += 1
            return
        pkt_type = parsed[0]
        now = time.monotonic()
        if pkt_type == TYPE_ADDR_CHECK:
            # A cookie coming home. Echoes come back v1-framed regardless of the client's protocol
            # (clients echo our framing verbatim), so match by ADDRESS across BOTH protocol states
            # — and never ADMIT anyone off one: an AddrCheck is proof, not a join request.
            for e in self.v2_clients.values():
                if e.addr == addr:
                    self._try_verify(e, data, addr, V1_HEADER_LEN)
                    return
            found = self._v1_find_slot(addr)
            if found is not None:
                self._try_verify(self.v1_peers[found], data, addr, V1_HEADER_LEN)
            return
        idx = self._v1_find_slot(addr)
        if idx is None:
            if self._ip_at_cap(addr[0]):
                self.stats.rejected_ip_cap += 1
                return
            self._v1_expire_idle(now)
            idx = self._v1_admit_or_replace(addr, now)
            if idx < 0:
                self.stats.dropped_unpaired += 1
                return
        peer = self.v1_peers[idx]
        peer.last_seen = now
        peer.rx_packets += 1
        self._send_addr_check(peer, addr, now)
        if len(self.v1_peers) == 2:
            other = self.v1_peers[1 - idx]
            if not self._may_forward_to(other, "v1"):
                return
            try:
                self.sock.sendto(data, other.addr)
                other.tx_packets += 1
                self.stats.forwarded += 1
            except OSError as e:
                self.log.warning(
                    "event=send_failed proto=v1 to=%s err=%s",
                    _fmt_addr(other.addr), e,
                )
        else:
            self.stats.dropped_unpaired += 1

    # ------- v2 (lobby) ----------------------------------------------------

    def _v2_build_roster_packet(self) -> bytes:
        """Build a LobbyRoster packet with the current membership."""
        # Use a separate per-build sequence — clients can ignore it; we use 0.
        header = bytearray(V2_HEADER_LEN)
        header[0:4] = MAGIC
        header[4] = V2_VERSION
        header[5] = TYPE_LOBBY_ROSTER
        struct.pack_into("<H", header, 6, 0)  # stream_id (unused)
        struct.pack_into("<I", header, 8, 0)  # sequence (unused)
        header[V2_CLIENT_ID_OFFSET:V2_CLIENT_ID_OFFSET + V2_CLIENT_ID_LEN] = SERVER_CLIENT_ID_BYTES
        payload = bytearray()
        members = list(self.v2_clients.items())[:255]  # 1-byte count
        payload.append(len(members))
        for cid, entry in members:
            payload.extend(cid.bytes)
            payload.extend(_encode_lobby_name(entry.display_name))
        return bytes(header) + bytes(payload)

    def _v2_broadcast_roster(self) -> None:
        if not self.v2_clients:
            self.v2_roster_dirty = False
            self.v2_last_roster_broadcast = time.monotonic()
            return
        packet = self._v2_build_roster_packet()
        for entry in self.v2_clients.values():
            # Under enforcement even the roster stays away from unverified addresses — it's
            # relay-originated traffic too, and it grows with the lobby. (Watch-only: send.)
            if self.require_addr_check and not entry.verified:
                continue
            try:
                self.sock.sendto(packet, entry.addr)
            except OSError as e:
                self.log.warning(
                    "event=send_failed proto=v2 reason=roster to=%s err=%s",
                    _fmt_addr(entry.addr), e,
                )
        self.v2_roster_dirty = False
        self.v2_last_roster_broadcast = time.monotonic()

    def _v2_send_lobby_full(self, attempted_client_id: uuid.UUID, addr: tuple[str, int]) -> None:
        """Send a LobbyFull packet back to an over-cap client and log it."""
        header = bytearray(V2_HEADER_LEN)
        header[0:4] = MAGIC
        header[4] = V2_VERSION
        header[5] = TYPE_LOBBY_FULL
        struct.pack_into("<H", header, 6, 0)
        struct.pack_into("<I", header, 8, 0)
        header[V2_CLIENT_ID_OFFSET:V2_CLIENT_ID_OFFSET + V2_CLIENT_ID_LEN] = SERVER_CLIENT_ID_BYTES
        # Payload: 1 byte current count, 1 byte max count.
        payload = bytes([len(self.v2_clients) & 0xFF, self.max_clients & 0xFF])
        try:
            self.sock.sendto(bytes(header) + payload, addr)
        except OSError as e:
            self.log.warning(
                "event=send_failed proto=v2 reason=lobby_full to=%s err=%s",
                _fmt_addr(addr), e,
            )
        self.log.info(
            "event=lobby_full attempted_client_id=%s addr=%s count=%d max=%d",
            attempted_client_id, _fmt_addr(addr),
            len(self.v2_clients), self.max_clients,
        )
        self.stats.dropped_lobby_full += 1

    def _v2_expire_idle(self, now: float) -> None:
        if not self.v2_clients:
            return
        expired: list[uuid.UUID] = []
        for cid, entry in self.v2_clients.items():
            if (now - entry.last_seen) > IDLE_TIMEOUT_SECONDS:
                expired.append(cid)
        for cid in expired:
            entry = self.v2_clients.pop(cid)
            self.log.info(
                "event=client_idle_expired client_id=%s addr=%s",
                cid, _fmt_addr(entry.addr),
            )
            self.stats.lobby_changes += 1
            self.v2_roster_dirty = True

    def _handle_v2(self, data: bytes, addr: tuple[str, int]) -> None:
        parsed = parse_header_v2(data)
        if parsed is None:
            self.stats.rejected_bad_header += 1
            return
        pkt_type, _stream_id, _sequence, cid_bytes = parsed
        try:
            client_id = uuid.UUID(bytes=cid_bytes)
        except ValueError:
            self.stats.rejected_bad_header += 1
            return
        now = time.monotonic()
        entry = self.v2_clients.get(client_id)
        # Capture whether this packet came from the endpoint this client_id is CURRENTLY registered
        # at, BEFORE the NAT-rebind update below overwrites entry.addr. Used to reject spoofed
        # control packets: the roster broadcast ships every member's client_id to all members, so on
        # an internet-facing relay anyone who joins learns the others' ids and could otherwise forge
        # a BYE to evict them. A genuine BYE always comes from the client's own registered endpoint.
        from_registered_endpoint = entry is not None and entry.addr == addr
        if entry is None:
            # Admit attempt.
            if self._ip_at_cap(addr[0]):
                self.stats.rejected_ip_cap += 1
                self.log.warning(
                    "event=join_rejected reason=ip_cap client_id=%s addr=%s", client_id, _fmt_addr(addr),
                )
                return
            if len(self.v2_clients) >= self.max_clients:
                self._v2_send_lobby_full(client_id, addr)
                return
            entry = ClientEntry(addr=addr, display_name="", last_seen=now)
            self.v2_clients[client_id] = entry
            self.log.info(
                "event=client_joined client_id=%s addr=%s count=%d",
                client_id, _fmt_addr(addr), len(self.v2_clients),
            )
            self.stats.lobby_changes += 1
            self.v2_roster_dirty = True
        else:
            # Refresh endpoint (handles NAT rebind) and last-seen. A MOVED endpoint must re-prove
            # itself — the new address hasn't echoed anything yet, and "rebind" is also exactly
            # what a spoofed takeover of a known client_id looks like.
            if entry.addr != addr:
                self.log.info(
                    "event=client_endpoint_update client_id=%s old=%s new=%s",
                    client_id, _fmt_addr(entry.addr), _fmt_addr(addr),
                )
                entry.addr = addr
                entry.verified = False
                entry.cookie = b""
                entry.cookie_sent = 0.0
                entry.would_block_logged = False
            entry.last_seen = now
        entry.rx_packets += 1
        if pkt_type == TYPE_ADDR_CHECK:
            # The cookie coming home (the client echoes our v1-framed challenge, so it can land in
            # the v2 handler only if the client wrapped it v2 — accept both framings). Never forward.
            header_len = V2_HEADER_LEN if len(data) >= V2_HEADER_LEN + ADDR_CHECK_COOKIE_LEN else V1_HEADER_LEN
            self._try_verify(entry, data, addr, header_len)
            return
        self._send_addr_check(entry, addr, now)

        # Type-specific handling.
        if pkt_type == TYPE_LOBBY_HELLO:
            payload = data[V2_HEADER_LEN:V2_HEADER_LEN + LOBBY_NAME_BYTES]
            new_name = _decode_lobby_name(payload)
            if new_name != entry.display_name:
                entry.display_name = new_name
                self.log.info(
                    "event=client_named client_id=%s name=%r", client_id, new_name,
                )
                self.v2_roster_dirty = True
            return
        if pkt_type == TYPE_LOBBY_BYE:
            # Only the endpoint a client is registered at may say goodbye for it — otherwise a
            # forged BYE bearing a known client_id (learned from the roster) could evict any peer.
            if not from_registered_endpoint:
                self.log.warning(
                    "event=bye_rejected reason=endpoint_mismatch client_id=%s from=%s",
                    client_id, _fmt_addr(addr),
                )
                return
            self.v2_clients.pop(client_id, None)
            self.log.info(
                "event=client_left client_id=%s addr=%s reason=bye",
                client_id, _fmt_addr(addr),
            )
            self.stats.lobby_changes += 1
            self.v2_roster_dirty = True
            return
        if pkt_type not in V2_FORWARDABLE_TYPES:
            # Unknown / server-originated type from a client. Ignore quietly.
            return

        # Fan-out forwarding to every OTHER client (verified addresses only, once enforcing).
        for other_id, other in self.v2_clients.items():
            if other_id == client_id:
                continue
            if not self._may_forward_to(other, "v2"):
                continue
            try:
                self.sock.sendto(data, other.addr)
                other.tx_packets += 1
                self.stats.forwarded += 1
            except OSError as e:
                self.log.warning(
                    "event=send_failed proto=v2 to=%s err=%s",
                    _fmt_addr(other.addr), e,
                )

    # ------- shared --------------------------------------------------------

    def handle_packet(self, data: bytes, addr: tuple[str, int]) -> None:
        if len(data) < 6 or data[0:4] != MAGIC:
            self.stats.rejected_bad_header += 1
            return
        version = data[4]
        if version == V1_VERSION:
            self._handle_v1(data, addr)
        elif version == V2_VERSION:
            self._handle_v2(data, addr)
        else:
            self.stats.rejected_bad_header += 1

    def tick(self, now: float) -> None:
        """Periodic housekeeping: idle expiry + roster broadcast."""
        self._v1_expire_idle(now)
        self._v2_expire_idle(now)
        if self.v2_clients and (
            self.v2_roster_dirty
            or (now - self.v2_last_roster_broadcast) >= ROSTER_HEARTBEAT_SECONDS
        ):
            self._v2_broadcast_roster()

    def maybe_log_stats(self, now: float) -> None:
        if (now - self.last_stats_log) < STATS_INTERVAL_SECONDS:
            return
        self.last_stats_log = now
        s = self.stats
        v1_summary = ", ".join(
            f"{_fmt_addr(p.addr)}(rx={p.rx_packets},tx={p.tx_packets})"
            for p in self.v1_peers
        ) or "none"
        v2_summary = ", ".join(
            f"{cid}@{_fmt_addr(e.addr)}(rx={e.rx_packets},tx={e.tx_packets})"
            for cid, e in self.v2_clients.items()
        ) or "none"
        self.log.info(
            "event=stats forwarded=%d dropped_unpaired=%d dropped_lobby_full=%d "
            "rejected_bad_header=%d pair_changes=%d lobby_changes=%d "
            "client_count=%d v1_peers=[%s] v2_clients=[%s]",
            s.forwarded, s.dropped_unpaired, s.dropped_lobby_full,
            s.rejected_bad_header, s.pair_changes, s.lobby_changes,
            len(self.v2_clients), v1_summary, v2_summary,
        )
        self.stats = RelayStats()
        for p in self.v1_peers:
            p.rx_packets = 0
            p.tx_packets = 0
        for e in self.v2_clients.values():
            e.rx_packets = 0
            e.tx_packets = 0


def main() -> int:
    parser = argparse.ArgumentParser(description="RemSound UDP relay (dual-protocol v1+v2)")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"UDP port to listen on (default {DEFAULT_PORT})")
    parser.add_argument("--host", default=LISTEN_HOST,
                        help=f"Bind address (default {LISTEN_HOST})")
    parser.add_argument("--log-path", default=DEFAULT_LOG_PATH,
                        help=f"Log file path (default {DEFAULT_LOG_PATH})")
    parser.add_argument(
        "--max-clients", type=int,
        default=int(os.environ.get("REMSOUND_MAX_CLIENTS", str(DEFAULT_MAX_CLIENTS))),
        help=f"v2 lobby capacity (default {DEFAULT_MAX_CLIENTS}, "
             "overridable via REMSOUND_MAX_CLIENTS env var)",
    )
    parser.add_argument(
        "--require-addr-check",
        action="store_true",
        default=os.environ.get("REMSOUND_REQUIRE_ADDR_CHECK", "") == "1",
        help="enforce the address-proof cookie: forwarded traffic is withheld from addresses that "
             "have not echoed their cookie (default off = watch-only, which only logs). Flip on "
             "once the 5.6+ client rollout is complete - pre-5.6 clients cannot echo.",
    )
    args = parser.parse_args()
    if args.max_clients < 2:
        sys.stderr.write("remsound-relay: --max-clients must be >= 2\n")
        return 2

    log = setup_logger(args.log_path)
    log.info(
        "event=startup version_supported=v1,v2 listen=%s:%d max_clients=%d addr_check=%s",
        args.host, args.port, args.max_clients,
        "ENFORCED" if args.require_addr_check else "watch-only",
    )

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind((args.host, args.port))
    except OSError as e:
        log.error("event=bind_failed err=%s", e)
        return 1

    relay = Relay(sock, log, args.max_clients, require_addr_check=args.require_addr_check)
    stop_flag = {"stop": False}

    def _stop_signal(_signum, _frame):
        stop_flag["stop"] = True

    signal.signal(signal.SIGTERM, _stop_signal)
    signal.signal(signal.SIGINT, _stop_signal)

    try:
        while not stop_flag["stop"]:
            try:
                ready, _, _ = select.select([sock], [], [], SOCKET_POLL_TIMEOUT_SECONDS)
            except InterruptedError:
                continue
            except OSError as e:
                # select() itself failed (e.g. a transient resource-pressure error on a long-
                # running, low-RAM host). Log and pause briefly rather than spin or exit.
                log.warning("event=select_failed err=%s", e)
                time.sleep(0.1)
                continue
            now = time.monotonic()
            # Per-iteration work, fully guarded. A relay that must stay up for DAYS — and that is
            # reachable from the open internet — can never let a single packet or a housekeeping
            # tick crash the whole process: that would drop EVERY connected client and force a ~5s
            # systemd restart. Anything unexpected is logged (with a traceback) and we carry on.
            try:
                if ready:
                    data, addr = sock.recvfrom(RECV_BUFFER_BYTES)
                    relay.handle_packet(data, addr)
                relay.tick(now)
                relay.maybe_log_stats(now)
            except OSError as e:
                # recvfrom, or a sendto that escaped its own guard — transient; keep serving.
                log.warning("event=io_error err=%s", e)
            except Exception:
                log.exception("event=loop_error — recovered, continuing")
    finally:
        log.info("event=shutdown")
        sock.close()

    return 0


if __name__ == "__main__":
    sys.exit(main())
