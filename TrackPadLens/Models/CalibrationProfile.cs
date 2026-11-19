namespace TrackPadLens.Models;

/// <summary>
/// Normalized active-area crop and aspect-ratio calibration profile for the touchpad.
/// </summary>
public sealed class CalibrationProfile
{
    /// <summary>Normalized left edge of the active writing area [0.0, 0.9].</summary>
    public double ActiveLeft { get; set; } = 0.0;

    /// <summary>Normalized top edge of the active writing area [0.0, 0.9].</summary>
    public double ActiveTop { get; set; } = 0.0;

    /// <summary>Normalized right edge of the active writing area [0.1, 1.0].</summary>
    public double ActiveRight { get; set; } = 1.0;

    /// <summary>Normalized bottom edge of the active writing area [0.1, 1.0].</summary>
    public double ActiveBottom { get; set; } = 1.0;

    /// <summary>When true, uses PhysicalWidthMm / PhysicalHeightMm whenever valid HID physical dimensions exist.</summary>
    public bool UsePhysicalAspectRatio { get; set; } = true;

    /// <summary>Fallback aspect ratio (Width / Height) used when physical dimensions are unavailable or overridden.</summary>
    public double ManualAspectRatio { get; set; } = 1.6;

    public bool InvertX { get; set; } = false;
    public bool InvertY { get; set; } = false;

    public void Sanitize()
    {
        ActiveLeft = Math.Clamp(ActiveLeft, 0.0, 0.85);
        ActiveTop = Math.Clamp(ActiveTop, 0.0, 0.85);
        ActiveRight = Math.Clamp(ActiveRight, ActiveLeft + 0.15, 1.0);
        ActiveBottom = Math.Clamp(ActiveBottom, ActiveTop + 0.15, 1.0);
        ManualAspectRatio = Math.Clamp(ManualAspectRatio, 0.5, 4.0);
    }
}
