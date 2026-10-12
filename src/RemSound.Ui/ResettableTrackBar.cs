namespace RemSound.App;

/// <summary>
/// A slider that Enter puts back where it starts (Ed, 2026-10-12, after the web listener page's sliders: "there are many other
/// sliders in remsound ... coudl enter do the same for them as well?"). With <see cref="ResetValue"/> set, Enter moves the
/// slider there as a move by hand: ValueChanged and Scroll both fire, so whatever listens for either hears it, and a screen
/// reader hears the new value as it does after an arrow key. Enter is the slider's own key then, so in a window whose OK is
/// Enter, Enter on a slider resets it and Enter anywhere else is still OK (Ed agreed, 2026-10-12).
/// </summary>
internal class ResettableTrackBar : TrackBar
{
    /// <summary>Where Enter puts the slider. Null leaves Enter to the window, as on any Windows slider.</summary>
    internal int? ResetValue;

    protected override bool IsInputKey(Keys keyData) => (keyData == Keys.Enter && ResetValue is not null) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyData == Keys.Enter && ResetValue is int start)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var to = Math.Clamp(start, Minimum, Maximum);
            if (Value != to)
            {
                Value = to;
                OnScroll(EventArgs.Empty);
            }
            return;
        }
        base.OnKeyDown(e);
    }
}
