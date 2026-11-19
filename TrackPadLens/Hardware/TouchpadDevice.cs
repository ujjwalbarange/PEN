using System.Runtime.InteropServices;
using TrackPadLens.Native;

namespace TrackPadLens.Hardware;

/// <summary>
/// Represents a discovered Precision Touchpad with pinned preparsed descriptor memory
/// for zero-allocation HidP_GetUsageValue decoding on the hot input path.
/// </summary>
public sealed class TouchpadDevice : IDisposable
{
    private GCHandle _pinnedPreparsed;
    private bool _hasPinnedHandle;
    private bool _disposed;

    public IntPtr DeviceHandle { get; }
    public string DevicePath { get; }
    public TouchpadDescriptorLayout? Layout { get; }
    public HardwareCapabilityReport Capabilities { get; }
    public IntPtr PreparsedPointer => _hasPinnedHandle ? _pinnedPreparsed.AddrOfPinnedObject() : IntPtr.Zero;

    public TouchpadDevice(
        IntPtr deviceHandle,
        string devicePath,
        byte[]? preparsedBytes,
        TouchpadDescriptorLayout? layout,
        HardwareCapabilityReport capabilities)
    {
        DeviceHandle = deviceHandle;
        DevicePath = devicePath;
        Layout = layout;
        Capabilities = capabilities;

        if (preparsedBytes != null && preparsedBytes.Length > 0)
        {
            _pinnedPreparsed = GCHandle.Alloc(preparsedBytes, GCHandleType.Pinned);
            _hasPinnedHandle = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hasPinnedHandle)
        {
            _pinnedPreparsed.Free();
            _hasPinnedHandle = false;
        }
    }
}
