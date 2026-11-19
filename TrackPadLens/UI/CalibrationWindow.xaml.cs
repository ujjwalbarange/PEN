using System.Windows;
using System.Windows.Controls;
using TrackPadLens.Hardware;
using TrackPadLens.Models;

namespace TrackPadLens.UI;

public partial class CalibrationWindow : Window
{
    private readonly CalibrationProfile _working;
    private readonly Action<CalibrationProfile> _onApply;
    private bool _loading;

    public CalibrationWindow(
        HardwareCapabilityReport? capabilities,
        CalibrationProfile current,
        Action<CalibrationProfile> onApply)
    {
        InitializeComponent();
        _onApply = onApply;
        _working = new CalibrationProfile
        {
            ActiveLeft = current.ActiveLeft,
            ActiveTop = current.ActiveTop,
            ActiveRight = current.ActiveRight,
            ActiveBottom = current.ActiveBottom,
            UsePhysicalAspectRatio = current.UsePhysicalAspectRatio,
            ManualAspectRatio = current.ManualAspectRatio,
            InvertX = current.InvertX,
            InvertY = current.InvertY
        };

        HardwareSummaryText.Text = capabilities == null
            ? "No Precision Touchpad detected. Configure manual aspect ratio and active region bounds."
            : capabilities.HasReliablePhysicalDimensions
                ? $"Detected {capabilities.DeviceName} — Physical size {capabilities.PhysicalWidthMm:F1} mm × {capabilities.PhysicalHeightMm:F1} mm (ratio {capabilities.PhysicalAspectRatio:F2}:1)."
                : $"Detected {capabilities.DeviceName} — Physical dimensions not reported in HID descriptor. Use manual calibration below.";

        PopulateControls();
    }

    private void PopulateControls()
    {
        _loading = true;
        UsePhysicalAspectCheck.IsChecked = _working.UsePhysicalAspectRatio;
        AspectSlider.Value = _working.ManualAspectRatio;
        LeftMarginSlider.Value = _working.ActiveLeft;
        RightMarginSlider.Value = _working.ActiveRight;
        TopMarginSlider.Value = _working.ActiveTop;
        BottomMarginSlider.Value = _working.ActiveBottom;
        InvertXCheck.IsChecked = _working.InvertX;
        InvertYCheck.IsChecked = _working.InvertY;
        _loading = false;

        UpdatePreview();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        SyncFromControls();
        UpdatePreview();
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        SyncFromControls();
        UpdatePreview();
    }

    private void SyncFromControls()
    {
        _working.UsePhysicalAspectRatio = UsePhysicalAspectCheck.IsChecked == true;
        _working.ManualAspectRatio = AspectSlider.Value;
        _working.ActiveLeft = LeftMarginSlider.Value;
        _working.ActiveRight = RightMarginSlider.Value;
        _working.ActiveTop = TopMarginSlider.Value;
        _working.ActiveBottom = BottomMarginSlider.Value;
        _working.InvertX = InvertXCheck.IsChecked == true;
        _working.InvertY = InvertYCheck.IsChecked == true;
        _working.Sanitize();
    }

    private void UpdatePreview()
    {
        AspectSlider.IsEnabled = UsePhysicalAspectCheck.IsChecked != true;
        AspectValueText.Text = $"{_working.ManualAspectRatio:F2}";
        HorizontalSpanText.Text = $"{(_working.ActiveRight - _working.ActiveLeft) * 100:F0}%";
        VerticalSpanText.Text = $"{(_working.ActiveBottom - _working.ActiveTop) * 100:F0}%";

        const double canvasW = 360.0;
        const double canvasH = 130.0;
        Canvas.SetLeft(ActiveRegionRect, _working.ActiveLeft * canvasW);
        Canvas.SetTop(ActiveRegionRect, _working.ActiveTop * canvasH);
        ActiveRegionRect.Width = Math.Max(8, (_working.ActiveRight - _working.ActiveLeft) * canvasW);
        ActiveRegionRect.Height = Math.Max(8, (_working.ActiveBottom - _working.ActiveTop) * canvasH);
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        var fresh = new CalibrationProfile();
        _working.ActiveLeft = fresh.ActiveLeft;
        _working.ActiveTop = fresh.ActiveTop;
        _working.ActiveRight = fresh.ActiveRight;
        _working.ActiveBottom = fresh.ActiveBottom;
        _working.UsePhysicalAspectRatio = fresh.UsePhysicalAspectRatio;
        _working.ManualAspectRatio = fresh.ManualAspectRatio;
        _working.InvertX = fresh.InvertX;
        _working.InvertY = fresh.InvertY;
        PopulateControls();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SyncFromControls();
        _onApply(_working);
        Close();
    }
}
