using TrackPadLens.Hardware;
using TrackPadLens.Models;

namespace TrackPadLens.Processing;

/// <summary>
/// Manages touchpad physical aspect ratio calculation, manual calibration overrides,
/// and aspect-preserving dynamic Lens scaling bounds (100px min .. 90% usable screen width).
/// </summary>
public sealed class CalibrationManager
{
    public const double MinLensWidthDip = 100.0;
    public const double MaxLensScreenFraction = 0.90;
    public const double WheelScaleFactorPerNotch = 1.08;

    public double ResolveAspectRatio(HardwareCapabilityReport? hardware, CalibrationProfile profile)
    {
        profile.Sanitize();

        double baseAspect;
        if (profile.UsePhysicalAspectRatio &&
            hardware != null &&
            hardware.HasReliablePhysicalDimensions &&
            hardware.PhysicalAspectRatio.HasValue)
        {
            baseAspect = hardware.PhysicalAspectRatio.Value;
        }
        else
        {
            baseAspect = profile.ManualAspectRatio;
        }

        // Adjust aspect ratio proportionally when the user crops the active touchpad region
        double activeW = Math.Max(0.15, profile.ActiveRight - profile.ActiveLeft);
        double activeH = Math.Max(0.15, profile.ActiveBottom - profile.ActiveTop);
        double effective = baseAspect * (activeW / activeH);
        return Math.Clamp(effective, 0.4, 4.0);
    }

    public double ScaleLensWidth(double currentWidthDip, int wheelDelta, double usableScreenWidthDip)
    {
        double maxWidth = Math.Max(MinLensWidthDip, usableScreenWidthDip * MaxLensScreenFraction);
        double notches = wheelDelta / 120.0;
        double factor = Math.Pow(WheelScaleFactorPerNotch, notches);
        return Math.Clamp(currentWidthDip * factor, MinLensWidthDip, maxWidth);
    }

    public double ComputeDisplayScaleMultiplier(double currentWidthDip, double baseWidthDip)
    {
        if (baseWidthDip <= 1.0) return 1.0;
        return Math.Round(currentWidthDip / baseWidthDip, 2);
    }
}
