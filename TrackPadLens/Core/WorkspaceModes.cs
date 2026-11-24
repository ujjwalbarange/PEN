namespace TrackPadLens.Core;

public enum WorkspaceEnvironment
{
    StandardOverlay,
    TrackpadLens,
    Whiteboard,
    SmartStudy
}

public enum CanvasLayoutMode
{
    InfiniteCanvas,
    PagedNotebook
}

public enum CanvasPaperStyle
{
    Blank,
    Ruled,
    Dotted,
    Grid
}

public enum StudyLayoutMode
{
    DocumentFocus,
    SplitWorkspace,
    FreeWorkspace
}

public enum WorkspaceTool
{
    Select,
    Lasso,
    Pen,
    Pencil,
    Marker,
    Highlighter,
    Eraser,
    Laser,
    Shape,
    Connector,
    Text,
    Equation,
    Image,
    StickyNote,
    Pan
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    Diamond,
    Line,
    Arrow
}

public enum DiagramProfile
{
    General,
    Mathematics,
    Physics,
    ElectricalEngineering
}

public enum AppThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>
/// Coordinates transitions among the four TrackPadLens environments
/// (Standard Overlay, Trackpad Lens, Whiteboard, Smart Study) while preserving state.
/// </summary>
public sealed class ModeManager
{
    public WorkspaceEnvironment ActiveEnvironment { get; private set; } = WorkspaceEnvironment.TrackpadLens;
    public event Action<WorkspaceEnvironment, WorkspaceEnvironment>? EnvironmentChanged;

    public void SwitchTo(WorkspaceEnvironment target)
    {
        if (ActiveEnvironment == target) return;
        var previous = ActiveEnvironment;
        ActiveEnvironment = target;
        EnvironmentChanged?.Invoke(previous, target);
    }
}
