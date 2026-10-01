using System.Buffers.Binary;
using System.Security.Cryptography;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// KNOWN ANSWERS (2026-09-27, from the review of every step): the exact bytes other RemSounds read and write, written out
/// here by hand. Every other test of these encodes and decodes with the same build, so a change made on both sides - the
/// time's byte order, a field moved, the nonce after the tag instead of before it - passed the whole gate while every
/// iPhone, Android phone, relay and DAW project already out there stopped understanding it. Like the CROSS-PORT CONTRACT:
/// if one of these fails, do NOT change the bytes to match. Ask Ed.
/// </summary>
internal static partial class SelfTest
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    private static string HexOf(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>CROSS-PORT KNOWN ANSWERS: the header, the format announcement, the heartbeat and the sealed payloads, byte for
    /// byte, both ways - what this build writes, and what it reads when another port writes exactly these bytes.</summary>
    private static string CrossPortKnownAnswers()
    {
        // The packet header: "RMND", version 1, the type, the stream (little-endian), the sequence (little-endian).
        var header = new byte[RemPacket.HeaderSize];
        RemPacket.WriteHeader(header, RemPacketType.Audio, 0x1234, 0x0A0B0C0D);
        Check(HexOf(header) == "524D4E4401023412" + "0D0C0B0A", $"the packet header's bytes have changed: {HexOf(header)}");
        // Stream id 0 never reaches the wire: the relay PAIRS peers on this field, and a shared zero would let it pair the
        // wrong two people. Our own reader normalises on read too, which is why only the bytes can show it (2026-08-24).
        RemPacket.WriteHeader(header, RemPacketType.Audio, 0, 1);
        Check(HexOf(header) == "524D4E4401020100" + "01000000", $"a stream id of 0 must go out as 1: {HexOf(header)}");
        Check(RemPacket.TryReadHeader(Hex("524D4E44 01 04 FFFF 07000000"), out var type, out var stream, out var sequence)
              && type == RemPacketType.Heartbeat && stream == 0xFFFF && sequence == 7,
            "a heartbeat header as another port writes it must read as one (type 4, stream FFFF, sequence 7)");

        // The format announcement, with the fingerprint and the capture latency: 46 bytes, every int little-endian.
        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192000, (int)AudioTransportCodec.Opus, 120, RenderRoute.AsioLane, 3.5);
        var payload = new byte[RemPacket.FormatPayloadWithCaptureSize];
        var written = RemPacket.WriteFormatPayload(payload, format, Hex("A0A1A2A3A4A5A6A7"));
        const string FormatBytes = "80BB0000 02000000 10000000 01000000 04000000 00EE0200 02000000 78000000"
                                 + "02 00 0000 A0A1A2A3A4A5A6A7 2300";
        Check(written == 46 && HexOf(payload) == HexOf(Hex(FormatBytes)), $"the format announcement's bytes have changed: {HexOf(payload.AsSpan(0, written))}");
        Check(RemPacket.TryReadFormat(Hex(FormatBytes), out var read, out var fingerprint) && read.SampleRate == 48000 && read.Channels == 2
              && read.Codec == (int)AudioTransportCodec.Opus && read.FrameSamplesPerChannel == 120 && read.Lane == RenderRoute.AsioLane
              && Math.Abs(read.CaptureLatencyMs - 3.5) < 0.01 && fingerprint is not null && HexOf(fingerprint) == "A0A1A2A3A4A5A6A7",
            "the format announcement as another port writes it must read back field for field");
        Check(RemPacket.TryReadFormat(Hex(FormatBytes)[..32], out var old) && old.SampleRate == 48000 && old.Lane == RenderRoute.Mixed,
            "the 32-byte announcement the oldest ports send must still read, as the mixed lane");

        // The heartbeat: kind, then the originator's tick (little-endian), then - only when set - the flags byte.
        var beat = new byte[RemPacket.HeartbeatPayloadWithFlagsSize];
        Check(RemPacket.WriteHeartbeatPayload(beat, HeartbeatKind.Pong, 0x0102030405060708) == 9 && HexOf(beat.AsSpan(0, 9)) == "010807060504030201",
            $"a heartbeat's bytes have changed: {HexOf(beat)}");
        Check(RemPacket.TryReadHeartbeat(Hex("00 0807060504030201"), out var kind, out var tick) && kind == HeartbeatKind.Ping && tick == 0x0102030405060708,
            "a 9-byte ping as the phones send it must read");

        // The sealed envelope every encrypted payload uses: nonce (12), then tag (16), then the ciphertext - AES-256-GCM.
        var key = Require(RemSoundCrypto.ForPlainPassword("known answer").Key, "a password must derive a key");
        var plain = Hex("00112233445566778899");
        var sealedByUs = RemSoundCrypto.Encrypt(key, plain);
        Check(sealedByUs.Length == 12 + 16 + plain.Length, $"a sealed payload must be nonce + tag + ciphertext ({sealedByUs.Length} bytes for {plain.Length})");
        Check(OpenByHand(key, sealedByUs) is { } opened && HexOf(opened) == HexOf(plain),
            "OUR ENVELOPE: what this build seals must open by the documented layout - nonce first, then tag, then ciphertext");
        var sealedByAnother = SealByHand(key, plain);
        Check(RemSoundCrypto.TryDecrypt(key, sealedByAnother, out var fromAnother) && HexOf(fromAnother) == HexOf(plain),
            "THEIR ENVELOPE: a payload another port seals by the documented layout must open");
        // The audio path's own sealing (a reusable cipher and a counting nonce): the same layout on the wire.
        using (var gcm = RemSoundCrypto.CreateGcm(key))
        {
            var audio = new byte[plain.Length + RemSoundCrypto.EncryptionOverheadBytes];
            var n = RemSoundCrypto.EncryptInto(gcm, new RemSoundCrypto.NonceSequence(), plain, audio);
            Check(n == audio.Length && OpenByHand(key, audio) is { } openedAudio && HexOf(openedAudio) == HexOf(plain),
                "OUR AUDIO: what the audio path seals must open by the documented layout - nonce, tag, ciphertext");
            var back = new byte[plain.Length];
            Check(RemSoundCrypto.TryDecryptInto(gcm, sealedByAnother, back, out var got) && got == plain.Length && HexOf(back) == HexOf(plain),
                "THEIR AUDIO: audio another port seals by the documented layout must open on the audio path");
        }

        // The tick proof (packet 11, the phones add it): version 1, the time (little-endian), the sender's id (big-endian).
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        const long When = 1_790_000_000;
        var tickPlain = Hex("01 803BB16A00000000 00112233445566778899AABBCCDDEEFF");
        Check(OpenByHand(key, TickProof.Seal(key, id, When)) is { } ourTick && HexOf(ourTick) == HexOf(tickPlain),
            "OUR TICK PROOF: its sealed bytes must be version 1, the time little-endian, the id big-endian - what the phones read");
        Check(TickProof.TryUnseal(key, SealByHand(key, tickPlain), out var gotId, out var gotWhen, out _) && gotId == id && gotWhen == When,
            "THEIR TICK PROOF: one sealed by hand to the phones' layout must open to the same id and time");

        // The sealed remote control: kind, signed delta, the time (little-endian).
        var controlPlain = Hex("01 FD 803BB16A00000000");
        Check(OpenByHand(key, ControlSealing.Seal(key, RemoteControlKind.VolumeDown, -3, When)) is { } ourControl && HexOf(ourControl) == HexOf(controlPlain),
            "OUR REMOTE CONTROL: its sealed bytes must be the kind, the signed delta and the time little-endian");
        Check(ControlSealing.TryUnseal(key, SealByHand(key, controlPlain), out var controlKind, out var delta, out var controlWhen, out _)
              && controlKind == RemoteControlKind.VolumeDown && delta == -3 && controlWhen == When,
            "THEIR REMOTE CONTROL: one sealed by hand must open to the same command");
        return "the header, the 46-byte format announcement (and the 32-byte one), the heartbeat, the sealed envelope, the tick "
             + "proof and the remote control are the exact bytes other ports read and write, both ways";
    }

    /// <summary>Seal by the documented layout, with a fixed nonce: nonce (12) || tag (16) || ciphertext.</summary>
    private static byte[] SealByHand(byte[] key, byte[] plain)
    {
        var nonce = Hex("0102030405060708090A0B0C");
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    /// <summary>Open by the documented layout, or null.</summary>
    private static byte[]? OpenByHand(byte[] key, byte[] sealedPayload)
    {
        if (sealedPayload.Length < 28) return null;
        var plain = new byte[sealedPayload.Length - 28];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(sealedPayload.AsSpan(0, 12), sealedPayload.AsSpan(28), sealedPayload.AsSpan(12, 16), plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>A project saved by the released v6.0 plugin: send on, receive off, two chosen people, not all of them.
    /// The plugin and AudioPlugSharp are unchanged since the v6.0 tag (checked 2026-09-27), so these are the bytes v6.0
    /// writes. Every DAW project with RemSound on a track holds a chunk like this.</summary>
    private const string V60PluginChunk =
        "3C3F786D6C2076657273696F6E3D22312E302220656E636F64696E673D227574662D38223F3E0D0A3C417564696F506C7567696E53617665"
        + "537461746520786D6C6E733A7873693D22687474703A2F2F7777772E77332E6F72672F323030312F584D4C536368656D612D696E7374616E"
        + "63652220786D6C6E733A7873643D22687474703A2F2F7777772E77332E6F72672F323030312F584D4C536368656D61223E0D0A20203C5061"
        + "72616D6574657256616C7565733E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A2020202020203C49"
        + "443E73656E643C2F49443E0D0A2020202020203C56616C75653E313C2F56616C75653E0D0A202020203C2F417564696F506C7567696E5361"
        + "7665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A2020202020203C49443E"
        + "726563656976653C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A202020203C2F417564696F506C7567696E53"
        + "617665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A2020202020203C4944"
        + "3E706565723C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A202020203C2F417564696F506C7567696E536176"
        + "65506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A2020202020203C49443E70"
        + "656572696E636C7564653C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A202020203C2F417564696F506C7567"
        + "696E53617665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A202020202020"
        + "3C49443E616C6C70656572733C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A202020203C2F417564696F506C"
        + "7567696E53617665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A20202020"
        + "20203C49443E73656E646C6576656C3C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A202020203C2F41756469"
        + "6F506C7567696E53617665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D657465723E0D0A20"
        + "20202020203C49443E726563656976656C6576656C3C2F49443E0D0A2020202020203C56616C75653E303C2F56616C75653E0D0A20202020"
        + "3C2F417564696F506C7567696E53617665506172616D657465723E0D0A202020203C417564696F506C7567696E53617665506172616D6574"
        + "65723E0D0A2020202020203C49443E6163746976653C2F49443E0D0A2020202020203C56616C75653E313C2F56616C75653E0D0A20202020"
        + "3C2F417564696F506C7567696E53617665506172616D657465723E0D0A20203C2F506172616D6574657256616C7565733E0D0A20203C4564"
        + "69746F7257696474683E3430303C2F456469746F7257696474683E0D0A20203C456469746F724865696768743E3230303C2F456469746F72"
        + "4865696768743E0D0A3C2F417564696F506C7567696E5361766553746174653E3C212D2D52656D536F756E64506565723A1600000076337C"
        + "2D7C31302E302E302E352C31302E302E302E39";

    /// <summary>CROSS-PORT KNOWN ANSWERS: the plugin's identity, and a project saved by v6.0 opening as it was saved. A DAW
    /// finds the plugin in a saved project by its id, and restores it from the chunk: change either and every project
    /// already saved opens without its RemSound, or with the wrong people.</summary>
    private static string PluginIdentityAndSavedStateHold()
    {
        using var host = new PluginBridgeHost((System.Net.IPAddress _, Span<float> _, int _) => 0, port: 0);
        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        var reopened = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            Check(plugin.PluginID == 0x52656D536E640600UL,
                $"THE PLUGIN'S ID has changed ({plugin.PluginID:X16}): every DAW project saved since v6.0 would open without its RemSound");
            plugin.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
            plugin.Initialize();
            plugin.SetJobForTest(send: true, receive: false, [System.Net.IPAddress.Parse("10.0.0.5"), System.Net.IPAddress.Parse("10.0.0.9")]);
            plugin.Parameters.First(p => p.ID == "send").EditValue = 1;   // as the window's Send switch sets it
            var chunk = plugin.SaveState();
            Check(HexOf(chunk) == V60PluginChunk,
                $"THE SAVED STATE has changed: what this build saves is no longer what v6.0 saved. Now: {HexOf(chunk)}");

            reopened.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
            reopened.Initialize();
            reopened.RestoreState(Hex(V60PluginChunk));
            Check(reopened.Parameters.First(p => p.ID == "send").EditValue >= 0.5 && reopened.Parameters.First(p => p.ID == "receive").EditValue < 0.5,
                "A v6.0 PROJECT must open with send on and receive off, as it was saved");
            Check(reopened.SavedPeerAddressesForTest.SequenceEqual(["10.0.0.5", "10.0.0.9"]) && !reopened.AllPeersForTest,
                $"a v6.0 project must open with the same two people chosen, and not all of them ({string.Join(", ", reopened.SavedPeerAddressesForTest)})");
            Check(HexOf(reopened.SaveState()) == V60PluginChunk, "and saved again unchanged, byte for byte");
        }
        finally
        {
            try { plugin.CloseForTest(); } catch { /* teardown */ }
            try { reopened.CloseForTest(); } catch { /* teardown */ }
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
        return "the plugin's id is the one every saved project holds; a chunk v6.0 saved opens with its switches and its two people, "
             + "and saves again byte for byte";
    }

    /// <summary>CROSS-PORT KNOWN ANSWERS: the plugin link's messages, the 24-bit samples and the stored password, byte for
    /// byte. The plugin link is a contract from 6.1 on (a v6.0 plugin in a DAW talks to a newer app); the 24-bit samples are
    /// what every phone, Pi and Mac decodes; the scramble is in every saved profile. None of the code behind these has changed
    /// since the v6.0 tag (checked 2026-09-28), so these are v6.0's bytes. The link half runs a real bridge host and talks to
    /// it with datagrams built by hand, the way a v6.0 plugin sends them.</summary>
    private static string PluginLinkSamplesAndPasswordBytesHold()
    {
        // The link's header: "RSBR", version 1, the type, the payload length (LE), the instance (LE), the peer's IPv4.
        var header = new byte[PluginBridgeProtocol.HeaderSize];
        PluginBridgeProtocol.WriteHeader(header, PluginBridgeMessage.ClaimPeers, 0x11223344, System.Net.IPAddress.Parse("10.0.0.5"), 25);
        Check(HexOf(header) == "52534252" + "0108" + "1900" + "44332211" + "0A000005", $"the plugin link's header has changed: {HexOf(header)}");
        Check(PluginBridgeProtocol.TryReadHeader(Hex("52534252 01 06 0000 78563412 00000000"), out var linkType, out var instance, out var none, out var length)
              && linkType == PluginBridgeMessage.Goodbye && instance == 0x12345678 && none is null && length == 0,
            "a goodbye as a v6.0 plugin writes it must read as one");
        foreach (var retired in new byte[] { 2, 5, 10 })
            Check(!PluginBridgeProtocol.TryReadHeader([.. Hex("52534252 01"), retired, .. Hex("0000 78563412 00000000")], out _, out _, out _, out _),
                $"message type {retired} is not one this link speaks, and must be refused");

        // The claim set a plugin sends every block: frames (LE), a count, the IPv4 addresses, then the ask number (LE).
        var claim = new byte[PluginBridgeProtocol.ClaimSetSizeWithAsk(2)];
        var claimed = PluginBridgeProtocol.WriteClaimSet(claim, 512, [System.Net.IPAddress.Parse("10.0.0.5"), System.Net.IPAddress.Parse("10.0.0.9")], 7);
        Check(claimed == 17 && HexOf(claim) == "00020000" + "02" + "0A000005" + "0A000009" + "07000000", $"the claim set's bytes have changed: {HexOf(claim)}");
        // The reply's part header: the round (LE), the ask number, this part's offset and the block's length, in floats.
        var round = new byte[PluginBridgeProtocol.AudioRoundHeaderSize];
        PluginBridgeProtocol.WriteAudioRoundHeader(round, 0x0102030405060708, 7, 0, 1024);
        Check(HexOf(round) == "0807060504030201" + "07000000" + "00000000" + "00040000", $"a block's part header has changed: {HexOf(round)}");

        // A real host, and a v6.0 plugin's datagrams built by hand.
        var trackAudio = new List<float>();
        using (var host = new PluginBridgeHost((System.Net.IPAddress _, Span<float> into, int frames) => { into[..(frames * 2)].Fill(0.125f); return frames; }, port: 0)
               { PeerListSource = () => [(System.Net.IPAddress.Parse("10.0.0.5"), "Andre")] })
        using (var plugin = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
        {
            host.TrackAudioReceived += (_, floats) => { lock (trackAudio) trackAudio.AddRange(floats.ToArray()); };
            plugin.Client.ReceiveTimeout = 3000;
            var app = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, host.Port);
            byte[] Ask(string hex)
            {
                var datagram = Hex(hex);
                plugin.Send(datagram, datagram.Length, app);
                var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                try { return plugin.Receive(ref from); }
                catch (System.Net.Sockets.SocketException) { return []; }
            }

            var list = Ask("52534252 01 01 0400 44332211 00000000" + "34120000");   // hello, from DAW process 0x1234
            Check(HexOf(list) == "52534252" + "0107" + "0E00" + "44332211" + "00000000" + HexOf("10.0.0.5\tAndre"u8),
                $"a v6.0 plugin's hello must be answered with the peer list, in the bytes it reads: {HexOf(list)}");

            var reply = Ask("52534252 01 08 0D00 44332211 00000000" + "04000000" + "01" + "0A000005" + "07000000");   // 4 frames of 10.0.0.5, ask 7
            Check(reply.Length == 16 + 20 + 32 && HexOf(reply.AsSpan(0, 8)) == "5253425201093400" && HexOf(reply.AsSpan(8, 4)) == "44332211",
                $"a v6.0 plugin's claim must be answered with a block, in the header it reads: {HexOf(reply)}");
            Check(reply.Length == 68 && HexOf(reply.AsSpan(16 + 8, 12)) == "07000000" + "00000000" + "08000000"
                  && HexOf(reply.AsSpan(36)) == string.Concat(Enumerable.Repeat("0000003E", 8)),
                $"and the block must carry its ask number, offset and length, then the samples as 32-bit floats: {HexOf(reply)}");

            var audio = Hex("52534252 01 04 0800 44332211 00000000" + "0000003F" + "000080BE");   // 0.5, -0.25 from the DAW track
            plugin.Send(audio, audio.Length, app);
            Check(WaitFor(() => { lock (trackAudio) return trackAudio.Count >= 2; }, TimeSpan.FromSeconds(3)),
                "a v6.0 plugin's track audio must reach the app");
            lock (trackAudio) Check(trackAudio[0] == 0.5f && trackAudio[1] == -0.25f, $"as the samples it sent ({string.Join(", ", trackAudio)})");
        }

        // 24-bit PCM, as every port packs it: signed, little-endian, scaled by 2^23 - 1 and truncated, clamped at full scale.
        var packed = new byte[18];
        PcmPack.FloatToInt24LE([0.5f, -0.5f, -1f, 1f, 2f, 0.25f], packed);
        Check(HexOf(packed) == "FFFF3F" + "0100C0" + "010080" + "FFFF7F" + "FFFF7F" + "FFFF1F", $"24-bit PCM's bytes have changed: {HexOf(packed)}");
        var unpacked = new float[4];
        PcmPack.Int24LEToFloat(Hex("FFFF7F 0100C0 000080 000000"), unpacked);
        Check(unpacked[0] == 1f && Math.Abs(unpacked[1] + 0.5f) < 1e-6 && unpacked[2] < -1f && unpacked[2] > -1.000001f && unpacked[3] == 0f,
            $"24-bit PCM from another port must read as full scale, minus a half, the one step below minus full scale, and zero ({string.Join(", ", unpacked)})");

        // The stored-password scramble every saved profile holds: UTF-8, XOR with the fixed key, Base64.
        Check(RemSoundCrypto.Obfuscate("hunter2") == "OhADJwoHXA==" && RemSoundCrypto.Obfuscate("p\u00e4ssw\u00f6rd \u2014 42") == "IqbJIBwCrdJfFFKN5v1MUR8=",
            $"THE STORED PASSWORD's scramble has changed ({RemSoundCrypto.Obfuscate("hunter2")}): every saved profile would open without its password");
        Check(RemSoundCrypto.Deobfuscate("OhADJwoHXA==") == "hunter2" && RemSoundCrypto.Deobfuscate("IqbJIBwCrdJfFFKN5v1MUR8=") == "p\u00e4ssw\u00f6rd \u2014 42",
            "a password saved by v6.0 must read back as itself");
        // Longer than the 37-byte key, so every byte of it is used and it wraps: a change anywhere in the key shows here.
        const string Long = "correct horse battery staple, forty-two!!";
        const string LongStored = "MQofIQoWGkRFHwAcA0kOBFkEBAEKVxwGBV0fBl5BCw0eEVRbRSUKTHI=";
        Check(RemSoundCrypto.Obfuscate(Long) == LongStored && RemSoundCrypto.Deobfuscate(LongStored) == Long,
            $"a password longer than the scramble's key must scramble as v6.0 did, both ways ({RemSoundCrypto.Obfuscate(Long)})");
        return "the plugin link's header, claim and block bytes, both ways, with a real host answering a v6.0 plugin's hand-built hello, "
             + "claim and track audio; 24-bit PCM packed and read as every port does; and the stored-password scramble, both ways";
    }
}
