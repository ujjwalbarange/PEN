namespace TrackPadLens.Models;

/// <summary>
/// Lightweight user configuration persisted as JSON via System.Text.Json.
/// </summary>
public sealed class AppSettings
{
    // Floating Writing Lens
    public AnchorMode AnchorMode { get; set; } = AnchorMode.Centered;
    public double AnchorOffsetX { get; set; } = 0.0;
    public double AnchorOffsetY { get; set; } = 0.0;
    public double DefaultLensWidthDip { get; set; } = 420.0;
    public MonitorScope MonitorScope { get; set; } = MonitorScope.PrimaryOnly;
    public bool LensSizeLocked { get; set; } = false;
    public string LensBorderColorHex { get; set; } = "#00D2FF";
    public double LensBorderOpacity { get; set; } = 1.0;

    // 1-Euro Filter smoothing
    public double OneEuroMinCutoff { get; set; } = 1.8;
    public double OneEuroBeta { get; set; } = 0.025;
    public double OneEuroDCutoff { get; set; } = 1.0;

    // Palm & multi-contact rejection
    public bool PalmRejectionEnabled { get; set; } = true;
    public bool MultiFingerRejectionEnabled { get; set; } = true;
    public int PalmMaxContactWidthLogical { get; set; } = 320;
    public int PalmMaxContactHeightLogical { get; set; } = 320;
    public double PalmMaxAreaFraction { get; set; } = 0.08;

    // Drawing state
    public string PenColorHex { get; set; } = "#FF3B30";
    public StrokeThicknessPreset ThicknessPreset { get; set; } = StrokeThicknessPreset.Medium;
    public DrawingTool ActiveTool { get; set; } = DrawingTool.Pen;
    public bool CompactToolbar { get; set; } = false;

    // Hotkeys (serialized as "Modifiers+Key", e.g., "Alt+D", "Ctrl+Z", "Ctrl+S", "C")
    public string HotkeyToggleDoodle { get; set; } = "Alt+D";
    public string HotkeyUndo { get; set; } = "Ctrl+Z";
    public string HotkeySaveSnapshot { get; set; } = "Ctrl+S";
    public string HotkeyClearInk { get; set; } = "C";

    // System
    public const int CurrentSettingsVersion = 2;
    public int SettingsVersion { get; set; } = 0;
    public bool StartWithWindows { get; set; } = false;
    public bool ConfirmBeforeClear { get; set; } = false;
    public bool SuppressTouchpadCursorInDoodleMode { get; set; } = true;

    // Touchpad calibration
    public CalibrationProfile Calibration { get; set; } = new();

    public void Sanitize()
    {
        DefaultLensWidthDip = Math.Clamp(DefaultLensWidthDip, 100.0, 2400.0);
        LensBorderOpacity = Math.Clamp(LensBorderOpacity, 0.15, 1.0);
        if (string.IsNullOrWhiteSpace(LensBorderColorHex))
        {
            LensBorderColorHex = "#00D2FF";
        }
        OneEuroMinCutoff = Math.Clamp(OneEuroMinCutoff, 0.1, 20.0);
        OneEuroBeta = Math.Clamp(OneEuroBeta, 0.0, 2.0);
        OneEuroDCutoff = Math.Clamp(OneEuroDCutoff, 0.1, 20.0);
        PalmMaxContactWidthLogical = Math.Clamp(PalmMaxContactWidthLogical, 10, 100000);
        PalmMaxContactHeightLogical = Math.Clamp(PalmMaxContactHeightLogical, 10, 100000);
        PalmMaxAreaFraction = Math.Clamp(PalmMaxAreaFraction, 0.005, 0.5);

        // One-time migration from v1 default Escape -> v2 Doodle-Mode-only C
        if (SettingsVersion < CurrentSettingsVersion)
        {
            if (string.Equals(HotkeyClearInk, "Escape", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(HotkeyClearInk, "Esc", StringComparison.OrdinalIgnoreCase))
            {
                HotkeyClearInk = "C";
            }
            SettingsVersion = CurrentSettingsVersion;
        }

        Calibration ??= new CalibrationProfile();
        Calibration.Sanitize();
    }
}
