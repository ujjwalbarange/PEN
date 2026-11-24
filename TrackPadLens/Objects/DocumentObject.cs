using System.Text.Json.Serialization;
using TrackPadLens.Core;

namespace TrackPadLens.Objects;

public readonly record struct ObjectRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width * 0.5;
    public double CenterY => Y + Height * 0.5;

    public bool Intersects(in ObjectRect other)
    {
        return Left <= other.Right && Right >= other.Left &&
               Top <= other.Bottom && Bottom >= other.Top;
    }

    public bool Contains(double px, double py, double padding = 0.0)
    {
        return px >= Left - padding && px <= Right + padding &&
               py >= Top - padding && py <= Bottom + padding;
    }

    public static ObjectRect Union(in ObjectRect a, in ObjectRect b)
    {
        double left = Math.Min(a.Left, b.Left);
        double top = Math.Min(a.Top, b.Top);
        double right = Math.Max(a.Right, b.Right);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        return new ObjectRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

public readonly record struct InkPointData(double X, double Y, double Timestamp, float Pressure = 0.5f);

/// <summary>
/// Unified polymorphic document object base stored in every Whiteboard and Study Workspace page.
/// Supports independent positioning, sizing, rotation, z-order, locking, grouping, and serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(InkStrokeObject), "ink")]
[JsonDerivedType(typeof(TextObject), "text")]
[JsonDerivedType(typeof(EquationObject), "equation")]
[JsonDerivedType(typeof(ImageObject), "image")]
[JsonDerivedType(typeof(VectorShapeObject), "shape")]
[JsonDerivedType(typeof(ConnectorObject), "connector")]
[JsonDerivedType(typeof(StickyNoteObject), "sticky")]
[JsonDerivedType(typeof(PdfPageObject), "pdfPage")]
[JsonDerivedType(typeof(DiagramObject), "diagram")]
public abstract class DocumentObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 120;
    public double Height { get; set; } = 60;
    public double RotationDegrees { get; set; }
    public int ZIndex { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public string? GroupId { get; set; }
    public string? SourceDocumentPath { get; set; }
    public int? SourcePageNumber { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsSelected { get; set; }

    [JsonIgnore]
    public virtual ObjectRect Bounds => new(X, Y, Math.Max(1.0, Width), Math.Max(1.0, Height));

    public virtual void Translate(double dx, double dy)
    {
        if (IsLocked) return;
        X += dx;
        Y += dy;
        ModifiedUtc = DateTime.UtcNow;
    }

    public virtual void ResizeTo(double newX, double newY, double newWidth, double newHeight)
    {
        if (IsLocked) return;
        X = newX;
        Y = newY;
        Width = Math.Max(12.0, newWidth);
        Height = Math.Max(12.0, newHeight);
        ModifiedUtc = DateTime.UtcNow;
    }

    public abstract DocumentObject Clone(bool newId = true);

    protected void CopyBaseTo(DocumentObject target, bool newId)
    {
        target.Id = newId ? Guid.NewGuid().ToString("N") : Id;
        target.X = X;
        target.Y = Y;
        target.Width = Width;
        target.Height = Height;
        target.RotationDegrees = RotationDegrees;
        target.ZIndex = ZIndex;
        target.IsVisible = IsVisible;
        target.IsLocked = IsLocked;
        target.GroupId = newId ? null : GroupId;
        target.SourceDocumentPath = SourceDocumentPath;
        target.SourcePageNumber = SourcePageNumber;
        target.CreatedUtc = CreatedUtc;
        target.ModifiedUtc = DateTime.UtcNow;
    }
}

public sealed class InkStrokeObject : DocumentObject
{
    public List<InkPointData> Points { get; set; } = new();
    public string ColorHex { get; set; } = "#FF3B30";
    public double Thickness { get; set; } = 3.5;
    public double Opacity { get; set; } = 1.0;
    public WorkspaceTool PenStyle { get; set; } = WorkspaceTool.Pen;

    public void RecalculateBounds()
    {
        if (Points.Count == 0)
        {
            X = 0;
            Y = 0;
            Width = 1;
            Height = 1;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        double pad = Math.Max(2.0, Thickness * 0.5);
        X = minX - pad;
        Y = minY - pad;
        Width = Math.Max(Thickness, (maxX - minX) + pad * 2.0);
        Height = Math.Max(Thickness, (maxY - minY) + pad * 2.0);
    }

    public override void Translate(double dx, double dy)
    {
        if (IsLocked) return;
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            Points[i] = new InkPointData(p.X + dx, p.Y + dy, p.Timestamp, p.Pressure);
        }
        base.Translate(dx, dy);
    }

    public override void ResizeTo(double newX, double newY, double newWidth, double newHeight)
    {
        if (IsLocked) return;
        double oldW = Math.Max(1.0, Width);
        double oldH = Math.Max(1.0, Height);
        double sx = Math.Max(12.0, newWidth) / oldW;
        double sy = Math.Max(12.0, newHeight) / oldH;
        double oldX = X;
        double oldY = Y;

        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            double nx = newX + (p.X - oldX) * sx;
            double ny = newY + (p.Y - oldY) * sy;
            Points[i] = new InkPointData(nx, ny, p.Timestamp, p.Pressure);
        }

        base.ResizeTo(newX, newY, newWidth, newHeight);
    }

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new InkStrokeObject
        {
            Points = new List<InkPointData>(Points),
            ColorHex = ColorHex,
            Thickness = Thickness,
            Opacity = Opacity,
            PenStyle = PenStyle
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class TextObject : DocumentObject
{
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 16.0;
    public string ForegroundHex { get; set; } = "#F8FAFC";
    public string BackgroundHex { get; set; } = "Transparent";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsHeading { get; set; }

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new TextObject
        {
            Text = Text,
            FontFamily = FontFamily,
            FontSize = FontSize,
            ForegroundHex = ForegroundHex,
            BackgroundHex = BackgroundHex,
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsHeading = IsHeading
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class EquationObject : DocumentObject
{
    public string RawExpression { get; set; } = string.Empty;
    public string LaTeX { get; set; } = string.Empty;
    public string? SolvedResult { get; set; }
    public List<string> SolutionSteps { get; set; } = new();
    public double RecognitionConfidence { get; set; } = 1.0;
    public bool IsVerified { get; set; } = true;
    public string ForegroundHex { get; set; } = "#38BDF8";

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new EquationObject
        {
            RawExpression = RawExpression,
            LaTeX = LaTeX,
            SolvedResult = SolvedResult,
            SolutionSteps = new List<string>(SolutionSteps),
            RecognitionConfidence = RecognitionConfidence,
            IsVerified = IsVerified,
            ForegroundHex = ForegroundHex
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class ImageObject : DocumentObject
{
    /// <summary>
    /// Reference key inside the project's binary AssetStore (never stores huge base64 strings inside JSON).
    /// </summary>
    public string AssetId { get; set; } = string.Empty;
    public string? OriginalFilePath { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;
    public double CropLeft { get; set; } = 0.0;
    public double CropTop { get; set; } = 0.0;
    public double CropRight { get; set; } = 1.0;
    public double CropBottom { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new ImageObject
        {
            AssetId = AssetId,
            OriginalFilePath = OriginalFilePath,
            PixelWidth = PixelWidth,
            PixelHeight = PixelHeight,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            PreserveAspectRatio = PreserveAspectRatio,
            CropLeft = CropLeft,
            CropTop = CropTop,
            CropRight = CropRight,
            CropBottom = CropBottom,
            Opacity = Opacity
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class VectorShapeObject : DocumentObject
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
    public string StrokeHex { get; set; } = "#00D2FF";
    public string FillHex { get; set; } = "#1A00D2FF";
    public double StrokeThickness { get; set; } = 2.2;
    public string? Label { get; set; }

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new VectorShapeObject
        {
            Kind = Kind,
            StrokeHex = StrokeHex,
            FillHex = FillHex,
            StrokeThickness = StrokeThickness,
            Label = Label
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class ConnectorObject : DocumentObject
{
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public bool HasArrowHead { get; set; } = true;
    public string StrokeHex { get; set; } = "#38BDF8";
    public double StrokeThickness { get; set; } = 2.0;
    public string? Label { get; set; }

    public void SyncBoundsFromEndpoints()
    {
        double minX = Math.Min(StartX, EndX);
        double minY = Math.Min(StartY, EndY);
        X = minX - 6;
        Y = minY - 6;
        Width = Math.Max(14, Math.Abs(EndX - StartX) + 12);
        Height = Math.Max(14, Math.Abs(EndY - StartY) + 12);
    }

    public override void Translate(double dx, double dy)
    {
        if (IsLocked) return;
        StartX += dx;
        StartY += dy;
        EndX += dx;
        EndY += dy;
        base.Translate(dx, dy);
    }

    public override void ResizeTo(double newX, double newY, double newWidth, double newHeight)
    {
        if (IsLocked) return;
        double oldW = Math.Max(1.0, Width);
        double oldH = Math.Max(1.0, Height);
        double sx = Math.Max(12.0, newWidth) / oldW;
        double sy = Math.Max(12.0, newHeight) / oldH;
        StartX = newX + (StartX - X) * sx;
        StartY = newY + (StartY - Y) * sy;
        EndX = newX + (EndX - X) * sx;
        EndY = newY + (EndY - Y) * sy;
        base.ResizeTo(newX, newY, newWidth, newHeight);
    }

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new ConnectorObject
        {
            StartX = StartX,
            StartY = StartY,
            EndX = EndX,
            EndY = EndY,
            HasArrowHead = HasArrowHead,
            StrokeHex = StrokeHex,
            StrokeThickness = StrokeThickness,
            Label = Label
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class StickyNoteObject : DocumentObject
{
    public string Text { get; set; } = "Note";
    public string NoteColorHex { get; set; } = "#FEF08A";
    public string TextColorHex { get; set; } = "#1E293B";

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new StickyNoteObject
        {
            Text = Text,
            NoteColorHex = NoteColorHex,
            TextColorHex = TextColorHex
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class PdfPageObject : DocumentObject
{
    public string PdfFilePath { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public string? CachedAssetId { get; set; }

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new PdfPageObject
        {
            PdfFilePath = PdfFilePath,
            PageIndex = PageIndex,
            CachedAssetId = CachedAssetId
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class DiagramObject : DocumentObject
{
    public DiagramProfile Profile { get; set; } = DiagramProfile.General;
    public string Summary { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.75;
    public string? SourceImageObjectId { get; set; }
    public List<string> MemberObjectIds { get; set; } = new();

    public override DocumentObject Clone(bool newId = true)
    {
        var copy = new DiagramObject
        {
            Profile = Profile,
            Summary = Summary,
            Confidence = Confidence,
            SourceImageObjectId = SourceImageObjectId,
            MemberObjectIds = new List<string>(MemberObjectIds)
        };
        CopyBaseTo(copy, newId);
        return copy;
    }
}

public sealed class WorkspacePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Page 1";
    public CanvasLayoutMode LayoutMode { get; set; } = CanvasLayoutMode.InfiniteCanvas;
    public CanvasPaperStyle PaperStyle { get; set; } = CanvasPaperStyle.Dotted;
    public double PageWidthDip { get; set; } = 1100;
    public double PageHeightDip { get; set; } = 1500;
    public string? LinkedDocumentPath { get; set; }
    public int? LinkedDocumentPageIndex { get; set; }
    public bool IsBookmarked { get; set; }
    public List<DocumentObject> Objects { get; set; } = new();
}

public sealed class WorkspaceNotebook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Study Notebook";
    public string Folder { get; set; } = "General";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<WorkspacePage> Pages { get; set; } = new() { new WorkspacePage() };
}

public sealed class WorkspaceDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Workspace";
    public string? FilePath { get; set; }
    public string? ActiveNotebookId { get; set; }
    public string? ActivePageId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<WorkspaceNotebook> Notebooks { get; set; } = new() { new WorkspaceNotebook() };

    public WorkspaceNotebook GetActiveNotebook()
    {
        if (Notebooks.Count == 0) Notebooks.Add(new WorkspaceNotebook());
        return Notebooks.FirstOrDefault(n => n.Id == ActiveNotebookId) ?? Notebooks[0];
    }

    public WorkspacePage GetActivePage()
    {
        var nb = GetActiveNotebook();
        if (nb.Pages.Count == 0) nb.Pages.Add(new WorkspacePage());
        return nb.Pages.FirstOrDefault(p => p.Id == ActivePageId) ?? nb.Pages[0];
    }
}
