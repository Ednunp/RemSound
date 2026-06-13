using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Audible typing feedback. When enabled, every character typed into any edit field anywhere in
/// RemSound plays one of several soft key-click sounds (chosen at random), and password fields
/// additionally play a distinct "passkey" sound at the same instant - so a screen-reader user
/// knows they're typing, and knows when they're in a masked password field.
///
/// Built for speed and overlap: the click WAVs are decoded into memory once and rendered through a
/// single persistent mixer + output, so a click fires the instant a key is pressed and fast typing
/// simply layers clicks rather than cutting them off. The hook is a single application-wide message
/// filter watching for WM_CHAR; it never consumes the keystroke, so typing is unaffected. If the
/// click sounds are missing or the output device won't open, the whole thing stays silently inert.
/// </summary>
internal static class KeyClickService
{
    private const int WM_CHAR = 0x0102;

    /// <summary>Tag value a password entry field sets on itself (<c>control.Tag = PasswordFieldTag</c>)
    /// so the key-click hook layers in the distinct passkey sound. RemSound's password boxes are
    /// deliberately NOT PasswordChar-masked (a screen-reader user can't see the mask anyway), so
    /// there's no built-in flag to detect - they mark themselves with this tag instead.</summary>
    public const string PasswordFieldTag = "remsound-password-field";

    private static KeyClickPlayer? player;
    private static MessageFilter? filter;

    /// <summary>Live on/off, read by the message filter on every keystroke. Set from Preferences.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Preload the click sounds, open the output, and install the app-wide key hook. Call
    /// once on the GUI thread after the message loop's owner exists. Best-effort - any failure
    /// leaves the service inert (no clicks), never throwing into startup.</summary>
    public static void Initialize(bool enabled)
    {
        Enabled = enabled;
        try
        {
            player = new KeyClickPlayer(AppConfig.SoundsDirectory);
            filter = new MessageFilter();
            System.Windows.Forms.Application.AddMessageFilter(filter);
        }
        catch
        {
            player = null;
            filter = null;
        }
    }

    public static void Shutdown()
    {
        try { if (filter is not null) System.Windows.Forms.Application.RemoveMessageFilter(filter); } catch { /* ignore */ }
        try { player?.Dispose(); } catch { /* ignore */ }
        filter = null;
        player = null;
    }

    private static void OnChar(IntPtr hwnd)
    {
        if (!Enabled || player is null) return;
        // The WM_CHAR target window is the focused control. Click only when it's an edit field.
        if (System.Windows.Forms.Control.FromHandle(hwnd) is not System.Windows.Forms.TextBoxBase edit) return;
        // A password field is marked by its Tag (RemSound's boxes aren't PasswordChar-masked), with
        // a fallback to the standard masking flags for any conventionally-masked box.
        var isPassword = (edit.Tag as string) == PasswordFieldTag
            || (edit is System.Windows.Forms.TextBox tb && (tb.UseSystemPasswordChar || tb.PasswordChar != '\0'));
        player.PlayClick(isPassword);
    }

    private sealed class MessageFilter : System.Windows.Forms.IMessageFilter
    {
        public bool PreFilterMessage(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CHAR) OnChar(m.HWnd);
            return false; // never consume - the character must still reach the edit field
        }
    }

    /// <summary>A short sound decoded into memory once, at the mixer's format, ready to play
    /// instantly and as many times over as fast typing needs.</summary>
    private sealed class CachedSound
    {
        public float[] AudioData { get; }
        public WaveFormat WaveFormat { get; }

        public CachedSound(string path, WaveFormat target)
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (source.WaveFormat.SampleRate != target.SampleRate)
                source = new WdlResamplingSampleProvider(source, target.SampleRate);
            if (source.WaveFormat.Channels == 1 && target.Channels == 2)
                source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels == 2 && target.Channels == 1)
                source = new StereoToMonoSampleProvider(source);
            WaveFormat = source.WaveFormat;

            var data = new List<float>(target.SampleRate); // ~1s headroom; clicks are far shorter
            var buffer = new float[target.SampleRate * target.Channels];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++) data.Add(buffer[i]);
            }
            AudioData = data.ToArray();
        }
    }

    /// <summary>One-shot reader over a <see cref="CachedSound"/>; the mixer drops it when it ends.</summary>
    private sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound sound;
        private long position;
        public CachedSoundSampleProvider(CachedSound sound) => this.sound = sound;
        public WaveFormat WaveFormat => sound.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var available = sound.AudioData.Length - position;
            var n = (int)Math.Min(available, count);
            if (n > 0) Array.Copy(sound.AudioData, position, buffer, offset, n);
            position += n;
            return n;
        }
    }

    private sealed class KeyClickPlayer : IDisposable
    {
        private readonly WaveOutEvent output;
        private readonly MixingSampleProvider mixer;
        private readonly List<CachedSound> keyClips = new();
        private readonly CachedSound? passkeyClip;
        private readonly bool ready;

        public KeyClickPlayer(string soundsDir)
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            mixer = new MixingSampleProvider(format) { ReadFully = true };
            // A modest buffer keeps the click snappy without risking dropouts under load.
            output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
            try
            {
                for (var i = 1; i <= 8; i++) // discover "key 1.wav" upward; ships with 4 but don't hard-code
                {
                    var p = Path.Combine(soundsDir, $"key {i}.wav");
                    if (File.Exists(p)) keyClips.Add(new CachedSound(p, format));
                }
                var passkeyPath = Path.Combine(soundsDir, "passkey.wav");
                if (File.Exists(passkeyPath)) passkeyClip = new CachedSound(passkeyPath, format);

                if (keyClips.Count > 0)
                {
                    output.Init(mixer);
                    output.Play();
                    ready = true;
                }
            }
            catch { ready = false; }
        }

        public void PlayClick(bool isPassword)
        {
            if (!ready || keyClips.Count == 0) return;
            try
            {
                mixer.AddMixerInput(new CachedSoundSampleProvider(keyClips[Random.Shared.Next(keyClips.Count)]));
                if (isPassword && passkeyClip is not null)
                    mixer.AddMixerInput(new CachedSoundSampleProvider(passkeyClip));
            }
            catch { /* a key click must never disturb anything */ }
        }

        public void Dispose()
        {
            try { output.Stop(); } catch { /* ignore */ }
            try { output.Dispose(); } catch { /* ignore */ }
        }
    }
}
