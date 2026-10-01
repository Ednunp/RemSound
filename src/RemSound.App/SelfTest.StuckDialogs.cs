namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>How long a dialog may sit open, unanswered, before the gate closes it and fails the step. Far longer than
    /// any step keeps a dialog open on purpose (the longest, a Preferences tab driven control by control, is a few
    /// seconds).</summary>
    internal static TimeSpan StuckDialogLimit = TimeSpan.FromSeconds(60);

    /// <summary>A DIALOG NOBODY CAN ANSWER FAILS THE STEP, IT NEVER HANGS THE RUN (2026-09-28). On the hidden desktop a
    /// dialog the step did not expect - "set a password to stream", a question - waits for a person who is not there,
    /// and the run hung until the outer script's timeout, with nothing said about why. Found when a new check ticked
    /// "Send my audio" on a profile with no password. While a step runs, anything shown with ShowDialog that stays open
    /// past <see cref="StuckDialogLimit"/> is cancelled and named, and the step fails. The look runs on the step's own
    /// thread, from inside the dialog's message loop - which is exactly the loop a stuck dialog keeps pumping.</summary>
    private sealed class StuckDialogWatch : IDisposable
    {
        private readonly Control marshal = new();
        private readonly System.Threading.Timer timer;
        private readonly Dictionary<Form, DateTime> firstSeen = [];
        internal string? Closed;

        internal StuckDialogWatch()
        {
            _ = marshal.Handle;
            timer = new System.Threading.Timer(_ =>
            {
                try { marshal.BeginInvoke(Look); } catch { /* the step has ended */ }
            }, null, 1000, 1000);
        }

        private void Look()
        {
            var now = DateTime.UtcNow;
            foreach (var form in Application.OpenForms.Cast<Form>().ToList())
            {
                if (!form.Modal) continue;
                if (!firstSeen.TryGetValue(form, out var since)) { firstSeen[form] = now; continue; }
                if (now - since < StuckDialogLimit) continue;
                Closed ??= $"\"{form.Text}\" ({form.GetType().Name})";
                void Cancel() { form.DialogResult = DialogResult.Cancel; form.Close(); }
                try { if (form.InvokeRequired) form.BeginInvoke(Cancel); else Cancel(); } catch { /* gone */ }
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            marshal.Dispose();
        }
    }
}
