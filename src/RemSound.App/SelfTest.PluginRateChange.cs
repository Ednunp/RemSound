using System.Net;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>A DAW THAT CHANGES RATE MID-SESSION (2026-09-28, a gap from the review of every step). The rate test runs
    /// each rate from a fresh start; a person who switches their project from 44.1 to 96 kHz with RemSound on the track
    /// takes the other path - the plugin notices the new rate on the next block and prepares again. Driven through the
    /// real plugin: a 1 kHz tone on the track at 44.1 kHz, then the host's rate moved to 96 kHz and the tone carried on at
    /// the new rate. On the wire (48 kHz) it must be 1 kHz both sides of the change, with the right number of frames.</summary>
    private static string? PluginSurvivesAHostRateChange()
    {
        const double ToneHz = 1000.0;
        const int Block = 512;
        int ReadNobody(IPAddress peer, Span<float> destination, int frames) => 0;
        using var host = new PluginBridgeHost(ReadNobody, port: 0);   // port 0, never the real one
        var sent = new List<float>();
        host.TrackAudioReceived += (_, audio) => { lock (sent) sent.AddRange(audio.ToArray()); };
        var daw = new RateChangingHost { SampleRate = 44100 };
        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        var plugin = new RemSoundPlugin { Host = daw };
        try
        {
            plugin.SetMaxAudioBufferSize(Block, EAudioBitsPerSample.Bits64);
            plugin.Initialize();
            plugin.Start();
            plugin.SetJobForTest(send: true, receive: false, peer: null);
            var input = (AudioIOPortManaged)plugin.InputPorts[0];

            var phase = 0.0;
            int Sent() { lock (sent) return sent.Count; }
            void Play(double seconds)
            {
                var step = 2 * Math.PI * ToneHz / daw.SampleRate;
                var blocks = (int)Math.Ceiling(daw.SampleRate * seconds / Block);
                for (var b = 0; b < blocks; b++)
                {
                    var left = input.GetAudioBuffer(0);
                    var right = input.GetAudioBuffer(1);
                    for (var i = 0; i < left.Length; i++) { left[i] = right[i] = 0.5 * Math.Sin(phase); phase += step; }
                    plugin.Process();
                }
                // Everything played has crossed the link when the count has stopped growing for a while.
                var settled = -1;
                var still = 0;
                WaitUntil(() => { var now = Sent(); still = now == settled ? still + 1 : 0; settled = now; return still >= 5; }, 3000);
            }

            Play(1.5);
            var atChange = Sent();
            daw.SampleRate = 96000;   // the person changes the project's rate
            Play(1.5);

            float[] wire;
            lock (sent) wire = [.. sent];
            var framesBefore = atChange / 2;
            var framesAfter = (wire.Length - atChange) / 2;
            var expectedBefore = Math.Ceiling(44100 * 1.5 / Block) * Block * 48000.0 / 44100;
            var expectedAfter = Math.Ceiling(96000 * 1.5 / Block) * Block * 48000.0 / 96000;
            Check(Math.Abs(framesBefore - expectedBefore) / expectedBefore < 0.02,
                $"premise: at 44.1 kHz the track must reach the wire as ~{expectedBefore:0} frames at 48 kHz ({framesBefore})");
            Check(Math.Abs(framesAfter - expectedAfter) / expectedAfter < 0.02,
                $"THE RATE CHANGE: after the project moves to 96 kHz, the track must reach the wire as ~{expectedAfter:0} frames at 48 kHz ({framesAfter}) - the old rate's resampler would send twice as many, an octave down");
            var tail = wire.Skip(atChange).ToList();
            var before = CentsFrom(ToneHz, PreciseHz(wire.Take(atChange).Skip(4800 * 2).ToList(), 48000, stride: 2));
            var after = CentsFrom(ToneHz, PreciseHz(tail.Skip(4800 * 2).ToList(), 48000, stride: 2));
            Check(Math.Abs(before) < 2, $"premise: at 44.1 kHz a 1 kHz tone is 1 kHz on the wire ({before:+0.0;-0.0} cents)");
            Check(Math.Abs(after) < 2, $"THE RATE CHANGE: after the change a 1 kHz tone must still be 1 kHz on the wire, within 2 cents ({after:+0.0;-0.0} cents)");
            return $"a DAW moved from 44.1 to 96 kHz mid-session: the track stayed 1 kHz on the wire ({before:+0.0;-0.0} then {after:+0.0;-0.0} cents), "
                 + $"{framesBefore} then {framesAfter} wire frames as expected";
        }
        finally
        {
            try { plugin.Stop(); } catch { /* teardown */ }
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
    }

    /// <summary>A DAW whose sample rate can be moved while the plugin runs.</summary>
    private sealed class RateChangingHost : IAudioHost
    {
        public double SampleRate { get; set; } = 48000;
        public uint MaxAudioBufferSize => 512;
        public uint CurrentAudioBufferSize => 512;
        public EAudioBitsPerSample BitsPerSample => EAudioBitsPerSample.Bits64;
        public double BPM => 120;
        public long CurrentProjectSample => 0;
        public bool IsPlaying => false;
        public void ProcessAllEvents() { }
        public int ProcessEvents() => 0;
        public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset) { }
        public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset) { }
        public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset) { }
        public void BeginEdit(int parameter) { }
        public void PerformEdit(int parameter, double normalizedValue) { }
        public void EndEdit(int parameter) { }
        public void SetParameter(int parameter, double normalizedValue) { }
        public void Log(string message) { }
    }
}
