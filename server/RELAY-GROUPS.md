# Relay groups: how a RemSound app joins one

From server-v2.7 and RemSound 6.0 for Windows, a relay carries groups instead of a
single pair. Everyone who uses the relay with the same password is one group:
each hears everyone else, each as a separate person. People on another password
use the same relay without ever meeting. This note is for anyone adding group
support to another RemSound app (iPhone, Android, Linux).

An app that does nothing keeps working exactly as before: it pairs through the
relay's two ordinary slots, and a group member can be its partner. Nothing below
is required. It only lets the app be one of many.

## The one rule that keeps old and new together

Until the relay has sent you a member list, behave exactly as you do today.
Send ordinary (version 1) packets, echo address checks and pair. Everything
below switches on only once a member list has arrived in the last 5 seconds,
and switches back off if they stop.

## 1. Notice the relay

Only a relay sends an address check (packet type 10: the ordinary 12-byte
header followed by a 16-byte cookie). Echo it back to its source exactly as it
came, as you should already. Also treat the source as a relay you can join.
From server-v2.7 a relay also sends one to a device its pair has no room for,
so you learn it even when you arrive third.

## 2. Join the group for your password

Pick a client id once per install and keep it: 16 random bytes, never all zero.
Every 2 seconds, and at once when you first notice the relay or change password,
send the relay a **hello** (type 6) in group framing (below). Its payload is:

| Bytes | What |
| ----- | ---- |
| 32 | your display name, UTF-8, cut to 32 bytes on a character boundary, zero-padded |
| 8 | your group tag: the 8-byte password fingerprint, the same bytes your Format payload carries at offset 36 |

The relay groups by that tag and nothing else, so the same password is the same
group. The tag is already public in every Format packet, so it tells the relay
nothing new. With no password there is no group to join. Send a **bye** (type 9,
no payload) when you leave.

## 3. Group framing

Group framing is the ordinary header with its version byte set to **2**, then
your 16-byte client id, then the ordinary payload, untouched:

| Offset | Bytes | What |
| ------ | ----- | ---- |
| 0 | 4 | magic `RMND` |
| 4 | 1 | version: **2** |
| 5 | 1 | type (Format 1, Audio 2, Heartbeat 4, Control 5, Hello 6, Bye 9) |
| 6 | 2 | stream id, little-endian |
| 8 | 4 | sequence, little-endian |
| 12 | 16 | client id: the sender's; all zeros from the relay itself |
| 28 | … | the ordinary payload |

That is 16 bytes more than an ordinary packet. Size uncompressed frames so they
still fit one packet: Windows sends 233 samples of 24-bit stereo, the most that
fits a 1,492-byte path with the extra 16 bytes.

## 4. The member list

About once a second the relay sends each member a **roster** (type 7, client id
all zeros). Its payload:

| Bytes | What |
| ----- | ---- |
| 1 | count |
| count × 48 | each member: 16-byte client id, then a 32-byte name as in the hello |
| 1 | flags. Bit 0: you also hold one of the relay's ordinary pair slots, with a partner |

It lists only your own group, and you are in it: leave yourself out. If
someone drops off the list, they have left. A **full** (type 8, payload
`current count, maximum`) means the relay has no room for you.

## 5. Sending, once in a group

- Send everything to the relay in group framing. The relay passes it to every
  other member of your group.
- Also send each **heartbeat** in ordinary form. That is what claims an
  ordinary pair slot beside a phone or older app that is waiting for one, so it
  still reaches somebody.
- While the roster's flags byte has bit 0 set, also send your audio, formats
  and control packets in ordinary form, for that partner.
- Echo address checks exactly as they came, never in group framing.

## 6. Receiving, once in a group

A packet from the relay with version 2 comes from the member whose client id it
carries. Treat that member as a person of their own: key their streams by
(client id, stream id), and give them their own name, volume, password status
and so on. Take the client id out, and the rest is an ordinary packet from that
person. On Windows, each member is given a reserved address of their own in
240.0.0.0/5, so everything that works per address works per person unchanged.

Everyone's pongs reach everyone in a group, each carrying the clock of whoever
pinged. Count a pong only if it answers one of your own recent pings.

An ordinary (version 1) packet from the relay comes from your pair partner, if
you have one. It is exactly what a paired relay sends today.

## 7. What the relay does

- Forwards a group packet to every other member of the sender's group, and to
  nobody else. Up to 64 clients across all groups (`--max-clients`), and up to
  4 per address (`--max-per-ip`).
- Admits a new client only on a hello or an ordinary packet type, never on an
  address check, a bye or a relay-only type.
- Pairs ordinary devices exactly as before. A group member takes a pair slot
  only beside a device that has no group, and only one on its own password when
  that device has said which it is. Two members never pair. A pair that turns
  out to be two members is dissolved.

The code: `remsound-relay.py` (the relay), `test_relay.py` (its rules), and on
Windows `src/RemSound.Core/RelayGroupClient.cs`.
