using System.Globalization;
using System.Windows;
using TrackPadLens.Models;

namespace TrackPadLens.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onSave;

    public SettingsWindow(AppSettings currentSettings, Action<AppSettings> onSave)
    {
        InitializeComponent();
        _settings = currentSettings;
        _onSave = onSave;
        LoadFields();
    }

    private void LoadFields()
    {
        AnchorModeCombo.SelectedIndex = _settings.AnchorMode == AnchorMode.TopLeft ? 1 : 0;
        OffsetXBox.Text = _settings.AnchorOffsetX.ToString("F0", CultureInfo.InvariantCulture);
        OffsetYBox.Text = _settings.AnchorOffsetY.ToString("F0", CultureInfo.InvariantCulture);
        MonitorScopeCombo.SelectedIndex = _settings.MonitorScope == MonitorScope.AllMonitors ? 1 : 0;

        MinCutoffSlider.Value = _settings.OneEuroMinCutoff;
        BetaSlider.Value = _settings.OneEuroBeta;
        DCutoffSlider.Value = _settings.OneEuroDCutoff;
        UpdateFilterLabels();

        PalmEnabledCheck.IsChecked = _settings.PalmRejectionEnabled;
        MultiFingerCheck.IsChecked = _settings.MultiFingerRejectionEnabled;
        PalmWidthBox.Text = _settings.PalmMaxContactWidthLogical.ToString(CultureInfo.InvariantCulture);

        HotkeyDoodleBox.Text = _settings.HotkeyToggleDoodle;
        HotkeyUndoBox.Text = _settings.HotkeyUndo;
        HotkeySnapshotBox.Text = _settings.HotkeySaveSnapshot;
        HotkeyClearBox.Text = _settings.HotkeyClearInk;

        SuppressTouchpadCursorCheck.IsChecked = _settings.SuppressTouchpadCursorInDoodleMode;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
    }

    private void OnFilterSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateFilterLabels();
    }

    private void UpdateFilterLabels()
    {
        MinCutoffVal.Text = $"{MinCutoffSlider.Value:F2}";
        BetaVal.Text = $"{BetaSlider.Value:F3}";
        DCutoffVal.Text = $"{DCutoffSlider.Value:F2}";
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.AnchorMode = AnchorModeCombo.SelectedIndex == 1 ? AnchorMode.TopLeft : AnchorMode.Centered;
        if (double.TryParse(OffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ox))
            _settings.AnchorOffsetX = ox;
        if (double.TryParse(OffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double oy))
            _settings.AnchorOffsetY = oy;

        _settings.MonitorScope = MonitorScopeCombo.SelectedIndex == 1 ? MonitorScope.AllMonitors : MonitorScope.PrimaryOnly;

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

        _settings.HotkeyToggleDoodle = HotkeyDoodleBox.Text.Trim();
        _settings.HotkeyUndo = HotkeyUndoBox.Text.Trim();
        _settings.HotkeySaveSnapshot = HotkeySnapshotBox.Text.Trim();
        _settings.HotkeyClearInk = HotkeyClearBox.Text.Trim();

        _settings.SuppressTouchpadCursorInDoodleMode = SuppressTouchpadCursorCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.Sanitize();

        _onSave(_settings);
        Close();
    }
}
