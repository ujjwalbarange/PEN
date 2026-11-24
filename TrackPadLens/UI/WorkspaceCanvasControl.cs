using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrackPadLens.Commands;
using TrackPadLens.Core;
using TrackPadLens.Documents;
using TrackPadLens.Objects;
using TrackPadLens.Selection;
using TrackPadLens.Storage;

namespace TrackPadLens.UI;

/// <summary>
/// High-performance retained WPF DrawingVisual canvas powering Whiteboard Mode and
/// Smart Study Document Annotation with SpatialIndex viewport culling, smooth zoom/pan,
/// multi-tool drawing (Pen/Pencil/Marker/Highlighter/Eraser/Shapes/Connectors/Text/Equation/StickyNote),
/// Lasso & Marquee selection, 8-handle resize, rotation handle, and command-based Undo/Redo.
/// </summary>
public sealed class WorkspaceCanvasControl : FrameworkElement
{
    private enum DragMode { None, Pan, DrawStroke, Erase, DrawShape, DrawConnector, Marquee, Lasso, MoveSelection, ResizeSelection, RotateSelection }

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _backgroundVisual = new();
    private readonly DrawingVisual _objectsVisual = new();
    private readonly DrawingVisual _overlayVisual = new();

    private ObjectManager _objects = new();
    private SelectionManager _selection;
    private TrackPadLens.Commands.CommandManager _commands = new();
    private AssetStore _assets = new();

    private BitmapSource? _documentBackground;
    private bool _darkTheme = true;
    private double _zoom = 1.0;
    private double _panX = 40.0;
    private double _panY = 40.0;

    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private Point _dragStartWorld;
    private Point _lastWorld;
    private double _panStartX;
    private double _panStartY;

    private InkStrokeObject? _activeStroke;
    private VectorShapeObject? _previewShape;
    private ConnectorObject? _previewConnector;
    private readonly List<(double X, double Y)> _lassoPoints = new(64);
    private ObjectRect? _marqueeRect;
    private List<ObjectGeometrySnapshot>? _transformStartSnapshots;
    private ObjectRect _resizeStartBounds;
    private int _activeResizeHandle = -1; // 0..7 clockwise from TopLeft
    private double _rotateStartAngle;
    private Point _rotateCenter;

    public WorkspaceTool ActiveTool { get; set; } = WorkspaceTool.Pen;
    public ShapeKind ActiveShapeKind { get; set; } = ShapeKind.Rectangle;
    public string ActiveColorHex { get; set; } = "#FF3B30";
    public double ActiveThickness { get; set; } = 3.5;
    public double ActiveOpacity { get; set; } = 1.0;

    public double Zoom => _zoom;
    public double PanX => _panX;
    public double PanY => _panY;

    public event Action<double>? ZoomChanged;
    public event Action<DocumentObject>? ObjectDoubleClicked;
    public event Action<Point>? TextPlacementRequested;
    public event Action<Point>? EquationPlacementRequested;
    public event Action<Point>? StickyNotePlacementRequested;
    public event Action? SelectionContextUpdated;
    public event Action? ContentModified;

    public WorkspaceCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _selection = new SelectionManager(_objects);

        _visuals = new VisualCollection(this);
        _visuals.Add(_backgroundVisual);
        _visuals.Add(_objectsVisual);
        _visuals.Add(_overlayVisual);

        SizeChanged += (_, _) => RedrawAll();
    }

    protected override int VisualChildrenCount => _visuals?.Count ?? 0;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void BindWorkspace(
        ObjectManager objectManager,
        SelectionManager selectionManager,
        TrackPadLens.Commands.CommandManager commandManager,
        AssetStore assetStore)
    {
        _objects.SceneChanged -= OnSceneChanged;
        _selection.SelectionChanged -= OnSelectionChanged;

        _objects = objectManager;
        _selection = selectionManager;
        _commands = commandManager;
        _assets = assetStore;

        _objects.SceneChanged += OnSceneChanged;
        _selection.SelectionChanged += OnSelectionChanged;
        RedrawAll();
    }

    public void SetTheme(bool darkTheme)
    {
        _darkTheme = darkTheme;
        RedrawAll();
    }

    public void SetDocumentBackground(BitmapSource? background)
    {
        _documentBackground = background;
        RedrawAll();
    }

    public void SetZoom(double newZoom, Point? anchorScreen = null)
    {
        double clamped = Math.Clamp(newZoom, 0.25, 4.0);
        var anchor = anchorScreen ?? new Point(ActualWidth * 0.5, ActualHeight * 0.5);
        Point worldBefore = ScreenToWorld(anchor);
        _zoom = clamped;
        _panX = anchor.X - worldBefore.X * _zoom;
        _panY = anchor.Y - worldBefore.Y * _zoom;
        ZoomChanged?.Invoke(_zoom);
        RedrawAll();
    }

    public void FitToViewport()
    {
        double targetW = _documentBackground != null
            ? _documentBackground.PixelWidth * 96.0 / Math.Max(72.0, _documentBackground.DpiX)
            : _objects.ActivePage.PageWidthDip;
        double targetH = _documentBackground != null
            ? _documentBackground.PixelHeight * 96.0 / Math.Max(72.0, _documentBackground.DpiY)
            : _objects.ActivePage.PageHeightDip;

        double viewW = Math.Max(200, ActualWidth - 48);
        double viewH = Math.Max(200, ActualHeight - 48);
        _zoom = Math.Clamp(Math.Min(viewW / Math.Max(100, targetW), viewH / Math.Max(100, targetH)), 0.35, 2.0);
        _panX = (ActualWidth - targetW * _zoom) * 0.5;
        _panY = Math.Max(24, (ActualHeight - targetH * _zoom) * 0.5);
        ZoomChanged?.Invoke(_zoom);
        RedrawAll();
    }

    public Point GetViewportCenterWorld() => ScreenToWorld(new Point(ActualWidth * 0.5, ActualHeight * 0.5));

    public Point ScreenToWorld(Point screen) => new((screen.X - _panX) / _zoom, (screen.Y - _panY) / _zoom);
    public Point WorldToScreen(Point world) => new(world.X * _zoom + _panX, world.Y * _zoom + _panY);

    public void RedrawAll()
    {
        RedrawBackground();
        RedrawObjects();
        RedrawOverlay();
    }

    private void OnSceneChanged()
    {
        RedrawObjects();
        RedrawOverlay();
        ContentModified?.Invoke();
    }

    private void OnSelectionChanged()
    {
        RedrawOverlay();
        SelectionContextUpdated?.Invoke();
    }

    private void RedrawBackground()
    {
        using DrawingContext dc = _backgroundVisual.RenderOpen();
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);

        var outerBg = _darkTheme ? Color.FromRgb(0x0B, 0x11, 0x20) : Color.FromRgb(0xE2, 0xE8, 0xF0);
        dc.DrawRectangle(new SolidColorBrush(outerBg), null, new Rect(0, 0, w, h));

        dc.PushTransform(new TranslateTransform(_panX, _panY));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));

        var page = _objects.ActivePage;
        if (_documentBackground != null)
        {
            double dw = _documentBackground.PixelWidth * 96.0 / Math.Max(72.0, _documentBackground.DpiX);
            double dh = _documentBackground.PixelHeight * 96.0 / Math.Max(72.0, _documentBackground.DpiY);
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)), 1.0 / _zoom), new Rect(0, 0, dw, dh));
            dc.DrawImage(_documentBackground, new Rect(0, 0, dw, dh));
        }
        else if (page.LayoutMode == CanvasLayoutMode.PagedNotebook)
        {
            var pageBg = _darkTheme ? Color.FromRgb(0x11, 0x18, 0x27) : Colors.White;
            var border = _darkTheme ? Color.FromRgb(0x33, 0x41, 0x55) : Color.FromRgb(0xCB, 0xD5, 0xE1);
            dc.DrawRoundedRectangle(new SolidColorBrush(pageBg), new Pen(new SolidColorBrush(border), 1.2 / _zoom),
                new Rect(0, 0, page.PageWidthDip, page.PageHeightDip), 6, 6);
            DocumentExporter.DrawPaperPattern(dc, page.PaperStyle, page.PageWidthDip, page.PageHeightDip, _darkTheme);
        }
        else
        {
            // Infinite canvas paper pattern tiled only across visible viewport bounds (zero wasted draw calls)
            Point topLeft = ScreenToWorld(new Point(0, 0));
            Point botRight = ScreenToWorld(new Point(w, h));
            DrawInfinitePattern(dc, page.PaperStyle, topLeft.X, topLeft.Y, botRight.X, botRight.Y);
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawInfinitePattern(DrawingContext dc, CanvasPaperStyle style, double minX, double minY, double maxX, double maxY)
    {
        if (style == CanvasPaperStyle.Blank) return;
        const double step = 32.0;
        double startX = Math.Floor(minX / step) * step;
        double startY = Math.Floor(minY / step) * step;

        Color lineColor = _darkTheme ? Color.FromArgb(36, 148, 163, 184) : Color.FromArgb(46, 100, 116, 139);
        var brush = new SolidColorBrush(lineColor);
        brush.Freeze();
        var pen = new Pen(brush, 1.0 / _zoom);
        pen.Freeze();

        if (style == CanvasPaperStyle.Ruled)
        {
            for (double y = startY; y <= maxY; y += step)
                dc.DrawLine(pen, new Point(minX, y), new Point(maxX, y));
        }
        else if (style == CanvasPaperStyle.Grid)
        {
            for (double x = startX; x <= maxX; x += step)
                dc.DrawLine(pen, new Point(x, minY), new Point(x, maxY));
            for (double y = startY; y <= maxY; y += step)
                dc.DrawLine(pen, new Point(minX, y), new Point(maxX, y));
        }
        else if (style == CanvasPaperStyle.Dotted)
        {
            double r = Math.Max(1.0, 1.25 / Math.Sqrt(_zoom));
            for (double x = startX; x <= maxX; x += step)
            {
                for (double y = startY; y <= maxY; y += step)
                {
                    dc.DrawEllipse(brush, null, new Point(x, y), r, r);
                }
            }
        }
    }

    private void RedrawObjects()
    {
        using DrawingContext dc = _objectsVisual.RenderOpen();
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);

        Point tl = ScreenToWorld(new Point(-80, -80));
        Point br = ScreenToWorld(new Point(w + 80, h + 80));
        var viewport = new ObjectRect(tl.X, tl.Y, Math.Max(1, br.X - tl.X), Math.Max(1, br.Y - tl.Y));

        // O(k) SpatialIndex viewport culling
        var visibleObjects = _objects.QueryViewport(in viewport);
        double ppd = PresentationSource.FromVisual(this) != null ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1.0;

        dc.PushTransform(new TranslateTransform(_panX, _panY));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));

        for (int i = 0; i < visibleObjects.Count; i++)
        {
            DocumentExporter.DrawObject(dc, visibleObjects[i], _assets, ppd);
        }

        dc.Pop();
        dc.Pop();
    }

    private void RedrawOverlay()
    {
        using DrawingContext dc = _overlayVisual.RenderOpen();
        double ppd = PresentationSource.FromVisual(this) != null ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1.0;

        dc.PushTransform(new TranslateTransform(_panX, _panY));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));

        if (_activeStroke != null)
        {
            DocumentExporter.DrawObject(dc, _activeStroke, _assets, ppd);
        }
        if (_previewShape != null)
        {
            DocumentExporter.DrawObject(dc, _previewShape, _assets, ppd);
        }
        if (_previewConnector != null)
        {
            DocumentExporter.DrawObject(dc, _previewConnector, _assets, ppd);
        }

        // Selection bounding box, 8 resize handles, and top rotation handle
        var selBounds = _selection.GetSelectionBounds();
        if (selBounds.HasValue)
        {
            var b = selBounds.Value;
            var cyan = new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF));
            var selPen = new Pen(cyan, 1.5 / _zoom) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, selPen, new Rect(b.Left, b.Top, b.Width, b.Height));

            double hs = 8.0 / _zoom;
            var handleFill = Brushes.White;
            var handlePen = new Pen(cyan, 1.5 / _zoom);

            foreach (var hp in GetResizeHandleCenters(in b))
            {
                dc.DrawRectangle(handleFill, handlePen, new Rect(hp.X - hs * 0.5, hp.Y - hs * 0.5, hs, hs));
            }

            // Rotation handle above top-center
            Point rotStemStart = new(b.CenterX, b.Top);
            Point rotKnob = new(b.CenterX, b.Top - 24.0 / _zoom);
            dc.DrawLine(handlePen, rotStemStart, rotKnob);
            dc.DrawEllipse(cyan, handlePen, rotKnob, 5.0 / _zoom, 5.0 / _zoom);
        }

        // Marquee rectangle preview
        if (_marqueeRect.HasValue)
        {
            var m = _marqueeRect.Value;
            var fill = new SolidColorBrush(Color.FromArgb(35, 0, 210, 255));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF)), 1.2 / _zoom);
            dc.DrawRectangle(fill, pen, new Rect(m.Left, m.Top, m.Width, m.Height));
        }

        // Freeform Lasso polygon preview
        if (_dragMode == DragMode.Lasso && _lassoPoints.Count >= 2)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(_lassoPoints[0].X, _lassoPoints[0].Y), true, true);
                for (int i = 1; i < _lassoPoints.Count; i++)
                {
                    ctx.LineTo(new Point(_lassoPoints[i].X, _lassoPoints[i].Y), true, true);
                }
            }
            geo.Freeze();
            var lassoFill = new SolidColorBrush(Color.FromArgb(30, 0, 210, 255));
            var lassoPen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF)), 1.6 / _zoom) { DashStyle = DashStyles.Dash };
            dc.DrawGeometry(lassoFill, lassoPen, geo);
        }

        dc.Pop();
        dc.Pop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double factor = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
            SetZoom(_zoom * factor, e.GetPosition(this));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _panX += e.Delta * 0.4;
            RedrawAll();
            e.Handled = true;
        }
        else
        {
            _panY += e.Delta * 0.45;
            RedrawAll();
            e.Handled = true;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        Point screen = e.GetPosition(this);
        Point world = ScreenToWorld(screen);
        _dragStartScreen = screen;
        _dragStartWorld = world;
        _lastWorld = world;

        if (e.ChangedButton == MouseButton.Middle || ActiveTool == WorkspaceTool.Pan ||
            (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _dragMode = DragMode.Pan;
            _panStartX = _panX;
            _panStartY = _panY;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        // Double-click edits Text / Equation / StickyNote objects
        if (e.ClickCount == 2)
        {
            var hit = _objects.HitTestTopMost(world.X, world.Y, 10.0 / _zoom);
            if (hit != null)
            {
                _selection.SelectSingle(hit);
                ObjectDoubleClicked?.Invoke(hit);
                return;
            }
        }

        switch (ActiveTool)
        {
            case WorkspaceTool.Select:
                HandleSelectMouseDown(world);
                break;

            case WorkspaceTool.Lasso:
                _dragMode = DragMode.Lasso;
                _lassoPoints.Clear();
                _lassoPoints.Add((world.X, world.Y));
                CaptureMouse();
                RedrawOverlay();
                break;

            case WorkspaceTool.Pen:
            case WorkspaceTool.Pencil:
            case WorkspaceTool.Marker:
            case WorkspaceTool.Highlighter:
                StartNewStroke(world, pressure: 0.5f);
                _dragMode = DragMode.DrawStroke;
                CaptureMouse();
                break;

            case WorkspaceTool.Eraser:
                _dragMode = DragMode.Erase;
                EraseAlong(world, world);
                CaptureMouse();
                break;

            case WorkspaceTool.Shape:
                _dragMode = DragMode.DrawShape;
                _previewShape = new VectorShapeObject
                {
                    Kind = ActiveShapeKind,
                    X = world.X,
                    Y = world.Y,
                    Width = 2,
                    Height = 2,
                    StrokeHex = ActiveColorHex,
                    FillHex = ActiveShapeKind is ShapeKind.Line or ShapeKind.Arrow ? "Transparent" : "#1A00D2FF",
                    StrokeThickness = Math.Max(2.0, ActiveThickness * 0.7)
                };
                CaptureMouse();
                RedrawOverlay();
                break;

            case WorkspaceTool.Connector:
                _dragMode = DragMode.DrawConnector;
                _previewConnector = new ConnectorObject
                {
                    StartX = world.X,
                    StartY = world.Y,
                    EndX = world.X,
                    EndY = world.Y,
                    HasArrowHead = true,
                    StrokeHex = ActiveColorHex,
                    StrokeThickness = Math.Max(2.0, ActiveThickness * 0.7)
                };
                CaptureMouse();
                RedrawOverlay();
                break;

            case WorkspaceTool.Text:
                TextPlacementRequested?.Invoke(world);
                break;

            case WorkspaceTool.Equation:
                EquationPlacementRequested?.Invoke(world);
                break;

            case WorkspaceTool.StickyNote:
                StickyNotePlacementRequested?.Invoke(world);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragMode == DragMode.None) return;

        Point screen = e.GetPosition(this);
        Point world = ScreenToWorld(screen);

        switch (_dragMode)
        {
            case DragMode.Pan:
                _panX = _panStartX + (screen.X - _dragStartScreen.X);
                _panY = _panStartY + (screen.Y - _dragStartScreen.Y);
                RedrawAll();
                break;

            case DragMode.DrawStroke when _activeStroke != null:
                double dx = world.X - _lastWorld.X, dy = world.Y - _lastWorld.Y;
                if (dx * dx + dy * dy >= 1.5 / (_zoom * _zoom))
                {
                    _activeStroke.Points.Add(new InkPointData(world.X, world.Y, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));
                    _lastWorld = world;
                    RedrawOverlay();
                }
                break;

            case DragMode.Erase:
                EraseAlong(_lastWorld, world);
                _lastWorld = world;
                break;

            case DragMode.DrawShape when _previewShape != null:
                _previewShape.X = Math.Min(_dragStartWorld.X, world.X);
                _previewShape.Y = Math.Min(_dragStartWorld.Y, world.Y);
                _previewShape.Width = Math.Max(8, Math.Abs(world.X - _dragStartWorld.X));
                _previewShape.Height = Math.Max(8, Math.Abs(world.Y - _dragStartWorld.Y));
                RedrawOverlay();
                break;

            case DragMode.DrawConnector when _previewConnector != null:
                _previewConnector.EndX = world.X;
                _previewConnector.EndY = world.Y;
                _previewConnector.SyncBoundsFromEndpoints();
                RedrawOverlay();
                break;

            case DragMode.Marquee:
                _marqueeRect = new ObjectRect(
                    Math.Min(_dragStartWorld.X, world.X),
                    Math.Min(_dragStartWorld.Y, world.Y),
                    Math.Abs(world.X - _dragStartWorld.X),
                    Math.Abs(world.Y - _dragStartWorld.Y));
                RedrawOverlay();
                break;

            case DragMode.Lasso:
                double ldx = world.X - _lastWorld.X, ldy = world.Y - _lastWorld.Y;
                if (ldx * ldx + ldy * ldy >= 9.0)
                {
                    _lassoPoints.Add((world.X, world.Y));
                    _lastWorld = world;
                    RedrawOverlay();
                }
                break;

            case DragMode.MoveSelection:
                double mdx = world.X - _lastWorld.X, mdy = world.Y - _lastWorld.Y;
                var moved = _selection.GetSelectedObjects();
                foreach (var obj in moved) obj.Translate(mdx, mdy);
                _objects.NotifyBatchChanged(moved);
                _lastWorld = world;
                break;

            case DragMode.ResizeSelection:
                ApplyLiveResize(world);
                break;

            case DragMode.RotateSelection:
                double curAngle = Math.Atan2(world.Y - _rotateCenter.Y, world.X - _rotateCenter.X) * 180.0 / Math.PI;
                double deltaDeg = curAngle - _rotateStartAngle;
                if (_transformStartSnapshots != null)
                {
                    var touched = new List<DocumentObject>();
                    foreach (var snap in _transformStartSnapshots)
                    {
                        var obj = _objects.GetById(snap.Id);
                        if (obj != null && !obj.IsLocked)
                        {
                            obj.RotationDegrees = snap.RotationDegrees + deltaDeg;
                            touched.Add(obj);
                        }
                    }
                    _objects.NotifyBatchChanged(touched);
                }
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragMode == DragMode.None) return;

        var mode = _dragMode;
        _dragMode = DragMode.None;
        ReleaseMouseCapture();

        switch (mode)
        {
            case DragMode.DrawStroke when _activeStroke != null:
                _activeStroke.RecalculateBounds();
                var finished = _activeStroke;
                _activeStroke = null;
                _commands.Execute(new AddObjectsCommand(_objects, new[] { finished }, "Draw Ink Stroke"));
                break;

            case DragMode.DrawShape when _previewShape != null:
                var shape = _previewShape;
                _previewShape = null;
                if (shape.Width >= 10 || shape.Height >= 10)
                {
                    _commands.Execute(new AddObjectsCommand(_objects, new[] { shape }, $"Add {shape.Kind}"));
                    _selection.SelectSingle(shape);
                }
                break;

            case DragMode.DrawConnector when _previewConnector != null:
                var conn = _previewConnector;
                _previewConnector = null;
                double dxLen = conn.EndX - conn.StartX;
                double dyLen = conn.EndY - conn.StartY;
                double len = Math.Sqrt(dxLen * dxLen + dyLen * dyLen);
                if (len >= 10)
                {
                    conn.SyncBoundsFromEndpoints();
                    _commands.Execute(new AddObjectsCommand(_objects, new[] { conn }, "Add Connector"));
                    _selection.SelectSingle(conn);
                }
                break;

            case DragMode.Marquee:
                if (_marqueeRect.HasValue)
                {
                    bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                    var mr = _marqueeRect.Value;
                    _selection.SelectInRect(in mr, additive);
                    _marqueeRect = null;
                }
                break;

            case DragMode.Lasso:
                bool addLasso = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                _selection.SelectInLasso(_lassoPoints, addLasso);
                _lassoPoints.Clear();
                break;

            case DragMode.MoveSelection:
            case DragMode.ResizeSelection:
            case DragMode.RotateSelection:
                CommitPendingTransform(mode.ToString());
                break;
        }

        RedrawOverlay();
    }

    private void HandleSelectMouseDown(Point world)
    {
        var selBounds = _selection.GetSelectionBounds();
        if (selBounds.HasValue)
        {
            var b = selBounds.Value;
            // Check rotation handle first
            Point rotKnob = new(b.CenterX, b.Top - 24.0 / _zoom);
            double rdx = world.X - rotKnob.X;
            double rdy = world.Y - rotKnob.Y;
            if (Math.Sqrt(rdx * rdx + rdy * rdy) <= 10.0 / _zoom)
            {
                _transformStartSnapshots = _selection.GetSelectedObjects().Select(ObjectGeometrySnapshot.Capture).ToList();
                _rotateCenter = new Point(b.CenterX, b.CenterY);
                _rotateStartAngle = Math.Atan2(world.Y - _rotateCenter.Y, world.X - _rotateCenter.X) * 180.0 / Math.PI;
                _dragMode = DragMode.RotateSelection;
                CaptureMouse();
                return;
            }

            // Check 8 resize handles
            var handles = GetResizeHandleCenters(in b);
            for (int i = 0; i < handles.Length; i++)
            {
                if (Math.Abs(world.X - handles[i].X) <= 9.0 / _zoom && Math.Abs(world.Y - handles[i].Y) <= 9.0 / _zoom)
                {
                    _activeResizeHandle = i;
                    _resizeStartBounds = b;
                    _transformStartSnapshots = _selection.GetSelectedObjects().Select(ObjectGeometrySnapshot.Capture).ToList();
                    _dragMode = DragMode.ResizeSelection;
                    CaptureMouse();
                    return;
                }
            }
        }

        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var hit = _objects.HitTestTopMost(world.X, world.Y, 10.0 / _zoom);
        if (hit != null)
        {
            if (!hit.IsSelected || additive)
            {
                _selection.SelectSingle(hit, additive);
            }
            _transformStartSnapshots = _selection.GetSelectedObjects().Select(ObjectGeometrySnapshot.Capture).ToList();
            _dragMode = DragMode.MoveSelection;
            CaptureMouse();
        }
        else
        {
            if (!additive) _selection.ClearSelection();
            _marqueeRect = new ObjectRect(world.X, world.Y, 0, 0);
            _dragMode = DragMode.Marquee;
            CaptureMouse();
        }
    }

    private void StartNewStroke(Point world, float pressure)
    {
        double thickness = ActiveTool switch
        {
            WorkspaceTool.Pencil => Math.Max(1.8, ActiveThickness * 0.75),
            WorkspaceTool.Marker => ActiveThickness * 1.9,
            WorkspaceTool.Highlighter => ActiveThickness * 3.6,
            _ => ActiveThickness
        };
        double opacity = ActiveTool switch
        {
            WorkspaceTool.Pencil => 0.75,
            WorkspaceTool.Highlighter => 0.38,
            _ => ActiveOpacity
        };
        string colorHex = ActiveTool == WorkspaceTool.Highlighter ? "#FFCC00" : ActiveColorHex;

        _activeStroke = new InkStrokeObject
        {
            ColorHex = colorHex,
            Thickness = thickness,
            Opacity = opacity,
            PenStyle = ActiveTool,
            Points = new List<InkPointData>(32)
            {
                new(world.X, world.Y, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, pressure)
            }
        };
        RedrawOverlay();
    }

    private void EraseAlong(Point p0, Point p1)
    {
        var hits = _objects.HitTestEraserSegment(p0.X, p0.Y, p1.X, p1.Y, 12.0 / _zoom);
        if (hits.Count > 0)
        {
            _commands.Execute(new RemoveObjectsCommand(_objects, hits, "Erase Stroke"));
        }
    }

    private void ApplyLiveResize(Point world)
    {
        if (_transformStartSnapshots == null || _transformStartSnapshots.Count == 0) return;
        double l = _resizeStartBounds.Left, t = _resizeStartBounds.Top;
        double r = _resizeStartBounds.Right, b = _resizeStartBounds.Bottom;

        switch (_activeResizeHandle)
        {
            case 0: l = Math.Min(r - 16, world.X); t = Math.Min(b - 16, world.Y); break; // TL
            case 1: t = Math.Min(b - 16, world.Y); break;                               // TC
            case 2: r = Math.Max(l + 16, world.X); t = Math.Min(b - 16, world.Y); break; // TR
            case 3: r = Math.Max(l + 16, world.X); break;                               // MR
            case 4: r = Math.Max(l + 16, world.X); b = Math.Max(t + 16, world.Y); break; // BR
            case 5: b = Math.Max(t + 16, world.Y); break;                               // BC
            case 6: l = Math.Min(r - 16, world.X); b = Math.Max(t + 16, world.Y); break; // BL
            case 7: l = Math.Min(r - 16, world.X); break;                               // ML
        }

        double newW = Math.Max(16, r - l);
        double newH = Math.Max(16, b - t);

        // Preserve aspect ratio when resizing a single aspect-locked ImageObject via a corner handle
        if (_transformStartSnapshots.Count == 1 && _activeResizeHandle is 0 or 2 or 4 or 6)
        {
            var single = _objects.GetById(_transformStartSnapshots[0].Id);
            if (single is ImageObject img && img.PreserveAspectRatio && _resizeStartBounds.Height > 1)
            {
                double ratio = _resizeStartBounds.Width / _resizeStartBounds.Height;
                newH = newW / ratio;
                if (_activeResizeHandle is 0 or 2) t = b - newH;
            }
        }

        double sx = newW / Math.Max(1.0, _resizeStartBounds.Width);
        double sy = newH / Math.Max(1.0, _resizeStartBounds.Height);
        var touched = new List<DocumentObject>(_transformStartSnapshots.Count);

        foreach (var snap in _transformStartSnapshots)
        {
            var obj = _objects.GetById(snap.Id);
            if (obj == null || obj.IsLocked) continue;
            snap.ApplyTo(obj);
            double ox = l + (snap.X - _resizeStartBounds.Left) * sx;
            double oy = t + (snap.Y - _resizeStartBounds.Top) * sy;
            obj.ResizeTo(ox, oy, snap.Width * sx, snap.Height * sy);
            touched.Add(obj);
        }

        _objects.NotifyBatchChanged(touched);
    }

    private void CommitPendingTransform(string actionName)
    {
        if (_transformStartSnapshots == null || _transformStartSnapshots.Count == 0) return;
        var after = _transformStartSnapshots
            .Select(s => _objects.GetById(s.Id))
            .Where(o => o != null)
            .Select(o => ObjectGeometrySnapshot.Capture(o!))
            .ToList();

        bool changed = false;
        for (int i = 0; i < Math.Min(_transformStartSnapshots.Count, after.Count); i++)
        {
            var a = _transformStartSnapshots[i];
            var b = after[i];
            if (Math.Abs(a.X - b.X) > 0.1 || Math.Abs(a.Y - b.Y) > 0.1 ||
                Math.Abs(a.Width - b.Width) > 0.1 || Math.Abs(a.Height - b.Height) > 0.1 ||
                Math.Abs(a.RotationDegrees - b.RotationDegrees) > 0.1)
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            _commands.Execute(new TransformObjectsCommand(_objects, _transformStartSnapshots, after, actionName));
        }

        _transformStartSnapshots = null;
        _activeResizeHandle = -1;
    }

    private static Point[] GetResizeHandleCenters(in ObjectRect b)
    {
        return new[]
        {
            new Point(b.Left, b.Top),
            new Point(b.CenterX, b.Top),
            new Point(b.Right, b.Top),
            new Point(b.Right, b.CenterY),
            new Point(b.Right, b.Bottom),
            new Point(b.CenterX, b.Bottom),
            new Point(b.Left, b.Bottom),
            new Point(b.Left, b.CenterY)
        };
    }
}
