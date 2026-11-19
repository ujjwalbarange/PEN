using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.Rendering;
using TrackPadLens.Services;
using TrackPadLens.UI;

namespace TrackPadLens;

/// <summary>
/// Central application orchestrator managing single-instance locking, hardware discovery,
/// overlay & toolbar coordination, global hotkeys, system tray, crash safety, and complete
/// deterministic disposal of all Win32 handles, hooks, and native resources.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ConfigurationService? _configService;
    private StartupService? _startupService;
    private ScreenshotService? _screenshotService;
    private TrayService? _trayService;
    private HardwareInputManager? _hardware;
    private HotkeyManager? _hotkeys;
    private CalibrationManager? _calibrationManager;
    private InkEngine? _inkEngine;
    private StrokeProcessor? _strokeProcessor;
    private OverlayWindow? _overlayWindow;
    private FloatingToolbar? _toolbar;
    private MainWindow? _mainWindow;

    private AppSettings _settings = new();
    private bool _doodleModeEnabled;
    private bool _rightButtonAnchorLock;
    private double _currentLensWidthDip = 420.0;
    private double _lastCursorDipX = 500.0;
    private double _lastCursorDipY = 360.0;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "TrackPadLens_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            InitializeSubsystems(startMinimized: e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            string? logPath = TryWriteStartupErrorLog(ex);
            CleanupAllResources();
            string logHint = string.IsNullOrEmpty(logPath) ? string.Empty : $"\n\nDetails written to:\n{logPath}";
            MessageBox.Show(
                $"TrackPadLens failed to start cleanly:\n{ex.GetType().Name}: {ex.Message}{logHint}",
                "TrackPadLens Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? TryWriteStartupErrorLog(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackPadLens");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "startup-error.log");
            File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n");
            return logPath;
        }
        catch
        {
            return null;
        }
    }

    private void InitializeSubsystems(bool startMinimized)
    {
        _configService = new ConfigurationService();
        _startupService = new StartupService();
        _screenshotService = new ScreenshotService();
        _calibrationManager = new CalibrationManager();

        _settings = _configService.Load();
        _settings.StartWithWindows = _startupService.IsEnabled();
        _currentLensWidthDip = _settings.DefaultLensWidthDip;

        // 1. Hardware Input Engine first (Phase 15 rule #5)
        _hardware = new HardwareInputManager
        {
            SuppressTouchpadCursorEnabled = _settings.SuppressTouchpadCursorInDoodleMode
        };
        _hardware.Initialize();

        // 2. Ink & Processing Engine
        _inkEngine = new InkEngine
        {
            ActiveTool = _settings.ActiveTool,
            ThicknessPreset = _settings.ThicknessPreset,
            ActiveColor = ParseColorOrDefault(_settings.PenColorHex, InkEngine.PenRed)
        };

        var filter = new OneEuroFilter2D(_settings.OneEuroMinCutoff, _settings.OneEuroBeta, _settings.OneEuroDCutoff);
        var palm = new PalmRejection();
        _strokeProcessor = new StrokeProcessor(filter, palm, _inkEngine);
        _strokeProcessor.ApplySettings(_settings);

        // 3. Overlay & Floating Toolbar
        _overlayWindow = new OverlayWindow();
        _overlayWindow.BindInkEngine(_inkEngine);
        _overlayWindow.ApplyMonitorScope(_settings.MonitorScope);
        _overlayWindow.Show();

        _strokeProcessor.HoverDotChanged += dot => _overlayWindow.UpdateHoverDot(in dot);
        _strokeProcessor.LaserPointAdded += pt => _overlayWindow.AddLaserPoint(pt);

        _toolbar = new FloatingToolbar();
        WireToolbarEvents();
        SyncToolbar();
        _toolbar.Show();

        // 4. Global Hotkeys
        _hotkeys = new HotkeyManager();
        _hotkeys.Initialize();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        RegisterConfiguredHotkeys();

        // 5. Wire Hardware Events
        _hardware.TouchpadFrameReceived += OnTouchpadFrameReceived;
        _hardware.ExternalMouseUpdated += OnExternalMouseUpdated;
        _hardware.ExternalMouseWheel += OnExternalMouseWheel;
        _hardware.AnchorLockRightButtonChanged += held =>
        {
            _rightButtonAnchorLock = held;
            UpdateLensPositionFromLastCursor();
        };
        _hardware.DevicesChanged += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateLensPositionFromLastCursor();
            _mainWindow?.RefreshDiagnostics();
        });

        // 6. Diagnostics Main Window
        _mainWindow = new MainWindow(
            _hardware,
            _overlayWindow,
            _hotkeys,
            () => _settings,
            () => _doodleModeEnabled,
            GetCurrentAspectRatio,
            ToggleDoodleMode,
            OpenCalibrationWindow,
            OpenSettingsWindow,
            CaptureSnapshot,
            SetHardwareTestMode);

        // 7. System Tray
        _trayService = new TrayService(_settings.StartWithWindows);
        _trayService.EnableDoodleRequested += () => SetDoodleMode(true);
        _trayService.DisableDoodleRequested += () => SetDoodleMode(false);
        _trayService.OpenCalibrationRequested += OpenCalibrationWindow;
        _trayService.OpenSettingsRequested += OpenSettingsWindow;
        _trayService.StartWithWindowsToggled += OnStartWithWindowsToggled;
        _trayService.OpenMainWindowRequested += ShowMainWindow;
        _trayService.ExitRequested += RequestExit;

        // Seed initial cursor position for lens placement
        if (Win32Interop.GetCursorPos(out var pt))
        {
            var (dipX, dipY) = CoordinateMapper.PhysicalToDip(
                pt.X - (_overlayWindow.Left * _overlayWindow.DpiScaleX),
                pt.Y - (_overlayWindow.Top * _overlayWindow.DpiScaleY),
                _overlayWindow.DpiScaleX,
                _overlayWindow.DpiScaleY);
            _lastCursorDipX = dipX;
            _lastCursorDipY = dipY;
        }
        UpdateLensPositionFromLastCursor();

        if (!startMinimized)
        {
            _mainWindow.Show();
        }
    }

    private void WireToolbarEvents()
    {
        if (_toolbar == null || _inkEngine == null) return;

        _toolbar.ToggleDoodleRequested += ToggleDoodleMode;
        _toolbar.PenColorSelected += color =>
        {
            _inkEngine.SelectPen(color);
            _settings.ActiveTool = DrawingTool.Pen;
            _settings.PenColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.HighlighterSelected += () =>
        {
            _inkEngine.SelectHighlighter();
            _settings.ActiveTool = DrawingTool.Highlighter;
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.ThicknessSelected += preset =>
        {
            _inkEngine.ThicknessPreset = preset;
            _settings.ThicknessPreset = preset;
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.LaserSelected += () =>
        {
            _inkEngine.SelectLaser();
            _settings.ActiveTool = DrawingTool.Laser;
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.UndoRequested += () => _inkEngine.Undo();
        _toolbar.ClearRequested += HandleClearInk;
        _toolbar.SaveSnapshotRequested += CaptureSnapshot;
        _toolbar.CalibrationRequested += OpenCalibrationWindow;
        _toolbar.SettingsRequested += OpenSettingsWindow;
        _toolbar.MinimizeToTrayRequested += () =>
        {
            _toolbar.Hide();
            _mainWindow?.Hide();
            _trayService?.ShowBalloon("TrackPadLens", "Running in system tray. Press Alt+D to toggle Doodle Mode.");
        };
        _toolbar.CompactModeChanged += compact =>
        {
            _settings.CompactToolbar = compact;
            SaveSettings();
        };
    }

    private void OnTouchpadFrameReceived(TouchpadFrameEvent frame)
    {
        if (_strokeProcessor == null || _overlayWindow == null) return;

        var lens = _overlayWindow.CurrentLens;
        _strokeProcessor.ProcessTouchpadFrame(in frame, in lens, _settings.Calibration);

        if (_strokeProcessor.IsHardwareTestMode)
        {
            _mainWindow?.AppendRawHardwareSample(in frame, _strokeProcessor.LastPalmDecision);
        }
    }

    private void OnExternalMouseUpdated(ExternalMouseEvent ev)
    {
        if (_overlayWindow == null) return;

        bool anchorLocked = _rightButtonAnchorLock || MouseHookManager.IsShiftKeyPressed();
        if (anchorLocked)
        {
            _overlayWindow.UpdateLens(_overlayWindow.CurrentLens, isAnchorLocked: true);
            return;
        }

        var (dipX, dipY) = CoordinateMapper.PhysicalToDip(
            ev.CursorPhysicalX,
            ev.CursorPhysicalY,
            _overlayWindow.DpiScaleX,
            _overlayWindow.DpiScaleY);

        // Convert virtual screen DIP into overlay window-local DIP
        _lastCursorDipX = dipX - _overlayWindow.Left;
        _lastCursorDipY = dipY - _overlayWindow.Top;

        UpdateLensPositionFromLastCursor();
    }

    private void OnExternalMouseWheel(int wheelDelta)
    {
        if (!_doodleModeEnabled || _overlayWindow == null || _calibrationManager == null) return;

        var viewport = _overlayWindow.GetViewportBoundsDip();
        _currentLensWidthDip = _calibrationManager.ScaleLensWidth(_currentLensWidthDip, wheelDelta, viewport.Width);
        UpdateLensPositionFromLastCursor();

        double scaleMultiplier = _calibrationManager.ComputeDisplayScaleMultiplier(_currentLensWidthDip, _settings.DefaultLensWidthDip);
        _overlayWindow.ShowScaleIndicator(scaleMultiplier);
    }

    private void UpdateLensPositionFromLastCursor()
    {
        if (_overlayWindow == null) return;

        bool anchorLocked = _rightButtonAnchorLock || MouseHookManager.IsShiftKeyPressed();
        var viewport = _overlayWindow.GetViewportBoundsDip();
        var rect = CoordinateMapper.ComputeLensRect(
            _lastCursorDipX,
            _lastCursorDipY,
            _currentLensWidthDip,
            GetCurrentAspectRatio(),
            _settings.AnchorMode,
            _settings.AnchorOffsetX,
            _settings.AnchorOffsetY,
            in viewport);

        _overlayWindow.UpdateLens(in rect, anchorLocked);
    }

    private double GetCurrentAspectRatio()
    {
        return _calibrationManager?.ResolveAspectRatio(_hardware?.PrimaryTouchpad?.Capabilities, _settings.Calibration) ?? 1.6;
    }

    private void ToggleDoodleMode() => SetDoodleMode(!_doodleModeEnabled);

    private void SetDoodleMode(bool enable)
    {
        if (enable && _hardware?.CurrentMode == OperationalMode.HardwareUnsupported)
        {
            _doodleModeEnabled = false;
            _overlayWindow?.SetLensVisibility(false);
            ShowMainWindow();
            _trayService?.ShowBalloon(
                "Touchpad Hardware Unsupported",
                "No Precision Touchpad with usable absolute HID coordinates was found. Check Diagnostics for details.",
                System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        _doodleModeEnabled = enable;
        if (_strokeProcessor != null)
        {
            _strokeProcessor.IsDoodleModeEnabled = enable;
            if (!enable) _strokeProcessor.CancelActiveStroke();
        }

        _hardware?.SetDoodleModeCursorIsolation(enable);
        _overlayWindow?.SetLensVisibility(enable);
        _trayService?.UpdateDoodleState(enable);
        SyncToolbar();
        _mainWindow?.RefreshDiagnostics();
    }

    private void SetHardwareTestMode(bool enabled)
    {
        if (_hardware != null) _hardware.HardwareTestMode = enabled;
        if (_strokeProcessor != null) _strokeProcessor.IsHardwareTestMode = enabled;
    }

    private void OnHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleDoodleMode:
                ToggleDoodleMode();
                break;
            case HotkeyAction.Undo:
                _inkEngine?.Undo();
                break;
            case HotkeyAction.SaveSnapshot:
                CaptureSnapshot();
                break;
            case HotkeyAction.ClearInk:
                HandleClearInk();
                break;
        }
    }

    private void HandleClearInk()
    {
        _strokeProcessor?.CancelActiveStroke();
        _overlayWindow?.ClearLaser();

        if (_inkEngine == null || _inkEngine.Strokes.Count == 0) return;

        // ClearAll pushes a single reversible batch onto the undo stack so Ctrl+Z immediately restores all strokes
        _inkEngine.ClearAll();
        _trayService?.ShowBalloon("Ink Cleared", "Press Ctrl+Z to restore cleared strokes.");
    }

    private void CaptureSnapshot()
    {
        if (_screenshotService == null || _overlayWindow == null) return;

        var result = _screenshotService.Capture(
            _settings.MonitorScope,
            beforeCapture: () =>
            {
                // Keep ink visible while hiding transient toolbar/cyan alignment box during capture
                _overlayWindow.SetTemporaryFrameHiddenForCapture(true);
                _toolbar?.Hide();
                Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            },
            afterCapture: () =>
            {
                _overlayWindow.SetTemporaryFrameHiddenForCapture(false);
                _toolbar?.Show();
            });

        if (result.Success)
        {
            _trayService?.ShowBalloon("Snapshot Saved", result.FilePath ?? "Saved to Pictures\\TrackPadLens");
        }
        else
        {
            _trayService?.ShowBalloon("Snapshot Failed", result.ErrorMessage ?? "Capture unavailable.", System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void RegisterConfiguredHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        _hotkeys.Register(HotkeyAction.ToggleDoodleMode, _settings.HotkeyToggleDoodle);
        _hotkeys.Register(HotkeyAction.Undo, _settings.HotkeyUndo);
        _hotkeys.Register(HotkeyAction.SaveSnapshot, _settings.HotkeySaveSnapshot);
        _hotkeys.Register(HotkeyAction.ClearInk, _settings.HotkeyClearInk);
    }

    private void OpenCalibrationWindow()
    {
        var win = new CalibrationWindow(_hardware?.PrimaryTouchpad?.Capabilities, _settings.Calibration, updated =>
        {
            _settings.Calibration = updated;
            SaveSettings();
            UpdateLensPositionFromLastCursor();
            _mainWindow?.RefreshDiagnostics();
        });
        win.ShowDialog();
    }

    private void OpenSettingsWindow()
    {
        var win = new SettingsWindow(_settings, updated =>
        {
            _settings = updated;
            SaveSettings();
            if (_hardware != null)
            {
                _hardware.SuppressTouchpadCursorEnabled = _settings.SuppressTouchpadCursorInDoodleMode;
                _hardware.SetDoodleModeCursorIsolation(_doodleModeEnabled);
            }
            _strokeProcessor?.ApplySettings(_settings);
            _overlayWindow?.ApplyMonitorScope(_settings.MonitorScope);
            RegisterConfiguredHotkeys();
            OnStartWithWindowsToggled(_settings.StartWithWindows);
            UpdateLensPositionFromLastCursor();
            _mainWindow?.RefreshDiagnostics();
        });
        win.ShowDialog();
    }

    private void OnStartWithWindowsToggled(bool enable)
    {
        bool ok = _startupService?.SetEnabled(enable) ?? false;
        _settings.StartWithWindows = ok && enable;
        _trayService?.UpdateStartWithWindowsState(_settings.StartWithWindows);
        SaveSettings();
    }

    private void ShowMainWindow()
    {
        if (_toolbar != null && !_toolbar.IsVisible) _toolbar.Show();
        if (_mainWindow == null) return;
        _mainWindow.RefreshDiagnostics();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    private void SyncToolbar()
    {
        if (_toolbar == null || _inkEngine == null) return;
        _toolbar.SyncState(
            _doodleModeEnabled,
            _inkEngine.ActiveTool,
            _inkEngine.ActiveColor,
            _inkEngine.ThicknessPreset,
            _settings.CompactToolbar);
    }

    private void SaveSettings() => _configService?.Save(_settings);

    private static Color ParseColorOrDefault(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                return c;
            }
        }
        catch
        {
            // Ignore malformed config color string
        }
        return fallback;
    }

    private void RequestExit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        CleanupAllResources();
        Shutdown(0);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep mouse/system safe even if an unexpected UI exception happens
        _hardware?.SetDoodleModeCursorIsolation(false);
        e.Handled = true;
        _trayService?.ShowBalloon("TrackPadLens Recovered", $"Unexpected UI error handled safely: {e.Exception.Message}", System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        CleanupAllResources();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupAllResources();
        base.OnExit(e);
    }

    private void CleanupAllResources()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;

        _hardware?.Dispose();
        _hardware = null;

        _trayService?.Dispose();
        _trayService = null;

        if (_mainWindow != null)
        {
            _mainWindow.AllowFullClose();
            _mainWindow.Close();
            _mainWindow = null;
        }

        if (_toolbar != null)
        {
            _toolbar.Close();
            _toolbar = null;
        }

        if (_overlayWindow != null)
        {
            _overlayWindow.Dispose();
            _overlayWindow.Close();
            _overlayWindow = null;
        }

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }
}
