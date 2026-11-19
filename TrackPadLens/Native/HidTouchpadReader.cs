using System.Runtime.InteropServices;
using TrackPadLens.Hardware;

namespace TrackPadLens.Native;

/// <summary>
/// Zero-allocation value struct representing a single finger contact parsed from a Precision Touchpad HID report.
/// </summary>
public struct TouchContact
{
    public int ContactId;
    public bool TipSwitch;
    public bool Confidence;
    public int RawX;
    public int RawY;
    public int Width;
    public int Height;
    public bool HasWidth;
    public bool HasHeight;
}

/// <summary>
/// Descriptor metadata for a single contact link collection inside a multi-contact HID report.
/// </summary>
public sealed class ContactSlotDescriptor
{
    public ushort LinkCollection { get; init; }
    public byte ReportId { get; init; }
    public bool HasX { get; set; }
    public bool HasY { get; set; }
    public bool HasContactId { get; set; }
    public bool HasTipSwitch { get; set; }
    public bool HasConfidence { get; set; }
    public bool HasWidth { get; set; }
    public bool HasHeight { get; set; }

    public int LogicalMinX { get; set; }
    public int LogicalMaxX { get; set; }
    public int LogicalMinY { get; set; }
    public int LogicalMaxY { get; set; }

    public int PhysicalMinX { get; set; }
    public int PhysicalMaxX { get; set; }
    public uint UnitsX { get; set; }
    public uint UnitsExpX { get; set; }

    public int PhysicalMinY { get; set; }
    public int PhysicalMaxY { get; set; }
    public uint UnitsY { get; set; }
    public uint UnitsExpY { get; set; }
}

/// <summary>
/// Parsed HID descriptor layout for a Precision Touchpad.
/// </summary>
public sealed class TouchpadDescriptorLayout
{
    public byte ReportId { get; init; }
    public ushort InputReportByteLength { get; init; }
    public bool SupportsContactCount { get; init; }
    public ushort ContactCountLinkCollection { get; init; }
    public bool SupportsScanTime { get; init; }
    public ushort ScanTimeLinkCollection { get; init; }
    public IReadOnlyList<ContactSlotDescriptor> ContactSlots { get; init; } = Array.Empty<ContactSlotDescriptor>();

    public bool HasUsableAbsoluteCoordinates { get; init; }
    public bool SupportsContactId { get; init; }
    public bool SupportsTipSwitch { get; init; }
    public bool SupportsContactArea { get; init; }

    public int LogicalMinX { get; init; }
    public int LogicalMaxX { get; init; }
    public int LogicalMinY { get; init; }
    public int LogicalMaxY { get; init; }

    public double? PhysicalWidthMm { get; init; }
    public double? PhysicalHeightMm { get; init; }
    public bool HasReliablePhysicalDimensions { get; init; }
    public string DiagnosticSummary { get; init; } = string.Empty;
}

/// <summary>
/// Inspects HID preparsed descriptors and parses incoming Precision Touchpad raw reports
/// into pre-allocated contact buffers with zero per-packet managed heap allocations.
/// </summary>
public sealed class HidTouchpadReader
{
    private readonly ushort[] _buttonUsageScratch = new ushort[16];

    public static TouchpadDescriptorLayout ParsePreparsedData(byte[] preparsedBytes)
    {
        GCHandle handle = GCHandle.Alloc(preparsedBytes, GCHandleType.Pinned);
        try
        {
            return ParsePreparsedPointer(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    public static TouchpadDescriptorLayout ParsePreparsedPointer(IntPtr preparsedPtr)
    {
        if (Win32Interop.HidP_GetCaps(preparsedPtr, out var caps) != Win32Interop.HIDP_STATUS_SUCCESS)
        {
            return new TouchpadDescriptorLayout
            {
                HasUsableAbsoluteCoordinates = false,
                DiagnosticSummary = "HidP_GetCaps failed on preparsed descriptor."
            };
        }

        var valueCaps = Array.Empty<Win32Interop.HIDP_VALUE_CAPS>();
        if (caps.NumberInputValueCaps > 0)
        {
            ushort count = caps.NumberInputValueCaps;
            valueCaps = new Win32Interop.HIDP_VALUE_CAPS[count];
            if (Win32Interop.HidP_GetValueCaps(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    valueCaps,
                    ref count,
                    preparsedPtr) == Win32Interop.HIDP_STATUS_SUCCESS && count < valueCaps.Length)
            {
                Array.Resize(ref valueCaps, count);
            }
        }

        var buttonCaps = Array.Empty<Win32Interop.HIDP_BUTTON_CAPS>();
        if (caps.NumberInputButtonCaps > 0)
        {
            ushort count = caps.NumberInputButtonCaps;
            buttonCaps = new Win32Interop.HIDP_BUTTON_CAPS[count];
            if (Win32Interop.HidP_GetButtonCaps(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    buttonCaps,
                    ref count,
                    preparsedPtr) == Win32Interop.HIDP_STATUS_SUCCESS && count < buttonCaps.Length)
            {
                Array.Resize(ref buttonCaps, count);
            }
        }

        return BuildLayoutFromCaps(caps.InputReportByteLength, valueCaps, buttonCaps);
    }

    /// <summary>
    /// Pure managed descriptor analyzer; directly unit-testable without physical hardware.
    /// </summary>
    public static TouchpadDescriptorLayout BuildLayoutFromCaps(
        ushort inputReportByteLength,
        ReadOnlySpan<Win32Interop.HIDP_VALUE_CAPS> valueCaps,
        ReadOnlySpan<Win32Interop.HIDP_BUTTON_CAPS> buttonCaps)
    {
        var slotsByCollection = new Dictionary<ushort, ContactSlotDescriptor>();
        bool hasContactCount = false;
        ushort contactCountCollection = 0;
        bool hasScanTime = false;
        ushort scanTimeCollection = 0;
        byte detectedReportId = 0;

        ContactSlotDescriptor GetOrCreateSlot(ushort linkCollection, byte reportId)
        {
            if (!slotsByCollection.TryGetValue(linkCollection, out var slot))
            {
                slot = new ContactSlotDescriptor
                {
                    LinkCollection = linkCollection,
                    ReportId = reportId
                };
                slotsByCollection[linkCollection] = slot;
            }
            return slot;
        }

        foreach (ref readonly var vc in valueCaps)
        {
            if (vc.IsRange != 0)
            {
                continue;
            }

            if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                vc.Usage == Win32Interop.HID_USAGE_DIGITIZER_CONTACT_COUNT)
            {
                hasContactCount = true;
                contactCountCollection = vc.LinkCollection;
                if (detectedReportId == 0) detectedReportId = vc.ReportID;
                continue;
            }

            if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                vc.Usage == Win32Interop.HID_USAGE_DIGITIZER_SCAN_TIME)
            {
                hasScanTime = true;
                scanTimeCollection = vc.LinkCollection;
                continue;
            }

            if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_GENERIC &&
                vc.Usage == Win32Interop.HID_USAGE_GENERIC_X &&
                vc.IsAbsolute != 0)
            {
                var slot = GetOrCreateSlot(vc.LinkCollection, vc.ReportID);
                slot.HasX = true;
                slot.LogicalMinX = vc.LogicalMin;
                slot.LogicalMaxX = vc.LogicalMax;
                slot.PhysicalMinX = vc.PhysicalMin;
                slot.PhysicalMaxX = vc.PhysicalMax;
                slot.UnitsX = vc.Units;
                slot.UnitsExpX = vc.UnitsExp;
                if (detectedReportId == 0) detectedReportId = vc.ReportID;
            }
            else if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_GENERIC &&
                     vc.Usage == Win32Interop.HID_USAGE_GENERIC_Y &&
                     vc.IsAbsolute != 0)
            {
                var slot = GetOrCreateSlot(vc.LinkCollection, vc.ReportID);
                slot.HasY = true;
                slot.LogicalMinY = vc.LogicalMin;
                slot.LogicalMaxY = vc.LogicalMax;
                slot.PhysicalMinY = vc.PhysicalMin;
                slot.PhysicalMaxY = vc.PhysicalMax;
                slot.UnitsY = vc.Units;
                slot.UnitsExpY = vc.UnitsExp;
                if (detectedReportId == 0) detectedReportId = vc.ReportID;
            }
            else if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                     vc.Usage == Win32Interop.HID_USAGE_DIGITIZER_CONTACT_ID)
            {
                var slot = GetOrCreateSlot(vc.LinkCollection, vc.ReportID);
                slot.HasContactId = true;
            }
            else if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                     vc.Usage == Win32Interop.HID_USAGE_DIGITIZER_WIDTH)
            {
                var slot = GetOrCreateSlot(vc.LinkCollection, vc.ReportID);
                slot.HasWidth = true;
            }
            else if (vc.UsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                     vc.Usage == Win32Interop.HID_USAGE_DIGITIZER_HEIGHT)
            {
                var slot = GetOrCreateSlot(vc.LinkCollection, vc.ReportID);
                slot.HasHeight = true;
            }
        }

        foreach (ref readonly var bc in buttonCaps)
        {
            if (bc.UsagePage != Win32Interop.HID_USAGE_PAGE_DIGITIZER) continue;

            bool containsTip = bc.IsRange != 0
                ? (Win32Interop.HID_USAGE_DIGITIZER_TIP_SWITCH >= bc.UsageMin && Win32Interop.HID_USAGE_DIGITIZER_TIP_SWITCH <= bc.UsageMax)
                : bc.Usage == Win32Interop.HID_USAGE_DIGITIZER_TIP_SWITCH;

            bool containsConfidence = bc.IsRange != 0
                ? (Win32Interop.HID_USAGE_DIGITIZER_CONFIDENCE >= bc.UsageMin && Win32Interop.HID_USAGE_DIGITIZER_CONFIDENCE <= bc.UsageMax)
                : bc.Usage == Win32Interop.HID_USAGE_DIGITIZER_CONFIDENCE;

            if (containsTip || containsConfidence)
            {
                var slot = GetOrCreateSlot(bc.LinkCollection, bc.ReportID);
                if (containsTip) slot.HasTipSwitch = true;
                if (containsConfidence) slot.HasConfidence = true;
            }
        }

        // Only keep slots that expose valid absolute X and Y with non-degenerate logical range
        var validSlots = slotsByCollection.Values
            .Where(s => s.HasX && s.HasY && s.LogicalMaxX > s.LogicalMinX && s.LogicalMaxY > s.LogicalMinY)
            .OrderBy(s => s.LinkCollection)
            .ToList();

        if (validSlots.Count == 0)
        {
            return new TouchpadDescriptorLayout
            {
                ReportId = detectedReportId,
                InputReportByteLength = inputReportByteLength,
                SupportsContactCount = hasContactCount,
                ContactCountLinkCollection = contactCountCollection,
                SupportsScanTime = hasScanTime,
                ScanTimeLinkCollection = scanTimeCollection,
                HasUsableAbsoluteCoordinates = false,
                DiagnosticSummary = "HID TouchPad descriptor contains no valid absolute X/Y contact collections with non-zero logical span."
            };
        }

        var primary = validSlots[0];
        double? physWidthMm = HidUnitConverter.TryCalculatePhysicalSpanMm(
            primary.PhysicalMinX, primary.PhysicalMaxX, primary.UnitsX, primary.UnitsExpX);
        double? physHeightMm = HidUnitConverter.TryCalculatePhysicalSpanMm(
            primary.PhysicalMinY, primary.PhysicalMaxY, primary.UnitsY, primary.UnitsExpY);

        bool reliablePhysical = physWidthMm.HasValue && physHeightMm.HasValue &&
                                physWidthMm.Value >= 20.0 && physWidthMm.Value <= 400.0 &&
                                physHeightMm.Value >= 15.0 && physHeightMm.Value <= 300.0;

        bool supportsArea = validSlots.Any(s => s.HasWidth || s.HasHeight);
        bool supportsTip = validSlots.Any(s => s.HasTipSwitch);
        bool supportsId = validSlots.Any(s => s.HasContactId);

        string summary = $"Verified {validSlots.Count} contact slot(s), logical X=[{primary.LogicalMinX}..{primary.LogicalMaxX}], " +
                         $"Y=[{primary.LogicalMinY}..{primary.LogicalMaxY}]" +
                         (reliablePhysical
                             ? $", physical {physWidthMm:F1}mm x {physHeightMm:F1}mm."
                             : ", physical dimensions unavailable (use manual calibration).");

        return new TouchpadDescriptorLayout
        {
            ReportId = primary.ReportId != 0 ? primary.ReportId : detectedReportId,
            InputReportByteLength = inputReportByteLength,
            SupportsContactCount = hasContactCount,
            ContactCountLinkCollection = contactCountCollection,
            SupportsScanTime = hasScanTime,
            ScanTimeLinkCollection = scanTimeCollection,
            ContactSlots = validSlots,
            HasUsableAbsoluteCoordinates = true,
            SupportsContactId = supportsId,
            SupportsTipSwitch = supportsTip,
            SupportsContactArea = supportsArea,
            LogicalMinX = primary.LogicalMinX,
            LogicalMaxX = primary.LogicalMaxX,
            LogicalMinY = primary.LogicalMinY,
            LogicalMaxY = primary.LogicalMaxY,
            PhysicalWidthMm = reliablePhysical ? physWidthMm : null,
            PhysicalHeightMm = reliablePhysical ? physHeightMm : null,
            HasReliablePhysicalDimensions = reliablePhysical,
            DiagnosticSummary = summary
        };
    }

    /// <summary>
    /// Parses a single raw HID input report into a pre-allocated contact buffer without heap allocations.
    /// Returns the number of contacts populated and the reported total contact count.
    /// </summary>
    public int ParseInputReport(
        IntPtr preparsedPtr,
        TouchpadDescriptorLayout layout,
        IntPtr reportPtr,
        uint reportLength,
        Span<TouchContact> contactBuffer,
        out int reportedContactCount)
    {
        reportedContactCount = 0;

        if (!layout.HasUsableAbsoluteCoordinates || preparsedPtr == IntPtr.Zero || reportPtr == IntPtr.Zero || reportLength == 0)
        {
            return 0;
        }

        // Check report ID match when descriptor uses report IDs
        if (layout.ReportId != 0)
        {
            byte incomingId = Marshal.ReadByte(reportPtr);
            if (incomingId != layout.ReportId)
            {
                return 0;
            }
        }

        if (layout.SupportsContactCount)
        {
            if (Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                    layout.ContactCountLinkCollection,
                    Win32Interop.HID_USAGE_DIGITIZER_CONTACT_COUNT,
                    out uint countVal,
                    preparsedPtr,
                    reportPtr,
                    reportLength) == Win32Interop.HIDP_STATUS_SUCCESS)
            {
                reportedContactCount = (int)countVal;
            }
        }

        int written = 0;
        int maxSlots = Math.Min(layout.ContactSlots.Count, contactBuffer.Length);

        for (int i = 0; i < maxSlots; i++)
        {
            var slot = layout.ContactSlots[i];
            if (Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_GENERIC,
                    slot.LinkCollection,
                    Win32Interop.HID_USAGE_GENERIC_X,
                    out uint rawX,
                    preparsedPtr,
                    reportPtr,
                    reportLength) != Win32Interop.HIDP_STATUS_SUCCESS)
            {
                continue;
            }

            if (Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_GENERIC,
                    slot.LinkCollection,
                    Win32Interop.HID_USAGE_GENERIC_Y,
                    out uint rawY,
                    preparsedPtr,
                    reportPtr,
                    reportLength) != Win32Interop.HIDP_STATUS_SUCCESS)
            {
                continue;
            }

            int contactId = i;
            if (slot.HasContactId &&
                Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                    slot.LinkCollection,
                    Win32Interop.HID_USAGE_DIGITIZER_CONTACT_ID,
                    out uint idVal,
                    preparsedPtr,
                    reportPtr,
                    reportLength) == Win32Interop.HIDP_STATUS_SUCCESS)
            {
                contactId = (int)idVal;
            }

            bool tipSwitch = !slot.HasTipSwitch; // If descriptor omits TipSwitch, assume active when present
            bool confidence = true;

            if (slot.HasTipSwitch || slot.HasConfidence)
            {
                uint usageLen = (uint)_buttonUsageScratch.Length;
                if (Win32Interop.HidP_GetUsages(
                        Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                        Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                        slot.LinkCollection,
                        _buttonUsageScratch,
                        ref usageLen,
                        preparsedPtr,
                        reportPtr,
                        reportLength) == Win32Interop.HIDP_STATUS_SUCCESS)
                {
                    if (slot.HasTipSwitch) tipSwitch = false;
                    if (slot.HasConfidence) confidence = false;

                    for (int u = 0; u < usageLen; u++)
                    {
                        if (_buttonUsageScratch[u] == Win32Interop.HID_USAGE_DIGITIZER_TIP_SWITCH)
                        {
                            tipSwitch = true;
                        }
                        else if (_buttonUsageScratch[u] == Win32Interop.HID_USAGE_DIGITIZER_CONFIDENCE)
                        {
                            confidence = true;
                        }
                    }
                }
            }

            int width = 0;
            bool hasWidth = false;
            if (slot.HasWidth &&
                Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                    slot.LinkCollection,
                    Win32Interop.HID_USAGE_DIGITIZER_WIDTH,
                    out uint wVal,
                    preparsedPtr,
                    reportPtr,
                    reportLength) == Win32Interop.HIDP_STATUS_SUCCESS)
            {
                width = (int)wVal;
                hasWidth = true;
            }

            int height = 0;
            bool hasHeight = false;
            if (slot.HasHeight &&
                Win32Interop.HidP_GetUsageValue(
                    Win32Interop.HIDP_REPORT_TYPE.HidP_Input,
                    Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                    slot.LinkCollection,
                    Win32Interop.HID_USAGE_DIGITIZER_HEIGHT,
                    out uint hVal,
                    preparsedPtr,
                    reportPtr,
                    reportLength) == Win32Interop.HIDP_STATUS_SUCCESS)
            {
                height = (int)hVal;
                hasHeight = true;
            }

            contactBuffer[written++] = new TouchContact
            {
                ContactId = contactId,
                TipSwitch = tipSwitch,
                Confidence = confidence,
                RawX = unchecked((int)rawX),
                RawY = unchecked((int)rawY),
                Width = width,
                Height = height,
                HasWidth = hasWidth,
                HasHeight = hasHeight
            };
        }

        if (!layout.SupportsContactCount)
        {
            int activeCount = 0;
            for (int i = 0; i < written; i++)
            {
                if (contactBuffer[i].TipSwitch) activeCount++;
            }
            reportedContactCount = activeCount;
        }

        return written;
    }
}
