using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE CROSS-PORT CONTRACT — everything another RemSound has to agree with to talk to this one.
///
/// <para>Ed, 2026-08-22: "for every bit of functionality we add, we need to ask, will this break the
/// other ports of RemSound? If it does, you need to ask me about it because we've too many in the
/// wild now like on mobiles — if we break them we really screw people's lives up."</para>
///
/// <para><b>Why this is a test and not a note.</b> A note is a thing somebody remembers to read. This
/// fails the build. Every value below is one that a phone in somebody's pocket, a relay on a Pi, or a
/// service on a machine nobody is sitting at has already been shipped believing. Change one and the
/// far end does not get an error — it gets silence, or noise, or a peer that never connects, and the
/// person on the other end has no idea why.</para>
///
/// <para><b>It has already happened.</b> v5.6 raised the password iteration count and the iOS app
/// stopped being able to hear anything, because the same password now derived a different key. The
/// change was correct in isolation and broke real people.</para>
///
/// <para><b>What to do when this fails.</b> Do NOT update the numbers to make it pass. That is
/// exactly the moment the rule exists for. Stop, and ask Ed, because he is the one who knows which
/// ports are in the wild and which of them can be updated in step. If a change genuinely has to go
/// ahead, it needs a version negotiation so old builds keep working — not a silent redefinition.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Who is on the other end of these values. Kept here so the failure message can say
    /// WHO breaks, not just what changed — "this breaks the iPhone app" lands differently from
    /// "constant changed".</summary>
    private const string PortsInTheWild =
        "the iOS/TestFlight app, the Python relay on the Pi, the send-only Windows service, and the VST plugin";

    private static string? CrossPortContract()
    {
        var pinned = new List<string>();

        void Pin(string what, object actual, object expected, string breaks)
        {
            Check(actual.ToString() == expected.ToString(),
                $"CROSS-PORT CONTRACT CHANGED — {what} is {actual}, was {expected}. This breaks {breaks}. "
              + "Do NOT update this number to make the test pass: ask Ed first. Anything already shipped is "
              + "still speaking the old value, and it will fail silently rather than report an error.");
            pinned.Add($"{what}={actual}");
        }

        // --- The wire itself. Every peer on every platform frames packets this way. ---------------
        Pin("packet magic", RemPacket.Magic, 0x444E4D52, "every peer of every version — nothing would parse at all");
        Pin("packet version", RemPacket.Version, 1, "every shipped peer; they reject an unknown version outright");
        Pin("header size", RemPacket.HeaderSize, 12, "every peer — the payload would start at the wrong byte");
        Pin("default port", RemPacket.DefaultPort, 47830, "every peer, plus any router port-forward a user has set up by hand");
        Pin("max audio payload", RemPacket.MaxAudioPayloadBytes, 1454, "peers on links with a smaller MTU — packets would fragment or vanish");

        // --- Packet type numbers. A renumber here silently reroutes audio into a control handler. --
        Pin("type Format", (byte)RemPacketType.Format, 1, "every peer's dispatch");
        Pin("type Audio", (byte)RemPacketType.Audio, 2, "every peer's dispatch");
        Pin("type KeepAlive", (byte)RemPacketType.KeepAlive, 3, "older peers that still send it");
        Pin("type Heartbeat", (byte)RemPacketType.Heartbeat, 4, "peer discovery and reachability on every port");

        // --- Payload shapes. The far end reads these by fixed offset. ------------------------------
        Pin("format payload", RemPacket.FormatPayloadSize, 32, "peers negotiating stream format");
        Pin("format payload extended", RemPacket.FormatPayloadExtendedSize, 36, "peers on the extended format");
        Pin("format payload with fingerprint", RemPacket.FormatPayloadWithFingerprintSize, 44, "the password-fingerprint handshake");
        Pin("password fingerprint", RemPacket.PasswordFingerprintSize, 8, "the wrong-password warning on every port");
        Pin("heartbeat payload", RemPacket.HeartbeatPayloadSize, 9, "reachability probing, so peers would look unreachable");
        Pin("control payload", RemPacket.ControlPayloadSize, 2, "remote volume and mute between peers");

        // --- The key recipe. THIS is the one that has already bitten. -------------------------------
        // Both ends derive a key from the same password. Any difference in the recipe means the same
        // password produces a different key, and the audio simply never decrypts — no error, no
        // warning, just a peer you can see and cannot hear.
        Pin("PBKDF2 iterations", RemSoundCrypto.Pbkdf2IterationsForTest, 100_000,
            "the iOS app — this exact change shipped in v5.6 and left iPhone users unable to hear anybody");

        // --- The relay's own protocol, spoken by the Python server on the Pi -----------------------
        Pin("relay AddrCheck type", 10, 10, "the Python relay — peers would stop proving their address and never be admitted");

        return $"{pinned.Count} values pinned that {PortsInTheWild} all depend on: " + string.Join(", ", pinned);
    }
}
