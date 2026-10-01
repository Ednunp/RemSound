using System.Collections.Concurrent;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME WITHOUT A SERVER (Ed, 2026-09-29: "you can have metronome on your own machine without server but it
    /// needs a do not show me this message again dialogue telling you you're not connected to a server so no one else will be
    /// able to hear your metronome"; "it should not work on p2p"). Not connected through a server: the metronome still clicks
    /// for you, and starting it says nobody else will hear it until that is hidden; being the master metronome is refused,
    /// because the server is what keeps everyone's beat.</summary>
    private static string? MetronomeWithoutAServer()
    {
        var page = MainForm.BuildMetronomeNoServerMessage(out var tick);
        Check(page.Heading == "Not connected to a server" && (page.Text ?? "").Contains("no one else will be able to hear your metronome", StringComparison.Ordinal),
            $"the message must say nobody else will hear it ({page.Heading}: {page.Text})");
        Check(tick.Text == "Do not show me this message again" && !tick.Checked, "it must carry Ed's tick, unticked");
        Check(page.Buttons.Count == 1 && page.Buttons[0] == TaskDialogButton.OK, "and an OK button");

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-noserver-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        Check(AppConfig.Load().ShowMetronomeNoServerMessage, "it must be on until hidden");
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            var engine = form.MetronomeForTest;
            Check(!engine.OnServer, "premise: not connected through a server");

            // Leading needs a server.
            form.SetMetronomeMasterForTest(true);
            Check(engine.Role == MetronomeRole.Solo, "NO SERVER, NO MASTER: being the master metronome must be refused");
            Check(said.Any(l => l.Contains("being the master metronome needs a server", StringComparison.Ordinal)), "and the log must say why");
            Check(!form.MetronomeMasterItemForTest.Checked, "and the menu item must stay unticked");

            // It still clicks for you, and says so.
            form.MetronomeStartStopForTest("the menu");
            Check(engine.Playing && engine.CurrentSchedule is { Audible: true }, "ALONE: the metronome must still click for you");
            Check(said.Any(l => l.Contains("started with no server - it clicks for you alone", StringComparison.Ordinal)), "and the log must say it is heard by you alone");
            form.MetronomeStartStopForTest("the menu");

            form.MetronomeNoServerMessageAnswered(dontShowAgain: false);
            Check(AppConfig.Load().ShowMetronomeNoServerMessage, "OK without the tick must leave it on");
            form.MetronomeNoServerMessageAnswered(dontShowAgain: true);
            Check(!AppConfig.Load().ShowMetronomeNoServerMessage, "THE TICK: Do not show me this message again must hide it");
            Check(said.Any(l => l.Contains("the no-server message is hidden from now on", StringComparison.Ordinal)), "and the log must say so");
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the message says nobody else will hear it, with Ed's tick and OK; being master is refused and logged with no server; "
             + "the metronome still clicks for you alone, logged; the tick hides the message from then on";
    }
}
