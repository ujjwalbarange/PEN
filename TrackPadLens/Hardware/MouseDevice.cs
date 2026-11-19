namespace TrackPadLens.Hardware;

/// <summary>
/// Represents a discovered mouse pointing device (USB, Bluetooth, or system pointing collection).
/// </summary>
public sealed class MouseDevice
{
    public IntPtr DeviceHandle { get; }
    public string DevicePath { get; }
    public string DeviceName { get; }
    public DeviceCategory Category { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public int ButtonCount { get; }
    public int SampleRateHz { get; }

    public bool IsExternalMouse =>
        Category == DeviceCategory.ExternalUsbMouse ||
        Category == DeviceCategory.ExternalBluetoothMouse;

    public MouseDevice(
        IntPtr deviceHandle,
        string devicePath,
        string deviceName,
        DeviceCategory category,
        ushort vendorId,
        ushort productId,
        int buttonCount,
        int sampleRateHz)
    {
        DeviceHandle = deviceHandle;
        DevicePath = devicePath;
        DeviceName = deviceName;
        Category = category;
        VendorId = vendorId;
        ProductId = productId;
        ButtonCount = buttonCount;
        SampleRateHz = sampleRateHz;
    }
}
