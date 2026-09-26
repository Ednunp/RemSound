using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A small "Loading audio driver..." window shown on its OWN dedicated UI thread while the
/// main thread builds <see cref="MainForm"/>.
///
/// Why it exists: opening an ASIO driver is slow — 1-3 seconds on many drivers — and that
/// happens synchronously during MainForm construction. Without a splash the user sees
/// nothing, or a blank "Not Responding" window shell, for those seconds, and it looks hung.
///
/// Why a SEPARATE thread: the ASIO driver must keep being opened on the main UI thread —
/// that is the STA, message-pumping thread every ASIO / COM driver is most compatible with,
/// and moving the driver open off it risks breaking drivers we cannot test. But the main
/// thread is therefore blocked for the whole open, so a splash shown on it would itself be
/// frozen. Running the splash on its own STA thread with its own message loop lets it paint
/// and stay alive while the main thread is busy. No ASIO or driver code is touched — only
/// this cosmetic window runs on the side thread.
///
/// Shown only for profiles that actually use an ASIO driver; a WASAPI-only profile builds
/// the main window near-instantly and gets no splash (it would just flash).
/// </summary>
internal sealed class AsioLoadingSplash
{
    /// <summary>Default message used by <see cref="StartIfNeeded"/> on first launch.</summary>
    public const string DefaultMessage = "Loading audio driver, please wait...";

    private readonly Thread thread;
    private readonly ManualResetEventSlim shown = new(false);
    private readonly string message;
    private readonly bool quiet;
    private volatile Form? form;
    // Asked to go. Kept, because the ask can come before the window exists: the constructor waits for it at most two
    // seconds, and a quick start then dismissed a splash that had no window yet - the dismiss did nothing, and the splash
    // came up a moment later and stayed on top for the rest of the session (2026-09-25 sweep).
    private volatile bool dismissed;

    /// <summary>Test seam: run on the splash's own thread just before its window is shown.</summary>
    internal static Action? BeforeShowForTest;

    private AsioLoadingSplash(string message, bool quiet = false)
    {
        this.message = message;
        this.quiet = quiet;
        thread = new Thread(RunSplash)
        {
            IsBackground = true,
            Name = "RemSound audio-driver splash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Wait (briefly, capped) for the window to actually be on screen before the caller
        // begins the slow ASIO work — so the user sees the splash, not a blank moment. The
        // cap means a splash hiccup can never stall startup.
        shown.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Test seam: a splash with a message of the test's own, whatever the profile.</summary>
    internal static AsioLoadingSplash StartForTest(string message) => new(message, quiet: true);

    /// <summary>Test seam: the splash's thread has finished - its window closed, or never shown.</summary>
    internal bool EndedForTest(TimeSpan wait) => thread.Join(wait);

    /// <summary>Test seam: close a splash that did not close itself, so a failing check leaves nothing on screen.</summary>
    internal void ForceCloseForTest()
    {
        try { if (form is { IsHandleCreated: true, IsDisposed: false } f) f.BeginInvoke(f.Close); } catch { /* gone */ }
    }

    /// <summary>
    /// Starts the splash only when <paramref name="profile"/> has an ASIO driver selected —
    /// the only case where MainForm construction is slow. Returns null for WASAPI-only
    /// profiles. Dismiss the returned handle once the main window has been built.
    /// </summary>
    public static AsioLoadingSplash? StartIfNeeded(Profile? profile) =>
        StartIfAsioDriverName(profile?.AsioDriverName, DefaultMessage);

    /// <summary>
    /// Generic version of <see cref="StartIfNeeded"/>: starts the splash when an ASIO driver
    /// name is configured, with a caller-supplied message. Used by the system-resume handler
    /// to show "Reconnecting to audio driver…" while the audio backend is rebuilt after wake.
    /// </summary>
    public static AsioLoadingSplash? StartIfAsioDriverName(string? asioDriverName, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(asioDriverName)) return null;
        // Never in a --headless copy. The splash runs on a thread of its own, which the hook that keeps a headless copy's
        // windows out of sight does not watch: it came up on screen, always on top, after every wake (2026-09-25).
        if (Windowless.Hiding) return null;
        try
        {
            return new AsioLoadingSplash(message ?? DefaultMessage);
        }
        catch
        {
            // The splash is purely cosmetic — if it can't even be created, let the app
            // start silently rather than have a splash failure block launch.
            return null;
        }
    }

    private void RunSplash()
    {
        try
        {
            using var splash = new SplashWindow(quiet)
            {
                Text = "RemSound",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                ControlBox = false,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                ClientSize = new Size(380, 96),
                AccessibleName = "RemSound is starting",
            };
            if (quiet)
            {
                // The self-test's: see-through, off the screen, never in front and never taking the keyboard, so even a
                // failing check puts nothing in front of the person at the computer.
                splash.TopMost = false;
                splash.Opacity = 0;
                splash.StartPosition = FormStartPosition.Manual;
                splash.Location = new Point(-32000, -32000);
            }
            splash.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = message,
                AccessibleName = message.TrimEnd('.', ' '),
            });
            splash.Shown += (_, _) =>
            {
                shown.Set();
                if (dismissed) splash.Close();   // dismissed while it was being built
            };
            form = splash;
            BeforeShowForTest?.Invoke();
            if (dismissed) return;               // dismissed before it was ever shown
            Application.Run(splash);
        }
        catch
        {
            // Never let a splash failure escape on its own thread.
        }
        finally
        {
            // Unblock the constructor even if the window never managed to show.
            shown.Set();
        }
    }

    /// <summary>The splash's window; the self-test's never takes the keyboard.</summary>
    private sealed class SplashWindow(bool quiet) : Form
    {
        protected override bool ShowWithoutActivation => quiet;
    }

    /// <summary>
    /// Closes the splash. Call from the main thread once <see cref="MainForm"/> has been
    /// constructed. Fire-and-forget — the splash's own background thread tears itself down.
    /// </summary>
    public void Dismiss()
    {
        dismissed = true;
        try
        {
            shown.Set();
            var f = form;
            if (f is not null && f.IsHandleCreated && !f.IsDisposed)
            {
                f.BeginInvoke(() =>
                {
                    try { f.Close(); }
                    catch { /* already gone */ }
                });
            }
        }
        catch
        {
            // Cosmetic teardown — never throw into the startup path.
        }
    }
}
