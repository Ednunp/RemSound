using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Reading a recording back, in every format RemSound writes - so a check can say what is IN the file, not how big it is.
/// Until 2026-09-24 the MP3, OGG and FLAC recordings were judged by "more than 200 bytes", which a file of silence passes.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The recording's audio, mixed to mono, and its sample rate.</summary>
    private static (float[] Mono, int Rate) DecodeRecording(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mono = new List<float>();
        switch (ext)
        {
            case ".wav":
            case ".mp3":
            {
                using var reader = new NAudio.Wave.AudioFileReader(path);
                var channels = reader.WaveFormat.Channels;
                var buffer = new float[reader.WaveFormat.SampleRate * channels];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    for (var i = 0; i + channels <= read; i += channels)
                    {
                        var sum = 0f;
                        for (var c = 0; c < channels; c++) sum += buffer[i + c];
                        mono.Add(sum / channels);
                    }
                return (mono.ToArray(), reader.WaveFormat.SampleRate);
            }
            case ".ogg":
            case ".opus":
            {
                // RemSound's OGG is Opus in an Ogg file (Concentus.Oggfile), always 48 kHz.
                using var stream = File.OpenRead(path);
                var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2);
                var ogg = new Concentus.Oggfile.OpusOggReadStream(decoder, stream);
                while (ogg.HasNextPacket)
                {
                    var pcm = ogg.DecodeNextPacket();
                    if (pcm is null) continue;
                    for (var i = 0; i + 1 < pcm.Length; i += 2) mono.Add((pcm[i] + pcm[i + 1]) / 2f / 32768f);
                }
                return (mono.ToArray(), 48000);
            }
            case ".flac":
            {
                using var reader = new CUETools.Codecs.FLAKE.FlakeReader(path, null);
                var pcm = reader.PCM;
                var scale = 1.0 / (1L << (pcm.BitsPerSample - 1));
                var buffer = new CUETools.Codecs.AudioBuffer(pcm, 4096);
                int read;
                while ((read = reader.Read(buffer, 4096)) > 0)
                    for (var f = 0; f < read; f++)
                    {
                        double sum = 0;
                        for (var c = 0; c < pcm.ChannelCount; c++) sum += buffer.Samples[f, c];
                        mono.Add((float)(sum / pcm.ChannelCount * scale));
                    }
                return (mono.ToArray(), pcm.SampleRate);
            }
            default:
                throw new InvalidOperationException($"no reader for {ext}");
        }
    }

    /// <summary>The RMS of a recording's decoded audio; 0 for a missing or empty file.</summary>
    private static double RmsOfRecording(string path)
    {
        if (!File.Exists(path)) return 0;
        float[] mono;
        try { mono = DecodeRecording(path).Mono; } catch { return 0; }
        return mono.Length == 0 ? 0 : Math.Sqrt(mono.Sum(x => (double)x * x) / mono.Length);
    }

    /// <summary>How strongly a frequency is present (Goertzel), relative to the signal's own power.</summary>
    private static double ToneStrength(float[] samples, int rate, double hz)
    {
        if (samples.Length == 0) return 0;
        var k = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double s1 = 0, s2 = 0, power = 0;
        foreach (var x in samples)
        {
            var s0 = x + k * s1 - s2;
            s2 = s1; s1 = s0;
            power += x * x;
        }
        var magnitude = s1 * s1 + s2 * s2 - k * s1 * s2;
        return power <= 0 ? 0 : magnitude / (power * samples.Length);
    }

    /// <summary>Check a recording holds the 440 Hz test tone: long enough, loud enough, and 440 Hz rather than anything
    /// else. Returns a short description of what was found.</summary>
    private static string CheckHoldsTheTestTone(string path, string what)
    {
        var (mono, rate) = DecodeRecording(path);
        var seconds = mono.Length / (double)rate;
        Check(seconds > 0.25, $"{what}: the recording must hold the ~0.4 s that was fed to it (it decodes to {seconds:0.00} s)");
        var rms = Math.Sqrt(mono.Sum(x => (double)x * x) / Math.Max(1, mono.Length));
        Check(rms > 0.05, $"{what}: the recording must not be silence (it decodes to an RMS of {rms:0.0000})");
        var at440 = ToneStrength(mono, rate, 440);
        var at1000 = ToneStrength(mono, rate, 1000);
        Check(at440 > at1000 * 20, $"{what}: the recording must hold the 440 Hz tone that was fed to it (440 Hz {at440:0.000}, 1 kHz {at1000:0.000})");
        return $"{seconds:0.00}s rms {rms:0.00}";
    }
}
