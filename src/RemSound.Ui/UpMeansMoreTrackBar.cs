namespace RemSound.App;

/// <summary>
/// A slider on which Up means more (Ed, 2026-10-02, on the metronome's volume: "right now down arrow makes it go louder which
/// is not logical"). The Windows slider moves its value up on Down and Page Down, as it does on Right. Here Up and Page Up are
/// handed to it as Right and Page Down, and Down and Page Down as Left and Page Up, before it sees them - so Windows still
/// moves the slider itself, and a screen reader hears the change exactly as it does on any slider. Left, Right, Home and End
/// are left as they are.
/// </summary>
internal class UpMeansMoreTrackBar : ResettableTrackBar
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int VK_PRIOR = 0x21;   // Page Up
    private const int VK_NEXT = 0x22;    // Page Down
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;

    /// <summary>The key a pressed key is handed on as: Up as Right, Down as Left, Page Up and Page Down the other way round.</summary>
    internal static int Swapped(int key) => key switch
    {
        VK_UP => VK_RIGHT,
        VK_DOWN => VK_LEFT,
        VK_PRIOR => VK_NEXT,
        VK_NEXT => VK_PRIOR,
        _ => key,
    };

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_KEYUP && Orientation == Orientation.Horizontal)
            m.WParam = (IntPtr)Swapped((int)m.WParam);
        base.WndProc(ref m);
    }
}
