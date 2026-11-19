using System.IO;
using System.Runtime.InteropServices;
using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.Services;
using Xunit;

namespace TrackPadLens.Tests;

/// <summary>
/// Validates HID descriptor analysis, USB HID 1.11 unit conversion, x64 Win32 struct sizes,
/// device classification, aspect ratio / scaling bounds, hotkey parsing, screenshot paths,
/// and deterministic resource disposal.
/// </summary>
public sealed class HidDescriptorAndSystemTests
{
    [Fact]
    public void HidUnitConverter_DecodesSiCentimetersAndEnglishInchesAccurately()
    {
        // SI Linear length (0x11) with exponent -2 (0x0E): 1200 * 10^-2 cm = 12.0 cm = 120.0 mm
        double? mmSi = HidUnitConverter.TryCalculatePhysicalSpanMm(0, 1200, hidUnit: 0x11, hidUnitExp: 0x0E);
        Assert.NotNull(mmSi);
        Assert.Equal(120.0, mmSi!.Value, 4);

        // English Linear length (0x13) with exponent -3 (0x0D): 4000 * 10^-3 in = 4.0 in = 101.6 mm
        double? mmIn = HidUnitConverter.TryCalculatePhysicalSpanMm(0, 4000, hidUnit: 0x13, hidUnitExp: 0x0D);
        Assert.NotNull(mmIn);
        Assert.Equal(101.6, mmIn!.Value, 4);

        // Missing physical range returns null rather than guessing
        Assert.Null(HidUnitConverter.TryCalculatePhysicalSpanMm(0, 0, hidUnit: 0x11, hidUnitExp: 0x0E));
    }

    [Fact]
    public void HidTouchpadReader_ParsesMultiContactDescriptorAndRejectsRelativeOnlyDescriptors()
    {
        var validValueCaps = new[]
        {
            new Win32Interop.HIDP_VALUE_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                Usage = Win32Interop.HID_USAGE_DIGITIZER_CONTACT_COUNT,
                ReportID = 1,
                LinkCollection = 0
            },
            new Win32Interop.HIDP_VALUE_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_GENERIC,
                Usage = Win32Interop.HID_USAGE_GENERIC_X,
                ReportID = 1,
                LinkCollection = 1,
                IsAbsolute = 1,
                LogicalMin = 0,
                LogicalMax = 3200,
                PhysicalMin = 0,
                PhysicalMax = 1200,
                Units = 0x11,
                UnitsExp = 0x0E
            },
            new Win32Interop.HIDP_VALUE_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_GENERIC,
                Usage = Win32Interop.HID_USAGE_GENERIC_Y,
                ReportID = 1,
                LinkCollection = 1,
                IsAbsolute = 1,
                LogicalMin = 0,
                LogicalMax = 2000,
                PhysicalMin = 0,
                PhysicalMax = 750,
                Units = 0x11,
                UnitsExp = 0x0E
            },
            new Win32Interop.HIDP_VALUE_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                Usage = Win32Interop.HID_USAGE_DIGITIZER_CONTACT_ID,
                ReportID = 1,
                LinkCollection = 1
            }
        };

        var validButtonCaps = new[]
        {
            new Win32Interop.HIDP_BUTTON_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_DIGITIZER,
                Usage = Win32Interop.HID_USAGE_DIGITIZER_TIP_SWITCH,
                ReportID = 1,
                LinkCollection = 1
            }
        };

        var layout = HidTouchpadReader.BuildLayoutFromCaps(64, validValueCaps, validButtonCaps);
        Assert.True(layout.HasUsableAbsoluteCoordinates);
        Assert.True(layout.HasReliablePhysicalDimensions);
        Assert.Equal(120.0, layout.PhysicalWidthMm!.Value, 2);
        Assert.Equal(75.0, layout.PhysicalHeightMm!.Value, 2);
        Assert.True(layout.SupportsTipSwitch);
        Assert.True(layout.SupportsContactId);
        Assert.Single(layout.ContactSlots);

        // Relative-only descriptor (IsAbsolute = 0) must be rejected cleanly
        var relativeOnly = new[]
        {
            new Win32Interop.HIDP_VALUE_CAPS
            {
                UsagePage = Win32Interop.HID_USAGE_PAGE_GENERIC,
                Usage = Win32Interop.HID_USAGE_GENERIC_X,
                LinkCollection = 1,
                IsAbsolute = 0,
                LogicalMin = -127,
                LogicalMax = 127
            }
        };
        var rejected = HidTouchpadReader.BuildLayoutFromCaps(16, relativeOnly, ReadOnlySpan<Win32Interop.HIDP_BUTTON_CAPS>.Empty);
        Assert.False(rejected.HasUsableAbsoluteCoordinates);
    }

    [Fact]
    public void Win32Structs_MatchExpectedX64NativeByteSizes()
    {
        Assert.Equal(72, Marshal.SizeOf<Win32Interop.HIDP_VALUE_CAPS>());
        Assert.Equal(72, Marshal.SizeOf<Win32Interop.HIDP_BUTTON_CAPS>());
        Assert.Equal(24, Marshal.SizeOf<Win32Interop.RAWINPUTHEADER>());
    }

    [Fact]
    public void DeviceEnumerator_ClassifiesUsbBluetoothAndTouchpadPathsAccurately()
    {
        Assert.Equal(
            DeviceCategory.BuiltInPrecisionTouchpad,
            DeviceEnumerator.ClassifyDevicePath(@"\\?\HID#VEN_ELAN&DEV_1200#...", isDigitizerTouchpad: true));

        Assert.Equal(
            DeviceCategory.ExternalBluetoothMouse,
            DeviceEnumerator.ClassifyDevicePath(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b023#...", isDigitizerTouchpad: false));

        Assert.Equal(
            DeviceCategory.ExternalUsbMouse,
            DeviceEnumerator.ClassifyDevicePath(@"\\?\HID#VID_046D&PID_C077&MI_00#7&1234#...", isDigitizerTouchpad: false));

        var (vid, pid) = DeviceEnumerator.ExtractVidPidFromPath(@"\\?\HID#VID_046D&PID_C077&MI_00");
        Assert.Equal((ushort)0x046D, vid);
        Assert.Equal((ushort)0xC077, pid);
    }

    [Fact]
    public void CalibrationManager_ResolvesPhysicalAspectRatioAndClampsWheelBounds()
    {
        var manager = new CalibrationManager();
        var hw = new HardwareCapabilityReport
        {
            PhysicalWidthMm = 128.0,
            PhysicalHeightMm = 80.0,
            HasReliablePhysicalDimensions = true
        };
        var profile = new CalibrationProfile { UsePhysicalAspectRatio = true };

        Assert.Equal(1.6, manager.ResolveAspectRatio(hw, profile), 4);
        Assert.Equal(100.0, manager.ScaleLensWidth(105.0, wheelDelta: -240, usableScreenWidthDip: 1920.0));
        Assert.Equal(1728.0, manager.ScaleLensWidth(1700.0, wheelDelta: +600, usableScreenWidthDip: 1920.0), 4);
    }

    [Fact]
    public void HotkeyManager_ParsesValidShortcutsAndRejectsMalformedStrings()
    {
        Assert.True(HotkeyManager.TryParseShortcut("Alt+D", out var altD));
        Assert.Equal(Win32Interop.MOD_ALT, altD.Modifiers);

        Assert.True(HotkeyManager.TryParseShortcut("Ctrl+Z", out var ctrlZ));
        Assert.Equal(Win32Interop.MOD_CONTROL, ctrlZ.Modifiers);

        Assert.True(HotkeyManager.TryParseShortcut("Esc", out var esc));
        Assert.Equal(0u, esc.Modifiers);

        Assert.False(HotkeyManager.TryParseShortcut("Ctrl+Alt+", out _));
        Assert.False(HotkeyManager.TryParseShortcut("A+B", out _));
    }

    [Fact]
    public void ScreenshotService_FormatsExpectedTimestampedPathInPicturesTrackPadLens()
    {
        var ts = new DateTime(2026, 11, 19, 14, 5, 9);
        string path = ScreenshotService.BuildSnapshotPath(ts, @"C:\Users\Test\Pictures");
        Assert.Equal(Path.Combine(@"C:\Users\Test\Pictures", "TrackPadLens", "TrackPadLens_20261119_140509.png"), path);
    }

    [Fact]
    public void ConfigurationService_RoundTripsSettingsSafely()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tpl_test_{Guid.NewGuid():N}.json");
        try
        {
            var svc = new ConfigurationService(tempFile);
            var original = new AppSettings
            {
                AnchorMode = AnchorMode.TopLeft,
                DefaultLensWidthDip = 520,
                OneEuroMinCutoff = 2.4,
                HotkeyToggleDoodle = "Alt+W"
            };
            svc.Save(original);

            var reloaded = svc.Load();
            Assert.Equal(AnchorMode.TopLeft, reloaded.AnchorMode);
            Assert.Equal(520, reloaded.DefaultLensWidthDip);
            Assert.Equal(2.4, reloaded.OneEuroMinCutoff, 4);
            Assert.Equal("Alt+W", reloaded.HotkeyToggleDoodle);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
