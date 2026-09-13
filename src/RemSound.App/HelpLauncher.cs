using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// Opens the bundled <c>readme.html</c> manual in the user's default browser. Wired to F1
/// app-wide via <see cref="HelpKeyMessageFilter"/>, which is installed once at startup and
/// catches F1 (without modifiers) before the message reaches any control. Works in every
/// modal dialog and on the very first form the user sees (the profile picker), because the
/// filter is registered before the first <c>ShowDialog</c>/<c>Application.Run</c> call.
///
/// File location: <c>&lt;exe&gt;\readme.html</c> (resolved via <see cref="AppContext.BaseDirectory"/>).
/// The .csproj copies it from the project root via a Content/Link rule so a fresh
/// <c>dotnet publish</c> always lands a current copy next to the executable.
/// </summary>
internal static class HelpLauncher
{
    /// <summary>Open the manual via Windows' shell association (default browser). Shows a
    /// MessageBox if the file is missing or shell-execute fails — better to surface an
    /// explanation than silently swallow the F1.</summary>
    public static void OpenManual()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "readme.html");
        if (!File.Exists(path))
        {
            MessageBox.Show(
                $"Manual not found at:\n\n{path}\n\nThe readme.html file should sit next to RemSound.exe. Unzipping RemSound again, or reinstalling it, will put it back.",
                "Manual not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        try
        {
            // UseShellExecute=true is the load-bearing flag — it lets the OS pick the .html
            // handler (Edge / Chrome / Firefox / whatever the user defaulted). Without it
            // Process.Start would treat the .html as an executable and fail.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the manual:\n\n{ex.Message}",
                "Manual open failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>Install the F1-catches-help message filter on the current thread's message
    /// loop. Call once from <c>Program.Main</c> before any form is shown. Idempotent — calling
    /// it twice would register two filters which is wasteful but not harmful.</summary>
    public static void Install()
    {
        Application.AddMessageFilter(new HelpKeyMessageFilter());
    }
}

/// <summary>
/// Catches F1 keypresses anywhere in the application before they reach the focused control.
/// Modifier-aware: bare F1 only — Ctrl+F1, Shift+F1, Alt+F1 fall through unchanged so we
/// don't steal future combos. Single-instance state is fine because the filter chain is
/// per-thread and RemSound is a single-threaded WinForms app.
/// </summary>
internal sealed class HelpKeyMessageFilter : IMessageFilter
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F1 = 0x70;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;
        if (m.WParam.ToInt32() != VK_F1) return false;
        // Bare F1 only — modifier combos are passed through. Lets a future "Shift+F1" do
        // something else (context help, etc.) without colliding with us.
        if ((Control.ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt)) != Keys.None) return false;
        HelpLauncher.OpenManual();
        return true; // consumed — no further dispatch
    }
}
