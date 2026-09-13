namespace RemSound.App;

/// <summary>
/// Where a user-driven control change gets recorded, for windows that do not own the log.
///
/// <para><b>Why this exists.</b> Ed, 2026-08-24: "we have spent months and months building logs on
/// top of logs on top of logs for every function. but the thing is, we've never tested those logs."
/// Testing them found that ten of the sixteen main-window controls that change something logged
/// nothing at all. The dialogs were worse: they have no logger at all, so every preference, every
/// recording setting and every service option a user changed left no trace whatsoever.</para>
///
/// <para><b>Why a sink rather than a constructor parameter.</b> Sixteen dialogs are built from ten
/// different places, several through static <c>Build</c> seams that return a bare
/// <see cref="System.Windows.Forms.Form"/>. Threading a logger through all of them would touch every
/// call site and every test that constructs one, to deliver a value that is the same object every
/// time: there is one app, one log and one user. So the dialogs call <see cref="Record"/> and the
/// main window decides what that means.</para>
///
/// <para><b>The configuration stamp comes free.</b> MainForm points this at its own
/// <c>LogUiChange</c>, which stamps every line with the audio configuration it happened in — so a
/// dialog line carries "[ASIO only]" without any dialog having to know that configurations exist.
/// <c>AuditDialogLoggingIsStamped</c> proves that wiring rather than assuming it.</para>
///
/// <para>Null until a main window sets it, so a dialog opened by a test — or by the send-only
/// service, which has no main window — records nothing and throws nothing.</para>
/// </summary>
internal static class UiChangeLog
{
    /// <summary>Set by MainForm to its own configuration-stamping logger; swapped by the gate to
    /// capture what a control says while it is being driven.</summary>
    internal static Action<string, string>? Sink;

    /// <summary>Record a control the user just changed. <paramref name="what"/> names the setting in
    /// the words the user would use, <paramref name="value"/> is what it became.</summary>
    internal static void Record(string what, string value) => Sink?.Invoke(what, value);
}
