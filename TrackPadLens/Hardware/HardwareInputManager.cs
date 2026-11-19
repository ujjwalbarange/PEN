using System.Diagnostics;
using TrackPadLens.Models;
using TrackPadLens.Native;

namespace TrackPadLens.Hardware;

public readonly record struct TouchpadFrameEvent(
    TouchpadDevice Device,
    TouchContact[] Contacts,
    int ContactLength,
    int ReportedContactCount,
    double TimestampSeconds);

public readonly record struct ExternalMouseEvent(
    MouseDevice? Mouse,
    int CursorPhysicalX,
    int CursorPhysicalY,
    short WheelDelta,
    bool IsRightButtonHeld);

/// <summary>
/// Coordinates Raw Input registration, HID touchpad descriptor discovery, external mouse
/// attribution, hotplug recovery, operational mode classification (Mode A / B / C), and
/// zero-allocation HID report dispatch.
/// </summary>
public sealed class HardwareInputManager : IDisposable
{
    private const int MaxContactsPerReport = 16;
    private const int ReportRateWindowSize = 64;

    private readonly DeviceEnumerator _enumerator = new();
    private readonly HidTouchpadReader _hidReader = new();
    private readonly RawInputManager _rawInput = new();
    private readonly MouseHookManager _mouseHook = new();
    private readonly ICursorIsolationProvider _isolationProvider;

    private readonly Dictionary<IntPtr, TouchpadDevice> _touchpadsByHandle = new();
    private readonly Dictionary<IntPtr, MouseDevice> _miceByHandle = new();
    private readonly TouchContact[] _contactBuffer = new TouchContact[MaxContactsPerReport];
    private readonly double[] _reportTimestamps = new double[ReportRateWindowSize];

    private int _timestampIndex;
    private int _timestampCount;
    private bool _disposed;

    public OperationalMode CurrentMode { get; private set; } = OperationalMode.HardwareUnsupported;
    public TouchpadDevice? PrimaryTouchpad { get; private set; }
    public IReadOnlyList<TouchpadDevice> Touchpads => _touchpadsByHandle.Values.ToList();
    public IReadOnlyList<MouseDevice> Mice => _miceByHandle.Values.ToList();
    public IReadOnlyList<MouseDevice> ExternalMice => _miceByHandle.Values.Where(m => m.IsExternalMouse).ToList();

    public bool IsRawInputTouchpadRegistered => _rawInput.IsTouchpadRegistered;
    public bool IsRawInputMouseRegistered => _rawInput.IsMouseRegistered;
    public bool IsCursorSuppressionActive { get; private set; }
    public bool HardwareTestMode { get; set; }
    public double MeasuredReportRateHz { get; private set; }
    public string ModeExplanation { get; private set; } = "Hardware discovery has not run yet.";

    public event Action<TouchpadFrameEvent>? TouchpadFrameReceived;
    public event Action<ExternalMouseEvent>? ExternalMouseUpdated;
    public event Action<int>? ExternalMouseWheel;
    public event Action<bool>? AnchorLockRightButtonChanged;
    public event Action? DevicesChanged;

    public HardwareInputManager(ICursorIsolationProvider? isolationProvider = null)
    {
        _isolationProvider = isolationProvider ?? new UserModeNoDriverIsolationProvider();
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _rawInput.RawHidReceived += OnRawHidReceived;
        _rawInput.RawMouseReceived += OnRawMouseReceived;
        _rawInput.DeviceChanged += OnDeviceChanged;
        _rawInput.Initialize();

        _mouseHook.CursorMoved += OnHookCursorMoved;
        _mouseHook.RightButtonStateChanged += held => AnchorLockRightButtonChanged?.Invoke(held);
        _mouseHook.Install();

        RefreshDevices();
    }

    public void RefreshDevices()
    {
        DisposeTouchpads();
        _miceByHandle.Clear();

        var (touchpads, mice) = _enumerator.EnumerateDevices();
        foreach (var tp in touchpads)
        {
            tp.Capabilities.SupportsDeviceSpecificCursorSuppression = _isolationProvider.IsDriverAvailable;
            _touchpadsByHandle[tp.DeviceHandle] = tp;
        }

        foreach (var m in mice)
        {
            _miceByHandle[m.DeviceHandle] = m;
        }

        PrimaryTouchpad = touchpads.FirstOrDefault(t => t.Capabilities.HasUsableAbsoluteCoordinates)
                          ?? touchpads.FirstOrDefault();

        EvaluateOperationalMode();
        DevicesChanged?.Invoke();
    }

    public void SetDoodleModeCursorIsolation(bool enable)
    {
        if (PrimaryTouchpad == null || !PrimaryTouchpad.Capabilities.HasUsableAbsoluteCoordinates)
        {
            IsCursorSuppressionActive = false;
            return;
        }

        if (enable && _isolationProvider.IsDriverAvailable)
        {
            IsCursorSuppressionActive = _isolationProvider.TryEnableTouchpadCursorSuppression(PrimaryTouchpad.DevicePath);
        }
        else
        {
            _isolationProvider.DisableTouchpadCursorSuppression();
            IsCursorSuppressionActive = false;
        }
    }

    private void EvaluateOperationalMode()
    {
        if (PrimaryTouchpad == null)
        {
            CurrentMode = OperationalMode.HardwareUnsupported;
            ModeExplanation =
                "Mode C (Hardware Unsupported): No Windows Precision Touchpad exposing HID Usage Page 0x0D / Usage 0x05 " +
                "was detected. Touchpad ink input is disabled rather than simulating fake input.";
            return;
        }

        if (!PrimaryTouchpad.Capabilities.HasUsableAbsoluteCoordinates)
        {
            CurrentMode = OperationalMode.HardwareUnsupported;
            ModeExplanation =
                $"Mode C (Hardware Unsupported): Touchpad '{PrimaryTouchpad.Capabilities.DeviceName}' was found, " +
                $"but Windows/firmware did not expose usable absolute X/Y HID collections. {PrimaryTouchpad.Capabilities.DiagnosticNotes}";
            return;
        }

        if (_isolationProvider.IsDriverAvailable)
        {
            CurrentMode = OperationalMode.NativeAbsoluteTouchpad;
            ModeExplanation =
                $"Mode A (Native Absolute Touchpad): Receiving verified absolute HID contact coordinates from " +
                $"'{PrimaryTouchpad.Capabilities.DeviceName}' with signed HID filter driver cursor suppression.";
        }
        else
        {
            CurrentMode = OperationalMode.CompatibleMode;
            ModeExplanation =
                $"Mode B (Compatible Mode): Receiving verified absolute HID contact coordinates from " +
                $"'{PrimaryTouchpad.Capabilities.DeviceName}' via Windows Raw Input. Because no signed HID filter driver " +
                "is installed, Windows may also move the system cursor when touching the pad; use Shift or Right-Click " +
                "Anchor Lock to freeze the Lens position while writing. External mouse operation remains 100% intact.";
        }
    }

    private void OnRawHidReceived(IntPtr deviceHandle, IntPtr reportPtr, uint reportSize, uint reportCount)
    {
        if (!_touchpadsByHandle.TryGetValue(deviceHandle, out var touchpad))
        {
            return;
        }

        if (touchpad.Layout == null || !touchpad.Capabilities.HasUsableAbsoluteCoordinates || touchpad.PreparsedPointer == IntPtr.Zero)
        {
            return;
        }

        double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        RecordReportTimestamp(nowSec);

        for (uint r = 0; r < reportCount; r++)
        {
            IntPtr singleReportPtr = IntPtr.Add(reportPtr, (int)(r * reportSize));
            int count = _hidReader.ParseInputReport(
                touchpad.PreparsedPointer,
                touchpad.Layout,
                singleReportPtr,
                reportSize,
                _contactBuffer,
                out int reportedContacts);

            if (count > 0)
            {
                TouchpadFrameReceived?.Invoke(new TouchpadFrameEvent(
                    touchpad,
                    _contactBuffer,
                    count,
                    reportedContacts,
                    nowSec));
            }
        }
    }

    private void OnRawMouseReceived(in RawMousePacket packet)
    {
        _miceByHandle.TryGetValue(packet.DeviceHandle, out var mouse);

        // Only treat wheel events from reliably identified external mice (or any mouse when no external mouse is classified)
        bool allowMouseSource = mouse == null || mouse.IsExternalMouse || ExternalMice.Count == 0;
        if (!allowMouseSource)
        {
            return;
        }

        if (packet.WheelDelta != 0)
        {
            ExternalMouseWheel?.Invoke(packet.WheelDelta);
        }

        if (packet.DeltaX != 0 || packet.DeltaY != 0)
        {
            if (Win32Interop.GetCursorPos(out var pt))
            {
                ExternalMouseUpdated?.Invoke(new ExternalMouseEvent(
                    mouse,
                    pt.X,
                    pt.Y,
                    packet.WheelDelta,
                    _mouseHook.IsRightButtonHeld));
            }
        }
    }

    private void OnHookCursorMoved(int physicalX, int physicalY)
    {
        // Keep lens smoothly synced with cursor position when external mouse is moving
        ExternalMouseUpdated?.Invoke(new ExternalMouseEvent(
            null,
            physicalX,
            physicalY,
            0,
            _mouseHook.IsRightButtonHeld));
    }

    private void OnDeviceChanged(IntPtr deviceHandle, bool arrived)
    {
        RefreshDevices();
    }

    private void RecordReportTimestamp(double nowSec)
    {
        _reportTimestamps[_timestampIndex] = nowSec;
        _timestampIndex = (_timestampIndex + 1) % ReportRateWindowSize;
        if (_timestampCount < ReportRateWindowSize) _timestampCount++;

        if (_timestampCount >= 4)
        {
            int oldestIndex = (_timestampIndex - _timestampCount + ReportRateWindowSize) % ReportRateWindowSize;
            double span = nowSec - _reportTimestamps[oldestIndex];
            if (span > 0.005)
            {
                MeasuredReportRateHz = (_timestampCount - 1) / span;
            }
        }
    }

    private void DisposeTouchpads()
    {
        foreach (var tp in _touchpadsByHandle.Values)
        {
            tp.Dispose();
        }
        _touchpadsByHandle.Clear();
        PrimaryTouchpad = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isolationProvider.DisableTouchpadCursorSuppression();
        _isolationProvider.Dispose();
        _mouseHook.Dispose();
        _rawInput.Dispose();
        DisposeTouchpads();
        _miceByHandle.Clear();
    }
}
