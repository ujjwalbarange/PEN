using System.Windows;
using System.Windows.Interop;

namespace TrackPadLens.Native;

/// <summary>
/// Manages Win32 extended styles and WM_NCHITTEST click-through semantics for the overlay
/// and floating toolbar without triggering focus theft or window flicker.
/// </summary>
public static class WindowStyles
{
    /// <summary>
    /// Configures a full-screen drawing overlay window as permanently click-through,
    /// hidden from Alt-Tab, layered, and non-activating so PowerPoint / PDF apps never lose focus.
    /// </summary>
    public static void ApplyClickThroughOverlayStyles(Window window)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        long exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
        exStyle |= Win32Interop.WS_EX_LAYERED
                 | Win32Interop.WS_EX_TRANSPARENT
                 | Win32Interop.WS_EX_TOOLWINDOW
                 | Win32Interop.WS_EX_NOACTIVATE;

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));
        Win32Interop.SetWindowPos(
            hwnd,
            Win32Interop.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED);

        // Reinforce hit-test transparency at the WndProc level because some WPF
        // AllowsTransparency layered configurations still receive hit-tests on opaque ink pixels.
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(AlwaysTransparentHitTestHook);
    }

    /// <summary>
    /// Configures the floating pill toolbar as topmost, hidden from Alt-Tab, and non-activating
    /// so clicking toolbar buttons never steals keyboard/slide focus from PowerPoint.
    /// </summary>
    public static void ApplyFloatingToolbarStyles(Window window)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        long exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
        exStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));
        Win32Interop.SetWindowPos(
            hwnd,
            Win32Interop.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Attaches a selective WM_NCHITTEST hook for hybrid overlays where only specific screen rectangles
    /// should accept pointer input while all other coordinates pass straight through.
    /// </summary>
    public static HwndSourceHook AttachSelectiveHitTestHook(Window window, Func<int, int, bool> isInteractivePointPx)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("Window HWND must be initialized before attaching hit-test hook.");

        IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32Interop.WM_NCHITTEST)
            {
                int screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                if (!isInteractivePointPx(screenX, screenY))
                {
                    handled = true;
                    return new IntPtr(Win32Interop.HTTRANSPARENT);
                }
            }
            return IntPtr.Zero;
        }

        source.AddHook(Hook);
        return Hook;
    }

    private static IntPtr AlwaysTransparentHitTestHook(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == Win32Interop.WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(Win32Interop.HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }
}
