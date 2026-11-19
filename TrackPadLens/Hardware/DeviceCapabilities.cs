namespace TrackPadLens.Hardware;

public enum DeviceCategory
{
    BuiltInPrecisionTouchpad,
    ExternalUsbMouse,
    ExternalBluetoothMouse,
    OtherPointingDevice
}

/// <summary>
/// Full hardware capability report populated strictly from inspected Windows HID descriptors.
/// </summary>
public sealed class HardwareCapabilityReport
{
    public string DeviceName { get; init; } = "None detected";
    public string DevicePath { get; init; } = string.Empty;
    public DeviceCategory Category { get; init; } = DeviceCategory.OtherPointingDevice;
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }

    public bool HasUsableAbsoluteCoordinates { get; init; }
    public int LogicalMinX { get; init; }
    public int LogicalMaxX { get; init; }
    public int LogicalMinY { get; init; }
    public int LogicalMaxY { get; init; }

    public double? PhysicalWidthMm { get; init; }
    public double? PhysicalHeightMm { get; init; }
    public bool HasReliablePhysicalDimensions { get; init; }

    public int SupportedContacts { get; init; }
    public bool SupportsContactId { get; init; }
    public bool SupportsContactCount { get; init; }
    public bool SupportsTipSwitch { get; init; }
    public bool SupportsContactArea { get; init; }
    public byte ReportId { get; init; }

    public bool SupportsDeviceSpecificCursorSuppression { get; set; }
    public string DiagnosticNotes { get; set; } = string.Empty;

    public double? PhysicalAspectRatio =>
        HasReliablePhysicalDimensions && PhysicalWidthMm > 0 && PhysicalHeightMm > 0
            ? PhysicalWidthMm.Value / PhysicalHeightMm.Value
            : null;
}

/// <summary>
/// Converts HID PhysicalMin/PhysicalMax + Unit + UnitExponent fields into millimeters
/// strictly following the USB HID 1.11 Specification §6.2.2.7 (Global Items).
/// </summary>
public static class HidUnitConverter
{
    /// <summary>
    /// Decodes a 4-bit two's-complement HID Unit Exponent nibble (0x0..0xF -> 0..+7, -8..-1).
    /// </summary>
    public static int DecodeUnitExponent(uint rawUnitExp)
    {
        int nibble = (int)(rawUnitExp & 0x0F);
        return nibble >= 8 ? nibble - 16 : nibble;
    }

    /// <summary>
    /// Converts a physical axis span (PhysicalMax - PhysicalMin) into millimeters.
    /// Returns null if the descriptor omits physical dimensions or specifies non-length units.
    /// </summary>
    public static double? TryCalculatePhysicalSpanMm(int physicalMin, int physicalMax, uint hidUnit, uint hidUnitExp)
    {
        if (physicalMax <= physicalMin)
        {
            return null;
        }

        // Per USB HID 1.11 §6.2.2.7:
        // Low nibble (bits 0..3): System
        //   0x1 = SI Linear (centimeters)
        //   0x3 = English Linear (inches)
        // Second nibble (bits 4..7): Length exponent (must be 1 for linear length)
        uint system = hidUnit & 0x0F;
        uint lengthNibble = (hidUnit >> 4) & 0x0F;

        if (lengthNibble != 1)
        {
            return null;
        }

        int exponent = DecodeUnitExponent(hidUnitExp);
        double span = (double)(physicalMax - physicalMin) * Math.Pow(10.0, exponent);

        return system switch
        {
            0x1 => span * 10.0,   // SI Linear base unit is cm -> multiply by 10 for mm
            0x3 => span * 25.4,   // English Linear base unit is inch -> multiply by 25.4 for mm
            _ => null
        };
    }
}
