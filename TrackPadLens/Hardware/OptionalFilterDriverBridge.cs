namespace TrackPadLens.Hardware;

/// <summary>
/// Optional integration seam for a separately installed, WHQL-signed KMDF HID filter driver
/// capable of suppressing built-in touchpad mouse-cursor synthesis while leaving external USB
/// and Bluetooth mice unaffected.
///
/// TrackPadLens never silently installs drivers, modifies system devices, disables hardware,
/// or requests administrator privileges.
/// </summary>
public interface ICursorIsolationProvider : IDisposable
{
    bool IsDriverAvailable { get; }
    string StatusDescription { get; }
    bool TryEnableTouchpadCursorSuppression(string touchpadDevicePath);
    void DisableTouchpadCursorSuppression();
}

/// <summary>
/// Default standard-user implementation when no optional signed HID filter driver is installed.
/// Honestly reports that per-device cursor suppression is not available in pure user mode.
/// </summary>
public sealed class UserModeNoDriverIsolationProvider : ICursorIsolationProvider
{
    public bool IsDriverAvailable => false;

    public string StatusDescription =>
        "User-mode only (no signed HID filter driver installed). Raw absolute touchpad packets are read " +
        "natively when exposed by the Precision Touchpad, while Windows may still move the system cursor. " +
        "External mouse operation is never blocked.";

    public bool TryEnableTouchpadCursorSuppression(string touchpadDevicePath) => false;

    public void DisableTouchpadCursorSuppression()
    {
        // No driver state to release in user mode.
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }
}
