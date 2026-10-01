namespace RemSound.Core;

/// <summary>Where a window was and how big, when it closed: its normal bounds (the size it goes back to when it is not
/// maximised) and whether it was maximised. Kept in <see cref="AppConfig.MainWindowPlacement"/>.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public override string ToString() => $"{Width} x {Height} at {X}, {Y}{(Maximized ? ", maximised" : "")}";
}
