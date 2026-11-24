using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrackPadLens.Objects;

namespace TrackPadLens.Intelligence;

public interface IHandwritingRecognitionProvider
{
    string ProviderName { get; }
    Task<OcrRecognitionResult> RecognizeStrokesAsync(IReadOnlyList<InkStrokeObject> strokes, CancellationToken ct = default);
}

/// <summary>
/// Renders selected InkStrokeObjects onto an anti-aliased high-DPI monochrome bitmap
/// and feeds it to Windows Offline OCR asynchronously (replaceable with Windows Ink Analysis / ONNX later).
/// </summary>
public sealed class WindowsOcrHandwritingProvider : IHandwritingRecognitionProvider
{
    private readonly OcrService _ocr;
    public string ProviderName => "Windows Offline OCR (Rasterized Ink)";

    public WindowsOcrHandwritingProvider(OcrService ocr) => _ocr = ocr;

    public async Task<OcrRecognitionResult> RecognizeStrokesAsync(IReadOnlyList<InkStrokeObject> strokes, CancellationToken ct = default)
    {
        if (strokes.Count == 0)
        {
            return new OcrRecognitionResult(false, string.Empty, new(), new(), 0.0, "Select handwritten strokes first.");
        }

        byte[] png = SmartInkEngine.RenderStrokesToRecognitionPng(strokes);
        return await _ocr.RecognizePngBytesAsync(png, ct);
    }
}

/// <summary>
/// Dedicated Smart Ink Engine:
/// - Async handwriting recognition via replaceable IHandwritingRecognitionProvider
/// - Convert to Text (non-destructive or replace)
/// - Clean Handwriting (geometric baseline alignment & line spacing normalization preserving original stroke style)
/// - Typeset (heading/list/paragraph TextObject creation)
/// Never runs full-document OCR on pointer movement.
/// </summary>
public sealed class SmartInkEngine
{
    private readonly IHandwritingRecognitionProvider _provider;

    public string ActiveProviderName => _provider.ProviderName;

    public SmartInkEngine(IHandwritingRecognitionProvider? provider = null)
    {
        _provider = provider ?? new WindowsOcrHandwritingProvider(new OcrService());
    }

    public Task<OcrRecognitionResult> RecognizeHandwritingAsync(IReadOnlyList<InkStrokeObject> strokes, CancellationToken ct = default)
    {
        return _provider.RecognizeStrokesAsync(strokes, ct);
    }

    /// <summary>
    /// Cleans handwritten strokes by grouping them into horizontal writing lines, leveling each line's
    /// baseline, and normalizing vertical line spacing while preserving the user's actual handwritten strokes.
    /// Returns cloned strokes so the original handwriting remains intact for Undo.
    /// </summary>
    public static List<InkStrokeObject> CleanHandwritingStrokes(IReadOnlyList<InkStrokeObject> sourceStrokes)
    {
        if (sourceStrokes.Count == 0) return new List<InkStrokeObject>();

        var clones = sourceStrokes.Select(s => (InkStrokeObject)s.Clone(newId: true)).ToList();
        if (clones.Count == 1) return clones;

        // Sort top-to-bottom and cluster strokes into writing lines by vertical center proximity
        var sorted = clones.OrderBy(s => s.Bounds.CenterY).ToList();
        double medianHeight = sorted.Select(s => s.Bounds.Height).OrderBy(h => h).ElementAt(sorted.Count / 2);
        double lineThreshold = Math.Max(22.0, medianHeight * 1.15);

        var lines = new List<List<InkStrokeObject>>();
        foreach (var stroke in sorted)
        {
            if (lines.Count == 0)
            {
                lines.Add(new List<InkStrokeObject> { stroke });
                continue;
            }

            var currentLine = lines[^1];
            double avgCy = currentLine.Average(s => s.Bounds.CenterY);
            if (Math.Abs(stroke.Bounds.CenterY - avgCy) <= lineThreshold)
            {
                currentLine.Add(stroke);
            }
            else
            {
                lines.Add(new List<InkStrokeObject> { stroke });
            }
        }

        double leftMargin = clones.Min(s => s.Bounds.Left);
        double startY = lines[0].Average(s => s.Bounds.Bottom);
        double uniformLineSpacing = Math.Max(36.0, medianHeight * 1.65);

        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var line = lines[lineIdx].OrderBy(s => s.Bounds.Left).ToList();
            double targetBaseline = startY + lineIdx * uniformLineSpacing;

            // Gentle baseline alignment (blend 65% toward line baseline so natural ascenders/descenders survive)
            foreach (var stroke in line)
            {
                double currentBottom = stroke.Bounds.Bottom;
                double dy = (targetBaseline - currentBottom) * 0.65;
                if (Math.Abs(dy) > 0.5)
                {
                    stroke.Translate(0, dy);
                }
            }

            // Gently snap line's left edge closer to common paragraph margin when within 45 DIP
            double lineLeft = line.Min(s => s.Bounds.Left);
            if (Math.Abs(lineLeft - leftMargin) <= 45.0)
            {
                double dx = (leftMargin - lineLeft) * 0.7;
                foreach (var stroke in line) stroke.Translate(dx, 0);
            }
        }

        return clones;
    }

    public static TextObject CreateTypesetTextObject(
        string recognizedText,
        in ObjectRect sourceBounds,
        bool darkTheme,
        bool formatAsTypeset = false)
    {
        string content = recognizedText.Trim();
        bool looksLikeHeading = formatAsTypeset && !content.Contains('\n') && content.Length <= 48 && !content.EndsWith('.');

        return new TextObject
        {
            Text = content,
            X = sourceBounds.Left,
            Y = sourceBounds.Top,
            Width = Math.Max(180, sourceBounds.Width + 24),
            Height = Math.Max(46, sourceBounds.Height + 16),
            FontFamily = formatAsTypeset ? "Georgia" : "Segoe UI",
            FontSize = looksLikeHeading ? 22.0 : 16.0,
            IsHeading = looksLikeHeading,
            IsBold = looksLikeHeading,
            ForegroundHex = darkTheme ? "#F8FAFC" : "#0F172A"
        };
    }

    public static byte[] RenderStrokesToRecognitionPng(IReadOnlyList<InkStrokeObject> strokes)
    {
        var bounds = strokes[0].Bounds;
        for (int i = 1; i < strokes.Count; i++)
        {
            bounds = ObjectRect.Union(in bounds, strokes[i].Bounds);
        }

        const double pad = 28.0;
        double scale = Math.Clamp(160.0 / Math.Max(40.0, bounds.Height), 1.5, 3.0);
        int width = Math.Clamp((int)Math.Ceiling((bounds.Width + pad * 2) * scale), 96, 2048);
        int height = Math.Clamp((int)Math.Ceiling((bounds.Height + pad * 2) * scale), 64, 2048);

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var pen = new Pen(Brushes.Black, Math.Max(3.0, 2.2 * scale))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            foreach (var stroke in strokes)
            {
                if (stroke.Points.Count == 0) continue;
                if (stroke.Points.Count == 1)
                {
                    var p = new Point((stroke.Points[0].X - bounds.Left + pad) * scale, (stroke.Points[0].Y - bounds.Top + pad) * scale);
                    dc.DrawEllipse(Brushes.Black, null, p, 2.5, 2.5);
                    continue;
                }

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point((stroke.Points[0].X - bounds.Left + pad) * scale, (stroke.Points[0].Y - bounds.Top + pad) * scale), false, false);
                    for (int i = 1; i < stroke.Points.Count; i++)
                    {
                        ctx.LineTo(new Point((stroke.Points[i].X - bounds.Left + pad) * scale, (stroke.Points[i].Y - bounds.Top + pad) * scale), true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
