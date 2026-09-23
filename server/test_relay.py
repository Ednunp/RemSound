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
import logging.handlers
import os
import struct
import sys
import time
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
        r.handle_packet(hello(CID, "a", G1), addr1)  # a client joins with a hello
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
        r.handle_packet(hello(CID, "a", G1), addr_a)
        r.handle_packet(hello(CID2, "b", G1), addr_b)
        # B forges a BYE for A's client_id from B's own address — must be refused; A stays.
        r.handle_packet(v2_packet(relay.TYPE_LOBBY_BYE, CID), addr_b)
        self.assertIn(uuid.UUID(bytes=CID), r.v2_clients, "a BYE from a non-registered address must not evict the victim")


class Caps(unittest.TestCase):
    def test_ip_cap_counts_across_protocols(self):
        r = make_relay(max_clients=10)
        # v2 clients from one IP fill that IP's quota, whatever the cap is set to.
        ip = "9.9.9.9"
        cap = relay.MAX_ENTRIES_PER_IP
        r = make_relay(max_clients=cap + 6)
        for i in range(cap):
            cid = uuid.UUID(bytes=bytes([i]) + bytes(15)).bytes
            r.handle_packet(hello(cid, f"c{i}", G1), (ip, 8000 + i))
        self.assertEqual(len(r.v2_clients), cap)
        # A v1 peer from the SAME IP must be refused — the cap counts both protocols.
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), (ip, 8100))
        self.assertGreater(r.stats.rejected_ip_cap, 0, "one more than the cap from one IP must be refused")
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


G1 = bytes([0x11] * 8)   # two password fingerprints, used as group tags
G2 = bytes([0x22] * 8)


def cid(n: int) -> bytes:
    return uuid.UUID(bytes=bytes([n]) * 16).bytes


def hello(client_id: bytes, name: str, group: bytes | None, ticked: list[bytes] | None = None) -> bytes:
    """A LobbyHello: the 32-byte name, the 8-byte group tag (None = a hello from before groups), and
    optionally the ticked list (a count byte then that many ids). No list at all means "everyone"."""
    payload = name.encode("utf-8")[:relay.LOBBY_NAME_BYTES].ljust(relay.LOBBY_NAME_BYTES, b"\x00")
    payload += group or b""
    if ticked is not None:
        payload += bytes([len(ticked)]) + b"".join(ticked)
    return v2_packet(relay.TYPE_LOBBY_HELLO, client_id, payload)


def format_payload(fingerprint: bytes) -> bytes:
    """A Format payload long enough to carry its password fingerprint at offset 36, as a sending app's does."""
    return bytes(relay.FORMAT_FINGERPRINT_OFFSET) + fingerprint


ROSTER_ENTRY = 16 + relay.LOBBY_NAME_BYTES + 1   # id, name, that member's own flags byte


def rosters_to(sock: FakeSocket, addr) -> list[tuple[set[bytes], int | None]]:
    """Every LobbyRoster the relay sent to addr: (the member ids it listed, its trailing flags byte)."""
    out = []
    for data, to in sock.sent:
        if to != addr or len(data) <= relay.V2_HEADER_LEN or data[4] != relay.V2_VERSION or data[5] != relay.TYPE_LOBBY_ROSTER:
            continue
        body = data[relay.V2_HEADER_LEN:]
        count = body[0]
        ids = {body[1 + i * ROSTER_ENTRY:1 + i * ROSTER_ENTRY + 16] for i in range(count)}
        end = 1 + count * ROSTER_ENTRY
        out.append((ids, body[end] if len(body) > end else None))
    return out


def who_ticks_you(sock: FakeSocket, addr) -> set[bytes]:
    """From the last roster sent to addr: the ids of the members whose own flags byte says they have ticked it."""
    for data, to in reversed(sock.sent):
        if to != addr or len(data) <= relay.V2_HEADER_LEN or data[4] != relay.V2_VERSION or data[5] != relay.TYPE_LOBBY_ROSTER:
            continue
        body = data[relay.V2_HEADER_LEN:]
        count = body[0]
        out = set()
        for i in range(count):
            at = 1 + i * ROSTER_ENTRY
            if body[at + ROSTER_ENTRY - 1] & relay.ROSTER_MEMBER_FLAG_TICKS_YOU:
                out.add(body[at:at + 16])
        return out
    return set()


class Groups(unittest.TestCase):
    """Ed, 2026-09-18: a relay carries groups. Everyone on one password hears everyone else in it; two
    passwords never meet. Before this a relay carried one pair, and a third person heard nothing."""

    def _six(self):
        r = make_relay(max_clients=64)
        members = []
        for n in range(6):
            addr = (f"10.1.0.{n + 1}", 7000 + n)
            group = G1 if n < 3 else G2
            r.handle_packet(hello(cid(n + 1), f"person{n + 1}", group), addr)
            members.append((cid(n + 1), addr, group))
        return r, members

    def test_audio_reaches_everyone_in_the_group_and_no_one_outside_it(self):
        r, m = self._six()
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, m[0][0], b"GROUP-ONE-AUDIO"), m[0][1])
        for n in (1, 2):
            self.assertTrue(forwarded_to(r.sock, m[n][1], b"GROUP-ONE-AUDIO"),
                            f"person{n + 1} shares person1's password and must be sent their audio")
        for n in (3, 4, 5):
            self.assertFalse(forwarded_to(r.sock, m[n][1], b"GROUP-ONE-AUDIO"),
                             f"person{n + 1} is on another password and must not be sent person1's audio")
        self.assertFalse(forwarded_to(r.sock, m[0][1], b"GROUP-ONE-AUDIO"), "a sender is never sent its own audio")

    def test_each_member_is_told_only_about_its_own_group(self):
        r, m = self._six()
        r.sock.sent.clear()
        r.v2_roster_dirty = True
        r.tick(time.monotonic())
        for n, (_, addr, group) in enumerate(m):
            rosters = rosters_to(r.sock, addr)
            self.assertTrue(rosters, f"person{n + 1} must be sent a roster")
            ids, flags = rosters[-1]
            self.assertEqual(ids, {c for c, _, g in m if g == group},
                             f"person{n + 1}'s roster must list exactly the members of its own group")
            self.assertEqual(flags, 0, "nobody holds a v1 pair here, so the paired flag must be clear")

    def test_a_hello_from_before_groups_still_reaches_its_kind(self):
        r = make_relay()
        a, b, c = ("10.2.0.1", 7100), ("10.2.0.2", 7101), ("10.2.0.3", 7102)
        r.handle_packet(hello(cid(1), "old-a", None), a)
        r.handle_packet(hello(cid(2), "old-b", None), b)
        r.handle_packet(hello(cid(3), "grouped", G1), c)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"NO-TAG"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"NO-TAG"), "two clients whose hello carries no group tag still hear each other")
        self.assertFalse(forwarded_to(r.sock, c, b"NO-TAG"), "a client with no tag is not in a tagged group")


class Ticks(unittest.TestCase):
    """Ed, 2026-09-20: through a relay, sound passes only when each has ticked the other, exactly as two
    people on one network must each tick the other. A client that sends no list has ticked everyone in its
    group, which is what every app that knows nothing about ticking sends — so none of them is affected."""

    def test_a_client_that_sends_no_list_reaches_its_whole_group(self):
        r = make_relay()
        a, b = ("10.6.0.1", 8000), ("10.6.0.2", 8001)
        r.handle_packet(hello(cid(1), "a", G1), a)
        r.handle_packet(hello(cid(2), "b", G1), b)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"NO-LIST"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"NO-LIST"),
                        "a client that sends no ticked list must still reach its group, as before ticking existed")

    def test_both_must_tick_each_other(self):
        r = make_relay()
        a, b = ("10.6.1.1", 8100), ("10.6.1.2", 8101)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[cid(2)]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=[]), b)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"ONE-WAY"), a)
        self.assertFalse(forwarded_to(r.sock, b, b"ONE-WAY"),
                         "a has ticked b, but b has ticked nobody: nothing may pass")
        r.handle_packet(hello(cid(2), "b", G1, ticked=[cid(1)]), b)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"BOTH-WAYS"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"BOTH-WAYS"), "once both have ticked each other, sound passes")
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(2), b"AND-BACK"), b)
        self.assertTrue(forwarded_to(r.sock, a, b"AND-BACK"), "and it passes the other way too")

    def test_unticking_stops_it_in_both_directions(self):
        r = make_relay()
        a, b = ("10.6.2.1", 8200), ("10.6.2.2", 8201)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[cid(2)]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=[cid(1)]), b)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)   # a unticks b
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"GONE-OUT"), a)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(2), b"GONE-BACK"), b)
        self.assertFalse(forwarded_to(r.sock, b, b"GONE-OUT"), "unticking must stop your sound reaching them")
        self.assertFalse(forwarded_to(r.sock, a, b"GONE-BACK"), "and must stop theirs reaching you")

    def test_someone_they_have_not_ticked_is_not_sent_it(self):
        r = make_relay()
        a, b, c = ("10.6.3.1", 8300), ("10.6.3.2", 8301), ("10.6.3.3", 8302)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[cid(2)]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=[cid(1)]), b)
        r.handle_packet(hello(cid(3), "c", G1, ticked=[cid(1), cid(2)]), c)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"FOR-B-ONLY"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"FOR-B-ONLY"), "b ticked a and a ticked b")
        self.assertFalse(forwarded_to(r.sock, c, b"FOR-B-ONLY"),
                         "c has ticked a, but a has not ticked c: c must not be sent a's audio")


class TicksYouFlag(unittest.TestCase):
    """Ed's relay list has to be able to say "waiting for them to tick you". On its own an app knows only
    who it has ticked, so the relay tells each person, in every roster, which of the others have ticked
    THEM. Nobody learns anything about a person they cannot already see in their own group."""

    def test_the_roster_says_who_has_ticked_you(self):
        r = make_relay()
        a, b, c = ("10.7.0.1", 9000), ("10.7.0.2", 9001), ("10.7.0.3", 9002)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=[cid(1)]), b)
        r.handle_packet(hello(cid(3), "c", G1, ticked=[]), c)
        r.sock.sent.clear()
        r.v2_roster_dirty = True
        r._v2_broadcast_roster()
        self.assertEqual(who_ticks_you(r.sock, a), {cid(2)},
                         "a must be told that b has ticked it, and that c has not")
        self.assertEqual(who_ticks_you(r.sock, b), set(),
                         "b must be told that nobody has ticked it yet, which is why it hears nothing")

    def test_ticking_sends_the_list_again_at_once(self):
        r = make_relay()
        a, b = ("10.7.1.1", 9100), ("10.7.1.2", 9101)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=[]), b)
        r._v2_broadcast_roster()          # settle: the list is up to date and nothing is outstanding
        self.assertFalse(r.v2_roster_dirty)
        r.sock.sent.clear()
        r.handle_packet(hello(cid(2), "b", G1, ticked=[cid(1)]), b)
        self.assertTrue(r.v2_roster_dirty, "a change of ticks must send the list again rather than wait a second")
        r._v2_broadcast_roster()
        self.assertEqual(who_ticks_you(r.sock, a), {cid(2)}, "and the list it sends must carry the new tick")

    def test_a_client_that_ticks_everyone_counts_as_ticking_you(self):
        r = make_relay()
        a, b = ("10.7.2.1", 9200), ("10.7.2.2", 9201)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)
        r.handle_packet(hello(cid(2), "b", G1, ticked=None), b)   # an app that knows nothing about ticking
        r.sock.sent.clear()
        r.v2_roster_dirty = True
        r._v2_broadcast_roster()
        self.assertEqual(who_ticks_you(r.sock, a), {cid(2)},
                         "an app that sends no list has ticked everyone, so it has ticked you")

    def test_another_group_is_never_named(self):
        r = make_relay()
        a, d = ("10.7.3.1", 9300), ("10.7.3.2", 9301)
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)
        r.handle_packet(hello(cid(4), "d", G2, ticked=[cid(1)]), d)
        r.sock.sent.clear()
        r.v2_roster_dirty = True
        r._v2_broadcast_roster()
        self.assertEqual(who_ticks_you(r.sock, a), set(),
                         "someone on another password must not appear in your list at all, ticked or not")


class AdmissionOrder(unittest.TestCase):
    """Review 2026-09-13, item 11: v2 used to admit an unknown client id first and check the packet type after."""

    def test_only_a_joining_packet_admits_an_unknown_client(self):
        r = make_relay()
        addr = ("10.3.0.1", 7200)
        for t in (relay.TYPE_ADDR_CHECK, relay.TYPE_LOBBY_BYE, relay.TYPE_LOBBY_ROSTER, relay.TYPE_LOBBY_FULL):
            r.handle_packet(v2_packet(t, cid(9), bytes(16)), addr)
        self.assertEqual(len(r.v2_clients), 0, "an echo, a BYE or a relay-only type from an unknown client id must admit nobody")
        r.handle_packet(hello(cid(9), "joiner", G1), addr)
        self.assertEqual(len(r.v2_clients), 1, "a hello admits")


class PhonesStillPair(unittest.TestCase):
    """A v1-only device (a phone, an older app) pairs exactly as before. A group member can partner it, so it
    still reaches one person; two members never pair over v1, because they already reach each other."""

    def test_two_v1_devices_pair_exactly_as_before(self):
        r = make_relay()
        a, b, c = ("10.4.4.1", 7700), ("10.4.4.2", 7701), ("10.4.4.3", 7702)
        for addr in (a, b, c):
            r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), addr)
        self.assertEqual([p.addr for p in r.v1_peers], [a, b], "the first two v1-only devices pair; a third waits")
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"A-TO-B"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"A-TO-B"), "a pair still reaches each other")
        self.assertFalse(forwarded_to(r.sock, c, b"A-TO-B"), "and nobody else")

    def test_a_member_partners_a_waiting_phone(self):
        r = make_relay()
        phone, win = ("10.4.0.1", 7300), ("10.4.0.2", 7301)
        r.handle_packet(hello(cid(1), "windows", G1), win)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), win)
        self.assertEqual(len(r.v1_peers), 0, "a group member must not wait alone in a pair slot")
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), phone)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), win)
        self.assertEqual({p.addr for p in r.v1_peers}, {phone, win}, "the member must partner the phone waiting for it")
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"PHONE-TO-WIN"), phone)
        self.assertTrue(forwarded_to(r.sock, win, b"PHONE-TO-WIN"), "the phone's audio must reach its partner")
        r.v2_roster_dirty = True
        r.tick(time.monotonic())
        self.assertEqual(rosters_to(r.sock, win)[-1][1], relay.ROSTER_FLAG_V1_PAIRED,
                         "the member must be told it has a v1 partner, so it keeps a v1 copy of its audio flowing")

    def test_two_members_never_pair_over_v1(self):
        r = make_relay()
        phone, w1, w2 = ("10.4.1.1", 7400), ("10.4.1.2", 7401), ("10.4.1.3", 7402)
        r.handle_packet(hello(cid(1), "w1", G1), w1)
        r.handle_packet(hello(cid(2), "w2", G1), w2)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), phone)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), w1)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), w2)
        self.assertEqual({p.addr for p in r.v1_peers}, {phone, w1}, "the second member must not take the other slot")
        # The phone goes quiet and its slot expires, leaving w1 alone in the pair: w2 still must not join it.
        for p in r.v1_peers:
            if p.addr == phone:
                p.last_seen -= relay.IDLE_TIMEOUT_SECONDS + 1
        r._v1_expire_idle(time.monotonic())
        self.assertEqual([p.addr for p in r.v1_peers], [w1], "the phone's slot must have expired for this to prove anything")
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), w2)
        self.assertEqual([p.addr for p in r.v1_peers], [w1], "a member left alone in the pair must not be partnered by another member")

    def test_a_pair_of_two_members_is_dissolved(self):
        r = make_relay()
        a, b = ("10.4.2.1", 7500), ("10.4.2.2", 7501)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), a)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), b)
        self.assertEqual(len(r.v1_peers), 2, "two v1 senders pair, exactly as before")
        r.handle_packet(hello(cid(1), "a", G1), a)
        self.assertEqual(len(r.v1_peers), 2, "one member beside a v1-only device keeps the pair")
        r.handle_packet(hello(cid(2), "b", G1), b)
        self.assertEqual(len(r.v1_peers), 0, "once both are group members, the v1 pair must be freed")

    def test_a_member_does_not_partner_a_phone_on_another_password(self):
        r = make_relay()
        phone, win, win2 = ("10.4.3.1", 7600), ("10.4.3.2", 7601), ("10.4.3.3", 7602)
        r.handle_packet(v1_packet(relay.TYPE_FORMAT, format_payload(G2)), phone)
        r.handle_packet(hello(cid(1), "windows", G1), win)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), win)
        self.assertEqual([p.addr for p in r.v1_peers], [phone], "a member must not partner a phone talking on another password")
        r.handle_packet(hello(cid(2), "windows2", G2), win2)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), win2)
        self.assertEqual({p.addr for p in r.v1_peers}, {phone, win2}, "a member on the phone's own password may partner it")


class RelayHint(unittest.TestCase):
    def test_a_device_the_pair_has_no_room_for_learns_it_reached_a_relay(self):
        r = make_relay()
        a, b, c = ("10.5.0.1", 7800), ("10.5.0.2", 7801), ("10.5.0.3", 7802)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), a)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), b)
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), c)
        self.assertIsNotNone(cookie_sent_to(r.sock, c),
                             "a device the pair has no room for must get an address check back, so a newer app knows it reached a relay")
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), c)
        self.assertIsNone(cookie_sent_to(r.sock, c), "and not again within the rate limit")


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


class OnlyAHelloAdmits(unittest.TestCase):
    """Review 2026-09-23, agreed by Ed: v2 used to admit an unknown client id on ANY packet a member might send, so
    anybody could fill every place with made-up ids. Only a hello lets somebody in now."""

    def test_sound_and_heartbeats_from_an_unknown_id_admit_nobody(self):
        r = make_relay()
        addr = ("10.9.0.1", 7300)
        for t in (relay.TYPE_FORMAT, relay.TYPE_AUDIO, relay.TYPE_KEEPALIVE, relay.TYPE_HEARTBEAT, relay.TYPE_CONTROL):
            r.handle_packet(v2_packet(t, cid(40), b"payload"), addr)
        self.assertEqual(len(r.v2_clients), 0, "sound, heartbeats or control from an unknown client id must admit nobody")
        r.handle_packet(hello(cid(40), "joiner", G1), addr)
        self.assertEqual(len(r.v2_clients), 1, "a hello admits")

    def test_a_flood_of_made_up_ids_fills_nothing(self):
        r = make_relay(max_clients=4)
        for i in range(50):
            r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(100 + i), b"x"), (f"10.9.{i}.1", 7400))
        self.assertEqual(len(r.v2_clients), 0, "made-up ids sending sound must not take a single place")
        r.handle_packet(hello(cid(1), "a real person", G1), ("10.9.200.1", 7401))
        self.assertEqual(len(r.v2_clients), 1, "and a real person saying hello still gets in")

    def test_a_member_whose_place_lapsed_is_back_on_its_next_hello(self):
        r = make_relay()
        addr = ("10.9.1.1", 7500)
        r.handle_packet(hello(cid(41), "back again", G1), addr)
        r.v2_clients.clear()   # its place lapsed (the relay restarted, say)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(41), b"x"), addr)
        self.assertEqual(len(r.v2_clients), 0, "its sound alone does not bring it back")
        r.handle_packet(hello(cid(41), "back again", G1), addr)
        self.assertIn(uuid.UUID(bytes=cid(41)), r.v2_clients, "its next hello, due within two seconds, does")


class CountsThatMatter(unittest.TestCase):
    """Review 2026-09-23, agreed by Ed: the counts the enforcement decision is read from were swollen by every group
    member's ordinary heartbeats. They count what matters now, and say how many DEVICES, not just packets."""

    def test_a_members_heartbeats_are_not_counted_as_drops_or_refusals(self):
        r = make_relay(max_clients=20)
        ip = "10.8.0.1"
        cap = relay.MAX_ENTRIES_PER_IP
        for i in range(cap):   # a household that fills its cap with group members
            r.handle_packet(hello(cid(60 + i), f"m{i}", G1), (ip, 8200 + i))
        before = (r.stats.dropped_unpaired, r.stats.rejected_ip_cap)
        for i in range(cap):   # each member's ordinary heartbeat, offering to partner a phone that isn't there
            r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), (ip, 8200 + i))
        self.assertEqual((r.stats.dropped_unpaired, r.stats.rejected_ip_cap), before,
                         "a member's heartbeats with no phone waiting are neither a drop nor a refusal")

    def test_a_member_at_its_households_cap_can_still_partner_a_phone(self):
        r = make_relay(max_clients=20)
        phone = ("10.8.1.9", 8300)
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), phone)   # a phone waits in a pair slot
        ip = "10.8.1.1"
        cap = relay.MAX_ENTRIES_PER_IP
        for i in range(cap):
            r.handle_packet(hello(cid(80 + i), f"m{i}", G1), (ip, 8310 + i))
        r.handle_packet(v1_packet(relay.TYPE_HEARTBEAT, b"hb"), (ip, 8310))
        self.assertTrue(r._v1_paired((ip, 8310)),
                        "a member is already counted against its household's cap; offering to partner a phone is not a second device")
        self.assertEqual(r.stats.rejected_ip_cap, 0, "and nothing was refused")

    def test_would_block_counts_devices_as_well_as_packets(self):
        r = make_relay()   # watch-only
        a, b = ("10.8.2.1", 8400), ("10.8.2.2", 8401)
        r.handle_packet(hello(cid(90), "a", G1), a)
        r.handle_packet(hello(cid(91), "b", G1), b)
        for _ in range(25):
            r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(90), b"x"), a)   # a to b, and b has not proved its address
        self.assertGreaterEqual(r.stats.would_block_unverified, 25, "the packet count still counts packets")
        self.assertEqual(len(r.stats.would_block_devices), 1, "but it is ONE device that would have been cut off")

    def test_the_device_counts_are_written_and_start_again(self):
        log = logging.getLogger(f"remsound-relay-test-devices-{uuid.uuid4()}")
        log.propagate = False
        log.setLevel(logging.INFO)
        captured = _CapturedLog()
        log.addHandler(captured)
        r = relay.Relay(FakeSocket(), log, 10)
        r.stats.would_block_devices.update({("10.0.0.1", 1), ("10.0.0.2", 2), ("10.0.0.3", 3)})
        r.stats.blocked_devices.add(("10.0.0.4", 4))
        r.maybe_log_stats(r.last_stats_log + relay.STATS_INTERVAL_SECONDS + 1)
        line = next(m for m in captured.messages if m.startswith("event=addr_check_stats "))
        fields = _key_values(line)
        self.assertEqual(fields.get("would_block_devices"), "3")
        self.assertEqual(fields.get("blocked_devices"), "1")
        self.assertEqual((len(r.stats.would_block_devices), len(r.stats.blocked_devices)), (0, 0),
                         "they start again from nothing for the next interval")


class TwoWeeksOfLog(unittest.TestCase):
    """Review 2026-09-23, Ed: keep two weeks. The log was one file that grew for ever - 2.1 MB in four days on the Pi."""

    def test_the_log_starts_a_new_file_each_midnight_and_keeps_fourteen(self):
        import tempfile
        path = os.path.join(tempfile.mkdtemp(), "relay.log")
        log = relay.setup_logger(path)
        try:
            files = [h for h in log.handlers if isinstance(h, logging.FileHandler)]
            self.assertEqual(len(files), 1, "the relay must write one log file")
            handler = files[0]
            self.assertIsInstance(handler, logging.handlers.TimedRotatingFileHandler,
                                  "a file that is never trimmed grows for ever on a Pi's SD card")
            self.assertEqual(handler.when, "MIDNIGHT", "a new file each midnight")
            self.assertEqual(handler.backupCount, 14, "and two weeks of old ones kept, the rest deleted")
            self.assertEqual(relay.LOG_KEEP_DAYS, 14)
        finally:
            for h in list(log.handlers):
                log.removeHandler(h)
                h.close()


if __name__ == "__main__":
    unittest.main()
