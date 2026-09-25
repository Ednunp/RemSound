# Relay groups: how a RemSound app joins one

From server-v2.9 and RemSound 6.0 for Windows, a relay carries groups instead of a
single pair. Everyone who uses the relay with the same password is one group:
you see each other by name, and you hear the ones you have each ticked, each as
a separate person. People on another password use the same relay without ever
meeting.

This note is for anyone adding group support to another RemSound app (iPhone,
Android, Linux).

An app that does nothing keeps working exactly as before: it pairs through the
relay's two ordinary slots, and a group member can be its partner. Nothing below
is required. It only lets the app be one of many.

## What a relay is now

A relay is a place you go, not a person in your peer list. On Windows the
Connectivity tab has a relay box: you type the relay's address and press
Connect. While you are connected, a list appears of the people on that relay who
share your password, and you tick them exactly as you tick anybody else. Ticking
means the same as it always did — I send to you and I accept yours — and sound
only flows between two people once each has ticked the other.

Nobody is ever typed in as a relay person; they only ever arrive from the
relay's own list.

## The one rule that keeps old and new together

Until the relay has sent you a member list, behave exactly as you do today.
Send ordinary (version 1) packets, echo address checks and pair. Everything
below switches on only once a member list has arrived in the last 5 seconds,
and switches back off if they stop.

## 1. Notice the relay

Only a relay sends an address check (packet type 10: the ordinary 12-byte
header followed by a 16-byte cookie). Echo it back to its source exactly as it
came, as you should already.

If a user types a relay's address into your add-a-peer box, that echo is how you
can tell: on Windows the app now offers "you have entered the address of a
relay, would you like to connect to this relay?" and moves it to the relay box.

## 2. Join the group for your password

Pick a client id once per install and keep it: 16 random bytes, never all zero.

Join only when the user asks to, by connecting to that relay. Then every 2
seconds, and at once on any change below, send the relay a **hello** (type 6) in
group framing (below). Its payload is:

| Bytes | What |
| ----- | ---- |
| 32 | your display name, UTF-8, cut to 32 bytes on a character boundary, zero-padded |
| 8 | your group tag: the 8-byte password fingerprint, the same bytes your Format payload carries at offset 36 |
| 1 | how many people you have ticked (0-64) |
| n x 16 | the client id of each of them |

The relay groups by the tag and nothing else, so the same password is the same
group. The tag is already public in every Format packet, so it tells the relay
nothing new. With no password there is no group to join. Send a **bye** (type 9,
no payload) when you leave.

**The tick list is what decides who hears you.** Leaving it off the hello
entirely — a 40-byte payload, the way every app sent it before this — means
"everyone in my group", which is why nothing you have today is affected. Sending
a count of 0 means "nobody yet", which is what a newly connected app sends before
the user has ticked anyone.

Send the hello again immediately whenever the user ticks or unticks somebody,
changes password, or changes their name. Do not wait for the next 2-second tick.

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
| 28 | ... | the ordinary payload |

That is 16 bytes more than an ordinary packet. Size uncompressed frames so they
still fit one packet: Windows sends 233 samples of 24-bit stereo, the most that
fits a 1,492-byte path with the extra 16 bytes.

You send **one** copy of your audio, to the relay. The relay makes the copies —
one per person who is due it. Nobody downloads a single shared stream: each
listener needs their own copy addressed to them, which is what UDP is.

## 4. The member list

About once a second, and at once whenever anything in it changes, the relay
sends each member a **roster** (type 7, client id all zeros). Its payload:

| Bytes | What |
| ----- | ---- |
| 1 | count |
| count x 49 | each member: 16-byte client id, 32-byte name as in the hello, then 1 flags byte |
| 1 | flags for the list as a whole. Bit 0: you also hold one of the relay's ordinary pair slots, with a partner |

Each member's own flags byte has **bit 0 set when that member has ticked you**.
That is what lets an app say "waiting for them to tick you" instead of simply
going quiet — which is otherwise the one thing a person cannot work out for
themselves. A member who sent no tick list at all counts as having ticked you.

The list contains only your own group, and you are in it: leave yourself out. If
someone drops off the list, they have left. A **full** (type 8, payload
`current count, maximum`) means the relay has no room for you.

Roster entries were 48 bytes before server-v2.9 (no per-member flags byte). If
you want to work with both, read the entry size from the payload length.

## 5. Who hears whom

The relay passes a packet from A to B only when **all** of these hold:

- A and B are in the same group (same password), and
- A has ticked B (or A sent no tick list at all), and
- B has ticked A (or B sent no tick list at all).

So ticking somebody who has not ticked you back gives silence in both
directions, and unticking stops it in both directions at once. This is exactly
the rule two people already have on a network, where each has to tick the other.

## 6. Sending, once in a group

- Send everything to the relay in group framing, once. The relay copies it to
  everyone who is due it.
- Also send each **heartbeat** in ordinary form. That is what claims an
  ordinary pair slot beside a phone or older app that is waiting for one, so it
  still reaches somebody.
- While the roster's trailing flags byte has bit 0 set **and the user has ticked
  that partner**, also send your audio, formats and control packets in ordinary
  form, for that partner. On Windows that partner appears in the relay list as
  "Someone on a phone or an older app" and is ticked like anyone else.
- Echo address checks exactly as they came, never in group framing.
- Echo them straight away, in a group as well. Since server-v2.12, when the
  relay or your address is full, a member that has never echoed its address
  check is turned out to make room for a newcomer, so that made-up addresses
  cannot keep real people out. Once you have echoed, you are not turned out
  (if your address changes, echo the new check the relay sends there).

## 7. Receiving, once in a group

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

## 8. What the relay does

- Forwards a group packet to every other member of the sender's group who
  passes the rule in section 5, and to nobody else. Up to 64 clients across all
  groups (`--max-clients`), and up to 8 per address (`--max-per-ip`).
- Admits a new client only on a hello (since server-v2.10). Sound, a heartbeat
  or anything else from a client id it does not know is dropped, so send a hello
  before anything else, and keep sending one every 2 seconds: if your place
  lapses (the relay restarts, say), your next hello puts you back.
- Pairs ordinary devices exactly as before. A group member takes a pair slot
  only beside a device that has no group, and only one on its own password when
  that device has said which it is. Two members never pair. A pair that turns
  out to be two members is dissolved.

## 9. Changing password while connected

A relay only ever shows you the people on your own password. A new password is a
new group, so when the user changes it while connected: send the hello again at
once with the new tag and an empty tick list, drop everybody you had ticked, and
let the new group's list arrive. Windows says so out loud when it happens.

The code: `remsound-relay.py` (the relay), `test_relay.py` (its rules), and on
Windows `src/RemSound.Core/RelayGroupClient.cs` and
`src/RemSound.App/MainForm.Relay.cs`.
