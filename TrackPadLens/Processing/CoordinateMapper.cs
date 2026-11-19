using TrackPadLens.Models;

namespace TrackPadLens.Processing;

public readonly record struct LogicalBounds(int MinX, int MaxX, int MinY, int MaxY);

public readonly record struct LensRectDip(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public readonly record struct MappedScreenPoint(double ScreenXDip, double ScreenYDip, double NormalizedU, double NormalizedV);

/// <summary>
/// Converts raw HID touchpad coordinates to normalized [0,1] active-area coordinates and maps
/// them into the floating Lens rectangle using a single consistent internal coordinate space
/// (WPF device-independent units) with explicit DPI and multi-monitor virtual screen conversions.
/// </summary>
public sealed class CoordinateMapper
{
    /// <summary>
    /// Normalizes a raw HID X/Y sample into [0,1] applying calibration active-area crop and optional inversion.
    /// </summary>
    public static (double U, double V) Normalize(
        int rawX,
        int rawY,
        in LogicalBounds bounds,
        CalibrationProfile? calibration = null)
    {
        double spanX = bounds.MaxX - bounds.MinX;
        double spanY = bounds.MaxY - bounds.MinY;
        if (spanX <= 0 || spanY <= 0)
        {
            return (0.0, 0.0);
        }

        double u = (rawX - bounds.MinX) / spanX;
        double v = (rawY - bounds.MinY) / spanY;

        if (calibration != null)
        {
            double activeSpanX = Math.Max(0.05, calibration.ActiveRight - calibration.ActiveLeft);
            double activeSpanY = Math.Max(0.05, calibration.ActiveBottom - calibration.ActiveTop);
            u = (u - calibration.ActiveLeft) / activeSpanX;
            v = (v - calibration.ActiveTop) / activeSpanY;

            if (calibration.InvertX) u = 1.0 - u;
            if (calibration.InvertY) v = 1.0 - v;
        }

        u = Math.Clamp(u, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        return (u, v);
    }

    /// <summary>
    /// Maps raw HID coordinates directly onto the active Lens bounds in WPF DIP coordinates.
    /// </summary>
    public static MappedScreenPoint MapToLens(
        int rawX,
        int rawY,
        in LogicalBounds bounds,
        in LensRectDip lens,
        CalibrationProfile? calibration = null)
    {
        var (u, v) = Normalize(rawX, rawY, in bounds, calibration);
        double screenX = lens.Left + u * lens.Width;
        double screenY = lens.Top + v * lens.Height;
        return new MappedScreenPoint(screenX, screenY, u, v);
    }

    /// <summary>
    /// Converts a physical virtual-screen pixel coordinate (including negative secondary monitor coordinates)
    /// into overlay-local WPF DIP coordinates using the active monitor/overlay DPI scale.
    /// </summary>
    public static (double XDip, double YDip) PhysicalToDip(
        double physicalX,
        double physicalY,
        double dpiScaleX,
        double dpiScaleY)
    {
        double sx = dpiScaleX > 0.01 ? dpiScaleX : 1.0;
        double sy = dpiScaleY > 0.01 ? dpiScaleY : 1.0;
        return (physicalX / sx, physicalY / sy);
    }

    /// <summary>
    /// Converts WPF DIP coordinates back into physical virtual-screen pixels (e.g. for GDI capture).
    /// </summary>
    public static (int PhysicalX, int PhysicalY) DipToPhysical(
        double dipX,
        double dipY,
        double dpiScaleX,
        double dpiScaleY)
    {
        double sx = dpiScaleX > 0.01 ? dpiScaleX : 1.0;
        double sy = dpiScaleY > 0.01 ? dpiScaleY : 1.0;
        return ((int)Math.Round(dipX * sx), (int)Math.Round(dipY * sy));
    }

    /// <summary>
    /// Computes the floating Lens rectangle anchored to the external mouse cursor (in DIPs),
    /// supporting Centered, TopLeft, configurable offset, and multi-monitor virtual desktop clamping.
    /// </summary>
    public static LensRectDip ComputeLensRect(
        double cursorDipX,
        double cursorDipY,
        double lensWidthDip,
        double aspectRatio,
        AnchorMode anchorMode,
        double offsetX,
        double offsetY,
        in LensRectDip viewportBoundsDip)
    {
        double safeAspect = Math.Clamp(aspectRatio, 0.4, 4.0);
        double width = Math.Clamp(lensWidthDip, 100.0, Math.Max(100.0, viewportBoundsDip.Width * 0.90));
        double height = width / safeAspect;

        if (height > viewportBoundsDip.Height * 0.90)
        {
            height = Math.Max(60.0, viewportBoundsDip.Height * 0.90);
            width = height * safeAspect;
        }

        double left = anchorMode == AnchorMode.Centered
            ? cursorDipX - width * 0.5 + offsetX
            : cursorDipX + offsetX;
        double top = anchorMode == AnchorMode.Centered
            ? cursorDipY - height * 0.5 + offsetY
            : cursorDipY + offsetY;

        // Keep frame inside virtual screen bounds (works with negative secondary monitor coordinates)
        left = Math.Clamp(left, viewportBoundsDip.Left, Math.Max(viewportBoundsDip.Left, viewportBoundsDip.Right - width));
        top = Math.Clamp(top, viewportBoundsDip.Top, Math.Max(viewportBoundsDip.Top, viewportBoundsDip.Bottom - height));

        return new LensRectDip(left, top, width, height);
    }
}
