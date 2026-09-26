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
import shutil
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


def make_relay(require_addr_check: bool = False, max_clients: int = 10, v2_watch_only: bool = False):
    return relay.Relay(FakeSocket(), _LOG, max_clients, require_addr_check=require_addr_check,
                       v2_watch_only=v2_watch_only)


def cookie_sent_to(sock: FakeSocket, addr) -> bytes | None:
    """The most recent address-proof cookie the relay sent to addr (the 16 bytes after the v1 header)."""
    for data, to in reversed(sock.sent):
        if to == addr and len(data) >= relay.V1_HEADER_LEN + relay.ADDR_CHECK_COOKIE_LEN and data[5] == relay.TYPE_ADDR_CHECK:
            return data[relay.V1_HEADER_LEN:relay.V1_HEADER_LEN + relay.ADDR_CHECK_COOKIE_LEN]
    return None


def forwarded_to(sock: FakeSocket, addr, payload: bytes) -> bool:
    """True if a packet carrying payload was forwarded to addr (ignores the cookie challenges)."""
    return any(to == addr and payload in data and data[5] != relay.TYPE_ADDR_CHECK for data, to in sock.sent)


def prove(r, addr) -> None:
    """Answer the address check the relay sent to addr, as a real app does."""
    cookie = cookie_sent_to(r.sock, addr)
    assert cookie is not None, f"no address check was sent to {addr}"
    r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie), addr)


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
    def test_a_proved_client_is_not_moved_by_one_packet_from_a_new_address(self):
        # Until the pre-release sweep of 2026-09-25 this was test_rebind_resets_verification: the entry MOVED to the new
        # address on the spot and dropped its proof. Moving at once is the takeover (NoTakeover, below); a proved client
        # now stays where it proved itself until the new address has proved itself too and the old one has gone quiet.
        r = make_relay()
        addr1, addr2 = ("10.0.0.9", 6001), ("10.0.0.9", 6002)
        r.handle_packet(hello(CID, "a", G1), addr1)  # a client joins with a hello
        cookie = cookie_sent_to(r.sock, addr1)
        self.assertIsNotNone(cookie)
        r.handle_packet(v1_packet(relay.TYPE_ADDR_CHECK, cookie), addr1)  # echo comes back v1-framed
        self.assertTrue(r.v2_clients[uuid.UUID(bytes=CID)].verified, "a correct echo must verify the v2 client")
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, CID, b"a"), addr2)
        entry = r.v2_clients[uuid.UUID(bytes=CID)]
        self.assertEqual((entry.addr, entry.verified), (addr1, True),
                         "one packet with a proved client's id from a new address must not move it or clear its proof")
        self.assertIsNotNone(cookie_sent_to(r.sock, addr2), "the new address is sent an address check of its own")

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


def join(r, client_id: bytes, name: str, group: bytes | None, addr, ticked: list[bytes] | None = None) -> None:
    """A client joining as every real app does: its hello, then its answer to the address check the relay sends back.
    Since the pre-release sweep of 2026-09-25 a group client that has not answered is sent nothing else and listed
    nowhere, so a test of what members hear or see must have its members answer, as they do in life."""
    r.handle_packet(hello(client_id, name, group, ticked), addr)
    prove(r, addr)


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
            join(r, cid(n + 1), f"person{n + 1}", group, addr)
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
        join(r, cid(1), "old-a", None, a)
        join(r, cid(2), "old-b", None, b)
        join(r, cid(3), "grouped", G1, c)
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
        join(r, cid(1), "a", G1, a)
        join(r, cid(2), "b", G1, b)
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"NO-LIST"), a)
        self.assertTrue(forwarded_to(r.sock, b, b"NO-LIST"),
                        "a client that sends no ticked list must still reach its group, as before ticking existed")

    def test_both_must_tick_each_other(self):
        r = make_relay()
        a, b = ("10.6.1.1", 8100), ("10.6.1.2", 8101)
        join(r, cid(1), "a", G1, a, ticked=[cid(2)])
        join(r, cid(2), "b", G1, b, ticked=[])
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
        join(r, cid(1), "a", G1, a, ticked=[cid(2)])
        join(r, cid(2), "b", G1, b, ticked=[cid(1)])
        r.handle_packet(hello(cid(1), "a", G1, ticked=[]), a)   # a unticks b
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"GONE-OUT"), a)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(2), b"GONE-BACK"), b)
        self.assertFalse(forwarded_to(r.sock, b, b"GONE-OUT"), "unticking must stop your sound reaching them")
        self.assertFalse(forwarded_to(r.sock, a, b"GONE-BACK"), "and must stop theirs reaching you")

    def test_someone_they_have_not_ticked_is_not_sent_it(self):
        r = make_relay()
        a, b, c = ("10.6.3.1", 8300), ("10.6.3.2", 8301), ("10.6.3.3", 8302)
        join(r, cid(1), "a", G1, a, ticked=[cid(2)])
        join(r, cid(2), "b", G1, b, ticked=[cid(1)])
        join(r, cid(3), "c", G1, c, ticked=[cid(1), cid(2)])
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
        join(r, cid(1), "a", G1, a, ticked=[])
        join(r, cid(2), "b", G1, b, ticked=[cid(1)])
        join(r, cid(3), "c", G1, c, ticked=[])
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
        join(r, cid(1), "a", G1, a, ticked=[])
        join(r, cid(2), "b", G1, b, ticked=[])
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
        join(r, cid(1), "a", G1, a, ticked=[])
        join(r, cid(2), "b", G1, b, ticked=None)   # an app that knows nothing about ticking
        r.sock.sent.clear()
        r.v2_roster_dirty = True
        r._v2_broadcast_roster()
        self.assertEqual(who_ticks_you(r.sock, a), {cid(2)},
                         "an app that sends no list has ticked everyone, so it has ticked you")

    def test_another_group_is_never_named(self):
        r = make_relay()
        a, d = ("10.7.3.1", 9300), ("10.7.3.2", 9301)
        join(r, cid(1), "a", G1, a, ticked=[])
        join(r, cid(4), "d", G2, d, ticked=[cid(1)])
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
        join(r, cid(1), "windows", G1, win)
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
        # The groups are enforced by default since the pre-release sweep of 2026-09-25, so "would block" is a group's
        # figure only under --v2-watch-only; enforced, the same packets are blocked, and counted the same way.
        for watch_only, would, blocked in ((True, "would_block_unverified", "would_block_devices"),
                                           (False, "blocked_unverified", "blocked_devices")):
            with self.subTest(v2_watch_only=watch_only):
                r = make_relay(v2_watch_only=watch_only)   # v1 watch-only
                a, b = ("10.8.2.1", 8400), ("10.8.2.2", 8401)
                join(r, cid(90), "a", G1, a)
                r.handle_packet(hello(cid(91), "b", G1), b)
                for _ in range(25):
                    r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(90), b"x"), a)   # a to b, and b has not proved its address
                self.assertGreaterEqual(getattr(r.stats, would), 25, "the packet count still counts packets")
                self.assertEqual(len(getattr(r.stats, blocked)), 1, "but it is ONE device that would have been cut off")

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




def _relay_with_captured_log(max_clients: int = 10):
    log = logging.getLogger(f"remsound-relay-test-limits-{uuid.uuid4()}")
    log.propagate = False
    log.setLevel(logging.INFO)
    captured = _CapturedLog()
    log.addHandler(captured)
    return relay.Relay(FakeSocket(), log, max_clients), captured


def _lines(captured: _CapturedLog, event: str) -> list[str]:
    return [m for m in captured.messages if m.startswith(f"event={event} ")]


class LogLimits(unittest.TestCase):
    """Review 2026-09-25, agreed by Ed: nearly every line the relay writes is set off by a packet, and a packet can come
    from anybody. One sender could have it write a line for every packet and fill the Pi's memory card in a day."""

    def _next_minute(self, r):
        r.maybe_log_stats(r.last_stats_log + relay.STATS_INTERVAL_SECONDS + 1)

    def test_one_sender_cannot_write_a_line_per_packet(self):
        r, captured = _relay_with_captured_log()
        for i in range(1000):   # a new name in every hello
            r.handle_packet(hello(cid(1), f"name{i}", G1), ("10.20.0.1", 9000))
        self.assertEqual(len(_lines(captured, "client_named")), relay.LOG_REPEATS_PER_ADDRESS,
                         "a thousand renames from one address must not write a thousand lines")
        self._next_minute(r)
        held = _lines(captured, "log_held_back")
        self.assertEqual(len(held), 1, "the minute's figures say what was held back, in one line")
        fields = _key_values(held[0])
        self.assertEqual((fields.get("kind"), fields.get("lines")),
                         ("client_named", str(1000 - relay.LOG_REPEATS_PER_ADDRESS)))

    def test_many_addresses_together_are_limited_too(self):
        r, captured = _relay_with_captured_log(max_clients=2)
        for n, addr in ((1, ("10.21.0.1", 9001)), (2, ("10.21.0.2", 9002))):
            r.handle_packet(hello(cid(n), f"p{n}", G1), addr)
            prove(r, addr)
        for i in range(1000):   # hellos from a thousand made-up addresses at a full relay
            r.handle_packet(hello(cid(10 + i % 200), "x", G1), (f"10.22.{i // 250}.{i % 250}", 9100))
        self.assertEqual(len(_lines(captured, "lobby_full")), relay.LOG_LINES_PER_KIND,
                         "however many addresses it comes from, one kind of line is limited each minute")
        self._next_minute(r)
        fields = _key_values(_lines(captured, "log_held_back")[0])
        self.assertEqual(fields.get("lines"), str(1000 - relay.LOG_LINES_PER_KIND))

    def test_the_limits_start_again_each_minute(self):
        r, captured = _relay_with_captured_log()
        addr = ("10.23.0.1", 9000)
        for i in range(50):
            r.handle_packet(hello(cid(1), f"a{i}", G1), addr)
        self._next_minute(r)
        before = len(_lines(captured, "client_named"))
        r.handle_packet(hello(cid(1), "a new minute", G1), addr)
        self.assertEqual(len(_lines(captured, "client_named")), before + 1, "a new minute, a new allowance")
        self._next_minute(r)
        self.assertEqual(len(_lines(captured, "log_held_back")), 1, "and nothing held back is reported twice")

    def test_a_full_relay_starting_again_is_written_in_full(self):
        r, captured = _relay_with_captured_log(max_clients=relay.DEFAULT_MAX_CLIENTS)
        for n in range(relay.DEFAULT_MAX_CLIENTS - relay.MAX_ENTRIES_PER_IP):
            r.handle_packet(hello(cid(n + 1), f"p{n}", G1), (f"10.24.0.{n + 1}", 9000))
        for n in range(relay.MAX_ENTRIES_PER_IP):   # and a household behind one address
            r.handle_packet(hello(cid(200 + n), f"h{n}", G1), ("10.24.1.1", 9000 + n))
        self.assertEqual(len(_lines(captured, "client_joined")), relay.DEFAULT_MAX_CLIENTS,
                         "every real join after a restart must still be written, a whole household's included")
        self.assertEqual(r.log_held_back, {})


class DailyLogLimit(unittest.TestCase):
    """Review 2026-09-25: whatever gets past the limits, a day's file stops growing at LOG_MAX_BYTES_PER_DAY, and the
    once-a-minute figures still get through."""

    def test_a_days_file_stops_at_its_limit_and_the_figures_go_on(self):
        import tempfile
        path = os.path.join(tempfile.mkdtemp(), "relay.log")
        handler = relay.DailyLog(path, max_bytes=4096)
        handler.setFormatter(logging.Formatter("%(asctime)s level=%(levelname)s %(message)s"))
        log = logging.getLogger(f"remsound-relay-test-daily-{uuid.uuid4()}")
        log.propagate = False
        log.setLevel(logging.INFO)
        log.addHandler(handler)
        try:
            for i in range(500):
                log.info("event=client_named client_id=%s name=%r", i, "x" * 40)
            log.info("event=stats forwarded=1", extra={"keep": True})
            handler.flush()
            with open(path, encoding="utf-8") as f:
                text = f.read()
            self.assertLess(os.path.getsize(path), 4096 + 512, "the file stops at its limit")
            self.assertEqual(text.count("event=log_full "), 1, "and says so, once")
            self.assertIn("event=stats forwarded=1", text, "the once-a-minute figures are still written")
            written = text.count("event=client_named ")
            self.assertGreater(written, 0)
            self.assertEqual(handler.held_back, 500 - written, "and every line not written is counted")

            handler.rolloverAt = int(time.time()) - 1   # midnight has just passed
            log.info("event=client_named client_id=%s name=%r", 999, "the next day")
            handler.flush()
            with open(path, encoding="utf-8") as f:
                first = f.readline()
            self.assertIn(f"event=log_was_full lines_not_written={500 - written}", first,
                          "the next day's file opens with how much the last one could not hold")
            self.assertEqual(handler.held_back, 0)
        finally:
            log.removeHandler(handler)
            handler.close()

    def test_under_systemd_the_journal_gets_errors_only(self):
        import tempfile
        restore = os.environ.get("JOURNAL_STREAM")
        try:
            for journal, level in (("8:12345", logging.ERROR), (None, logging.NOTSET)):
                if journal is None:
                    os.environ.pop("JOURNAL_STREAM", None)
                else:
                    os.environ["JOURNAL_STREAM"] = journal
                log = relay.setup_logger(os.path.join(tempfile.mkdtemp(), "relay.log"))
                try:
                    screens = [h for h in log.handlers if type(h) is logging.StreamHandler]
                    self.assertEqual(len(screens), 1)
                    self.assertEqual(screens[0].level, level,
                                     "under systemd the journal kept a second copy of every line, with no limits")
                finally:
                    for h in list(log.handlers):
                        log.removeHandler(h)
                        h.close()
        finally:
            if restore is None:
                os.environ.pop("JOURNAL_STREAM", None)
            else:
                os.environ["JOURNAL_STREAM"] = restore


class MadeUpAddressesGiveWay(unittest.TestCase):
    """Review 2026-09-25, agreed by Ed: a hello is one packet from an address that can be made up, so anybody could
    fill the relay, or somebody else's address, with made-up hellos and keep real people out. A made-up address can
    never answer its address check; when there is no room, a place that never answered gives way."""

    def test_made_up_addresses_cannot_keep_a_real_person_out(self):
        r = make_relay(max_clients=4)
        for i in range(4):   # made up: they never answer
            r.handle_packet(hello(cid(120 + i), "fake", G1), (f"10.30.{i}.1", 9100))
        real = ("10.30.9.9", 9200)
        r.handle_packet(hello(cid(1), "real", G1), real)
        self.assertIn(uuid.UUID(bytes=cid(1)), r.v2_clients, "a real person gets in")
        self.assertEqual(len(r.v2_clients), 4, "and the relay holds no more than it may")
        self.assertNotIn(uuid.UUID(bytes=cid(120)), r.v2_clients, "the longest-waiting made-up place went")
        self.assertIn(uuid.UUID(bytes=cid(121)), r.v2_clients, "and only that one")
        prove(r, real)
        for i in range(40):
            r.handle_packet(hello(cid(130 + i), "fake", G1), (f"10.31.{i}.1", 9100))
        self.assertIn(uuid.UUID(bytes=cid(1)), r.v2_clients, "once proved, no made-up hello can turn them out")
        self.assertEqual(len(r.v2_clients), 4)

    def test_a_relay_full_of_proven_people_is_still_full(self):
        r = make_relay(max_clients=3)
        for n in range(3):
            addr = (f"10.32.0.{n + 1}", 9300)
            r.handle_packet(hello(cid(n + 1), f"p{n}", G1), addr)
            prove(r, addr)
        late = ("10.32.9.1", 9300)
        r.sock.sent.clear()
        r.handle_packet(hello(cid(9), "late", G1), late)
        self.assertNotIn(uuid.UUID(bytes=cid(9)), r.v2_clients)
        self.assertEqual(len(r.v2_clients), 3, "nobody who proved their address is turned out")
        self.assertTrue(any(to == late and data[5] == relay.TYPE_LOBBY_FULL for data, to in r.sock.sent),
                        "and the latecomer is told the relay is full, as before")

    def test_made_up_hellos_at_a_households_address_cannot_lock_it_out(self):
        r = make_relay(max_clients=64)
        elsewhere = ("10.33.9.9", 9399)
        r.handle_packet(hello(cid(139), "somebody elsewhere, not answered yet", G1), elsewhere)
        ip = "10.33.0.1"
        for i in range(relay.MAX_ENTRIES_PER_IP):   # made up, at somebody else's address
            r.handle_packet(hello(cid(140 + i), "fake", G1), (ip, 9400 + i))
        r.handle_packet(hello(cid(2), "real", G1), (ip, 9500))
        self.assertIn(uuid.UUID(bytes=cid(2)), r.v2_clients, "the real device at that address gets in")
        self.assertEqual(r.stats.rejected_ip_cap, 0)
        self.assertEqual(sum(1 for e in r.v2_clients.values() if e.addr[0] == ip), relay.MAX_ENTRIES_PER_IP,
                         "and the address still holds no more than its limit")
        self.assertIn(uuid.UUID(bytes=cid(139)), r.v2_clients,
                      "room at one address is made at that address, not by turning out somebody elsewhere")

    def test_a_household_of_proven_devices_is_still_capped(self):
        r = make_relay(max_clients=64)
        ip = "10.34.0.1"
        for i in range(relay.MAX_ENTRIES_PER_IP):
            r.handle_packet(hello(cid(160 + i), f"d{i}", G1), (ip, 9600 + i))
            prove(r, (ip, 9600 + i))
        r.handle_packet(hello(cid(3), "one more", G1), (ip, 9700))
        self.assertNotIn(uuid.UUID(bytes=cid(3)), r.v2_clients)
        self.assertEqual(r.stats.rejected_ip_cap, 1, "one address's limit still holds for devices that proved themselves")

    def test_nothing_changes_while_there_is_room(self):
        r = make_relay(max_clients=10)
        for n in range(6):   # none of them has answered yet
            r.handle_packet(hello(cid(n + 1), f"p{n}", G1), (f"10.35.0.{n + 1}", 9800))
        self.assertEqual(len(r.v2_clients), 6, "with room to spare, nobody is turned out for not answering yet")


def _all_to(sock: FakeSocket, addr) -> list[bytes]:
    """Every packet the relay sent to addr."""
    return [data for data, to in sock.sent if to == addr]


class NothingForTheUnproven(unittest.TestCase):
    """Pre-release sweep 2026-09-25, agreed by Ed: in watch-only mode anybody could send hellos from made-up addresses, in
    a group they made up or a real one. Each made-up address was sent the member list every second for a minute, and the
    made-up people were LISTED, so real members saw them and, accepting automatically, had their sound sent to them: 56
    made-up entries drew about 155 KB a second out of the relay. A group client is now sent nothing but its address check
    until it answers it - unless --v2-watch-only puts the groups back as they were."""

    def _two_members_and_fakes(self, r, fakes: int = 5):
        a, b = ("10.40.0.1", 9000), ("10.40.0.2", 9001)
        join(r, cid(1), "a", G1, a)   # both accept automatically: neither sends a tick list, so each has ticked everyone
        join(r, cid(2), "b", G1, b)
        fake_addrs = [(f"10.41.{i}.1", 9100 + i) for i in range(fakes)]
        for i, f in enumerate(fake_addrs):
            r.handle_packet(hello(cid(100 + i), f"fake{i}", G1), f)   # made up: they never answer
        return a, b, fake_addrs

    def test_a_made_up_member_is_sent_only_its_address_check_and_is_listed_nowhere(self):
        r = make_relay()
        a, b, fakes = self._two_members_and_fakes(r)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"A-SPEAKS"), a)
        start = time.monotonic()
        for i in range(30):   # half a minute of heartbeat lists
            r.tick(start + i * 1.5)
        for f in fakes:
            self.assertEqual({data[5] for data in _all_to(r.sock, f)}, {relay.TYPE_ADDR_CHECK},
                             f"{f} never answered: it must be sent its address check and nothing else")
        self.assertTrue(forwarded_to(r.sock, b, b"A-SPEAKS"), "the real members still hear each other")
        for addr in (a, b):
            self.assertEqual(rosters_to(r.sock, addr)[-1][0], {cid(1), cid(2)},
                             "made-up people must not be shown to real members, who would tick them")
        self.assertEqual(r.stats.rosters_withheld_unverified, 30 * len(fakes))

    def test_once_it_answers_it_is_a_member_at_once(self):
        r = make_relay()
        a, b, _ = self._two_members_and_fakes(r, fakes=0)
        c = ("10.40.0.3", 9002)
        r.handle_packet(hello(cid(3), "c", G1), c)
        start = time.monotonic()
        r._v2_broadcast_roster(start)
        self.assertEqual(rosters_to(r.sock, c), [], "not answered yet: no list")
        self.assertNotIn(cid(3), rosters_to(r.sock, a)[-1][0], "and not listed")
        prove(r, c)
        self.assertTrue(r.v2_roster_dirty, "answering must send the lists out again at once, not on the next heartbeat")
        r.sock.sent.clear()
        r.tick(start + 0.5)
        self.assertEqual(rosters_to(r.sock, c)[-1][0], {cid(1), cid(2), cid(3)}, "it is sent the list")
        self.assertIn(cid(3), rosters_to(r.sock, a)[-1][0], "and is in everybody else's")
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"NOW-C-HEARS"), a)
        self.assertTrue(forwarded_to(r.sock, c, b"NOW-C-HEARS"), "and hears them")

    def test_the_withholding_is_logged_once_per_client_and_counted(self):
        r, captured = _relay_with_captured_log()
        a, fake = ("10.40.1.1", 9000), ("10.40.1.2", 9001)
        join(r, cid(1), "a", G1, a)
        r.handle_packet(hello(cid(2), "fake", G1), fake)
        start = time.monotonic()
        for i in range(10):
            r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"x"), a)
            r.tick(start + i * 1.5)
        self.assertEqual(len(_lines(captured, "withheld_unverified")), 1, "one line for the client, not one per list or packet")
        self.assertEqual((r.stats.rosters_withheld_unverified, len(r.stats.blocked_devices)), (10, 1))
        r.maybe_log_stats(r.last_stats_log + relay.STATS_INTERVAL_SECONDS + 1)
        fields = _key_values(_lines(captured, "addr_check_stats")[0])
        self.assertEqual((fields.get("addr_check"), fields.get("v2_addr_check"), fields.get("rosters_withheld")),
                         ("watch-only", "ENFORCED", "10"), "the minute's figures say what was withheld, and why")

    def test_v2_watch_only_puts_the_groups_back_as_they_were(self):
        r = make_relay(v2_watch_only=True)
        a, b, fakes = self._two_members_and_fakes(r, fakes=2)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"TO-EVERYONE"), a)
        r._v2_broadcast_roster(time.monotonic())
        for f in fakes:
            self.assertTrue(forwarded_to(r.sock, f, b"TO-EVERYONE"), "watch-only: an unproven member is still sent sound")
            self.assertTrue(rosters_to(r.sock, f), "and the list")
        self.assertEqual(rosters_to(r.sock, a)[-1][0], {cid(1), cid(2), cid(100), cid(101)}, "and is listed")
        self.assertEqual(r.stats.blocked_unverified, 0)
        self.assertGreater(r.stats.would_block_unverified, 0, "but it is still counted as one that would be blocked")

    def test_require_addr_check_still_enforces_the_groups(self):
        r = make_relay(require_addr_check=True, v2_watch_only=True)
        a, _, fakes = self._two_members_and_fakes(r, fakes=1)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"ENFORCED"), a)
        r._v2_broadcast_roster(time.monotonic())
        self.assertFalse(forwarded_to(r.sock, fakes[0], b"ENFORCED"))
        self.assertEqual(rosters_to(r.sock, fakes[0]), [])
        self.assertEqual(rosters_to(r.sock, a)[-1][0], {cid(1), cid(2)}, "and, enforced, the unproven are not listed")

    def test_phones_and_older_apps_on_v1_are_untouched(self):
        r = make_relay()   # the defaults: v1 pairs watch-only, groups enforced
        phone1, phone2, unproven = ("10.40.2.1", 9000), ("10.40.2.2", 9001), ("10.40.2.3", 9002)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), phone1)
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"x"), phone2)
        r.handle_packet(hello(cid(5), "a group client that has not answered", G1), unproven)
        r.sock.sent.clear()
        r.handle_packet(v1_packet(relay.TYPE_AUDIO, b"PHONE-AUDIO"), phone1)
        self.assertTrue(forwarded_to(r.sock, phone2, b"PHONE-AUDIO"),
                        "a v1 pair that never answers its address check still reaches each other, exactly as before")
        self.assertGreater(r.stats.would_block_unverified, 0, "and is only watched, as before")
        self.assertEqual(r.stats.blocked_unverified, 0)


class ListsAreRateLimited(unittest.TestCase):
    """Pre-release sweep 2026-09-25, agreed by Ed: a changed member list went out on the very next packet, every time.
    One 68-byte hello with a new name made the relay send the list to every member at once - about 155 KB for 56
    members, some 2300 times what it was sent. A changed list now waits until ROSTER_DIRTY_MIN_INTERVAL_SECONDS after the
    last; the heartbeat list still goes every second."""

    def _group(self):
        r = make_relay(max_clients=64)
        addrs = [(f"10.42.0.{i + 1}", 9000 + i) for i in range(4)]
        for i, addr in enumerate(addrs):
            join(r, cid(i + 1), f"p{i}", G1, addr)
        return r, addrs

    def test_a_burst_of_changes_goes_out_as_one_list(self):
        r, addrs = self._group()
        start = time.monotonic()
        r.tick(start)   # the lists go out; the window starts
        r.sock.sent.clear()
        for i in range(50):   # fifty new names inside a fifth of a second, the loop ticking after each packet
            r.handle_packet(hello(cid(2), f"name{i}", G1), addrs[1])
            r.tick(start + 0.004 * i)
        self.assertEqual(len(rosters_to(r.sock, addrs[0])), 0, "no list sooner than the window after the last")
        self.assertTrue(r.v2_roster_dirty, "the change is still owed")
        r.tick(start + relay.ROSTER_DIRTY_MIN_INTERVAL_SECONDS + 0.01)
        self.assertEqual(len(rosters_to(r.sock, addrs[0])), 1, "and goes out as ONE list when the window allows")
        self.assertFalse(r.v2_roster_dirty)
        r.sock.sent.clear()
        r.tick(start + 0.9)
        self.assertEqual(len(rosters_to(r.sock, addrs[0])), 0, "nothing changed: nothing until the heartbeat")
        r.tick(start + relay.ROSTER_DIRTY_MIN_INTERVAL_SECONDS + relay.ROSTER_HEARTBEAT_SECONDS + 0.02)
        self.assertEqual(len(rosters_to(r.sock, addrs[0])), 1, "and the heartbeat list still goes every second")

    def test_a_stream_of_changes_is_held_to_a_few_lists_a_second(self):
        r, addrs = self._group()
        start = time.monotonic()
        r.tick(start)
        r.sock.sent.clear()
        for i in range(200):   # a new name every 10 ms for two seconds
            r.handle_packet(hello(cid(2), f"n{i}", G1), addrs[1])
            r.tick(start + 0.01 * (i + 1))
        sent = len(rosters_to(r.sock, addrs[0]))
        self.assertLessEqual(sent, int(2 / relay.ROSTER_DIRTY_MIN_INTERVAL_SECONDS),
                             f"two seconds of changes sent {sent} lists to each member")
        self.assertGreaterEqual(sent, 6, "but a change still goes out several times a second, not only on the heartbeat")


class NoTakeover(unittest.TestCase):
    """Pre-release sweep 2026-09-25, agreed by Ed: any packet bearing a member's client id, from any address, moved that
    member to the address it came from. Every member of a group knows every other member's id - it is in the list - so
    one keepalive sent somebody else's sound to whoever sent it: to hear them, speak as them or cut them off. A member who
    has proved its address now moves only when the new address has proved itself too AND the old one has gone quiet."""

    def _pair(self):
        r, captured = _relay_with_captured_log()
        a, b = ("10.50.0.1", 9000), ("10.50.0.2", 9001)
        join(r, cid(1), "alice", G1, a)
        join(r, cid(2), "bob", G1, b)
        return r, captured, a, b, r.v2_clients[uuid.UUID(bytes=cid(1))]

    def test_somebody_else_using_a_members_id_does_not_take_her_place(self):
        r, captured, a, b, alice = self._pair()
        thief = ("10.50.9.9", 9999)   # a member of the group, who knows alice's id from the list
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_KEEPALIVE, cid(1), b"k"), thief)
        r.handle_packet(hello(cid(1), "not alice", G2, ticked=[]), thief)
        prove(r, thief)   # the thief's own address is real, so it can answer the check it is sent
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"SPOKEN-AS-ALICE"), thief)
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(2), b"BOB-TO-ALICE"), b)
        r.handle_packet(v2_packet(relay.TYPE_LOBBY_BYE, cid(1)), thief)
        self.assertIn(uuid.UUID(bytes=cid(1)), r.v2_clients, "a BYE from the thief must not remove alice")
        self.assertEqual((alice.addr, alice.verified), (a, True), "alice stays where she proved herself")
        self.assertEqual((alice.display_name, alice.group, alice.ticked), ("alice", G1, None),
                         "the thief's hello is not taken as hers")
        self.assertTrue(forwarded_to(r.sock, a, b"BOB-TO-ALICE"), "bob's sound still goes to alice")
        self.assertFalse(forwarded_to(r.sock, thief, b"BOB-TO-ALICE"), "and never to the thief")
        self.assertFalse(forwarded_to(r.sock, b, b"SPOKEN-AS-ALICE"), "nobody hears the thief speaking as alice")
        # Alice keeps talking. Once the thief has waited REBIND_SILENCE_SECONDS it is still refused, and that is logged once.
        alice.move.since -= relay.REBIND_SILENCE_SECONDS + 1
        for _ in range(5):
            r.handle_packet(v2_packet(relay.TYPE_KEEPALIVE, cid(1), b"k"), a)
            r.handle_packet(v2_packet(relay.TYPE_KEEPALIVE, cid(1), b"k"), thief)
        self.assertEqual(alice.addr, a)
        self.assertEqual(len(_lines(captured, "client_endpoint_move_refused")), 1, "refused, and said once")

    def test_the_thiefs_packets_do_not_keep_her_place_alive(self):
        r, _, a, b, alice = self._pair()
        alice.last_seen -= 3   # alice has been quiet for three seconds
        before = alice.last_seen
        for _ in range(10):
            r.handle_packet(v2_packet(relay.TYPE_KEEPALIVE, cid(1), b"k"), ("10.50.9.9", 9999))
        self.assertEqual(alice.last_seen, before,
                         "only alice's own address may say when she was last heard, or her going quiet could be hidden")

    def test_a_real_move_goes_through_once_the_old_address_is_quiet(self):
        r, captured, a, b, alice = self._pair()
        a2 = ("10.50.0.1", 9500)   # her router gave her a new port; nothing more comes from the old one
        r.handle_packet(hello(cid(1), "alice", G1), a2)
        self.assertEqual(alice.addr, a, "not before the new address has answered its own check")
        prove(r, a2)
        self.assertEqual(alice.addr, a, "nor while the old address was heard from less than REBIND_SILENCE_SECONDS ago")
        alice.last_seen -= relay.REBIND_SILENCE_SECONDS + 0.1   # time passes, and the old address stays silent
        r.handle_packet(hello(cid(1), "alice", G1), a2)   # her next hello
        self.assertEqual((alice.addr, alice.verified), (a2, True), "then she moves, already proved")
        self.assertIsNone(alice.move)
        self.assertTrue(r.v2_roster_dirty, "and her list follows her at once")
        self.assertEqual(len(_lines(captured, "client_endpoint_moved")), 1)
        self.assertEqual(len(_lines(captured, "client_endpoint_move_refused")), 0, "a real move is never logged as refused")
        r.sock.sent.clear()
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(2), b"BOB-TO-NEW"), b)
        self.assertTrue(forwarded_to(r.sock, a2, b"BOB-TO-NEW"), "bob's sound reaches her new address")
        self.assertFalse(forwarded_to(r.sock, a, b"BOB-TO-NEW"), "and not the old one")
        r.handle_packet(v2_packet(relay.TYPE_AUDIO, cid(1), b"ALICE-FROM-NEW"), a2)
        self.assertTrue(forwarded_to(r.sock, b, b"ALICE-FROM-NEW"), "and hers reaches bob")

    def test_the_answer_itself_completes_a_move_when_the_old_address_is_already_quiet(self):
        r, _, a, b, alice = self._pair()
        a2 = ("10.50.0.7", 9000)   # moved from Wi-Fi to a cable a while ago
        alice.last_seen -= relay.REBIND_SILENCE_SECONDS + 1
        r.handle_packet(v2_packet(relay.TYPE_KEEPALIVE, cid(1), b"k"), a2)
        self.assertEqual(alice.addr, a, "the old address is quiet, but the new one has not answered yet")
        prove(r, a2)
        self.assertEqual((alice.addr, alice.verified), (a2, True), "its answer is the move")

    def test_a_member_that_never_proved_its_address_moves_at_once_as_before(self):
        r = make_relay()
        a, a2 = ("10.50.1.1", 9000), ("10.50.1.1", 9001)
        r.handle_packet(hello(cid(1), "c", G1), a)   # has not answered yet: it has nothing anybody could steal
        r.handle_packet(hello(cid(1), "c", G1), a2)
        entry = r.v2_clients[uuid.UUID(bytes=cid(1))]
        self.assertEqual((entry.addr, entry.verified), (a2, False), "an unproven client still moves at once")
        self.assertIsNotNone(cookie_sent_to(r.sock, a2), "and is sent an address check at its new address")
        prove(r, a2)
        self.assertTrue(entry.verified)


def _find_bash() -> str | None:
    """Git for Windows' bash on Windows - never System32's bash.exe, which is WSL's and sees none of these paths - and the
    system's bash anywhere else."""
    if os.name == "nt":
        for candidate in (os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Git", "bin", "bash.exe"),
                          r"C:\Program Files\Git\bin\bash.exe"):
            if os.path.isfile(candidate):
                return candidate
        return None
    return shutil.which("bash")


def _slashes(path: str) -> str:
    return path.replace("\\", "/")


# The updater's own main(), sourced and run in bash, with only what reaches outside the machine stood in for: the network
# (curl serves the release list and the files from $D), the service manager, the backup and the install itself (it
# records what it would have installed), and the signature check (a .sig reading "good-signature" is good). python3 is
# this Python, with the carriage returns Windows adds taken off, as Linux never adds them.
_UPDATER_HARNESS = r'''
source "$UPDATER"
set +e
LOG_FILE="$D/update.log"; VERSION_FILE="$D/version"; HEALTH_WAIT_SECONDS=0
require_root() { :; }; ensure_dirs() { :; }; snapshot_backup() { :; }; restore_backup() { :; }
openssl() { :; }; systemctl() { return 0; }
python3() { "$PY" "$@" | tr -d '\r'; }
verify_release_signature() { grep -qx 'good-signature' "$2"; }
install_from_staging() { echo "installed $(tr -d '[:space:]' < "$1/VERSION")" >> "$D/calls"; }
curl() {
    local out="" url="" src
    while [[ $# -gt 0 ]]; do
        case "$1" in -o) out="$2"; shift 2 ;; -H|--max-time) shift 2 ;; -*) shift ;; *) url="$1"; shift ;; esac
    done
    echo "fetched $url" >> "$D/calls"
    case "$url" in *api.github.com*) src="$D/releases.json" ;; *) src="$D/files/${url##*/}" ;; esac
    [[ -f "$src" ]] || return 22
    if [[ -n "$out" ]]; then cp "$src" "$out"; else cat "$src"; fi
}
if [[ "${1:-}" == "list" ]]; then get_latest_release | tr -d '\r'; echo "rc=$?"; exit 0; fi
( set -e; trap cleanup_work_dir EXIT; main ) 2>/dev/null
echo "rc=$?"
'''


class UpdaterWalksPastARefusedRelease(unittest.TestCase):
    """Pre-release sweep 2026-09-25, agreed by Ed: the updater only ever tried the highest-numbered server release. If
    that one was refused - no signature, a bad one, a VERSION that does not match its tag, a mistyped tag - the relay was
    refused it again every hour for ever and never looked at the good release behind it. It now walks down the releases
    newer than its own, newest first, installs the first that passes every check, and logs each one it refuses."""

    @classmethod
    def setUpClass(cls):
        cls.bash = _find_bash()
        if cls.bash is None:
            raise unittest.SkipTest("no bash here (Git for Windows) to run the updater with")

    def _run(self, releases: list[dict], installed: str, mode: str = "main") -> tuple[str, str, str, str]:
        """Run the updater over these releases, each {tag, version?, sig: "good"|"bad"|None, draft?}. Returns (what bash
        printed, its log, the stand-ins' record of what was fetched and installed, the version file afterwards)."""
        import json, tarfile, tempfile
        d = tempfile.mkdtemp(prefix="remsound-updater-walk-")
        self.addCleanup(shutil.rmtree, d, True)
        files = os.path.join(d, "files")
        os.makedirs(files)
        listing = [{"tag_name": "v6.1.0", "draft": False, "prerelease": False,   # the app's releases share the list
                    "assets": [{"name": "RemSound.zip", "browser_download_url": "https://example.invalid/RemSound.zip"}]}]
        for rel in releases:
            tag = rel["tag"]
            name = f"remsound-{tag}.tar.gz"
            folder = os.path.join(d, "build", f"remsound-{tag}")
            os.makedirs(folder)
            with open(os.path.join(folder, "VERSION"), "w", newline="\n") as f:
                f.write(rel.get("version", tag) + "\n")
            with open(os.path.join(folder, "remsound-relay.py"), "w", newline="\n") as f:
                f.write("# relay\n")
            with tarfile.open(os.path.join(files, name), "w:gz") as t:
                t.add(folder, arcname=f"remsound-{tag}")
            assets = [{"name": name, "browser_download_url": f"https://example.invalid/{name}"}]
            if rel.get("sig") is not None:
                with open(os.path.join(files, name + ".sig"), "w", newline="\n") as f:
                    f.write("good-signature\n" if rel["sig"] == "good" else "signed with some other key\n")
                assets.append({"name": name + ".sig", "browser_download_url": f"https://example.invalid/{name}.sig"})
            listing.append({"tag_name": tag, "draft": rel.get("draft", False), "prerelease": False, "assets": assets})
        with open(os.path.join(d, "releases.json"), "w", newline="\n") as f:
            json.dump(listing, f)
        with open(os.path.join(d, "version"), "w", newline="\n") as f:
            f.write(installed + "\n")
        env = dict(os.environ, D=_slashes(d), PY=_slashes(sys.executable),
                   UPDATER=_slashes(os.path.join(_HERE, "remsound-relay-update.sh")))
        import subprocess
        p = subprocess.run([self.bash, "-c", _UPDATER_HARNESS, "harness", mode], env=env, capture_output=True,
                           text=True, timeout=120)

        def read(name):
            path = os.path.join(d, name)
            if not os.path.exists(path):
                return ""
            with open(path, encoding="utf-8") as f:
                return f.read()
        return p.stdout.replace("\r", ""), read("update.log"), read("calls"), read("version").strip()

    # Newest first once sorted, though GitHub lists them in any order; server-v2.12 is what is installed.
    RELEASES = [
        {"tag": "server-v2.13", "sig": "good"},                              # good: the one to install
        {"tag": "server-v2.16", "sig": None},                                # no signature at all
        {"tag": "server-v2.99", "sig": "good", "draft": True},               # a draft: never considered
        {"tag": "server-v2.15", "sig": "bad"},                               # signed with some other key
        {"tag": "server-v2.14", "sig": "good", "version": "server-v2.11"},   # an old release under a new name
        {"tag": "server-v2.12", "sig": "good"},                              # installed already
        {"tag": "server-v2.11", "sig": "good"},                              # older still: never even fetched
    ]

    def test_a_refused_release_no_longer_blocks_the_good_one_behind_it(self):
        out, log, calls, version = self._run(self.RELEASES, "server-v2.12")
        self.assertIn("rc=0", out, f"the updater must succeed (log:\n{log})")
        self.assertEqual(version, "server-v2.13", "the newest release that passes every check is installed")
        self.assertEqual([c for c in calls.splitlines() if c.startswith("installed")], ["installed server-v2.13"])
        for tag, why in (("server-v2.16", "has no signature"), ("server-v2.15", "is not signed by the RemSound release key"),
                         ("server-v2.14", "says it is 'server-v2.11', not server-v2.14")):
            lines = [l for l in log.splitlines() if f"REFUSED {tag}:" in l]
            self.assertEqual(len(lines), 1, f"{tag} must be refused, and said once (log:\n{log})")
            self.assertIn(why, lines[0])
        self.assertNotIn("server-v2.11.tar.gz", calls, "a release older than the one installed is never fetched")
        self.assertNotIn("server-v2.99", calls + log, "nor is a draft")

    def test_when_every_newer_release_is_refused_nothing_is_touched(self):
        out, log, calls, version = self._run(self.RELEASES, "server-v2.13")
        self.assertIn("rc=1", out, "the run fails, so the service shows it")
        self.assertEqual(version, "server-v2.13", "and the relay stays as it was")
        self.assertNotIn("installed", calls)
        self.assertIn("none of the 3 newer release(s) passed its checks", log)

    def test_an_up_to_date_relay_fetches_nothing(self):
        out, log, calls, version = self._run(self.RELEASES, "server-v2.16")
        self.assertIn("rc=0", out)
        self.assertIn("up to date (installed server-v2.16 >= available server-v2.16)", log)
        self.assertEqual([c for c in calls.splitlines() if ".tar.gz" in c], [], "nothing is downloaded")

    def test_the_list_still_starts_with_the_newest_release(self):
        # The app's self-test (SelfTest.RelayUpdater.cs) reads the first line of get_latest_release as the newest server
        # release, and looks for its signature: listing every release must not change that.
        out, _, _, _ = self._run(self.RELEASES, "server-v2.12", mode="list")
        lines = out.split("\n")
        self.assertIn("rc=0", out)
        self.assertEqual(lines[0:4], ["server-v2.16", "https://example.invalid/remsound-server-v2.16.tar.gz",
                                      "remsound-server-v2.16.tar.gz", ""], "the newest first, with its (missing) signature")
        self.assertEqual([lines[i] for i in range(0, 24, 4)],
                         ["server-v2.16", "server-v2.15", "server-v2.14", "server-v2.13", "server-v2.12", "server-v2.11"],
                         "then every other server release, newest first")


if __name__ == "__main__":
    unittest.main()
