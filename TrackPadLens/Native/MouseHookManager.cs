using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrackPadLens.Native;

/// <summary>
/// Low-level mouse hook (WH_MOUSE_LL) with Precision Touchpad activity-gated cursor suppression.
///
/// Design:
/// - WH_MOUSE_LL alone does not carry the originating Raw Input device handle, and LLMHF_INJECTED
///   is never used as a sole classifier.
/// - Instead, HardwareInputManager correlates real-time Raw Input HID reports from the built-in
///   Precision Touchpad (0x0D:0x05) with external USB/Bluetooth mouse Raw Input (RIM_TYPEMOUSE).
/// - When Doodle Mode is active AND touchpad cursor suppression is enabled, synthesized cursor
///   moves and accidental tap-to-click / scroll events emitted during active touchpad contact
///   (plus a brief post-lift inertia guard) are swallowed so the cursor stays completely stationary
///   while writing, while the external USB/Bluetooth mouse moves and clicks freely.
/// - When Doodle Mode is OFF (or no touchpad contact is active), 100% of events pass through untouched.
/// </summary>
public sealed class MouseHookManager : IDisposable
{
    // Windows MI_WP_SIGNATURE (0xFF515700, mask 0xFFFFFF00) marks touch/pen/PTP-synthesized mouse messages.
    private const ulong TouchSynthesizedSignatureMask = 0xFFFFFF00UL;
    private const ulong TouchSynthesizedSignature = 0xFF515700UL;

    private static readonly long ActiveTouchWindowTicks = (long)(0.160 * Stopwatch.Frequency);
    private static readonly long LiftGuardWindowTicks = (long)(0.130 * Stopwatch.Frequency);
    private static readonly long ExternalInputGraceTicks = (long)(0.090 * Stopwatch.Frequency);
    private static readonly long PreTouchSnapWindowTicks = (long)(0.045 * Stopwatch.Frequency);

    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32Interop.LowLevelMouseProc? _procDelegate;
    private bool _disposed;

    private volatile bool _suppressTouchpadCursor;
    private volatile bool _isTouchpadFingerDown;
    private long _suppressUntilTicks;

    private volatile bool _isExternalLeftButtonDown;
    private long _lastExternalMoveTicks;
    private long _lastExternalClickTicks;
    private long _lastExternalWheelTicks;

    // Anchor tracking to undo any 1-frame pre-HID cursor nudge on fast touchpad touch-down
    private int _stableCursorX;
    private int _stableCursorY;
    private bool _hasStableCursor;
    private long _lastUnattributedMoveTicks;

    public bool IsInstalled => _hookHandle != IntPtr.Zero;
    public bool IsRightButtonHeld { get; private set; }

    public bool SuppressTouchpadCursor
    {
        get => _suppressTouchpadCursor;
        set
        {
            _suppressTouchpadCursor = value;
            if (!value)
            {
                _isTouchpadFingerDown = false;
                Volatile.Write(ref _suppressUntilTicks, 0);
            }
            else if (Win32Interop.GetCursorPos(out var pt))
            {
                _stableCursorX = pt.X;
                _stableCursorY = pt.Y;
                _hasStableCursor = true;
            }
        }
    }

    public bool IsCurrentlySuppressingTouchpad
    {
        get
        {
            if (!_suppressTouchpadCursor) return false;
            return _isTouchpadFingerDown || Stopwatch.GetTimestamp() < Volatile.Read(ref _suppressUntilTicks);
        }
    }

    public event Action<int, int>? CursorMoved;
    public event Action<bool>? RightButtonStateChanged;
    public event Action<int>? MouseWheelObserved;

    public bool Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookHandle != IntPtr.Zero) return true;

        _procDelegate = HookCallback;
        using var currentProcess = Process.GetCurrentProcess();
        using var mainModule = currentProcess.MainModule;
        IntPtr moduleHandle = Win32Interop.GetModuleHandle(mainModule?.ModuleName);

        _hookHandle = Win32Interop.SetWindowsHookEx(
            Win32Interop.WH_MOUSE_LL,
            _procDelegate,
            moduleHandle,
            0);

        return _hookHandle != IntPtr.Zero;
    }

    public static bool IsShiftKeyPressed()
    {
        return (Win32Interop.GetAsyncKeyState(Win32Interop.VK_SHIFT) & 0x8000) != 0;
    }

    /// <summary>
    /// Called by HardwareInputManager on every incoming Precision Touchpad HID report (0x0D:0x05).
    /// Arms cursor suppression while any finger is down plus a short post-lift inertia guard.
    /// </summary>
    public void NotifyTouchpadActivity(bool hasActiveContact)
    {
        long now = Stopwatch.GetTimestamp();
        bool wasDown = _isTouchpadFingerDown;

        if (hasActiveContact)
        {
            _isTouchpadFingerDown = true;
            Volatile.Write(ref _suppressUntilTicks, now + ActiveTouchWindowTicks);

            // If Windows PTP managed to nudge the cursor a few milliseconds before the first
            // WM_INPUT HID packet arrived (with no external mouse activity), snap it back cleanly.
            if (!wasDown && _suppressTouchpadCursor && _hasStableCursor)
            {
                long extAge = now - Volatile.Read(ref _lastExternalMoveTicks);
                long unattrAge = now - Volatile.Read(ref _lastUnattributedMoveTicks);
                if (extAge > ExternalInputGraceTicks && unattrAge <= PreTouchSnapWindowTicks)
                {
                    Win32Interop.SetCursorPos(_stableCursorX, _stableCursorY);
                }
            }
        }
        else
        {
            _isTouchpadFingerDown = false;
            long liftDeadline = now + LiftGuardWindowTicks;
            long currentDeadline = Volatile.Read(ref _suppressUntilTicks);
            if (liftDeadline > currentDeadline)
            {
                Volatile.Write(ref _suppressUntilTicks, liftDeadline);
            }
        }
    }

    /// <summary>
    /// Called by HardwareInputManager whenever a verified external USB/Bluetooth mouse generates Raw Input.
    /// </summary>
    public void NotifyExternalMouseActivity(bool hasMove, bool leftDown, bool leftUp, bool hasWheel, int? knownCursorX = null, int? knownCursorY = null)
    {
        long now = Stopwatch.GetTimestamp();
        if (hasMove)
        {
            Volatile.Write(ref _lastExternalMoveTicks, now);
            if (knownCursorX.HasValue && knownCursorY.HasValue)
            {
                _stableCursorX = knownCursorX.Value;
                _stableCursorY = knownCursorY.Value;
                _hasStableCursor = true;
            }
        }
        if (leftDown)
        {
            _isExternalLeftButtonDown = true;
            Volatile.Write(ref _lastExternalClickTicks, now);
        }
        if (leftUp)
        {
            _isExternalLeftButtonDown = false;
            Volatile.Write(ref _lastExternalClickTicks, now);
        }
        if (hasWheel)
        {
            Volatile.Write(ref _lastExternalWheelTicks, now);
        }
    }

    /// <summary>
    /// Pure decision helper determining whether a WH_MOUSE_LL event should be suppressed
    /// as built-in touchpad synthesis while keeping external mouse input untouched.
    /// </summary>
    public static bool ShouldSuppressHookEvent(
        int msg,
        bool suppressEnabled,
        bool touchpadContactOrGuardActive,
        ulong dwExtraInfo,
        bool externalClickRecent,
        bool externalWheelRecent)
    {
        if (!suppressEnabled)
        {
            return false;
        }

        bool isTouchSynthesizedExtra = (dwExtraInfo & TouchSynthesizedSignatureMask) == TouchSynthesizedSignature;

        switch (msg)
        {
            case Win32Interop.WM_MOUSEMOVE:
                return touchpadContactOrGuardActive || isTouchSynthesizedExtra;

            case Win32Interop.WM_LBUTTONDOWN:
            case Win32Interop.WM_LBUTTONUP:
            case Win32Interop.WM_LBUTTONDBLCLK:
                // Swallow accidental touchpad tap-to-click while writing, never real external mouse clicks
                return (touchpadContactOrGuardActive || isTouchSynthesizedExtra) && !externalClickRecent;

            default:
                // Never suppress 2-finger scroll (so slides/PDFs still scroll), right-click (Anchor Lock), or other buttons
                return false;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<Win32Interop.MSLLHOOKSTRUCT>(lParam);
            long now = Stopwatch.GetTimestamp();

            bool touchpadGuardActive = _isTouchpadFingerDown || now < Volatile.Read(ref _suppressUntilTicks);
            bool externalClickRecent = _isExternalLeftButtonDown || (now - Volatile.Read(ref _lastExternalClickTicks)) <= ExternalInputGraceTicks;
            bool externalWheelRecent = (now - Volatile.Read(ref _lastExternalWheelTicks)) <= ExternalInputGraceTicks;
            ulong extra = unchecked((ulong)data.dwExtraInfo.ToInt64());

            if (ShouldSuppressHookEvent(msg, _suppressTouchpadCursor, touchpadGuardActive, extra, externalClickRecent, externalWheelRecent))
            {
                // Swallow touchpad-synthesized cursor movement / tap-click / scroll
                return new IntPtr(1);
            }

            switch (msg)
            {
                case Win32Interop.WM_MOUSEMOVE:
                    // Remember last confirmed non-touchpad cursor coordinate before updating
                    if (now - Volatile.Read(ref _lastExternalMoveTicks) <= ExternalInputGraceTicks)
                    {
                        _stableCursorX = data.pt.X;
                        _stableCursorY = data.pt.Y;
                        _hasStableCursor = true;
                    }
                    else
                    {
                        if (!_hasStableCursor || now - Volatile.Read(ref _lastUnattributedMoveTicks) > PreTouchSnapWindowTicks)
                        {
                            if (Win32Interop.GetCursorPos(out var prev))
                            {
                                _stableCursorX = prev.X;
                                _stableCursorY = prev.Y;
                                _hasStableCursor = true;
                            }
                        }
                        Volatile.Write(ref _lastUnattributedMoveTicks, now);
                    }
                    CursorMoved?.Invoke(data.pt.X, data.pt.Y);
                    break;

                case Win32Interop.WM_RBUTTONDOWN:
                    if (!IsRightButtonHeld)
                    {
                        IsRightButtonHeld = true;
                        RightButtonStateChanged?.Invoke(true);
                    }
                    break;

                case Win32Interop.WM_RBUTTONUP:
                    if (IsRightButtonHeld)
                    {
                        IsRightButtonHeld = false;
                        RightButtonStateChanged?.Invoke(false);
                    }
                    break;

                case Win32Interop.WM_MOUSEWHEEL:
                    short wheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
                    if (wheelDelta != 0)
                    {
                        MouseWheelObserved?.Invoke(wheelDelta);
                    }
                    break;
            }
        }

        return Win32Interop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _suppressTouchpadCursor = false;
        _isTouchpadFingerDown = false;
        Volatile.Write(ref _suppressUntilTicks, 0);

        if (_hookHandle != IntPtr.Zero)
        {
            Win32Interop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _procDelegate = null;
    }
}
