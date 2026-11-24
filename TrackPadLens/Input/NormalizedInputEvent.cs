namespace TrackPadLens.Input;

public enum InputPointerType
{
    PrecisionTouchpad,
    ExternalMouse,
    Touchscreen,
    DigitalStylus,
    DrawingTablet
}

/// <summary>
/// Shared normalized input packet spanning Precision Touchpad, external mouse,
/// touchscreen, Windows digital pen/stylus, and external drawing tablets.
/// Pressure and tilt remain null whenever hardware does not genuinely report them.
/// </summary>
public readonly record struct NormalizedInputEvent(
    InputPointerType PointerType,
    double X,
    double Y,
    bool IsInContact,
    double TimestampSeconds,
    string DeviceId,
    double? Pressure = null,
    double? TiltX = null,
    double? TiltY = null,
    int ContactId = 0);

public static class InputNormalizer
{
    public static NormalizedInputEvent FromStylusOrMouse(
        InputPointerType pointerType,
        double x,
        double y,
        bool isInContact,
        double timestampSeconds,
        string deviceId,
        double? pressure = null,
        double? tiltX = null,
        double? tiltY = null,
        int contactId = 0)
    {
        double? safePressure = pressure.HasValue && !double.IsNaN(pressure.Value)
            ? Math.Clamp(pressure.Value, 0.0, 1.0)
            : null;
        return new NormalizedInputEvent(
            pointerType,
            x,
            y,
            isInContact,
            timestampSeconds,
            deviceId,
            safePressure,
            tiltX,
            tiltY,
            contactId);
    }
}
