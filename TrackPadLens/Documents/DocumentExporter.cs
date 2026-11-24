using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrackPadLens.Core;
using TrackPadLens.Objects;
using TrackPadLens.Storage;

namespace TrackPadLens.Documents;

/// <summary>
/// Renders WorkspacePages or selected DocumentObjects into crisp PNG, JPEG, or Flattened PDF files
/// without modifying original source PDF/DOCX documents.
/// </summary>
public static class DocumentExporter
{
    public static RenderTargetBitmap RenderPageToBitmap(
        WorkspacePage page,
        AssetStore assets,
        BitmapSource? backgroundImage = null,
        bool darkCanvasBackground = false,
        double dpi = 144.0)
    {
        double widthDip = Math.Max(640, page.PageWidthDip);
        double heightDip = Math.Max(480, page.PageHeightDip);

        if (backgroundImage != null)
        {
            widthDip = Math.Max(widthDip, backgroundImage.PixelWidth * 96.0 / Math.Max(72.0, backgroundImage.DpiX));
            heightDip = Math.Max(heightDip, backgroundImage.PixelHeight * 96.0 / Math.Max(72.0, backgroundImage.DpiY));
        }
        else if (page.Objects.Count > 0)
        {
            double maxR = page.Objects.Max(o => o.Bounds.Right) + 60;
            double maxB = page.Objects.Max(o => o.Bounds.Bottom) + 60;
            widthDip = Math.Clamp(Math.Max(widthDip, maxR), 640, 2800);
            heightDip = Math.Clamp(Math.Max(heightDip, maxB), 480, 3200);
        }

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            var bgColor = darkCanvasBackground ? Color.FromRgb(0x0F, 0x17, 0x2A) : Colors.White;
            dc.DrawRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, widthDip, heightDip));

            if (backgroundImage != null)
            {
                dc.DrawImage(backgroundImage, new Rect(0, 0, widthDip, heightDip));
            }
            else
            {
                DrawPaperPattern(dc, page.PaperStyle, widthDip, heightDip, darkCanvasBackground);
            }

            foreach (var obj in page.Objects.Where(o => o.IsVisible).OrderBy(o => o.ZIndex))
            {
                DrawObject(dc, obj, assets, dpi / 96.0);
            }
        }

        int pxW = Math.Clamp((int)Math.Round(widthDip * dpi / 96.0), 1, 4096);
        int pxH = Math.Clamp((int)Math.Round(heightDip * dpi / 96.0), 1, 4096);
        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static void ExportPageToPng(string outputPath, WorkspacePage page, AssetStore assets, BitmapSource? background = null, bool darkBg = false)
    {
        var rtb = RenderPageToBitmap(page, assets, background, darkBg);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }

    public static void ExportPageToJpeg(string outputPath, WorkspacePage page, AssetStore assets, BitmapSource? background = null, bool darkBg = false, int quality = 92)
    {
        var rtb = RenderPageToBitmap(page, assets, background, darkBg);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 50, 100) };
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }

    public static void ExportPagesToFlattenedPdf(
        string outputPath,
        IReadOnlyList<(WorkspacePage Page, BitmapSource? Background)> pages,
        AssetStore assets,
        bool darkBg = false)
    {
        var pdfPages = new List<PdfExportPage>(pages.Count);
        foreach (var (page, bg) in pages)
        {
            var rtb = RenderPageToBitmap(page, assets, bg, darkBg, dpi: 144.0);
            var jpegEncoder = new JpegBitmapEncoder { QualityLevel = 90 };
            jpegEncoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            jpegEncoder.Save(ms);

            double widthPt = rtb.Width * 72.0 / 96.0;
            double heightPt = rtb.Height * 72.0 / 96.0;
            pdfPages.Add(new PdfExportPage(ms.ToArray(), rtb.PixelWidth, rtb.PixelHeight, widthPt, heightPt));
        }

        SimplePdfWriter.WriteFlattenedPdf(outputPath, pdfPages);
    }

    public static void DrawPaperPattern(DrawingContext dc, CanvasPaperStyle style, double width, double height, bool dark)
    {
        if (style == CanvasPaperStyle.Blank) return;
        Color lineColor = dark ? Color.FromArgb(40, 148, 163, 184) : Color.FromArgb(45, 100, 116, 139);
        var brush = new SolidColorBrush(lineColor);
        brush.Freeze();
        var pen = new Pen(brush, 1.0);
        pen.Freeze();

        const double step = 32.0;
        if (style == CanvasPaperStyle.Ruled)
        {
            for (double y = step; y < height; y += step)
            {
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            }
        }
        else if (style == CanvasPaperStyle.Grid)
        {
            for (double x = step; x < width; x += step)
                dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            for (double y = step; y < height; y += step)
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
        else if (style == CanvasPaperStyle.Dotted)
        {
            for (double x = step; x < width; x += step)
            {
                for (double y = step; y < height; y += step)
                {
                    dc.DrawEllipse(brush, null, new Point(x, y), 1.3, 1.3);
                }
            }
        }
    }

    public static void DrawObject(DrawingContext dc, DocumentObject obj, AssetStore assets, double pixelsPerDip = 1.0)
    {
        bool hasRotation = Math.Abs(obj.RotationDegrees) > 0.01;
        if (hasRotation)
        {
            var b = obj.Bounds;
            dc.PushTransform(new RotateTransform(obj.RotationDegrees, b.CenterX, b.CenterY));
        }

        switch (obj)
        {
            case InkStrokeObject stroke:
                DrawInkStroke(dc, stroke);
                break;

            case ImageObject img:
                DrawImageObject(dc, img, assets);
                break;

            case VectorShapeObject shape:
                DrawShapeObject(dc, shape, pixelsPerDip);
                break;

            case ConnectorObject conn:
                DrawConnectorObject(dc, conn, pixelsPerDip);
                break;

            case TextObject txt:
                DrawTextObject(dc, txt, pixelsPerDip);
                break;

            case EquationObject eq:
                DrawEquationObject(dc, eq, pixelsPerDip);
                break;

            case StickyNoteObject note:
                DrawStickyNoteObject(dc, note, pixelsPerDip);
                break;

            case PdfPageObject pdfPage when !string.IsNullOrEmpty(pdfPage.CachedAssetId):
                var pdfBmp = assets.GetDecodedBitmap(pdfPage.CachedAssetId);
                if (pdfBmp != null)
                {
                    dc.DrawImage(pdfBmp, new Rect(pdfPage.X, pdfPage.Y, pdfPage.Width, pdfPage.Height));
                }
                break;
        }

        if (hasRotation)
        {
            dc.Pop();
        }
    }

    private static void DrawInkStroke(DrawingContext dc, InkStrokeObject stroke)
    {
        var pts = stroke.Points;
        if (pts.Count == 0) return;

        Color baseColor = ParseColor(stroke.ColorHex, Colors.Red);
        byte alpha = (byte)Math.Clamp(Math.Round(stroke.Opacity * 255.0), 10, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();

        var cap = stroke.PenStyle == WorkspaceTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;
        var pen = new Pen(brush, stroke.Thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();

        if (pts.Count == 1)
        {
            double r = stroke.Thickness * 0.5;
            dc.DrawEllipse(brush, null, new Point(pts[0].X, pts[0].Y), r, r);
            return;
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), false, false);
            if (pts.Count == 2)
            {
                ctx.LineTo(new Point(pts[1].X, pts[1].Y), true, true);
            }
            else
            {
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    var mid = new Point((pts[i].X + pts[i + 1].X) * 0.5, (pts[i].Y + pts[i + 1].Y) * 0.5);
                    ctx.QuadraticBezierTo(new Point(pts[i].X, pts[i].Y), mid, true, true);
                }
                ctx.LineTo(new Point(pts[^1].X, pts[^1].Y), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawImageObject(DrawingContext dc, ImageObject img, AssetStore assets)
    {
        var bmp = assets.GetDecodedBitmap(img.AssetId);
        var rect = new Rect(img.X, img.Y, img.Width, img.Height);
        if (bmp == null)
        {
            dc.DrawRectangle(Brushes.DimGray, new Pen(Brushes.Gray, 1), rect);
            return;
        }

        BitmapSource renderSource = bmp;
        if (img.CropLeft > 0.001 || img.CropTop > 0.001 || img.CropRight < 0.999 || img.CropBottom < 0.999)
        {
            int cx = (int)Math.Clamp(Math.Round(img.CropLeft * bmp.PixelWidth), 0, Math.Max(0, bmp.PixelWidth - 2));
            int cy = (int)Math.Clamp(Math.Round(img.CropTop * bmp.PixelHeight), 0, Math.Max(0, bmp.PixelHeight - 2));
            int cw = (int)Math.Clamp(Math.Round((img.CropRight - img.CropLeft) * bmp.PixelWidth), 1, bmp.PixelWidth - cx);
            int ch = (int)Math.Clamp(Math.Round((img.CropBottom - img.CropTop) * bmp.PixelHeight), 1, bmp.PixelHeight - cy);
            renderSource = new CroppedBitmap(bmp, new Int32Rect(cx, cy, cw, ch));
        }

        bool flip = img.FlipHorizontal || img.FlipVertical;
        if (flip)
        {
            dc.PushTransform(new ScaleTransform(
                img.FlipHorizontal ? -1 : 1,
                img.FlipVertical ? -1 : 1,
                rect.Left + rect.Width * 0.5,
                rect.Top + rect.Height * 0.5));
        }

        if (img.Opacity < 0.99) dc.PushOpacity(img.Opacity);
        dc.DrawImage(renderSource, rect);
        if (img.Opacity < 0.99) dc.Pop();
        if (flip) dc.Pop();
    }

    private static void DrawShapeObject(DrawingContext dc, VectorShapeObject shape, double ppd)
    {
        var strokeBrush = new SolidColorBrush(ParseColor(shape.StrokeHex, Color.FromRgb(0x00, 0xD2, 0xFF)));
        var fillBrush = shape.FillHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
            ? null
            : new SolidColorBrush(ParseColor(shape.FillHex, Color.FromArgb(30, 0, 210, 255)));
        var pen = new Pen(strokeBrush, shape.StrokeThickness) { LineJoin = PenLineJoin.Round };

        var r = new Rect(shape.X, shape.Y, shape.Width, shape.Height);
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
                dc.DrawRoundedRectangle(fillBrush, pen, r, 4, 4);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(fillBrush, pen, new Point(r.Left + r.Width * 0.5, r.Top + r.Height * 0.5), r.Width * 0.5, r.Height * 0.5);
                break;
            case ShapeKind.Triangle:
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(r.Left + r.Width * 0.5, r.Top), true, true);
                    ctx.LineTo(new Point(r.Right, r.Bottom), true, true);
                    ctx.LineTo(new Point(r.Left, r.Bottom), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(fillBrush, pen, geo);
                break;
            }
            case ShapeKind.Diamond:
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(r.Left + r.Width * 0.5, r.Top), true, true);
                    ctx.LineTo(new Point(r.Right, r.Top + r.Height * 0.5), true, true);
                    ctx.LineTo(new Point(r.Left + r.Width * 0.5, r.Bottom), true, true);
                    ctx.LineTo(new Point(r.Left, r.Top + r.Height * 0.5), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(fillBrush, pen, geo);
                break;
            }
            case ShapeKind.Line:
            case ShapeKind.Arrow:
                dc.DrawLine(pen, new Point(r.Left, r.Top + r.Height * 0.5), new Point(r.Right, r.Top + r.Height * 0.5));
                if (shape.Kind == ShapeKind.Arrow)
                {
                    DrawArrowHead(dc, strokeBrush, new Point(r.Left, r.Top + r.Height * 0.5), new Point(r.Right, r.Top + r.Height * 0.5), 10);
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(shape.Label))
        {
            var ft = new FormattedText(shape.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, strokeBrush, ppd) { MaxTextWidth = Math.Max(20, r.Width - 10) };
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) * 0.5, r.Top + (r.Height - ft.Height) * 0.5));
        }
    }

    private static void DrawConnectorObject(DrawingContext dc, ConnectorObject conn, double ppd)
    {
        var brush = new SolidColorBrush(ParseColor(conn.StrokeHex, Color.FromRgb(0x38, 0xBD, 0xF8)));
        var pen = new Pen(brush, conn.StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var p0 = new Point(conn.StartX, conn.StartY);
        var p1 = new Point(conn.EndX, conn.EndY);
        dc.DrawLine(pen, p0, p1);
        if (conn.HasArrowHead)
        {
            DrawArrowHead(dc, brush, p0, p1, 11);
        }
        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            var ft = new FormattedText(conn.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, brush, ppd);
            dc.DrawText(ft, new Point((p0.X + p1.X) * 0.5 - ft.Width * 0.5, (p0.Y + p1.Y) * 0.5 - ft.Height - 4));
        }
    }

    private static void DrawArrowHead(DrawingContext dc, Brush brush, Point start, Point end, double size)
    {
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var left = new Point(end.X - size * Math.Cos(angle - 0.45), end.Y - size * Math.Sin(angle - 0.45));
        var right = new Point(end.X - size * Math.Cos(angle + 0.45), end.Y - size * Math.Sin(angle + 0.45));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(left, false, false);
            ctx.LineTo(right, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawTextObject(DrawingContext dc, TextObject txt, double ppd)
    {
        if (!txt.BackgroundHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(ParseColor(txt.BackgroundHex, Colors.Transparent)), null, new Rect(txt.X, txt.Y, txt.Width, txt.Height), 4, 4);
        }

        var typeface = new Typeface(
            new FontFamily(txt.FontFamily),
            txt.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            txt.IsBold || txt.IsHeading ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);

        var ft = new FormattedText(
            txt.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            txt.IsHeading ? Math.Max(20, txt.FontSize) : txt.FontSize,
            new SolidColorBrush(ParseColor(txt.ForegroundHex, Colors.White)),
            ppd)
        {
            MaxTextWidth = Math.Max(40, txt.Width - 8)
        };
        dc.DrawText(ft, new Point(txt.X + 4, txt.Y + 4));
    }

    private static void DrawEquationObject(DrawingContext dc, EquationObject eq, double ppd)
    {
        var rect = new Rect(eq.X, eq.Y, eq.Width, eq.Height);
        var bg = new SolidColorBrush(Color.FromArgb(215, 15, 23, 42));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF)), 1.2);
        dc.DrawRoundedRectangle(bg, border, rect, 6, 6);

        var exprText = string.IsNullOrWhiteSpace(eq.LaTeX) ? eq.RawExpression : eq.LaTeX;
        var ftExpr = new FormattedText(exprText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cambria Math, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            17, new SolidColorBrush(ParseColor(eq.ForegroundHex, Color.FromRgb(0x38, 0xBD, 0xF8))), ppd)
        {
            MaxTextWidth = Math.Max(40, rect.Width - 20)
        };
        dc.DrawText(ftExpr, new Point(rect.Left + 10, rect.Top + 8));

        if (!string.IsNullOrWhiteSpace(eq.SolvedResult))
        {
            var ftSol = new FormattedText($"⇒  {eq.SolvedResult}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                14, new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), ppd)
            {
                MaxTextWidth = Math.Max(40, rect.Width - 20)
            };
            dc.DrawText(ftSol, new Point(rect.Left + 10, rect.Top + 12 + ftExpr.Height));
        }
    }

    private static void DrawStickyNoteObject(DrawingContext dc, StickyNoteObject note, double ppd)
    {
        var rect = new Rect(note.X, note.Y, note.Width, note.Height);
        var bg = new SolidColorBrush(ParseColor(note.NoteColorHex, Color.FromRgb(0xFE, 0xF0, 0x8A)));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04)), 1.0);
        dc.DrawRoundedRectangle(bg, border, rect, 6, 6);

        var ft = new FormattedText(note.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, new SolidColorBrush(ParseColor(note.TextColorHex, Color.FromRgb(0x1E, 0x29, 0x3B))), ppd)
        {
            MaxTextWidth = Math.Max(40, rect.Width - 16)
        };
        dc.DrawText(ft, new Point(rect.Left + 8, rect.Top + 8));
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c) return c;
        }
        catch { }
        return fallback;
    }
}
