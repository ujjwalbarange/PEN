using System.Runtime.InteropServices;

namespace TrackPadLens.Native;

/// <summary>
/// Native x64 Win32, Raw Input, HID, Windowing, DPI, Hotkey, and GDI P/Invoke declarations.
/// Struct layouts match the Windows 10/11 SDK headers (winuser.h, hidpi.h, hidsdi.h, shellscalingapi.h).
/// </summary>
public static class Win32Interop
{
    // --- Window Messages ---
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const int WM_INPUT = 0x00FF;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MOUSEMOVE = 0x0200;

    // --- Hit-test constants ---
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;

    // --- Window Extended Styles ---
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    // --- SetWindowPos flags ---
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    // --- Raw Input Constants ---
    public const uint RID_INPUT = 0x10000003;
    public const uint RID_HEADER = 0x10000005;

    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIM_TYPEHID = 2;

    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;

    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000B;

    public const int GIDC_ARRIVAL = 1;
    public const int GIDC_REMOVAL = 2;

    // --- Raw Mouse Button Flags ---
    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    public const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    public const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    public const ushort RI_MOUSE_WHEEL = 0x0400;

    // --- HID Usage Pages & Usages ---
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    public const ushort HID_USAGE_GENERIC_X = 0x30;
    public const ushort HID_USAGE_GENERIC_Y = 0x31;

    public const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    public const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x05;
    public const ushort HID_USAGE_DIGITIZER_FINGER = 0x22;
    public const ushort HID_USAGE_DIGITIZER_TIP_SWITCH = 0x42;
    public const ushort HID_USAGE_DIGITIZER_CONFIDENCE = 0x47;
    public const ushort HID_USAGE_DIGITIZER_WIDTH = 0x48;
    public const ushort HID_USAGE_DIGITIZER_HEIGHT = 0x49;
    public const ushort HID_USAGE_DIGITIZER_CONTACT_ID = 0x51;
    public const ushort HID_USAGE_DIGITIZER_CONTACT_COUNT = 0x54;
    public const ushort HID_USAGE_DIGITIZER_SCAN_TIME = 0x56;

    // --- HID Status Codes ---
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    // --- Hooks ---
    public const int WH_MOUSE_LL = 14;
    public const uint LLMHF_INJECTED = 0x00000001;
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    // --- Hotkey Modifiers ---
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // --- System Metrics ---
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // --- File Access for HidD_GetPreparsedData ---
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // --- Monitor DPI ---
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const int MDT_EFFECTIVE_DPI = 0;

    // --- Raw Input Structs ---

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public uint ulButtons;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public short usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWHID_HEADER
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_MOUSE
    {
        public uint dwId;
        public uint dwNumberOfButtons;
        public uint dwSampleRate;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fHasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_KEYBOARD
    {
        public uint dwType;
        public uint dwSubType;
        public uint dwKeyboardMode;
        public uint dwNumberOfFunctionKeys;
        public uint dwNumberOfIndicators;
        public uint dwNumberOfKeysTotal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_MOUSE mouse;
        [FieldOffset(8)] public RID_DEVICE_INFO_KEYBOARD keyboard;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
    }

    // --- HID Preparsed Data Structs (hidpi.h) ---

    public enum HIDP_REPORT_TYPE
    {
        HidP_Input = 0,
        HidP_Output = 1,
        HidP_Feature = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// Exact 72-byte HIDP_VALUE_CAPS layout from Windows SDK hidpi.h.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(17)] public byte Reserved;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        // Range / NotRange union at offset 56
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(56)] public ushort Usage;
        [FieldOffset(58)] public ushort UsageMax;
        [FieldOffset(60)] public ushort StringMin;
        [FieldOffset(62)] public ushort StringMax;
        [FieldOffset(64)] public ushort DesignatorMin;
        [FieldOffset(66)] public ushort DesignatorMax;
        [FieldOffset(68)] public ushort DataIndexMin;
        [FieldOffset(70)] public ushort DataIndexMax;
    }

    /// <summary>
    /// Exact 72-byte HIDP_BUTTON_CAPS layout from Windows SDK hidpi.h.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_BUTTON_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(56)] public ushort Usage;
        [FieldOffset(58)] public ushort UsageMax;
        [FieldOffset(68)] public ushort DataIndexMin;
        [FieldOffset(70)] public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // --- user32.dll ---

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        [Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // --- shcore.dll ---

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // --- hid.dll ---

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetProductString(IntPtr HidDeviceObject, IntPtr Buffer, uint BufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetManufacturerString(IntPtr HidDeviceObject, IntPtr Buffer, uint BufferLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(
        HIDP_REPORT_TYPE ReportType,
        [Out] HIDP_VALUE_CAPS[] ValueCaps,
        ref ushort ValueCapsLength,
        IntPtr PreparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(
        HIDP_REPORT_TYPE ReportType,
        [Out] HIDP_BUTTON_CAPS[] ButtonCaps,
        ref ushort ButtonCapsLength,
        IntPtr PreparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(
        HIDP_REPORT_TYPE ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        ushort Usage,
        out uint UsageValue,
        IntPtr PreparsedData,
        IntPtr Report,
        uint ReportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(
        HIDP_REPORT_TYPE ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        [Out] ushort[] UsageList,
        ref uint UsageLength,
        IntPtr PreparsedData,
        IntPtr Report,
        uint ReportLength);

    // --- kernel32.dll ---

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
}
