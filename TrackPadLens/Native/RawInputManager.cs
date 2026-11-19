using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TrackPadLens.Native;

public readonly record struct RawMousePacket(
    IntPtr DeviceHandle,
    int DeltaX,
    int DeltaY,
    short WheelDelta,
    bool RightButtonDown,
    bool RightButtonUp);

public delegate void RawHidPacketHandler(IntPtr deviceHandle, IntPtr reportPtr, uint reportSize, uint reportCount);
public delegate void RawMousePacketHandler(in RawMousePacket packet);
public delegate void DeviceChangeHandler(IntPtr deviceHandle, bool arrived);

/// <summary>
/// Registers for Windows Raw Input (Precision Touchpad 0x0D:0x05 + Mouse 0x01:0x02)
/// on a dedicated message-only HWND and reuses a single unmanaged packet buffer
/// so zero managed allocations occur during high-rate pointer/touchpad streaming.
/// </summary>
public sealed class RawInputManager : IDisposable
{
    private HwndSource? _messageWindow;
    private IntPtr _rawBuffer = IntPtr.Zero;
    private int _rawBufferCapacity = 0;
    private bool _isRegistered;
    private bool _disposed;

    public bool IsTouchpadRegistered { get; private set; }
    public bool IsMouseRegistered { get; private set; }
    public int LastWin32Error { get; private set; }

    public event RawHidPacketHandler? RawHidReceived;
    public event RawMousePacketHandler? RawMouseReceived;
    public event DeviceChangeHandler? DeviceChanged;

    public bool Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parameters = new HwndSourceParameters("TrackPadLensRawInputSink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        // Pre-allocate 4 KB unmanaged scratch buffer for GetRawInputData
        EnsureBufferCapacity(4096);

        uint ridSize = (uint)Marshal.SizeOf<Win32Interop.RAWINPUTDEVICE>();

        // Register Precision Touchpad (Digitizer 0x0D / Touch Pad 0x05)
        var touchpadRid = new[]
        {
            new Win32Interop.RAWINPUTDEVICE
            {
                usUsagePage = Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                usUsage = Win32Interop.HID_USAGE_DIGITIZER_TOUCH_PAD,
                dwFlags = Win32Interop.RIDEV_INPUTSINK | Win32Interop.RIDEV_DEVNOTIFY,
                hwndTarget = _messageWindow.Handle
            }
        };

        IsTouchpadRegistered = Win32Interop.RegisterRawInputDevices(touchpadRid, 1, ridSize);
        if (!IsTouchpadRegistered)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
        }

        // Register Generic Mouse (Generic Desktop 0x01 / Mouse 0x02)
        var mouseRid = new[]
        {
            new Win32Interop.RAWINPUTDEVICE
            {
                usUsagePage = Win32Interop.HID_USAGE_PAGE_GENERIC,
                usUsage = Win32Interop.HID_USAGE_GENERIC_MOUSE,
                dwFlags = Win32Interop.RIDEV_INPUTSINK | Win32Interop.RIDEV_DEVNOTIFY,
                hwndTarget = _messageWindow.Handle
            }
        };

        IsMouseRegistered = Win32Interop.RegisterRawInputDevices(mouseRid, 1, ridSize);
        if (!IsMouseRegistered)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
        }

        _isRegistered = IsTouchpadRegistered || IsMouseRegistered;
        return _isRegistered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_INPUT)
        {
            ProcessRawInput(lParam);
        }
        else if (msg == Win32Interop.WM_INPUT_DEVICE_CHANGE)
        {
            long changeCode = wParam.ToInt64();
            bool arrived = changeCode == Win32Interop.GIDC_ARRIVAL;
            if (arrived || changeCode == Win32Interop.GIDC_REMOVAL)
            {
                DeviceChanged?.Invoke(lParam, arrived);
            }
        }

        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<Win32Interop.RAWINPUTHEADER>();
        uint requiredSize = 0;

        if (Win32Interop.GetRawInputData(hRawInput, Win32Interop.RID_INPUT, IntPtr.Zero, ref requiredSize, headerSize) != 0 ||
            requiredSize < headerSize)
        {
            return;
        }

        EnsureBufferCapacity((int)requiredSize);

        uint bytesRead = Win32Interop.GetRawInputData(hRawInput, Win32Interop.RID_INPUT, _rawBuffer, ref requiredSize, headerSize);
        if (bytesRead == uint.MaxValue || bytesRead < headerSize)
        {
            return;
        }

        var header = Marshal.PtrToStructure<Win32Interop.RAWINPUTHEADER>(_rawBuffer);
        IntPtr payloadPtr = IntPtr.Add(_rawBuffer, (int)headerSize);

        if (header.dwType == Win32Interop.RIM_TYPEHID)
        {
            if (bytesRead < headerSize + 8) return;
            var hidHeader = Marshal.PtrToStructure<Win32Interop.RAWHID_HEADER>(payloadPtr);
            if (hidHeader.dwSizeHid == 0 || hidHeader.dwCount == 0) return;

            IntPtr reportBase = IntPtr.Add(payloadPtr, 8);
            RawHidReceived?.Invoke(header.hDevice, reportBase, hidHeader.dwSizeHid, hidHeader.dwCount);
        }
        else if (header.dwType == Win32Interop.RIM_TYPEMOUSE)
        {
            var mouse = Marshal.PtrToStructure<Win32Interop.RAWMOUSE>(payloadPtr);
            short wheelDelta = (mouse.usButtonFlags & Win32Interop.RI_MOUSE_WHEEL) != 0 ? mouse.usButtonData : (short)0;
            bool rightDown = (mouse.usButtonFlags & Win32Interop.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0;
            bool rightUp = (mouse.usButtonFlags & Win32Interop.RI_MOUSE_RIGHT_BUTTON_UP) != 0;

            var packet = new RawMousePacket(
                header.hDevice,
                mouse.lLastX,
                mouse.lLastY,
                wheelDelta,
                rightDown,
                rightUp);

            RawMouseReceived?.Invoke(in packet);
        }
    }

    private void EnsureBufferCapacity(int requiredBytes)
    {
        if (requiredBytes <= _rawBufferCapacity && _rawBuffer != IntPtr.Zero)
        {
            return;
        }

        if (_rawBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rawBuffer);
        }

        int newCap = Math.Max(requiredBytes, 4096);
        _rawBuffer = Marshal.AllocHGlobal(newCap);
        _rawBufferCapacity = newCap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isRegistered)
        {
            uint ridSize = (uint)Marshal.SizeOf<Win32Interop.RAWINPUTDEVICE>();
            var removeList = new[]
            {
                new Win32Interop.RAWINPUTDEVICE
                {
                    usUsagePage = Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                    usUsage = Win32Interop.HID_USAGE_DIGITIZER_TOUCH_PAD,
                    dwFlags = Win32Interop.RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                },
                new Win32Interop.RAWINPUTDEVICE
                {
                    usUsagePage = Win32Interop.HID_USAGE_PAGE_GENERIC,
                    usUsage = Win32Interop.HID_USAGE_GENERIC_MOUSE,
                    dwFlags = Win32Interop.RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                }
            };
            Win32Interop.RegisterRawInputDevices(removeList, (uint)removeList.Length, ridSize);
            _isRegistered = false;
        }

        if (_messageWindow != null)
        {
            _messageWindow.RemoveHook(WndProc);
            _messageWindow.Dispose();
            _messageWindow = null;
        }

        if (_rawBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rawBuffer);
            _rawBuffer = IntPtr.Zero;
            _rawBufferCapacity = 0;
        }
    }
}
