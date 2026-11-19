namespace TrackPadLens.Models;

/// <summary>
/// Operational input mode determined strictly from verified HID hardware capabilities.
/// </summary>
public enum OperationalMode
{
    /// <summary>
    /// Mode A: Built-in Precision Touchpad exposes valid absolute HID contact reports
    /// and a verified per-device cursor isolation provider is active.
    /// </summary>
    NativeAbsoluteTouchpad,

    /// <summary>
    /// Mode B: Touchpad exposes valid absolute HID contact reports in user mode,
    /// without kernel-level per-device cursor suppression (Windows cursor may still move
    /// unless an optional signed HID filter driver is installed), OR fallback compatible input.
    /// </summary>
    CompatibleMode,

    /// <summary>
    /// Mode C: No Precision Touchpad exposing usable absolute coordinates was detected.
    /// Touchpad drawing is disabled rather than fabricating synthetic input.
    /// </summary>
    HardwareUnsupported
}

public enum AnchorMode
{
    Centered,
    TopLeft
}

public enum MonitorScope
{
    PrimaryOnly,
    AllMonitors
}

public enum DrawingTool
{
    Pen,
    Highlighter,
    Laser
}

public enum StrokeThicknessPreset
{
    Thin = 2,
    Medium = 4,
    Thick = 7
}
