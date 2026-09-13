"""Unit tests for the RemSound relay's address-proof, per-IP cap, and eviction logic.

The relay ships to the Pi and auto-updates every user, but its branch logic (cookie verify,
watch-only vs enforce, per-IP cap across v1+v2, NAT-rebind reset, forged-BYE rejection) had no
automated coverage — a one-line regression there would sail past the C# gate and re-open the
reflection / occupation / takeover surface the 2026-07-27 address-proof was built to close. These
tests exercise that logic directly with a fake socket, no network, no real Python needed on the Pi.

Run:  py -m unittest test_relay      (from the server/ folder)
"""
from __future__ import annotations

import importlib.util
import logging
import os
import struct
import sys
import unittest
import uuid

# The relay filename has a hyphen, so it can't be `import`ed by name — load it from its path.
# It must be registered in sys.modules BEFORE exec so @dataclass can resolve its own module.
_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("remsound_relay", os.path.join(_HERE, "remsound-relay.py"))
relay = importlib.util.module_from_spec(_spec)
sys.modules["remsound_relay"] = relay
_spec.loader.exec_module(relay)

# A quiet logger so tests don't spam the console.
_LOG = logging.getLogger("remsound-relay-test")
_LOG.addHandler(logging.NullHandler())
_LOG.setLevel(logging.CRITICAL)

CID = uuid.UUID(bytes=bytes(range(16))).bytes  # a fixed 16-byte client id for the v2 tests
CID2 = uuid.UUID(bytes=bytes(range(16, 32))).bytes


class FakeSocket:
    """Records every sendto so a test can inspect what the relay emitted (cookies, forwards)."""

    def __init__(self):
        self.sent: list[tuple[bytes, tuple[str, int]]] = []

    def sendto(self, data, addr):
        self.sent.append((bytes(data), addr))
        return len(data)


def v1_packet(pkt_type: int, payload: bytes = b"", stream_id: int = 1, seq: int = 1) -> bytes:
    return relay.MAGIC + bytes([relay.V1_VERSION, pkt_type]) + struct.pack("<H", stream_id) + struct.pack("<I", seq) + payload


def v2_packet(pkt_type: int, client_id: bytes, payload: bytes = b"", stream_id: int = 1, seq: int = 1) -> bytes:
    return (relay.MAGIC + bytes([relay.V2_VERSION, pkt_type]) + struct.pack("<H", stream_id)
            + struct.pack("<I", seq) + client_id + payload)


def make_relay(require_addr_check: bool = False, max_clients: int = 10):
    return relay.Relay(FakeSocket(), _LOG, max_clients, require_addr_check=require_addr_check)


def cookie_sent_to(sock: FakeSocket, addr) -> bytes | None:
    """The most recent address-proof cookie the relay sent to addr (the 16 bytes after the v1 header)."""
    for data, to in reversed(sock.sent):
        if to == addr and len(data) >= relay.V1_HEADER_LEN + relay.ADDR_CHECK_COOKIE_LEN and data[5] == relay.TYPE_ADDR_CHECK:
            return data[relay.V1_HEADER_LEN:relay.V1_HEADER_LEN + relay.ADDR_CHECK_COOKIE_LEN]
    return None


def forwarded_to(sock: FakeSocket, addr, payload: bytes) -> bool:
    """True if a packet carrying payload was forwarded to addr (ignores the cookie challenges)."""
    return any(to == addr and payload in data and data[5] != relay.TYPE_ADDR_CHECK for data, to in sock.sent)


class AddrCheckV1(unittest.TestCase):
    """The shipping RemSound client is v1-framed (pairwise); these are the load-bearing cases."""

    def test_cookie_issued_on_join_and_verifies_on_echo(self):
        r = make_relay()
        a, b = ("10.0.0.1", 5001), ("10.0.0.2", 5002)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"aud-a"), a)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"aud-b"), b)
        cookie_a = cookie_sent_to(r.sock, a)
        self.assertIsNotNone(cookie_a, "the relay must challenge a newly seen address with a cookie")
        # Wrong cookie must NOT verify.
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, b"\x00" * 16), a)
        self.assertEqual(r.stats.addr_checks_verified, 0, "a wrong cookie must not verify an address")
        # The genuine cookie, echoed back, verifies exactly once (idempotent thereafter).
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie_a), a)
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie_a), a)
        self.assertEqual(r.stats.addr_checks_verified, 1, "echoing the right cookie verifies once, not repeatedly")

    def test_enforce_blocks_unverified_then_forwards_after_verify(self):
        r = make_relay(require_addr_check=True)
        a, b = ("10.0.0.1", 5001), ("10.0.0.2", 5002)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"join-a"), a)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"join-b"), b)
        cookie_a = cookie_sent_to(r.sock, a)   # captured before we clear the socket
        self.assertIsNotNone(cookie_a, "A must have been challenged with a cookie on join")
        r.sock.sent.clear()
        # B streams while A is unverified → enforcement withholds it.
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"SECRET-AUDIO"), b)
        self.assertFalse(forwarded_to(r.sock, a, b"SECRET-AUDIO"), "unverified A must NOT receive forwarded audio under enforcement")
        self.assertGreater(r.stats.blocked_unverified, 0, "the withheld forward must be counted")
        # A proves its address, then the same stream reaches it.
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie_a), a)
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"NOW-DELIVERED"), b)
        self.assertTrue(forwarded_to(r.sock, a, b"NOW-DELIVERED"), "a verified address must receive forwarded audio")

    def test_watch_only_forwards_but_records_would_block(self):
        r = make_relay(require_addr_check=False)
        a, b = ("10.0.0.1", 5001), ("10.0.0.2", 5002)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"join-a"), a)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"join-b"), b)
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"WATCHED"), b)
        self.assertTrue(forwarded_to(r.sock, a, b"WATCHED"), "watch-only mode must still forward (never break pre-5.6 clients)")
        self.assertGreater(r.stats.would_block_unverified, 0, "watch-only must record who WOULD have been blocked")
        self.assertEqual(r.stats.blocked_unverified, 0, "watch-only must not actually block")


class AddrCheckV2(unittest.TestCase):
    def test_rebind_resets_verification(self):
        r = make_relay()
        addr1, addr2 = ("10.0.0.9", 6001), ("10.0.0.9", 6002)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, CID, b"a"), addr1)
        cookie = cookie_sent_to(r.sock, addr1)
        self.assertIsNotNone(cookie)
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie), addr1)  # echo comes back v1-framed
        self.assertTrue(r.v2_clients[uuid.UUID(bytes=CID)].verified, "a correct echo must verify the v2 client")
        # The same client_id appearing from a NEW address must drop verification (spoof-takeover guard).
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, CID, b"a"), addr2)
        self.assertFalse(r.v2_clients[uuid.UUID(bytes=CID)].verified, "an endpoint rebind must clear verified")

    def test_forged_bye_from_other_address_rejected(self):
        r = make_relay()
        addr_a, addr_b = ("10.0.0.1", 7001), ("10.0.0.2", 7002)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, CID, b"a"), addr_a)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, CID2, b"b"), addr_b)
        # B forges a BYE for A's client_id from B's own address — must be refused; A stays.
        r.handle_packet(v2_packet(relay.TYPE_LOBBY_BYE, CID), addr_b)
        self.assertIn(uuid.UUID(bytes=CID), r.v2_clients, "a BYE from a non-registered address must not evict the victim")


class Caps(unittest.TestCase):
    def test_ip_cap_counts_across_protocols(self):
        r = make_relay(max_clients=10)
        # Four v2 clients from one IP fill that IP's quota (MAX_ENTRIES_PER_IP == 4).
        ip = "9.9.9.9"
        for i in range(4):
            cid = uuid.UUID(bytes=bytes([i]) + bytes(15)).bytes
            r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid, b"x"), (ip, 8000 + i))
        self.assertEqual(len(r.v2_clients), 4)
        # A v1 peer from the SAME IP must be refused — the cap counts both protocols.
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), (ip, 8100))
        self.assertGreater(r.stats.rejected_ip_cap, 0, "a 5th entry from a capped IP must be refused")
        self.assertEqual(len(r.v1_peers), 0, "the over-cap v1 peer must not be admitted")
        # A different IP is unaffected.
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), ("8.8.8.8", 8100))
        self.assertEqual(len(r.v1_peers), 1, "a peer from a different IP must still be admitted")


class HeaderGate(unittest.TestCase):
    def test_bad_headers_rejected(self):
        r = make_relay()
        r.handle_packet(b"XY", ("1.1.1.1", 1))              # too short
        r.handle_packet(b"BADX\x01\x02" + bytes(6), ("1.1.1.1", 1))  # wrong magic
        r.handle_packet(relay.MAGIC + bytes([99, 2]) + bytes(6), ("1.1.1.1", 1))  # unknown version
        self.assertEqual(r.stats.rejected_bad_header, 3, "short / wrong-magic / unknown-version must all be rejected")
        self.assertEqual(len(r.v1_peers), 0)
        self.assertEqual(len(r.v2_clients), 0)


class _CapturedLog(logging.Handler):
    """Keeps every formatted log message so a test can read what the relay actually wrote."""

    def __init__(self):
        super().__init__(logging.INFO)
        self.messages: list[str] = []

    def emit(self, record):
        self.messages.append(record.getMessage())


def _key_values(line: str) -> dict[str, str]:
    """Split an event=... log line into its key=value fields (words without '=' are ignored)."""
    return dict(part.split("=", 1) for part in line.split() if "=" in part)


class StatsLog(unittest.TestCase):
    """The address-proof counters are the evidence for deciding when --require-addr-check can be
    switched on. They used to be counted and never written anywhere, so nobody could see them."""

    def _relay_with_log(self, require_addr_check: bool):
        log = logging.getLogger(f"remsound-relay-test-stats-{uuid.uuid4()}")
        log.propagate = False
        log.setLevel(logging.INFO)
        captured = _CapturedLog()
        log.addHandler(captured)
        return relay.Relay(FakeSocket(), log, 10, require_addr_check=require_addr_check), captured

    def test_addr_check_counters_logged_each_interval_then_reset(self):
        for enforce, mode in ((False, "watch-only"), (True, "ENFORCED")):
            with self.subTest(mode=mode):
                r, captured = self._relay_with_log(require_addr_check=enforce)
                # Distinct values, so a counter written under the wrong name cannot pass.
                r.stats.addr_checks_verified = 3
                r.stats.blocked_unverified = 5
                r.stats.would_block_unverified = 7
                r.stats.rejected_ip_cap = 11
                start = r.last_stats_log
                r.maybe_log_stats(start + relay.STATS_INTERVAL_SECONDS / 2)
                self.assertFalse(any(m.startswith("event=addr_check_stats") for m in captured.messages),
                                 "nothing is logged before the stats interval has passed")
                r.maybe_log_stats(start + relay.STATS_INTERVAL_SECONDS + 1)
                lines = [m for m in captured.messages if m.startswith("event=addr_check_stats ")]
                self.assertEqual(len(lines), 1, "one address-proof stats line per interval")
                fields = _key_values(lines[0])
                self.assertEqual(fields.get("addr_check"), mode, "the line must say whether enforcement is on")
                self.assertEqual(fields.get("addr_checks_verified"), "3")
                self.assertEqual(fields.get("blocked_unverified"), "5")
                self.assertEqual(fields.get("would_block_unverified"), "7")
                self.assertEqual(fields.get("rejected_ip_cap"), "11")
                self.assertTrue(any(m.startswith("event=stats forwarded=") for m in captured.messages),
                                "the existing event=stats line must still be written")
                self.assertEqual(
                    (r.stats.addr_checks_verified, r.stats.blocked_unverified,
                     r.stats.would_block_unverified, r.stats.rejected_ip_cap),
                    (0, 0, 0, 0),
                    "the counters start again from zero for the next interval",
                )


if __name__ == "__main__":
    unittest.main()
