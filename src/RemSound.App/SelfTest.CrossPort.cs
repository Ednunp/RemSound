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
        var changed = new List<string>();

        // Failures are COLLECTED, not thrown on the first one. Check throws, so a single changed
        // value used to hide every other change behind it — and on the one step where the whole
        // point is knowing the blast radius, "one of your constants moved" is the wrong report.
        // A refactor that renumbers three things should say all three. 2026-08-24.
        void Pin(string what, object actual, object expected, string breaks)
        {
            if (actual.ToString() != expected.ToString())
                changed.Add($"{what} is {actual}, was {expected} — this breaks {breaks}");
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
        Pin("format payload with capture latency", RemPacket.FormatPayloadWithCaptureSize, 46,
            "the sender's own capture figure, appended 2026-08-24 so a receive-only peer stops guessing at it");
        Pin("capture latency ticks per ms", (int)RemPacket.CaptureLatencyTicksPerMs, 10,
            "the tenths-of-a-millisecond scale that figure travels in — change it and every other port "
            + "reads the number ten times too big or ten times too small");
        Pin("password fingerprint", RemPacket.PasswordFingerprintSize, 8, "the wrong-password warning on every port");
        Pin("heartbeat payload", RemPacket.HeartbeatPayloadSize, 9, "reachability probing, so peers would look unreachable");
        Pin("control payload", RemPacket.ControlPayloadSize, 2, "remote volume and mute between peers");
        // The SEALED control size is the discriminator between a modern sealed command and a legacy
        // 2-byte plaintext one, so it is as much a wire value as the plaintext size beside it.
        // Changing the sealed plaintext layout moves it, and nothing noticed until 2026-08-24.
        Pin("sealed control payload", ControlSealing.SealedPayloadBytes, 38,
            "remote control from any other port — the receiver tells sealed from legacy plaintext by this exact size, "
          + "so a sealed command of a different length is read as neither");

        // --- The key recipe. THIS is the one that has already bitten. -------------------------------
        // Both ends derive a key from the same password. Any difference in the recipe means the same
        // password produces a different key, and the audio simply never decrypts — no error, no
        // warning, just a peer you can see and cannot hear.
        Pin("PBKDF2 iterations", RemSoundCrypto.Pbkdf2IterationsForTest, 100_000,
            "the iOS app — this exact change shipped in v5.6 and left iPhone users unable to hear anybody");

        // A GOLDEN VECTOR, not just the iteration count. The iteration count was pinned because it is
        // what changed in v5.6, but it is one ingredient of four: the salt, the hash, the key length
        // and the count all have to match on every port, and only one of them was watched. Moving the
        // salt would break the iOS app exactly as v5.6 did and nothing here would have said a word.
        //
        // One derived key covers all four at once, which is also the form another port can check
        // itself against: derive from this password, expect these bytes.
        const string vectorPassword = "remsound cross-port vector";
        Pin("PBKDF2 key for the reference password",
            Convert.ToHexString(RemSoundCrypto.DeriveKey(vectorPassword)),
            "9CD07772496B22220FAC888EB0F5FBA005953FF2F71AABF391740FB1D9491B74",
            "every port that derives a key from a password — the salt, the hash, the key length or the count has moved, "
          + "and the same password now produces a different key. This is the v5.6 failure exactly: no error, just a peer "
          + "you can see and cannot hear");
        Pin("password fingerprint for the reference password",
            Convert.ToHexString(RemSoundCrypto.Fingerprint(vectorPassword)),
            "A77BF56B9EF1266B",
            "the wrong-password warning on every port — peers would stop recognising that they share a password");

        // And the fingerprint must not BE the key. It travels in the Format packet in the clear,
        // before any encryption, so a fingerprint derived with the key's salt would broadcast the
        // first 8 bytes of the AES key to the network and the relay on every stream start. The two
        // salts exist for exactly this reason; nothing checked it until 2026-08-24, when pointing
        // Fingerprint at KeySalt left the whole gate green.
        var vectorKey = RemSoundCrypto.DeriveKey(vectorPassword);
        var vectorPrint = RemSoundCrypto.Fingerprint(vectorPassword);
        if (vectorKey.AsSpan(0, vectorPrint.Length).SequenceEqual(vectorPrint))
            changed.Add("the password FINGERPRINT is derived from the key's salt — it is sent UNENCRYPTED in the Format "
                      + "packet, so every stream start would broadcast the leading bytes of the AES key to anyone on the "
                      + "network or the relay. This breaks the secrecy of every peer's password");

        // --- The relay's own protocol, spoken by the Python server on the Pi -----------------------
        // This line read Pin("relay AddrCheck type", 10, 10, …) until 2026-08-24 — a literal compared
        // to itself, in the one test whose whole purpose is to notice a constant moving. It pins the
        // real enum member now.
        Pin("relay AddrCheck type", (byte)RemPacketType.AddrCheck, 10,
            "the Python relay — peers would stop proving their address and never be admitted");

        // --- Discovery: how RemSounds on one network find each other. The iPhone, Android and Pi senders
        // all announce and listen; none of this was pinned until 2026-09-23, the day Christopher Wright's
        // issue #32 found the manual giving the wrong port for it. --------------------------------------
        Pin("discovery port", PeerDiscoveryService.DefaultDiscoveryPort, 47821,
            "every port that finds peers on a network — each side would announce where the other never listens");
        Pin("discovery announce interval ms", (int)PeerDiscoveryService.AnnounceInterval.TotalMilliseconds, 1500,
            "nothing on the wire, but it is the rate other ports are told to match");
        Pin("discovery expiry ms", (int)PeerDiscoveryService.PeerExpiry.TotalMilliseconds, 8000,
            "any port that announces less often than this — since issue #31 it drops out of the list between announcements");
        // The announcement itself is JSON with these exact names, in this case. .NET reads names case-sensitively, so
        // a renamed property is a silent break in both directions.
        var discoveryId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Pin("discovery announcement",
            PeerDiscoveryService.AnnouncementJsonForTest(discoveryId, "ED_DT", 47830, canSend: true, canReceive: false),
            "{\"InstanceId\":\"11111111-2222-3333-4444-555555555555\",\"Name\":\"ED_DT\",\"AudioPort\":47830,\"CanSend\":true,\"CanReceive\":false}",
            "every port that finds peers on a network — a renamed field reads as a peer with no name, no port and no id");
        var theirs = System.Text.Encoding.UTF8.GetBytes(
            "{\"InstanceId\":\"66666666-7777-8888-9999-000000000000\",\"Name\":\"iPhone\",\"AudioPort\":47830,\"CanSend\":true,\"CanReceive\":true}");
        var parsed = PeerDiscoveryService.TryParseAnnouncement(theirs, System.Net.IPAddress.Parse("192.168.1.8"), discoveryId, out var heard);
        Pin("discovery announcement from another port parses",
            parsed ? $"{heard.Name}@{heard.AudioPort}" : "rejected", "iPhone@47830",
            "the iPhone and Android apps — their announcements would be thrown away and they would never appear");

        // --- The server's group protocol: several people on one relay. The Python relay speaks it, the Pi baby
        // monitor speaks it, and the iPhone app is about to be built from the notes that describe it. None of it was
        // pinned until 2026-09-23. ------------------------------------------------------------------------------------
        Pin("server group version", RelayGroupClient.GroupVersion, 2,
            "the Python relay and anything that joins a server — group packets would be read as ordinary ones");
        Pin("server client id size", RelayGroupClient.ClientIdSize, 16,
            "the Python relay — every group packet's payload would start at the wrong byte");
        Pin("server group header size", RelayGroupClient.GroupHeaderSize, 28,
            "the Python relay and every group member");
        Pin("server hello type", RelayGroupClient.TypeHello, 6, "the Python relay — nobody would ever join a group");
        Pin("server member list type", RelayGroupClient.TypeRoster, 7, "every group member — nobody would ever see who is there");
        Pin("server full type", RelayGroupClient.TypeFull, 8, "every group member");
        Pin("server bye type", RelayGroupClient.TypeBye, 9, "the Python relay — people would linger a minute after leaving");
        Pin("server name bytes", RelayGroupClient.NameBytes, 32, "the Python relay and every group member — names would be read from the wrong bytes");
        Pin("server group tag bytes", RelayGroupClient.GroupTagBytes, 8,
            "the Python relay — it groups people by these bytes, so everybody would land in the wrong group");
        Pin("server most people one hello can tick", RelayGroupClient.MaxTickedIds, 64, "the Python relay, which caps the list at the same number");
        Pin("server member list entry bytes", RelayGroupClient.RosterEntryBytes, 49,
            "every group member — every name after the first would be read from the wrong place");
        Pin("server member flag: has ticked you", RelayGroupClient.RosterMemberFlagTicksUs, 1,
            "every group member — \"waiting for them to tick you\" would be wrong both ways");
        Pin("server list flag: paired with a phone", RelayGroupClient.RosterFlagV1Paired, 1,
            "phones and older apps on a server — nobody would send them anything");
        Pin("server hello interval ms", (int)RelayGroupClient.HelloInterval.TotalMilliseconds, 2000,
            "nothing on the wire, but the relay forgets a member after 60 s of silence and the notes tell other ports this figure");

        // --- The app↔plugin bridge. The VST3 is named above as a port in the wild, and it is one in
        // the most literal sense: it is INSTALLED separately, into the user's own VST3 folder, and it
        // stays there across app updates — Anthony has a build sitting in Reaper right now. Change any
        // of these in the app and that installed copy does not report an error: it finds nobody, goes
        // silent, and tells him the app is not running. None of it was pinned until 2026-08-24, when
        // moving the port broke nothing in the gate at all. ----------------------------------------
        Pin("plugin bridge magic", System.Text.Encoding.ASCII.GetString(PluginBridgeProtocol.Magic), "RSBR",
            "any already-installed VST3 — every message it sends would be discarded as foreign");
        Pin("plugin bridge version", PluginBridgeProtocol.Version, 1,
            "any already-installed VST3 — it rejects an unknown version outright");
        Pin("plugin bridge header size", PluginBridgeProtocol.HeaderSize, 16,
            "any already-installed VST3 — the payload would start at the wrong byte");
        Pin("plugin bridge port", PluginBridgeProtocol.DefaultPort, 47831,
            "any already-installed VST3 — it dials this port with no discovery step, so it would simply never find the app");
        Pin("plugin bridge wire rate", PluginBridgeProtocol.WireSampleRate, 48000,
            "any already-installed VST3 — a rate mismatch transposes everybody's voice rather than failing");
        Pin("plugin bridge wire channels", PluginBridgeProtocol.WireChannels, 2,
            "any already-installed VST3 — the interleaving would be read wrong");
        Pin("plugin bridge max audio bytes", PluginBridgeProtocol.MaxAudioBytes, 32768,
            "any already-installed VST3 — a legitimate large block would be rejected as a lying length");

        Check(changed.Count == 0,
            $"CROSS-PORT CONTRACT CHANGED ({changed.Count} of {pinned.Count} values): {string.Join("; ", changed)}. "
          + "Do NOT update these numbers to make the test pass: ask Ed first. Anything already shipped is still "
          + "speaking the old values, and it will fail silently rather than report an error.");

        // A pin count that COLLAPSES is the other way this test could quietly stop working — an
        // early return, a refactor that drops half the list — so the floor is asserted too.
        Check(pinned.Count >= 48,
            $"only {pinned.Count} values were pinned; the contract has lost entries rather than gained them");

        return $"{pinned.Count} values pinned that {PortsInTheWild} all depend on: " + string.Join(", ", pinned);
    }

    /// <summary>
    /// THE SERVER AND THE APP AGREE. The contract above pins the app's side of the server protocol; this reads the
    /// Python relay's own source and holds it to the same numbers, so neither end can move without the other. Until
    /// 2026-09-23 nothing connected them but care.
    /// </summary>
    private static string? CrossPortServerAgreesWithTheApp()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var path = Path.Combine(root, "server", "remsound-relay.py");
        Check(File.Exists(path), $"the relay's source must be where it always is ({path})");
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^([A-Z][A-Z0-9_]*)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*(#.*)?$");
            if (!m.Success) continue;
            var text = m.Groups[2].Value;
            values[m.Groups[1].Value] = text.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt32(text[2..], 16) : int.Parse(text);
        }
        var disagree = new List<string>();
        var compared = 0;
        void Same(string pythonName, int app)
        {
            compared++;
            if (!values.TryGetValue(pythonName, out var server)) disagree.Add($"{pythonName} is missing from the relay");
            else if (server != app) disagree.Add($"{pythonName} is {server} on the relay and {app} in the app");
        }
        Same("DEFAULT_PORT", RemPacket.DefaultPort);
        Same("V1_VERSION", RemPacket.Version);
        Same("V1_HEADER_LEN", RemPacket.HeaderSize);
        Same("V2_VERSION", RelayGroupClient.GroupVersion);
        Same("V2_HEADER_LEN", RelayGroupClient.GroupHeaderSize);
        Same("V2_CLIENT_ID_OFFSET", RemPacket.HeaderSize);
        Same("V2_CLIENT_ID_LEN", RelayGroupClient.ClientIdSize);
        Same("TYPE_FORMAT", (int)RemPacketType.Format);
        Same("TYPE_AUDIO", (int)RemPacketType.Audio);
        Same("TYPE_KEEPALIVE", (int)RemPacketType.KeepAlive);
        Same("TYPE_HEARTBEAT", (int)RemPacketType.Heartbeat);
        Same("TYPE_LOBBY_HELLO", RelayGroupClient.TypeHello);
        Same("TYPE_LOBBY_ROSTER", RelayGroupClient.TypeRoster);
        Same("TYPE_LOBBY_FULL", RelayGroupClient.TypeFull);
        Same("TYPE_LOBBY_BYE", RelayGroupClient.TypeBye);
        Same("TYPE_ADDR_CHECK", (int)RemPacketType.AddrCheck);
        Same("LOBBY_NAME_BYTES", RelayGroupClient.NameBytes);
        Same("GROUP_TAG_BYTES", RelayGroupClient.GroupTagBytes);
        Same("MAX_TICKED_IDS", RelayGroupClient.MaxTickedIds);
        Same("FORMAT_FINGERPRINT_OFFSET", RemPacket.FormatPayloadExtendedSize);
        Same("ROSTER_FLAG_V1_PAIRED", RelayGroupClient.RosterFlagV1Paired);
        Same("ROSTER_MEMBER_FLAG_TICKS_YOU", RelayGroupClient.RosterMemberFlagTicksUs);
        Check(values.Count >= 20, $"the relay's constants must actually have been read ({values.Count} found) — or this compares nothing");
        Check(disagree.Count == 0,
            $"THE SERVER AND THE APP DISAGREE ({disagree.Count} of {compared}): {string.Join("; ", disagree)}. "
            + "One of them has moved without the other. Do not change either to make this pass: ask Ed, because the Pi, the "
            + "baby monitor and every other port are already speaking the old value.");
        return $"the relay and the app agree on all {compared} values they share";
    }
}
