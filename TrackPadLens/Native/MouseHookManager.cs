using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrackPadLens.Native;

/// <summary>
/// Passive low-level mouse observer (WH_MOUSE_LL).
///
/// IMPORTANT WINDOWS COMPATIBILITY DESIGN:
/// - WH_MOUSE_LL does NOT expose the originating Raw Input device handle.
/// - LLMHF_INJECTED only marks software-injected events, NOT built-in touchpad vs external mouse.
/// - Therefore, this hook NEVER blocks or suppresses mouse movement or clicks, and NEVER claims
///   to isolate devices via LLMHF_INJECTED.
/// - It strictly observes right-button hold (for Anchor Lock) and cursor coordinates/wheel while
///   preserving 100% of normal Windows mouse and right-click behavior.
/// </summary>
public sealed class MouseHookManager : IDisposable
{
    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32Interop.LowLevelMouseProc? _procDelegate;
    private bool _disposed;

    public bool IsInstalled => _hookHandle != IntPtr.Zero;
    public bool IsRightButtonHeld { get; private set; }

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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<Win32Interop.MSLLHOOKSTRUCT>(lParam);

            switch (msg)
            {
                case Win32Interop.WM_MOUSEMOVE:
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

        // Always pass through untouched so external mouse clicks and scrolling are never swallowed
        return Win32Interop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            Win32Interop.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _procDelegate = null;
    }
}
