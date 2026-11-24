using System.Windows;
using System.Windows.Media.Imaging;
using TrackPadLens.Objects;
using TrackPadLens.Storage;

namespace TrackPadLens.Clipboard;

public enum ClipboardPayloadKind
{
    Unsupported,
    Image,
    Text,
    FileDrop
}

public sealed record ClipboardPasteResult(
    ClipboardPayloadKind Kind,
    DocumentObject? CreatedObject,
    List<string>? DroppedFilePaths,
    string StatusMessage);

/// <summary>
/// Universal Windows Clipboard integration preserving PNG alpha transparency, DIB/DIBV5 bitmaps,
/// Unicode text, and file drops without altering the user's clipboard contents.
/// </summary>
public sealed class ClipboardService
{
    public ClipboardPasteResult TryReadClipboardForWorkspace(AssetStore assets, double targetCenterX, double targetCenterY, bool darkTheme)
    {
        try
        {
            // 1. Prefer raw PNG clipboard format first so alpha transparency is preserved 100%
            if (System.Windows.Clipboard.ContainsData("PNG"))
            {
                if (System.Windows.Clipboard.GetData("PNG") is MemoryStream pngStream)
                {
                    byte[] bytes = pngStream.ToArray();
                    if (bytes.Length > 8)
                    {
                        var imgObj = CreateImageObjectFromBytes(bytes, ".png", assets, targetCenterX, targetCenterY, null);
                        return new ClipboardPasteResult(ClipboardPayloadKind.Image, imgObj, null, "Pasted PNG image from clipboard (transparency preserved).");
                    }
                }
            }

            // 2. Standard WPF BitmapSource (covers Windows Snipping Tool, DIB, DIBV5, browser Copy Image)
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bmp = System.Windows.Clipboard.GetImage();
                if (bmp != null)
                {
                    byte[] pngBytes = AssetStore.EncodeBitmapToPng(bmp);
                    var imgObj = CreateImageObjectFromBytes(pngBytes, ".png", assets, targetCenterX, targetCenterY, null);
                    return new ClipboardPasteResult(ClipboardPayloadKind.Image, imgObj, null, "Pasted image from clipboard.");
                }
            }

            // 3. File drop list (copied files from Explorer: PDF, DOCX, PNG, JPG, etc.)
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                var paths = new List<string>();
                foreach (string? f in files)
                {
                    if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) paths.Add(f);
                }
                if (paths.Count > 0)
                {
                    return new ClipboardPasteResult(ClipboardPayloadKind.FileDrop, null, paths, $"Detected {paths.Count} file(s) from clipboard.");
                }
            }

            // 4. Unicode / plain text
            if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var textObj = CreateTextObject(text, targetCenterX, targetCenterY, darkTheme);
                    return new ClipboardPasteResult(ClipboardPayloadKind.Text, textObj, null, "Pasted text into a new TextObject.");
                }
            }

            return new ClipboardPasteResult(
                ClipboardPayloadKind.Unsupported,
                null,
                null,
                "Clipboard does not contain supported image, text, or document file data.");
        }
        catch (Exception ex)
        {
            return new ClipboardPasteResult(
                ClipboardPayloadKind.Unsupported,
                null,
                null,
                $"Clipboard is temporarily locked by another app or unsupported: {ex.Message}");
        }
    }

    public static ImageObject CreateImageObjectFromBytes(
        byte[] imageBytes,
        string extension,
        AssetStore assets,
        double centerX,
        double centerY,
        string? originalPath)
    {
        string assetId = assets.PutBytes(imageBytes, extension);
        var bmp = assets.GetDecodedBitmap(assetId);
        int pxW = bmp?.PixelWidth ?? 320;
        int pxH = bmp?.PixelHeight ?? 200;

        // Scale sensibly into workspace DIP while preserving aspect ratio
        double displayW = Math.Clamp(pxW, 120, 680);
        double displayH = displayW * (pxH / (double)Math.Max(1, pxW));

        return new ImageObject
        {
            AssetId = assetId,
            OriginalFilePath = originalPath,
            PixelWidth = pxW,
            PixelHeight = pxH,
            X = Math.Max(20, centerX - displayW * 0.5),
            Y = Math.Max(20, centerY - displayH * 0.5),
            Width = displayW,
            Height = Math.Max(60, displayH),
            PreserveAspectRatio = true
        };
    }

    public static TextObject CreateTextObject(string text, double centerX, double centerY, bool darkTheme)
    {
        int lineCount = Math.Max(1, text.Split('\n').Length);
        double w = Math.Clamp(text.Length * 8.5, 180, 560);
        double h = Math.Clamp(lineCount * 26 + 24, 48, 420);
        return new TextObject
        {
            Text = text.TrimEnd(),
            X = Math.Max(20, centerX - w * 0.5),
            Y = Math.Max(20, centerY - h * 0.5),
            Width = w,
            Height = h,
            FontSize = 15.0,
            ForegroundHex = darkTheme ? "#F8FAFC" : "#0F172A"
        };
    }
}
