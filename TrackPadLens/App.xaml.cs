using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TrackPadLens.Commands;
using TrackPadLens.Core;
using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.Rendering;
using TrackPadLens.Services;
using TrackPadLens.Storage;
using TrackPadLens.UI;

namespace TrackPadLens;

/// <summary>
/// Central application orchestrator managing single-instance locking, hardware discovery,
/// overlay & toolbar coordination, Whiteboard & Smart Study Workspace, Autosave/Recovery,
/// global hotkeys, system tray, crash safety, and deterministic disposal of all Win32 resources.
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
    private WorkspaceWindow? _workspaceWindow;
    private ModeManager? _modeManager;
    private ShortcutManager? _shortcutManager;
    private AutosaveService? _autosaveService;

    private AppSettings _settings = new();
    private bool _doodleModeEnabled;
    private bool _modalDialogOpen;
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
        _modeManager = new ModeManager();
        _shortcutManager = new ShortcutManager();

        _settings = _configService.Load();
        _settings.StartWithWindows = _startupService.IsEnabled();
        _shortcutManager.ApplyOverrides(_settings.ShortcutOverrides);
        _currentLensWidthDip = _settings.DefaultLensWidthDip;

        // 1. Hardware Input Engine first
        _hardware = new HardwareInputManager
        {
            SuppressTouchpadCursorEnabled = _settings.SuppressTouchpadCursorInDoodleMode
        };
        _hardware.Initialize();

        // 2. Ink & Processing Engine
        var savedPenColor = ParseColorOrDefault(_settings.PenColorHex, InkEngine.PenRed);
        _inkEngine = new InkEngine
        {
            ThicknessPreset = _settings.ThicknessPreset
        };
        _inkEngine.SetInitialPenColor(savedPenColor);
        switch (_settings.ActiveTool)
        {
            case DrawingTool.Highlighter:
                _inkEngine.SelectHighlighter();
                break;
            case DrawingTool.Laser:
                _inkEngine.SelectLaser();
                break;
            case DrawingTool.Eraser:
                _inkEngine.SelectEraser();
                break;
            default:
                _inkEngine.SelectPen(savedPenColor);
                break;
        }

        var filter = new OneEuroFilter2D(_settings.OneEuroMinCutoff, _settings.OneEuroBeta, _settings.OneEuroDCutoff);
        var palm = new PalmRejection();
        _strokeProcessor = new StrokeProcessor(filter, palm, _inkEngine);
        _strokeProcessor.ApplySettings(_settings);

        // 3. Overlay & Floating Toolbar
        _overlayWindow = new OverlayWindow();
        _overlayWindow.BindInkEngine(_inkEngine);
        _overlayWindow.ApplyMonitorScope(_settings.MonitorScope);
        _overlayWindow.ApplyLensAppearance(
            ParseColorOrDefault(_settings.LensBorderColorHex, Color.FromRgb(0x00, 0xD2, 0xFF)),
            _settings.LensBorderOpacity);
        _overlayWindow.Show();

        _strokeProcessor.HoverDotChanged += dot => _overlayWindow.UpdateHoverDot(in dot);
        _strokeProcessor.LaserPointAdded += pt => _overlayWindow.AddLaserPoint(pt);

        _toolbar = new FloatingToolbar();
        WireToolbarEvents();
        SyncToolbar();
        _toolbar.Show();

        // 4. Autosave & Unified Whiteboard / Smart Study Workspace
        _autosaveService = new AutosaveService(new ProjectSerializer(), intervalSeconds: _settings.AutosaveIntervalSeconds);
        _workspaceWindow = new WorkspaceWindow(
            () => _settings,
            SaveSettings,
            OpenSettingsWindow,
            ShowMainWindow,
            ActivateTrackpadLensMode,
            TriggerRegionScreenshotCapture,
            _modeManager,
            _autosaveService);

        bool hadRecovery = _autosaveService.HasRecoverySnapshot();
        _autosaveService.Start(
            () => _workspaceWindow?.Document,
            () => _workspaceWindow?.Assets,
            _settings.AutosaveIntervalSeconds);

        // 5. Global Hotkeys
        _hotkeys = new HotkeyManager();
        _hotkeys.Initialize();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        RegisterConfiguredHotkeys();

        // 6. Wire Hardware Events
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

        // 7. Diagnostics Main Window
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
            SetHardwareTestMode,
            ShowWorkspaceWindow,
            TriggerRegionScreenshotCapture);

        // 8. System Tray
        _trayService = new TrayService(_settings.StartWithWindows);
        _trayService.EnableDoodleRequested += () => SetDoodleMode(true);
        _trayService.DisableDoodleRequested += () => SetDoodleMode(false);
        _trayService.OpenWorkspaceRequested += ShowWorkspaceWindow;
        _trayService.RegionCaptureRequested += TriggerRegionScreenshotCapture;
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

        if (hadRecovery && _autosaveService != null)
        {
            var restoreChoice = MessageBox.Show(
                "An autosaved Whiteboard & Study session was found from a previous session.\n\nRestore it now?",
                "TrackPadLens — Session Recovery",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (restoreChoice == MessageBoxResult.Yes)
            {
                ShowWorkspaceWindow();
                _workspaceWindow?.TryLoadProjectFile(_autosaveService.RecoveryFilePath);
            }
            else
            {
                _autosaveService.ClearRecoverySnapshot();
            }
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
        _toolbar.EraserSelected += () =>
        {
            _inkEngine.SelectEraser();
            _settings.ActiveTool = DrawingTool.Eraser;
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.ToggleSizeLockRequested += () =>
        {
            _settings.LensSizeLocked = !_settings.LensSizeLocked;
            SaveSettings();
            SyncToolbar();
        };
        _toolbar.OpenWorkspaceRequested += ShowWorkspaceWindow;
        _toolbar.UndoRequested += () => _inkEngine.Undo();
        _toolbar.ClearRequested += HandleClearInk;
        _toolbar.SaveSnapshotRequested += CaptureSnapshot;
        _toolbar.CalibrationRequested += OpenCalibrationWindow;
        _toolbar.SettingsRequested += OpenSettingsWindow;
        _toolbar.MinimizeToTrayRequested += () =>
        {
            _toolbar.Hide();
            _mainWindow?.Hide();
            _trayService?.ShowBalloon("TrackPadLens", "Running in system tray. Press Alt+D for Doodle Mode or Alt+W for Whiteboard Workspace.");
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

        _lastCursorDipX = dipX - _overlayWindow.Left;
        _lastCursorDipY = dipY - _overlayWindow.Top;

        UpdateLensPositionFromLastCursor();
    }

    private void OnExternalMouseWheel(int wheelDelta)
    {
        if (!_doodleModeEnabled || _settings.LensSizeLocked || _overlayWindow == null || _calibrationManager == null) return;

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

    private void ActivateTrackpadLensMode()
    {
        _modeManager?.SwitchTo(WorkspaceEnvironment.TrackpadLens);
        SetDoodleMode(true);
    }

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
        if (enable) _modeManager?.SwitchTo(WorkspaceEnvironment.TrackpadLens);
        else _modeManager?.SwitchTo(WorkspaceEnvironment.StandardOverlay);

        if (_strokeProcessor != null)
        {
            _strokeProcessor.IsDoodleModeEnabled = enable;
            if (!enable) _strokeProcessor.CancelActiveStroke();
        }

        _hardware?.SetDoodleModeCursorIsolation(enable);
        UpdateDoodleModeHotkeys(enable && !_modalDialogOpen);
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
            case HotkeyAction.OpenTrackpadLensMode:
                ActivateTrackpadLensMode();
                break;
            case HotkeyAction.OpenWhiteboardWorkspace:
                ShowWorkspaceWindow();
                break;
            case HotkeyAction.RegionScreenshotCapture:
                TriggerRegionScreenshotCapture();
                break;
            case HotkeyAction.Undo:
                _inkEngine?.Undo();
                break;
            case HotkeyAction.SaveSnapshot:
                CaptureSnapshot();
                break;
            case HotkeyAction.ClearInk:
                if (_doodleModeEnabled) HandleClearInk();
                break;
            case HotkeyAction.DoodleSelectEraser:
                if (_doodleModeEnabled && _inkEngine != null)
                {
                    _inkEngine.SelectEraser();
                    _settings.ActiveTool = DrawingTool.Eraser;
                    SaveSettings();
                    SyncToolbar();
                }
                break;
            case HotkeyAction.DoodleThicknessThin:
                if (_doodleModeEnabled && _inkEngine != null)
                {
                    _inkEngine.ThicknessPreset = StrokeThicknessPreset.Thin;
                    _settings.ThicknessPreset = StrokeThicknessPreset.Thin;
                    SaveSettings();
                    SyncToolbar();
                }
                break;
            case HotkeyAction.DoodleThicknessMedium:
                if (_doodleModeEnabled && _inkEngine != null)
                {
                    _inkEngine.ThicknessPreset = StrokeThicknessPreset.Medium;
                    _settings.ThicknessPreset = StrokeThicknessPreset.Medium;
                    SaveSettings();
                    SyncToolbar();
                }
                break;
            case HotkeyAction.DoodleThicknessThick:
                if (_doodleModeEnabled && _inkEngine != null)
                {
                    _inkEngine.ThicknessPreset = StrokeThicknessPreset.Thick;
                    _settings.ThicknessPreset = StrokeThicknessPreset.Thick;
                    SaveSettings();
                    SyncToolbar();
                }
                break;
            case HotkeyAction.DoodlePenCycle:
                if (_doodleModeEnabled && _inkEngine != null)
                {
                    var penColor = _inkEngine.SelectOrCyclePen();
                    _settings.ActiveTool = DrawingTool.Pen;
                    _settings.PenColorHex = $"#{penColor.R:X2}{penColor.G:X2}{penColor.B:X2}";
                    SaveSettings();
                    SyncToolbar();
                }
                break;
        }
    }

    private void ShowWorkspaceWindow()
    {
        if (_doodleModeEnabled)
        {
            SetDoodleMode(false);
        }

        _modeManager?.SwitchTo(WorkspaceEnvironment.Whiteboard);
        if (_workspaceWindow == null) return;
        _workspaceWindow.Show();
        if (_workspaceWindow.WindowState == WindowState.Minimized)
        {
            _workspaceWindow.WindowState = WindowState.Normal;
        }
        _workspaceWindow.Activate();
    }

    private void TriggerRegionScreenshotCapture()
    {
        if (_modalDialogOpen) return;
        bool wasDoodle = _doodleModeEnabled;
        if (wasDoodle) SetDoodleMode(false);

        _modalDialogOpen = true;
        UpdateDoodleModeHotkeys(false);

        bool toolbarWasVisible = _toolbar?.IsVisible == true;
        _overlayWindow?.SetTemporaryFrameHiddenForCapture(true);
        _toolbar?.Hide();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        byte[]? captured = null;
        try
        {
            var captureWin = new RegionCaptureWindow();
            if (captureWin.ShowDialog() == true)
            {
                captured = captureWin.CapturedPngBytes;
            }
        }
        finally
        {
            _overlayWindow?.SetTemporaryFrameHiddenForCapture(false);
            if (toolbarWasVisible) _toolbar?.Show();
            _modalDialogOpen = false;
            UpdateDoodleModeHotkeys(_doodleModeEnabled);
        }

        if (captured != null && captured.Length > 0)
        {
            ShowWorkspaceWindow();
            _workspaceWindow?.InsertCapturedRegionPng(captured);
        }
    }

    private void HandleClearInk()
    {
        _strokeProcessor?.CancelActiveStroke();
        _overlayWindow?.ClearLaser();

        if (_inkEngine == null || _inkEngine.Strokes.Count == 0) return;

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
        _hotkeys.Register(HotkeyAction.OpenTrackpadLensMode, _settings.HotkeyTrackpadLens);
        _hotkeys.Register(HotkeyAction.OpenWhiteboardWorkspace, _settings.HotkeyWhiteboard);
        _hotkeys.Register(HotkeyAction.RegionScreenshotCapture, _settings.HotkeyRegionCapture);
        _hotkeys.Register(HotkeyAction.Undo, _settings.HotkeyUndo);
        _hotkeys.Register(HotkeyAction.SaveSnapshot, _settings.HotkeySaveSnapshot);
        UpdateDoodleModeHotkeys(_doodleModeEnabled && !_modalDialogOpen);
    }

    /// <summary>
    /// Registers single-key drawing bindings (E, 1, 2, 3, D, and Clear Ink [default C])
    /// ONLY while Doodle Mode is ON and no modal dialog is open,
    /// so normal typing in other apps or settings fields is never intercepted.
    /// </summary>
    private void UpdateDoodleModeHotkeys(bool enable)
    {
        if (_hotkeys == null) return;

        _hotkeys.Unregister(HotkeyAction.DoodleSelectEraser);
        _hotkeys.Unregister(HotkeyAction.DoodleThicknessThin);
        _hotkeys.Unregister(HotkeyAction.DoodleThicknessMedium);
        _hotkeys.Unregister(HotkeyAction.DoodleThicknessThick);
        _hotkeys.Unregister(HotkeyAction.DoodlePenCycle);
        _hotkeys.Unregister(HotkeyAction.ClearInk);

        if (!enable) return;

        _hotkeys.Register(HotkeyAction.DoodleSelectEraser, "E");
        _hotkeys.Register(HotkeyAction.DoodleThicknessThin, "1");
        _hotkeys.Register(HotkeyAction.DoodleThicknessMedium, "2");
        _hotkeys.Register(HotkeyAction.DoodleThicknessThick, "3");
        _hotkeys.Register(HotkeyAction.DoodlePenCycle, "D");
        _hotkeys.Register(HotkeyAction.ClearInk, string.IsNullOrWhiteSpace(_settings.HotkeyClearInk) ? "C" : _settings.HotkeyClearInk);
    }

    private void OpenCalibrationWindow()
    {
        _modalDialogOpen = true;
        UpdateDoodleModeHotkeys(false);
        try
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
        finally
        {
            _modalDialogOpen = false;
            UpdateDoodleModeHotkeys(_doodleModeEnabled);
            _mainWindow?.RefreshDiagnostics();
        }
    }

    private void OpenSettingsWindow()
    {
        _modalDialogOpen = true;
        UpdateDoodleModeHotkeys(false);
        try
        {
            var win = new SettingsWindow(_settings, _shortcutManager ?? new ShortcutManager(), updated =>
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
                _overlayWindow?.ApplyLensAppearance(
                    ParseColorOrDefault(_settings.LensBorderColorHex, Color.FromRgb(0x00, 0xD2, 0xFF)),
                    _settings.LensBorderOpacity);
                _autosaveService?.UpdateInterval(_settings.AutosaveIntervalSeconds);
                _workspaceWindow?.ApplyThemeFromSettings();
                RegisterConfiguredHotkeys();
                OnStartWithWindowsToggled(_settings.StartWithWindows);
                UpdateLensPositionFromLastCursor();
                _mainWindow?.RefreshDiagnostics();
            });
            win.ShowDialog();
        }
        finally
        {
            _modalDialogOpen = false;
            UpdateDoodleModeHotkeys(_doodleModeEnabled);
            _mainWindow?.RefreshDiagnostics();
        }
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
            _settings.CompactToolbar,
            _settings.LensSizeLocked);
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
        catch { }
        return fallback;
    }

    private void RequestExit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _autosaveService?.SaveIfDirty();
        CleanupAllResources();
        Shutdown(0);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _hardware?.SetDoodleModeCursorIsolation(false);
        _autosaveService?.SaveIfDirty();
        e.Handled = true;
        _trayService?.ShowBalloon("TrackPadLens Recovered", $"Unexpected UI error handled safely: {e.Exception.Message}", System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _autosaveService?.SaveIfDirty();
        CleanupAllResources();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupAllResources();
        base.OnExit(e);
    }

    private void CleanupAllResources()
    {
        _autosaveService?.Dispose();
        _autosaveService = null;

        _hotkeys?.Dispose();
        _hotkeys = null;

        _hardware?.Dispose();
        _hardware = null;

        _trayService?.Dispose();
        _trayService = null;

        if (_workspaceWindow != null)
        {
            _workspaceWindow.AllowFullClose();
            _workspaceWindow.Close();
            _workspaceWindow = null;
        }

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
