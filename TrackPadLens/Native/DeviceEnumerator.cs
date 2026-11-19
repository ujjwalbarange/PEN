using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TrackPadLens.Hardware;

namespace TrackPadLens.Native;

/// <summary>
/// Discovers connected Raw Input pointing and digitizer HID devices and classifies them
/// into Built-in Precision Touchpad, External USB Mouse, External Bluetooth Mouse, or Other.
/// </summary>
public sealed class DeviceEnumerator
{
    private static readonly Regex VidRegex = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
    private static readonly Regex PidRegex = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    public (IReadOnlyList<TouchpadDevice> Touchpads, IReadOnlyList<MouseDevice> Mice) EnumerateDevices()
    {
        var touchpads = new List<TouchpadDevice>();
        var mice = new List<MouseDevice>();

        uint deviceCount = 0;
        uint structSize = (uint)Marshal.SizeOf<Win32Interop.RAWINPUTDEVICELIST>();

        if (Win32Interop.GetRawInputDeviceList(null, ref deviceCount, structSize) != 0 || deviceCount == 0)
        {
            return (touchpads, mice);
        }

        var list = new Win32Interop.RAWINPUTDEVICELIST[deviceCount];
        uint fetched = Win32Interop.GetRawInputDeviceList(list, ref deviceCount, structSize);
        if (fetched == uint.MaxValue)
        {
            return (touchpads, mice);
        }

        for (int i = 0; i < fetched; i++)
        {
            IntPtr hDevice = list[i].hDevice;
            uint dwType = list[i].dwType;

            string devicePath = GetDevicePath(hDevice);
            if (!TryGetDeviceInfo(hDevice, out var info))
            {
                continue;
            }

            if (dwType == Win32Interop.RIM_TYPEHID)
            {
                var hid = info.hid;
                if (hid.usUsagePage == Win32Interop.HID_USAGE_PAGE_DIGITIZER &&
                    hid.usUsage == Win32Interop.HID_USAGE_DIGITIZER_TOUCH_PAD)
                {
                    var touchpad = InspectTouchpadDevice(hDevice, devicePath, hid);
                    if (touchpad != null)
                    {
                        touchpads.Add(touchpad);
                    }
                }
            }
            else if (dwType == Win32Interop.RIM_TYPEMOUSE)
            {
                var mouse = BuildMouseDevice(hDevice, devicePath, info.mouse);
                mice.Add(mouse);
            }
        }

        return (touchpads, mice);
    }

    public static DeviceCategory ClassifyDevicePath(string devicePath, bool isDigitizerTouchpad)
    {
        if (isDigitizerTouchpad)
        {
            return DeviceCategory.BuiltInPrecisionTouchpad;
        }

        if (string.IsNullOrEmpty(devicePath))
        {
            return DeviceCategory.OtherPointingDevice;
        }

        string upper = devicePath.ToUpperInvariant();

        // Bluetooth HID mice expose BTHENUM or BTHLEDEVICE in their symbolic link path
        if (upper.Contains("BTHENUM") || upper.Contains("BTHLE") || upper.Contains("{00001812"))
        {
            return DeviceCategory.ExternalBluetoothMouse;
        }

        // Precision Touchpad mouse-emulation collection often carries ACPI/I2C/ELAN/SYNA/MSFT0001
        if (upper.Contains("ACPI") || upper.Contains("I2C") || upper.Contains("SYNA") ||
            upper.Contains("ELAN") || upper.Contains("MSFT0001") || upper.Contains("DLL0"))
        {
            return DeviceCategory.OtherPointingDevice;
        }

        // External USB mice expose USB# or HID#VID_...&PID_...
        if (upper.Contains("USB") || upper.Contains("HID#VID_"))
        {
            return DeviceCategory.ExternalUsbMouse;
        }

        return DeviceCategory.OtherPointingDevice;
    }

    public static (ushort Vid, ushort Pid) ExtractVidPidFromPath(string devicePath)
    {
        ushort vid = 0;
        ushort pid = 0;
        if (string.IsNullOrEmpty(devicePath)) return (vid, pid);

        var vm = VidRegex.Match(devicePath);
        if (vm.Success && ushort.TryParse(vm.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ushort v))
        {
            vid = v;
        }

        var pm = PidRegex.Match(devicePath);
        if (pm.Success && ushort.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ushort p))
        {
            pid = p;
        }

        return (vid, pid);
    }

    private static TouchpadDevice? InspectTouchpadDevice(
        IntPtr hDevice,
        string devicePath,
        Win32Interop.RID_DEVICE_INFO_HID hidInfo)
    {
        // Prefer preparsed data directly from Raw Input so we do not require exclusive HID file open
        byte[]? preparsedBytes = GetRawInputPreparsedData(hDevice);
        string productName = QueryHidProductName(devicePath) ?? $"HID Touchpad (VID_{hidInfo.dwVendorId:X4} PID_{hidInfo.dwProductId:X4})";

        TouchpadDescriptorLayout? layout = null;
        if (preparsedBytes != null && preparsedBytes.Length > 0)
        {
            layout = HidTouchpadReader.ParsePreparsedData(preparsedBytes);
        }
        else
        {
            // Secondary fallback: open HID path with 0 access (metadata query only, no admin required)
            layout = TryReadViaHidHandle(devicePath);
        }

        var report = new HardwareCapabilityReport
        {
            DeviceName = productName,
            DevicePath = devicePath,
            Category = DeviceCategory.BuiltInPrecisionTouchpad,
            VendorId = (ushort)hidInfo.dwVendorId,
            ProductId = (ushort)hidInfo.dwProductId,
            UsagePage = hidInfo.usUsagePage,
            Usage = hidInfo.usUsage,
            HasUsableAbsoluteCoordinates = layout?.HasUsableAbsoluteCoordinates ?? false,
            LogicalMinX = layout?.LogicalMinX ?? 0,
            LogicalMaxX = layout?.LogicalMaxX ?? 0,
            LogicalMinY = layout?.LogicalMinY ?? 0,
            LogicalMaxY = layout?.LogicalMaxY ?? 0,
            PhysicalWidthMm = layout?.PhysicalWidthMm,
            PhysicalHeightMm = layout?.PhysicalHeightMm,
            HasReliablePhysicalDimensions = layout?.HasReliablePhysicalDimensions ?? false,
            SupportedContacts = layout?.ContactSlots.Count ?? 0,
            SupportsContactId = layout?.SupportsContactId ?? false,
            SupportsContactCount = layout?.SupportsContactCount ?? false,
            SupportsTipSwitch = layout?.SupportsTipSwitch ?? false,
            SupportsContactArea = layout?.SupportsContactArea ?? false,
            ReportId = layout?.ReportId ?? 0,
            SupportsDeviceSpecificCursorSuppression = false, // Updated by HardwareInputManager via ICursorIsolationProvider
            DiagnosticNotes = layout == null
                ? "Device exposes Digitizer/TouchPad usage (0x0D:0x05), but HID preparsed descriptor could not be queried."
                : layout.DiagnosticSummary
        };

        return new TouchpadDevice(hDevice, devicePath, preparsedBytes, layout, report);
    }

    private static MouseDevice BuildMouseDevice(
        IntPtr hDevice,
        string devicePath,
        Win32Interop.RID_DEVICE_INFO_MOUSE mouseInfo)
    {
        var category = ClassifyDevicePath(devicePath, isDigitizerTouchpad: false);
        var (vid, pid) = ExtractVidPidFromPath(devicePath);
        string? product = QueryHidProductName(devicePath);
        string fallbackName = category switch
        {
            DeviceCategory.ExternalBluetoothMouse => $"Bluetooth Mouse (VID_{vid:X4} PID_{pid:X4})",
            DeviceCategory.ExternalUsbMouse => $"USB Mouse (VID_{vid:X4} PID_{pid:X4})",
            _ => $"System Pointing Device (VID_{vid:X4} PID_{pid:X4})"
        };

        return new MouseDevice(
            hDevice,
            devicePath,
            product ?? fallbackName,
            category,
            vid,
            pid,
            (int)mouseInfo.dwNumberOfButtons,
            (int)mouseInfo.dwSampleRate);
    }

    private static string GetDevicePath(IntPtr hDevice)
    {
        uint size = 0;
        Win32Interop.GetRawInputDeviceInfo(hDevice, Win32Interop.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal((int)(size * sizeof(char)));
        try
        {
            uint result = Win32Interop.GetRawInputDeviceInfo(hDevice, Win32Interop.RIDI_DEVICENAME, buffer, ref size);
            if (result == uint.MaxValue) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetDeviceInfo(IntPtr hDevice, out Win32Interop.RID_DEVICE_INFO info)
    {
        info = new Win32Interop.RID_DEVICE_INFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32Interop.RID_DEVICE_INFO>()
        };
        uint size = info.cbSize;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            uint res = Win32Interop.GetRawInputDeviceInfo(hDevice, Win32Interop.RIDI_DEVICEINFO, buffer, ref size);
            if (res == uint.MaxValue) return false;
            info = Marshal.PtrToStructure<Win32Interop.RID_DEVICE_INFO>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[]? GetRawInputPreparsedData(IntPtr hDevice)
    {
        uint size = 0;
        Win32Interop.GetRawInputDeviceInfo(hDevice, Win32Interop.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0 || size > 1024 * 1024) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint res = Win32Interop.GetRawInputDeviceInfo(hDevice, Win32Interop.RIDI_PREPARSEDDATA, buffer, ref size);
            if (res == uint.MaxValue) return null;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, (int)size);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TouchpadDescriptorLayout? TryReadViaHidHandle(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;

        IntPtr handle = Win32Interop.CreateFile(
            devicePath,
            0, // Metadata query access only; avoids exclusive lock / admin privilege issues
            Win32Interop.FILE_SHARE_READ | Win32Interop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32Interop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle == Win32Interop.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!Win32Interop.HidD_GetPreparsedData(handle, out IntPtr preparsedPtr) || preparsedPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return HidTouchpadReader.ParsePreparsedPointer(preparsedPtr);
            }
            finally
            {
                Win32Interop.HidD_FreePreparsedData(preparsedPtr);
            }
        }
        finally
        {
            Win32Interop.CloseHandle(handle);
        }
    }

    private static string? QueryHidProductName(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;

        IntPtr handle = Win32Interop.CreateFile(
            devicePath,
            0,
            Win32Interop.FILE_SHARE_READ | Win32Interop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32Interop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle == Win32Interop.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
        {
            return null;
        }

        const int byteLen = 256;
        IntPtr buffer = Marshal.AllocHGlobal(byteLen);
        try
        {
            if (Win32Interop.HidD_GetProductString(handle, buffer, byteLen))
            {
                string? name = Marshal.PtrToStringUni(buffer)?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Win32Interop.CloseHandle(handle);
        }
    }
}
