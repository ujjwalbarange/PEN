using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.UI;

namespace TrackPadLens;

/// <summary>
/// Diagnostics and Hardware Test Mode window displaying real-time hardware capabilities,
/// HID coordinate availability, report rate, input mode, active Lens dimensions, smoothing,
/// FPS, and working set memory usage.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HardwareInputManager _hardware;
    private readonly OverlayWindow _overlay;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<bool> _getDoodleEnabled;
    private readonly Func<double> _getAspectRatio;
    private readonly Action _toggleDoodle;
    private readonly Action _openCalibration;
    private readonly Action _openSettings;
    private readonly Action _saveSnapshot;
    private readonly Action<bool> _setHardwareTestMode;
    private readonly Action _openWorkspace;
    private readonly Action _regionCapture;

    private readonly DispatcherTimer _diagnosticsTimer;
    private readonly Queue<string> _rawLogLines = new(64);
    private bool _allowClose;

    public MainWindow(
        HardwareInputManager hardware,
        OverlayWindow overlay,
        HotkeyManager hotkeys,
        Func<AppSettings> getSettings,
        Func<bool> getDoodleEnabled,
        Func<double> getAspectRatio,
        Action toggleDoodle,
        Action openCalibration,
        Action openSettings,
        Action saveSnapshot,
        Action<bool> setHardwareTestMode,
        Action openWorkspace,
        Action regionCapture)
    {
        InitializeComponent();

        _hardware = hardware;
        _overlay = overlay;
        _hotkeys = hotkeys;
        _getSettings = getSettings;
        _getDoodleEnabled = getDoodleEnabled;
        _getAspectRatio = getAspectRatio;
        _toggleDoodle = toggleDoodle;
        _openCalibration = openCalibration;
        _openSettings = openSettings;
        _saveSnapshot = saveSnapshot;
        _setHardwareTestMode = setHardwareTestMode;
        _openWorkspace = openWorkspace;
        _regionCapture = regionCapture;

        // Only refresh diagnostics while the diagnostics window is visible (zero idle background timer work)
        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshDiagnostics();
                _diagnosticsTimer.Start();
            }
            else
            {
                _diagnosticsTimer.Stop();
            }
        };

        RefreshDiagnostics();
    }

    public void AllowFullClose() => _allowClose = true;

    public void AppendRawHardwareSample(in TouchpadFrameEvent frame, PalmDecision palmDecision)
    {
        if (!IsVisible || HardwareTestToggle.IsChecked != true) return;

        var sb = new StringBuilder();
        sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] contacts={frame.ReportedContactCount} palm={palmDecision}");
        for (int i = 0; i < frame.ContactLength; i++)
        {
            var c = frame.Contacts[i];
            sb.Append($" | id={c.ContactId} tip={(c.TipSwitch ? 1 : 0)} conf={(c.Confidence ? 1 : 0)} X={c.RawX} Y={c.RawY}");
            if (c.HasWidth || c.HasHeight)
            {
                sb.Append($" W={c.Width} H={c.Height}");
            }
        }

        _rawLogLines.Enqueue(sb.ToString());
        while (_rawLogLines.Count > 40) _rawLogLines.Dequeue();
        RawLogBox.Text = string.Join(Environment.NewLine, _rawLogLines);
        RawLogBox.ScrollToEnd();
    }

    public void RefreshDiagnostics()
    {
        var tp = _hardware.PrimaryTouchpad;
        var caps = tp?.Capabilities;
        var settings = _getSettings();
        bool doodleOn = _getDoodleEnabled();

        ToggleDoodleBtn.Content = doodleOn ? "Disable Doodle Mode" : "Enable Doodle Mode";
        ModeBannerText.Text = _hardware.ModeExplanation;

        TouchpadStatusText.Text = caps != null
            ? $"{caps.DeviceName} (VID_{caps.VendorId:X4} PID_{caps.ProductId:X4})"
            : "None detected (0x0D:0x05)";

        var extMice = _hardware.ExternalMice;
        MouseStatusText.Text = extMice.Count > 0
            ? $"{extMice.Count} external ({string.Join(", ", extMice.Select(m => m.Category == DeviceCategory.ExternalBluetoothMouse ? "BT" : "USB"))})"
            : $"{_hardware.Mice.Count} system pointing device(s), 0 external mouse";

        HidCoordText.Text = caps?.HasUsableAbsoluteCoordinates == true
            ? $"Available X:[{caps.LogicalMinX}..{caps.LogicalMaxX}] Y:[{caps.LogicalMinY}..{caps.LogicalMaxY}]"
            : "Unavailable (check HID descriptor)";

        ReportRateText.Text = _hardware.MeasuredReportRateHz > 0.5
            ? $"{_hardware.MeasuredReportRateHz:F1} Hz"
            : "0.0 Hz (idle)";

        InputModeText.Text = _hardware.CurrentMode switch
        {
            OperationalMode.NativeAbsoluteTouchpad => "Mode A — Native Absolute Touchpad",
            OperationalMode.CompatibleMode => "Mode B — Compatible Mode (User-Mode Raw HID)",
            _ => "Mode C — Hardware Unsupported"
        };

        CursorIsolationText.Text = _hardware.IsCursorSuppressionActive
            ? "Active (Touchpad cursor suppressed; external mouse free)"
            : settings.SuppressTouchpadCursorInDoodleMode
                ? "Ready (Suppresses touchpad cursor when Doodle Mode is ON)"
                : "Disabled in Settings";

        var lens = _overlay.CurrentLens;
        LensDimensionsText.Text = $"{lens.Width:F0} × {lens.Height:F0} DIP @ ({lens.Left:F0}, {lens.Top:F0})";
        AspectRatioText.Text = $"{_getAspectRatio():F3}:1 ({(caps?.HasReliablePhysicalDimensions == true && settings.Calibration.UsePhysicalAspectRatio ? "Physical HID" : "Manual")})";

        SmoothingParamsText.Text = $"MinCutoff={settings.OneEuroMinCutoff:F2} Beta={settings.OneEuroBeta:F3} DCutoff={settings.OneEuroDCutoff:F2}";

        using var proc = Process.GetCurrentProcess();
        double ramMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        FpsMemoryText.Text = $"{_overlay.MeasuredFps:F1} FPS  |  {ramMb:F1} MB Working Set";

        PhysicalSpecText.Text = caps == null
            ? "No touchpad descriptor"
            : caps.HasReliablePhysicalDimensions
                ? $"{caps.PhysicalWidthMm:F1}×{caps.PhysicalHeightMm:F1} mm | {caps.SupportedContacts} contact(s) | Area={(caps.SupportsContactArea ? "Yes" : "No")}"
                : $"Physical N/A | {caps.SupportedContacts} contact(s) | Area={(caps.SupportsContactArea ? "Yes" : "No")}";

        int registered = _hotkeys.Statuses.Values.Count(s => s.Registered);
        int total = _hotkeys.Statuses.Count;
        string doodleKeys = doodleOn
            ? $" + Doodle: E, 1/2/3, D, {settings.HotkeyClearInk}"
            : $" (Doodle keys E/1/2/3/D/{settings.HotkeyClearInk} armed when ON)";
        HotkeyStatusText.Text = $"{registered}/{total} active ({settings.HotkeyToggleDoodle}, {settings.HotkeyUndo}, {settings.HotkeySaveSnapshot}){doodleKeys}";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Keep running cleanly in system tray when user closes the diagnostics window
            e.Cancel = true;
            Hide();
            return;
        }
        _diagnosticsTimer.Stop();
        base.OnClosing(e);
    }

    private void ToggleDoodleBtn_Click(object sender, RoutedEventArgs e)
    {
        _toggleDoodle();
        RefreshDiagnostics();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _hardware.RefreshDevices();
        RefreshDiagnostics();
    }

    private void HardwareTestToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = HardwareTestToggle.IsChecked == true;
        _setHardwareTestMode(enabled);
        if (!enabled)
        {
            _rawLogLines.Clear();
            RawLogBox.Text = "Hardware Test Mode disabled.";
        }
        else
        {
            RawLogBox.Text = _hardware.CurrentMode == OperationalMode.HardwareUnsupported
                ? _hardware.ModeExplanation
                : "Listening for real Precision Touchpad HID reports (touch the built-in touchpad)...";
        }
    }

    private void WorkspaceBtn_Click(object sender, RoutedEventArgs e) => _openWorkspace();
    private void RegionCaptureBtn_Click(object sender, RoutedEventArgs e) => _regionCapture();
    private void CalibrationBtn_Click(object sender, RoutedEventArgs e) => _openCalibration();
    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void SnapshotBtn_Click(object sender, RoutedEventArgs e) => _saveSnapshot();
    private void HideToTrayBtn_Click(object sender, RoutedEventArgs e) => Hide();
}
