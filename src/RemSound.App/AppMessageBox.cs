using System.Drawing;
using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// Every message box the app shows goes through here. Normally it IS <see cref="MessageBox"/>, argument for argument.
///
/// <para>In a <c>--headless</c> copy (<see cref="Windowless"/>) a Windows message box would still make its sound — the
/// system "ding" that goes with its icon — however well the box itself is hidden, and Ed, 2026-09-24: "if you're
/// running tests, I don't want to hear those sounds ... it should be silent and invisible." So while headless the
/// question is asked in a plain window of our own instead: same words, same buttons, same answer handed back, no icon
/// and no sound. The control channel lists it with its text and answers, and <c>answer</c> presses one.</para>
/// </summary>
internal static class AppMessageBox
{
    public static DialogResult Show(string text) => Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => Show(null, text, caption, buttons, icon, defaultButton);
    public static DialogResult Show(IWin32Window? owner, string text) => Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(IWin32Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
    {
        if (!Windowless.Hiding)
            return owner is null
                ? MessageBox.Show(text, caption, buttons, icon, defaultButton)
                : MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
        RemSoundLog.Current?.Event($"headless: a question is waiting, silently - \"{caption}\": {text.Replace('\n', ' ').Replace("\r", "")}");
        using var form = new HeadlessQuestionForm(text, caption, buttons, defaultButton);
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }
}

/// <summary>
/// Every task dialog the app shows goes through here, as every message box goes through <see cref="AppMessageBox"/>.
/// Normally it IS <see cref="TaskDialog"/>. While headless it is asked in <see cref="HeadlessQuestionForm"/>: a task dialog
/// makes the sound of its icon however hidden it is, and the control channel could not read or press one - its words and
/// buttons are drawn, not windows - so a headless copy that showed one stopped taking commands (found 2026-09-25).
/// </summary>
internal static class AppTaskDialog
{
    public static TaskDialogButton ShowDialog(IWin32Window? owner, TaskDialogPage page)
    {
        if (!Windowless.Hiding)
            return owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        var buttons = page.Buttons.Count > 0 ? page.Buttons.ToList() : [TaskDialogButton.OK];
        var text = string.Join("\n\n", new[] { page.Heading, page.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var caption = string.IsNullOrEmpty(page.Caption) ? "RemSound" : page.Caption;
        RemSoundLog.Current?.Event($"headless: a question is waiting, silently - \"{caption}\": {text.Replace('\n', ' ').Replace("\r", "")}");
        using var form = new HeadlessQuestionForm(text, caption, buttons.Select(LabelOf).ToList(), page.AllowCancel);
        _ = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        if (form.ChosenIndex >= 0) return buttons[form.ChosenIndex];
        // Closed without an answer (Escape): what a task dialog gives for that.
        return buttons.FirstOrDefault(b => b == TaskDialogButton.Cancel) ?? TaskDialogButton.Cancel;
    }

    /// <summary>A button's words: its own text, or for one of Windows' standard buttons the word Windows shows on it.</summary>
    internal static string LabelOf(TaskDialogButton b)
    {
        if (!string.IsNullOrEmpty(b.Text)) return b.Text;
        if (b == TaskDialogButton.OK) return "&OK";
        if (b == TaskDialogButton.Cancel) return "&Cancel";
        if (b == TaskDialogButton.Yes) return "&Yes";
        if (b == TaskDialogButton.No) return "&No";
        if (b == TaskDialogButton.Abort) return "&Abort";
        if (b == TaskDialogButton.Retry) return "&Retry";
        if (b == TaskDialogButton.Ignore) return "&Ignore";
        if (b == TaskDialogButton.TryAgain) return "&Try Again";
        if (b == TaskDialogButton.Continue) return "C&ontinue";
        if (b == TaskDialogButton.Close) return "&Close";
        if (b == TaskDialogButton.Help) return "&Help";
        return "OK";
    }
}

/// <summary>
/// The silent stand-in for a message box in a headless copy. Only ever shown while <see cref="Windowless.Hiding"/>,
/// so it is never in front of a person: once the window is handed over, <see cref="AppMessageBox"/> uses the real
/// thing again. Plain WinForms buttons for that reason, not the house controls a screen reader needs.
/// </summary>
internal sealed class HeadlessQuestionForm : Form
{
    /// <summary>The words of the question, as the control channel reads them out.</summary>
    public string Question { get; }

    /// <summary>The answers on offer, in order.</summary>
    public IReadOnlyList<string> Answers { get; private set; } = [];

    /// <summary>Which answer was chosen, by its place in <see cref="Answers"/>; -1 when none was (closed with Escape).</summary>
    public int ChosenIndex { get; private set; } = -1;

    public HeadlessQuestionForm(string text, string caption, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
    {
        Question = text;
        var made = Build(text, caption, ButtonsFor(buttons));

        var first = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            MessageBoxDefaultButton.Button4 => 3,
            _ => 0,
        };
        AcceptButton = made[Math.Min(first, made.Count - 1)];
        // Escape answers as a message box's does: Cancel if there is one, OK when it is the only button, else nothing.
        CancelButton = made.FirstOrDefault(b => b.DialogResult == DialogResult.Cancel) ?? (made.Count == 1 ? made[0] : null);
    }

    /// <summary>A task dialog's question: its own buttons, whatever they say, answered by their place
    /// (<see cref="ChosenIndex"/>). Escape closes it with no answer when the task dialog allowed cancelling.</summary>
    public HeadlessQuestionForm(string text, string caption, IReadOnlyList<string> buttonLabels, bool escapeCancels)
    {
        Question = text;
        var made = Build(text, caption, buttonLabels.Select(l => (l, DialogResult.OK)).ToList());
        AcceptButton = made[0];
        if (escapeCancels)
        {
            // An Escape-only button, out of sight: pressing Escape closes the window with no answer chosen.
            var escape = new Button { DialogResult = DialogResult.Cancel, Size = new Size(0, 0), TabStop = false };
            Controls.Add(escape);
            CancelButton = escape;
        }
    }

    /// <summary>The window, its words and its buttons. Each button notes its place when pressed.</summary>
    private List<Button> Build(string text, string caption, IReadOnlyList<(string Label, DialogResult Result)> buttons)
    {
        Text = string.IsNullOrEmpty(caption) ? "RemSound" : caption;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = Windowless.OffScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };
        layout.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(480, 0), TabIndex = 0 });
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        var made = new List<Button>();
        foreach (var (label, result) in buttons)
        {
            var b = new Button { Text = label, DialogResult = result, AutoSize = true, TabIndex = made.Count + 1 };
            var index = made.Count;
            b.Click += (_, _) => ChosenIndex = index;
            made.Add(b);
            row.Controls.Add(b);
        }
        layout.Controls.Add(row);
        Controls.Add(layout);
        Answers = made.Select(b => Windowless.Clean(b.Text)).ToList();
        return made;
    }

    /// <summary>The buttons a message box of this kind has, with the answers they give, in Windows' own order.</summary>
    internal static IReadOnlyList<(string Label, DialogResult Result)> ButtonsFor(MessageBoxButtons buttons) => buttons switch
    {
        MessageBoxButtons.OKCancel => [("&OK", DialogResult.OK), ("&Cancel", DialogResult.Cancel)],
        MessageBoxButtons.AbortRetryIgnore => [("&Abort", DialogResult.Abort), ("&Retry", DialogResult.Retry), ("&Ignore", DialogResult.Ignore)],
        MessageBoxButtons.YesNoCancel => [("&Yes", DialogResult.Yes), ("&No", DialogResult.No), ("&Cancel", DialogResult.Cancel)],
        MessageBoxButtons.YesNo => [("&Yes", DialogResult.Yes), ("&No", DialogResult.No)],
        MessageBoxButtons.RetryCancel => [("&Retry", DialogResult.Retry), ("&Cancel", DialogResult.Cancel)],
        MessageBoxButtons.CancelTryContinue => [("&Cancel", DialogResult.Cancel), ("&Try Again", DialogResult.TryAgain), ("C&ontinue", DialogResult.Continue)],
        _ => [("&OK", DialogResult.OK)],
    };
}
