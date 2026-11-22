using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Rendering;

namespace TrackPadLens.UI;

/// <summary>
/// Draggable, non-activating topmost pill toolbar providing quick-access controls
/// (including Stroke Eraser and Lens Size Lock/Unlock) and a compact mode for presentations.
/// </summary>
public partial class FloatingToolbar : Window
{
    private static readonly SolidColorBrush ActiveRing = new(Color.FromRgb(0x00, 0xD2, 0xFF));
    private static readonly SolidColorBrush DoodleOnBrush = new(Color.FromRgb(0x00, 0xD2, 0xFF));
    private static readonly SolidColorBrush DoodleOffBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush LockedStrokeBrush = new(Color.FromRgb(0x00, 0xD2, 0xFF));
    private static readonly SolidColorBrush UnlockedStrokeBrush = new(Color.FromRgb(0x94, 0xA3, 0xB8));

    private bool _compactMode;

    public event Action? ToggleDoodleRequested;
    public event Action<Color>? PenColorSelected;
    public event Action? HighlighterSelected;
    public event Action<StrokeThicknessPreset>? ThicknessSelected;
    public event Action? EraserSelected;
    public event Action? LaserSelected;
    public event Action? UndoRequested;
    public event Action? ClearRequested;
    public event Action? SaveSnapshotRequested;
    public event Action? ToggleSizeLockRequested;
    public event Action? CalibrationRequested;
    public event Action? SettingsRequested;
    public event Action? MinimizeToTrayRequested;
    public event Action<bool>? CompactModeChanged;

    public FloatingToolbar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyles.ApplyFloatingToolbarStyles(this);
        Loaded += (_, _) => PositionAtBottomCenter();
    }

    public void PositionAtBottomCenter()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) * 0.5;
        Top = workArea.Bottom - ActualHeight - 24;
    }

    public void SyncState(
        bool doodleEnabled,
        DrawingTool tool,
        Color penColor,
        StrokeThicknessPreset thickness,
        bool compact,
        bool lensSizeLocked)
    {
        DoodleDot.Fill = doodleEnabled ? DoodleOnBrush : DoodleOffBrush;
        DoodleToggleBtn.BorderBrush = doodleEnabled ? ActiveRing : Brushes.Transparent;

        ColorRedBtn.BorderBrush = tool == DrawingTool.Pen && penColor == InkEngine.PenRed ? ActiveRing : Brushes.Transparent;
        ColorBlueBtn.BorderBrush = tool == DrawingTool.Pen && penColor == InkEngine.PenBlue ? ActiveRing : Brushes.Transparent;
        ColorHighlighterBtn.BorderBrush = tool == DrawingTool.Highlighter ? ActiveRing : Brushes.Transparent;
        ColorWhiteBtn.BorderBrush = tool == DrawingTool.Pen && penColor == InkEngine.PenWhite ? ActiveRing : Brushes.Transparent;
        ColorBlackBtn.BorderBrush = tool == DrawingTool.Pen && penColor == InkEngine.PenBlack ? ActiveRing : Brushes.Transparent;
        EraserBtn.BorderBrush = tool == DrawingTool.Eraser ? ActiveRing : Brushes.Transparent;
        LaserBtn.BorderBrush = tool == DrawingTool.Laser ? ActiveRing : Brushes.Transparent;

        ThickThinBtn.BorderBrush = thickness == StrokeThicknessPreset.Thin ? ActiveRing : Brushes.Transparent;
        ThickMedBtn.BorderBrush = thickness == StrokeThicknessPreset.Medium ? ActiveRing : Brushes.Transparent;
        ThickHeavyBtn.BorderBrush = thickness == StrokeThicknessPreset.Thick ? ActiveRing : Brushes.Transparent;

        SizeLockBtn.BorderBrush = lensSizeLocked ? ActiveRing : Brushes.Transparent;
        LockClosedShackle.Visibility = lensSizeLocked ? Visibility.Visible : Visibility.Collapsed;
        LockOpenShackle.Visibility = lensSizeLocked ? Visibility.Collapsed : Visibility.Visible;
        LockBody.Stroke = lensSizeLocked ? LockedStrokeBrush : UnlockedStrokeBrush;
        SizeLockBtn.ToolTip = lensSizeLocked
            ? "Lens Size Locked (mouse wheel scrolls instead of resizing) — click to unlock"
            : "Lens Size Unlocked (mouse wheel resizes box) — click to lock";

        SetCompactMode(compact, raiseEvent: false);
    }

    private void SetCompactMode(bool compact, bool raiseEvent)
    {
        _compactMode = compact;
        ExpandedControls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactGlyph.Text = compact ? "›" : "‹";
        if (raiseEvent) CompactModeChanged?.Invoke(compact);
    }

    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void DoodleToggleBtn_Click(object sender, RoutedEventArgs e) => ToggleDoodleRequested?.Invoke();
    private void ColorRedBtn_Click(object sender, RoutedEventArgs e) => PenColorSelected?.Invoke(InkEngine.PenRed);
    private void ColorBlueBtn_Click(object sender, RoutedEventArgs e) => PenColorSelected?.Invoke(InkEngine.PenBlue);
    private void ColorHighlighterBtn_Click(object sender, RoutedEventArgs e) => HighlighterSelected?.Invoke();
    private void ColorWhiteBtn_Click(object sender, RoutedEventArgs e) => PenColorSelected?.Invoke(InkEngine.PenWhite);
    private void ColorBlackBtn_Click(object sender, RoutedEventArgs e) => PenColorSelected?.Invoke(InkEngine.PenBlack);
    private void ThickThinBtn_Click(object sender, RoutedEventArgs e) => ThicknessSelected?.Invoke(StrokeThicknessPreset.Thin);
    private void ThickMedBtn_Click(object sender, RoutedEventArgs e) => ThicknessSelected?.Invoke(StrokeThicknessPreset.Medium);
    private void ThickHeavyBtn_Click(object sender, RoutedEventArgs e) => ThicknessSelected?.Invoke(StrokeThicknessPreset.Thick);
    private void EraserBtn_Click(object sender, RoutedEventArgs e) => EraserSelected?.Invoke();
    private void LaserBtn_Click(object sender, RoutedEventArgs e) => LaserSelected?.Invoke();
    private void UndoBtn_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void ClearBtn_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void SnapshotBtn_Click(object sender, RoutedEventArgs e) => SaveSnapshotRequested?.Invoke();
    private void SizeLockBtn_Click(object sender, RoutedEventArgs e) => ToggleSizeLockRequested?.Invoke();
    private void CalibrationBtn_Click(object sender, RoutedEventArgs e) => CalibrationRequested?.Invoke();
    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void TrayBtn_Click(object sender, RoutedEventArgs e) => MinimizeToTrayRequested?.Invoke();
    private void CompactBtn_Click(object sender, RoutedEventArgs e) => SetCompactMode(!_compactMode, raiseEvent: true);
}
