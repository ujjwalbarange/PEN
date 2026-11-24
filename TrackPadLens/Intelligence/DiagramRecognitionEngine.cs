using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TrackPadLens.Core;
using TrackPadLens.Objects;

namespace TrackPadLens.Intelligence;

public sealed record DiagramReconstructionResult(
    bool Success,
    DiagramProfile Profile,
    byte[] CleanedImagePng,
    List<DocumentObject> ReconstructedObjects,
    double AverageConfidence,
    string Summary,
    List<string> AmbiguousNotes);

/// <summary>
/// Offline image cleaning and diagram vector reconstruction engine:
/// - Clean Image output: adaptive luminance background whitening & ink contrast enhancement
///   (preserves original image unchanged).
/// - Editable Vector output: connected-component geometry analysis detecting rectangles/boxes,
///   ellipses/circles, diamonds, straight connectors/arrows, and OCR text labels mapped to exact coordinates.
/// Never fabricates missing components; explicitly reports confidence and ambiguities.
/// </summary>
public sealed class DiagramRecognitionEngine
{
    private readonly OcrService _ocr;

    public DiagramRecognitionEngine(OcrService ocr) => _ocr = ocr;

    public static byte[] BuildCleanedImagePng(byte[] sourceBytes)
    {
        using var srcStream = new MemoryStream(sourceBytes, writable: false);
        using var srcBmp = new Bitmap(srcStream);
        using var bmp = new Bitmap(srcBmp.Width, srcBmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(srcBmp, 0, 0, srcBmp.Width, srcBmp.Height);
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = Math.Abs(data.Stride) * bmp.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            // Estimate global background brightness (top 25% percentile luminance)
            int[] hist = new int[256];
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int idx = row + x * 4;
                    int lum = (pixels[idx + 2] * 77 + pixels[idx + 1] * 150 + pixels[idx] * 29) >> 8;
                    hist[lum]++;
                }
            }

            int total = bmp.Width * bmp.Height;
            int acc = 0, bgLum = 235;
            for (int l = 255; l >= 0; l--)
            {
                acc += hist[l];
                if (acc >= total / 4) { bgLum = l; break; }
            }

            bool darkBackground = bgLum < 110;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int idx = row + x * 4;
                    byte b = pixels[idx], g = pixels[idx + 1], r = pixels[idx + 2];
                    int lum = (r * 77 + g * 150 + b * 29) >> 8;

                    if (!darkBackground)
                    {
                        // Light-background whiteboard/paper cleanup: push near-white paper to pure crisp white, deepen ink
                        if (lum >= bgLum - 22)
                        {
                            pixels[idx] = 255; pixels[idx + 1] = 255; pixels[idx + 2] = 255; pixels[idx + 3] = 255;
                        }
                        else
                        {
                            pixels[idx] = (byte)Math.Clamp((int)(b * 0.82), 0, 255);
                            pixels[idx + 1] = (byte)Math.Clamp((int)(g * 0.82), 0, 255);
                            pixels[idx + 2] = (byte)Math.Clamp((int)(r * 0.82), 0, 255);
                            pixels[idx + 3] = 255;
                        }
                    }
                    else
                    {
                        // Dark-theme screenshot cleanup: deepen slate background & sharpen bright lines
                        if (lum <= bgLum + 22)
                        {
                            pixels[idx] = 24; pixels[idx + 1] = 17; pixels[idx + 2] = 11; pixels[idx + 3] = 255;
                        }
                        else
                        {
                            pixels[idx] = (byte)Math.Clamp((int)(b * 1.15), 0, 255);
                            pixels[idx + 1] = (byte)Math.Clamp((int)(g * 1.15), 0, 255);
                            pixels[idx + 2] = (byte)Math.Clamp((int)(r * 1.15), 0, 255);
                            pixels[idx + 3] = 255;
                        }
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var outMs = new MemoryStream();
        bmp.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }

    public async Task<DiagramReconstructionResult> AnalyzeAndReconstructAsync(
        byte[] imageBytes,
        ImageObject sourceImage,
        DiagramProfile profile,
        bool darkTheme,
        CancellationToken ct = default)
    {
        var objects = new List<DocumentObject>();
        var notes = new List<string>();
        byte[] cleanedPng = BuildCleanedImagePng(imageBytes);

        // Step 1: Run OCR for text labels
        var ocrResult = await _ocr.RecognizePngBytesAsync(cleanedPng, ct);

        // Step 2: Connected-component foreground geometry analysis on downscaled binary mask
        using var ms = new MemoryStream(cleanedPng, writable: false);
        using var bmp = new Bitmap(ms);

        int origW = Math.Max(1, bmp.Width);
        int origH = Math.Max(1, bmp.Height);
        double scaleX = sourceImage.Width / origW;
        double scaleY = sourceImage.Height / origH;

        const int maxAnalysisDim = 320;
        double downScale = Math.Min(1.0, maxAnalysisDim / (double)Math.Max(origW, origH));
        int aw = Math.Max(16, (int)Math.Round(origW * downScale));
        int ah = Math.Max(16, (int)Math.Round(origH * downScale));

        using var small = new Bitmap(aw, ah, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.DrawImage(bmp, 0, 0, aw, ah);
        }

        bool[] mask = ExtractForegroundMask(small);
        var components = FindConnectedComponents(mask, aw, ah);

        // Offset reconstructed diagram slightly to the right of the original image for side-by-side comparison
        double originX = sourceImage.X + sourceImage.Width + 32;
        double originY = sourceImage.Y;

        int shapeCount = 0;
        int connectorCount = 0;
        double confSum = 0;
        int confItems = 0;

        foreach (var comp in components)
        {
            // Ignore tiny noise specks or full-image border frames
            if (comp.Width < 10 && comp.Height < 10) continue;
            if (comp.Width > aw * 0.94 && comp.Height > ah * 0.94) continue;

            double worldX = originX + (comp.MinX / downScale) * scaleX;
            double worldY = originY + (comp.MinY / downScale) * scaleY;
            double worldW = (comp.Width / downScale) * scaleX;
            double worldH = (comp.Height / downScale) * scaleY;

            // Skip small regions that overlap strongly with OCR text boxes (those become editable TextObjects instead)
            if (IsTextComponent(comp, downScale, ocrResult.Words))
            {
                continue;
            }

            double aspect = comp.Width / (double)Math.Max(1, comp.Height);
            double fillRatio = comp.PixelCount / (double)Math.Max(1, comp.Width * comp.Height);

            if ((aspect >= 3.2 && comp.Height <= 24) || (aspect <= 0.31 && comp.Width <= 24))
            {
                // Straight connector or arrow segment
                bool horizontal = comp.Width >= comp.Height;
                var conn = new ConnectorObject
                {
                    StartX = horizontal ? worldX : worldX + worldW * 0.5,
                    StartY = horizontal ? worldY + worldH * 0.5 : worldY,
                    EndX = horizontal ? worldX + worldW : worldX + worldW * 0.5,
                    EndY = horizontal ? worldY + worldH * 0.5 : worldY + worldH,
                    HasArrowHead = profile is DiagramProfile.Physics or DiagramProfile.General,
                    StrokeHex = darkTheme ? "#38BDF8" : "#0284C7",
                    StrokeThickness = 2.2
                };
                conn.SyncBoundsFromEndpoints();
                objects.Add(conn);
                connectorCount++;
                confSum += 0.80;
                confItems++;
            }
            else if (comp.Width >= 18 && comp.Height >= 18)
            {
                // Classify geometric shape by border/corner occupancy
                var kind = ClassifyShapeKind(mask, aw, comp, fillRatio);
                var shape = new VectorShapeObject
                {
                    Kind = kind,
                    X = worldX,
                    Y = worldY,
                    Width = Math.Max(24, worldW),
                    Height = Math.Max(24, worldH),
                    StrokeHex = darkTheme ? "#00D2FF" : "#0369A1",
                    FillHex = darkTheme ? "#1800D2FF" : "#140369A1",
                    StrokeThickness = 2.0
                };
                objects.Add(shape);
                shapeCount++;
                confSum += kind == ShapeKind.Rectangle ? 0.84 : 0.72;
                confItems++;
            }
        }

        // Step 3: Add editable OCR text labels mapped to exact relative positions
        int labelCount = 0;
        if (ocrResult.Success)
        {
            foreach (var word in ocrResult.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var txt = new TextObject
                {
                    Text = word.Text,
                    X = originX + word.X * scaleX,
                    Y = originY + word.Y * scaleY,
                    Width = Math.Max(48, word.Width * scaleX + 16),
                    Height = Math.Max(26, word.Height * scaleY + 8),
                    FontSize = Math.Clamp(word.Height * scaleY * 0.82, 11, 24),
                    ForegroundHex = darkTheme ? "#F8FAFC" : "#0F172A"
                };
                objects.Add(txt);
                labelCount++;
                confSum += ocrResult.EstimatedConfidence;
                confItems++;
            }
        }
        else
        {
            notes.Add(ocrResult.DiagnosticMessage);
        }

        if (profile == DiagramProfile.ElectricalEngineering)
        {
            notes.Add("Electrical profile: standard rectangular/circular nodes and circuit lines reconstructed; specialized symbols (transistors/op-amps/logic gates) require manual refinement or a dedicated ONNX circuit symbol model.");
        }

        double avgConf = confItems > 0 ? confSum / confItems : 0.0;
        string summary = $"Detected {shapeCount} shape(s), {connectorCount} connector(s), and {labelCount} text label(s) [{profile}, confidence {avgConf:P0}].";

        return new DiagramReconstructionResult(
            objects.Count > 0,
            profile,
            cleanedPng,
            objects,
            avgConf,
            summary,
            notes);
    }

    private sealed record ComponentBox(int MinX, int MinY, int MaxX, int MaxY, int PixelCount)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
    }

    private static bool[] ExtractForegroundMask(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        bool[] mask = new bool[w * h];
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] px = new byte[Math.Abs(data.Stride) * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);

            long lumSum = 0;
            int[] lums = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x * 4;
                    int lum = (px[idx + 2] * 77 + px[idx + 1] * 150 + px[idx] * 29) >> 8;
                    lums[y * w + x] = lum;
                    lumSum += lum;
                }
            }

            double avgLum = lumSum / (double)(w * h);
            bool lightBg = avgLum >= 128.0;
            for (int i = 0; i < lums.Length; i++)
            {
                mask[i] = lightBg ? lums[i] < (avgLum - 35) : lums[i] > (avgLum + 35);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return mask;
    }

    private static List<ComponentBox> FindConnectedComponents(bool[] mask, int w, int h)
    {
        bool[] visited = new bool[w * h];
        var results = new List<ComponentBox>();
        var queue = new Queue<int>(256);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int start = y * w + x;
                if (!mask[start] || visited[start]) continue;

                visited[start] = true;
                queue.Enqueue(start);
                int minX = x, maxX = x, minY = y, maxY = y, count = 0;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    int cx = cur % w;
                    int cy = cur / w;
                    count++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= w) continue;
                            int nIdx = ny * w + nx;
                            if (mask[nIdx] && !visited[nIdx])
                            {
                                visited[nIdx] = true;
                                queue.Enqueue(nIdx);
                            }
                        }
                    }
                }

                if (count >= 12)
                {
                    results.Add(new ComponentBox(minX, minY, maxX, maxY, count));
                }
            }
        }

        return results.OrderByDescending(c => c.Width * c.Height).Take(40).ToList();
    }

    private static bool IsTextComponent(ComponentBox comp, double downScale, List<OcrWordBox> words)
    {
        double ox0 = comp.MinX / downScale, oy0 = comp.MinY / downScale;
        double ox1 = comp.MaxX / downScale, oy1 = comp.MaxY / downScale;
        double cx = (ox0 + ox1) * 0.5, cy = (oy0 + oy1) * 0.5;

        foreach (var w in words)
        {
            if (cx >= w.X - 4 && cx <= w.X + w.Width + 4 && cy >= w.Y - 4 && cy <= w.Y + w.Height + 4)
            {
                if ((ox1 - ox0) <= w.Width * 1.4 && (oy1 - oy0) <= w.Height * 1.5)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static ShapeKind ClassifyShapeKind(bool[] mask, int w, ComponentBox c, double fillRatio)
    {
        // Check corner pixel presence vs mid-edge pixel presence to distinguish rectangle, ellipse, diamond, triangle
        int cornerHits = 0;
        int samplePad = Math.Max(1, Math.Min(c.Width, c.Height) / 8);
        if (HasInkNear(mask, w, c.MinX, c.MinY, samplePad)) cornerHits++;
        if (HasInkNear(mask, w, c.MaxX, c.MinY, samplePad)) cornerHits++;
        if (HasInkNear(mask, w, c.MinX, c.MaxY, samplePad)) cornerHits++;
        if (HasInkNear(mask, w, c.MaxX, c.MaxY, samplePad)) cornerHits++;

        if (cornerHits >= 3) return ShapeKind.Rectangle;
        if (cornerHits == 2 && HasInkNear(mask, w, (c.MinX + c.MaxX) / 2, c.MinY, samplePad)) return ShapeKind.Triangle;
        if (fillRatio < 0.35 && cornerHits == 0)
        {
            double aspect = c.Width / (double)Math.Max(1, c.Height);
            return Math.Abs(aspect - 1.0) < 0.25 ? ShapeKind.Ellipse : ShapeKind.Diamond;
        }
        return ShapeKind.Ellipse;
    }

    private static bool HasInkNear(bool[] mask, int w, int cx, int cy, int r)
    {
        int h = mask.Length / w;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(h - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(w - 1, cx + r); x++)
                if (mask[y * w + x]) return true;
        return false;
    }
}
