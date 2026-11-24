using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TrackPadLens.Commands;
using TrackPadLens.Core;
using TrackPadLens.Documents;
using TrackPadLens.Intelligence;
using TrackPadLens.Models;

namespace TrackPadLens.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ShortcutManager _shortcuts;
    private readonly Action<AppSettings> _onSave;

    public SettingsWindow(AppSettings currentSettings, ShortcutManager shortcuts, Action<AppSettings> onSave)
    {
        InitializeComponent();
        _settings = currentSettings;
        _shortcuts = shortcuts;
        _onSave = onSave;
        LoadFields();
        PopulateCapabilityDiagnostics();
    }

    private void LoadFields()
    {
        ThemeModeCombo.SelectedIndex = _settings.ThemeMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.System => 2,
            _ => 0
        };

        AnchorModeCombo.SelectedIndex = _settings.AnchorMode == AnchorMode.TopLeft ? 1 : 0;
        OffsetXBox.Text = _settings.AnchorOffsetX.ToString("F0", CultureInfo.InvariantCulture);
        OffsetYBox.Text = _settings.AnchorOffsetY.ToString("F0", CultureInfo.InvariantCulture);
        MonitorScopeCombo.SelectedIndex = _settings.MonitorScope == MonitorScope.AllMonitors ? 1 : 0;

        LensColorBox.Text = _settings.LensBorderColorHex;
        LensOpacitySlider.Value = _settings.LensBorderOpacity;
        UpdateLensAppearancePreview();

        MinCutoffSlider.Value = _settings.OneEuroMinCutoff;
        BetaSlider.Value = _settings.OneEuroBeta;
        DCutoffSlider.Value = _settings.OneEuroDCutoff;
        UpdateFilterLabels();

        PalmEnabledCheck.IsChecked = _settings.PalmRejectionEnabled;
        MultiFingerCheck.IsChecked = _settings.MultiFingerRejectionEnabled;
        PalmWidthBox.Text = _settings.PalmMaxContactWidthLogical.ToString(CultureInfo.InvariantCulture);

        SuppressTouchpadCursorCheck.IsChecked = _settings.SuppressTouchpadCursorInDoodleMode;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;

        AutosaveSlider.Value = _settings.AutosaveIntervalSeconds;
        AutosaveVal.Text = $"{(int)Math.Round(AutosaveSlider.Value)}s";
        PreferLibreOfficeCheck.IsChecked = _settings.PreferLibreOfficeForDocx;

        HotkeyDoodleBox.Text = _settings.HotkeyToggleDoodle;
        HotkeyLensBox.Text = _settings.HotkeyTrackpadLens;
        HotkeyWhiteboardBox.Text = _settings.HotkeyWhiteboard;
        HotkeyRegionBox.Text = _settings.HotkeyRegionCapture;
        HotkeyUndoBox.Text = _settings.HotkeyUndo;
        HotkeySnapshotBox.Text = _settings.HotkeySaveSnapshot;
        HotkeyClearBox.Text = _settings.HotkeyClearInk;
    }

    private void PopulateCapabilityDiagnostics()
    {
        PdfStatusText.Text = PdfEngine.IsPlatformSupported
            ? "PDF Engine: Windows.Data.Pdf.PdfDocument AVAILABLE (native Windows 10/11 rendering)"
            : "PDF Engine: Windows.Data.Pdf unavailable on this OS build";

        string? soffice = DocxEngine.FindInstalledLibreOffice();
        DocxStatusText.Text = !string.IsNullOrEmpty(soffice)
            ? $"DOCX Engine: LibreOffice detected ({soffice}) + Native Open XML fallback ready"
            : "DOCX Engine: Native Open XML parser ready (install LibreOffice for full Word pagination)";

        bool ocrReady = OcrService.IsAvailable(out string ocrStatus);
        OcrStatusText.Text = $"Offline OCR: {ocrStatus}";
        OcrStatusText.Foreground = new SolidColorBrush(ocrReady ? Color.FromRgb(0x34, 0xD3, 0x99) : Color.FromRgb(0xFB, 0xBF, 0x24));
    }

    private void LensPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            LensColorBox.Text = hex;
            UpdateLensAppearancePreview();
        }
    }

    private void LensColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateLensAppearancePreview();
    }

    private void OnLensOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLensAppearancePreview();
    }

    private void UpdateLensAppearancePreview()
    {
        double opacity = Math.Clamp(LensOpacitySlider.Value, 0.15, 1.0);
        LensOpacityVal.Text = $"{(int)Math.Round(opacity * 100)}%";
        LensColorPreview.Opacity = opacity;

        try
        {
            string raw = LensColorBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(raw) && ColorConverter.ConvertFromString(raw) is Color parsed)
            {
                LensColorPreview.Background = new SolidColorBrush(parsed);
            }
        }
        catch { }
    }

    private void OnFilterSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateFilterLabels();
    }

    private void OnAutosaveSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        AutosaveVal.Text = $"{(int)Math.Round(AutosaveSlider.Value)}s";
    }

    private void UpdateFilterLabels()
    {
        MinCutoffVal.Text = $"{MinCutoffSlider.Value:F2}";
        BetaVal.Text = $"{BetaSlider.Value:F3}";
        DCutoffVal.Text = $"{DCutoffSlider.Value:F2}";
    }

    private void RestoreDefaultShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _shortcuts.ResetToDefaults();
        HotkeyDoodleBox.Text = "Alt+D";
        HotkeyLensBox.Text = "Alt+T";
        HotkeyWhiteboardBox.Text = "Alt+W";
        HotkeyRegionBox.Text = "Alt+Shift+S";
        HotkeyUndoBox.Text = "Ctrl+Z";
        HotkeySnapshotBox.Text = "Ctrl+S";
        HotkeyClearBox.Text = "C";
    }

    private void ExportShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Shortcut Profile",
            Filter = "JSON Profile (*.json)|*.json",
            FileName = "TrackPadLens-Shortcuts.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _shortcuts.ExportProfileToFile(dlg.FileName);
            MessageBox.Show(this, "Shortcut profile exported.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Shortcut Profile",
            Filter = "JSON Profile (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var warnings = _shortcuts.ImportProfileFromFile(dlg.FileName);
            HotkeyDoodleBox.Text = _shortcuts.GetGesture("Mode.Standard");
            HotkeyLensBox.Text = _shortcuts.GetGesture("Mode.TrackpadLens");
            HotkeyWhiteboardBox.Text = _shortcuts.GetGesture("Mode.Whiteboard");
            HotkeyRegionBox.Text = _shortcuts.GetGesture("Capture.Region");

            string msg = warnings.Count == 0
                ? "Shortcut profile imported with zero conflicts."
                : "Imported with conflicts skipped:\n" + string.Join("\n", warnings);
            MessageBox.Show(this, msg, "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed: {ex.Message}", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        // Validate global shortcut conflicts before committing
        var candidateGlobal = new (string Id, string Text)[]
        {
            ("Mode.Standard", HotkeyDoodleBox.Text.Trim()),
            ("Mode.TrackpadLens", HotkeyLensBox.Text.Trim()),
            ("Mode.Whiteboard", HotkeyWhiteboardBox.Text.Trim()),
            ("Capture.Region", HotkeyRegionBox.Text.Trim())
        };

        foreach (var (id, gesture) in candidateGlobal)
        {
            var (ok, conflict) = _shortcuts.TryUpdateGesture(id, gesture);
            if (!ok)
            {
                MessageBox.Show(this, $"Shortcut validation failed for {id}:\n{conflict}", "Shortcut Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _settings.ThemeMode = ThemeModeCombo.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.System,
            _ => AppThemeMode.Dark
        };

        _settings.AnchorMode = AnchorModeCombo.SelectedIndex == 1 ? AnchorMode.TopLeft : AnchorMode.Centered;
        if (double.TryParse(OffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ox))
            _settings.AnchorOffsetX = ox;
        if (double.TryParse(OffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double oy))
            _settings.AnchorOffsetY = oy;

        _settings.MonitorScope = MonitorScopeCombo.SelectedIndex == 1 ? MonitorScope.AllMonitors : MonitorScope.PrimaryOnly;
        _settings.LensBorderColorHex = LensColorBox.Text.Trim();
        _settings.LensBorderOpacity = LensOpacitySlider.Value;

        _settings.OneEuroMinCutoff = MinCutoffSlider.Value;
        _settings.OneEuroBeta = BetaSlider.Value;
        _settings.OneEuroDCutoff = DCutoffSlider.Value;

        _settings.PalmRejectionEnabled = PalmEnabledCheck.IsChecked == true;
        _settings.MultiFingerRejectionEnabled = MultiFingerCheck.IsChecked == true;
        if (int.TryParse(PalmWidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pw))
        {
            _settings.PalmMaxContactWidthLogical = pw;
            _settings.PalmMaxContactHeightLogical = pw;
        }

        _settings.AutosaveIntervalSeconds = (int)Math.Round(AutosaveSlider.Value);
        _settings.PreferLibreOfficeForDocx = PreferLibreOfficeCheck.IsChecked == true;

        _settings.HotkeyToggleDoodle = HotkeyDoodleBox.Text.Trim();
        _settings.HotkeyTrackpadLens = HotkeyLensBox.Text.Trim();
        _settings.HotkeyWhiteboard = HotkeyWhiteboardBox.Text.Trim();
        _settings.HotkeyRegionCapture = HotkeyRegionBox.Text.Trim();
        _settings.HotkeyUndo = HotkeyUndoBox.Text.Trim();
        _settings.HotkeySaveSnapshot = HotkeySnapshotBox.Text.Trim();
        _settings.HotkeyClearInk = HotkeyClearBox.Text.Trim();
        _settings.ShortcutOverrides = _shortcuts.ExportOverrides();

        _settings.SuppressTouchpadCursorInDoodleMode = SuppressTouchpadCursorCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.Sanitize();

        _onSave(_settings);
        Close();
    }
}
