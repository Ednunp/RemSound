namespace RemSound.Core;

/// <summary>Which of your own send paths push to talk holds shut until you talk. The other path sends as normal - an ASIO
/// backing track playing, say, while a WASAPI microphone waits for the key.</summary>
public enum PushToTalkPath
{
    Wasapi = 0,
    Asio = 1,
    Both = 2,
}

/// <summary>What opens push to talk: a key (held, tapped or double-tapped), or your own sound reaching a level.</summary>
public enum PushToTalkTrigger
{
    Key = 0,
    Level = 1,
}

/// <summary>How far apart two presses of the push to talk key may be and still be a double tap (Ed, 2026-09-28: "slow
/// medium fast"). Slow is 0.9 seconds, the slowest Windows' own double-click setting allows; Medium is half a second,
/// Windows' default double-click time; Fast is 0.3 seconds, Android's standard double tap. A slower setting is for
/// anyone who finds pressing twice quickly hard (GNOME's help: "If you have difficulty pressing the mouse button
/// quickly, you should increase the timeout").</summary>
public enum PushToTalkDoubleTapSpeed
{
    Slow = 0,
    Medium = 1,
    Fast = 2,
}

/// <summary>
/// PUSH TO TALK (Ed, 2026-09-28). Kept in the profile, and off in every new one. With sending on and push to talk on,
/// nothing on the chosen path goes out until push to talk opens it: sending is the inner airlock, push to talk the outer
/// one, which opens and shuts at once. The stream itself keeps running and carries silence while shut, so opening costs
/// nothing but the ordinary delay. The key is a computer-wide shortcut (<see cref="AppConfig.PushToTalkHotkey"/>), not part
/// of this. The send-only service ignores all of it (Ed: "not part of the service full stop").
/// PLAN-push-to-talk-2026-09-28.md holds every decision.
/// </summary>
public sealed class PushToTalkSettings
{
    public const int MinLevelDb = -70;
    public const int MaxLevelDb = -10;
    public const int DefaultLevelDb = -40;

    public bool Enabled { get; set; }
    public PushToTalkPath Path { get; set; } = PushToTalkPath.Both;
    public PushToTalkTrigger Trigger { get; set; } = PushToTalkTrigger.Key;
    /// <summary>Key mode: sound goes out only while the key is held.</summary>
    public bool HoldToSpeak { get; set; } = true;
    /// <summary>Key mode: a double tap locks talking on, the next double tap releases it. On by default, with hold to speak
    /// (Ed, 2026-09-28: "both hold the key and double tap the key should be on by default").</summary>
    public bool DoubleTapToHold { get; set; } = true;

    /// <summary>Key mode, with double tap to hold: how far apart the two presses may be. Medium unless changed.</summary>
    public PushToTalkDoubleTapSpeed DoubleTapSpeed { get; set; } = PushToTalkDoubleTapSpeed.Medium;
    /// <summary>Sound-level mode: the peak level, in dB below full scale, that opens push to talk.</summary>
    public int LevelDb { get; set; } = DefaultLevelDb;
    /// <summary>Off: while not talking, the push to talk path is recorded as silence, as a listener hears it. On: the
    /// recording holds everything coming out of the send channels.</summary>
    public bool BypassForRecording { get; set; }
    /// <summary>Off: a DAW track sent through the plugin goes out only while talking. On: it goes out regardless.</summary>
    public bool BypassForPlugin { get; set; }

    public PushToTalkSettings Clone() => (PushToTalkSettings)MemberwiseClone();

    public bool SameAs(PushToTalkSettings other) =>
        Enabled == other.Enabled && Path == other.Path && Trigger == other.Trigger && HoldToSpeak == other.HoldToSpeak
        && DoubleTapToHold == other.DoubleTapToHold && DoubleTapSpeed == other.DoubleTapSpeed && LevelDb == other.LevelDb && BypassForRecording == other.BypassForRecording
        && BypassForPlugin == other.BypassForPlugin;

    /// <summary>The level as a linear peak, 0..1.</summary>
    public float LevelLinear => (float)Math.Pow(10, Math.Clamp(LevelDb, MinLevelDb, MaxLevelDb) / 20.0);

    /// <summary>The most milliseconds between two presses that still make a double tap.</summary>
    public static int DoubleTapMs(PushToTalkDoubleTapSpeed speed) => speed switch
    {
        PushToTalkDoubleTapSpeed.Slow => 900,
        PushToTalkDoubleTapSpeed.Fast => 300,
        _ => 500,
    };

    /// <summary>The speed as the list says it: "Slow, 0.9 seconds".</summary>
    public static string DoubleTapSpeedName(PushToTalkDoubleTapSpeed speed) => speed switch
    {
        PushToTalkDoubleTapSpeed.Slow => "Slow, 0.9 seconds",
        PushToTalkDoubleTapSpeed.Fast => "Fast, 0.3 seconds",
        _ => "Medium, 0.5 seconds",
    };

    public static string PathName(PushToTalkPath path) => path switch
    {
        PushToTalkPath.Wasapi => "WASAPI",
        PushToTalkPath.Asio => "ASIO",
        _ => "both WASAPI and ASIO",
    };

    /// <summary>One line for the log.</summary>
    public string Describe() => !Enabled
        ? "off"
        : $"on, {PathName(Path)}, "
          + (Trigger == PushToTalkTrigger.Key
              ? $"by key ({KeyBehaviour(HoldToSpeak, DoubleTapToHold)}"
                + (DoubleTapToHold ? $", double tap speed {DoubleTapSpeedName(DoubleTapSpeed).ToLowerInvariant()})" : ")")
              : $"by sound reaching {LevelDb} dB")
          + $", recording bypass {(BypassForRecording ? "on" : "off")}, plugin bypass {(BypassForPlugin ? "on" : "off")}";

    public static string KeyBehaviour(bool hold, bool doubleTap) => (hold, doubleTap) switch
    {
        (false, false) => "a tap turns talking on, the next tap off",
        (true, false) => "hold to speak",
        (false, true) => "double tap to hold",
        _ => "hold to speak, double tap to hold",
    };
}

/// <summary>
/// What the push to talk key means, press by press. Ed, 2026-09-28:
/// <list type="bullet">
/// <item>Hold to speak: talking while the key is held.</item>
/// <item>Double tap to hold: a double tap locks talking on; one tap releases it (Ed, 2026-09-29: "single tap should do it").</item>
/// <item>Both: holding talks while held, and a double tap locks it on until the next tap.</item>
/// <item>Neither: one tap turns talking on, the next tap turns it off.</item>
/// </list>
/// A double tap is two presses no further apart than the chosen speed allows (<see cref="PushToTalkSettings.DoubleTapMs"/>).
/// A key held down repeats; the repeats are not presses.
/// Times are whatever clock the caller keeps, in milliseconds - the keyboard hook's own, or the simulator's.
/// </summary>
public sealed class PushToTalkKeyState
{
    private bool hold = true;
    private bool doubleTap = true;
    private int doubleTapMs = PushToTalkSettings.DoubleTapMs(PushToTalkDoubleTapSpeed.Medium);
    private bool keyDown;
    private bool latched;
    private bool toggled;
    private long lastPressMs;
    private bool havePress;

    public bool Talking { get; private set; }

    /// <summary>Set the behaviour. Anything held or locked is let go: a change of rules starts from silence.</summary>
    public void Configure(bool holdToSpeak, bool doubleTapToHold, PushToTalkDoubleTapSpeed speed = PushToTalkDoubleTapSpeed.Medium)
    {
        hold = holdToSpeak;
        doubleTap = doubleTapToHold;
        doubleTapMs = PushToTalkSettings.DoubleTapMs(speed);
        Reset();
    }

    /// <summary>Everything let go - push to talk switched off, or its settings changed.</summary>
    public void Reset()
    {
        keyDown = false;
        latched = false;
        toggled = false;
        havePress = false;
        Talking = false;
    }

    /// <summary>The key went down. Returns true when talking changed.</summary>
    public bool Press(long ms)
    {
        if (keyDown) return false;   // a repeat while held
        keyDown = true;
        var isDouble = havePress && ms - lastPressMs <= doubleTapMs;
        // A double tap is used up, so a third quick press starts a new pair rather than counting twice.
        havePress = !isDouble;
        lastPressMs = ms;
        if (!hold && !doubleTap) toggled = !toggled;
        else if (doubleTap && latched)
        {
            // Locked on by a double tap: one tap lets it go, and that tap starts no new pair.
            latched = false;
            havePress = false;
        }
        else if (doubleTap && isDouble) latched = true;
        return Recompute();
    }

    /// <summary>The key came up. Returns true when talking changed.</summary>
    public bool Release(long ms)
    {
        if (!keyDown) return false;
        keyDown = false;
        return Recompute();
    }

    private bool Recompute()
    {
        var was = Talking;
        Talking = !hold && !doubleTap ? toggled : (hold && keyDown) || (doubleTap && latched);
        return Talking != was;
    }
}
