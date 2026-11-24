using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TrackPadLens.Intelligence;

public sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height);

public sealed record OcrRecognitionResult(
    bool Success,
    string FullText,
    List<string> Lines,
    List<OcrWordBox> Words,
    double EstimatedConfidence,
    string DiagnosticMessage);

/// <summary>
/// Offline Windows 10/11 OCR service powered by Windows.Media.Ocr.OcrEngine.
/// Runs asynchronously off the UI thread, checks OS language pack availability honestly,
/// and never fabricates text when recognition cannot run.
/// </summary>
public sealed class OcrService
{
    public static bool IsPlatformSupported =>
        ApiInformation.IsTypePresent("Windows.Media.Ocr.OcrEngine");

    public static bool IsAvailable(out string status)
    {
        if (!IsPlatformSupported)
        {
            status = "Windows.Media.Ocr is unavailable on this OS build (requires Windows 10 2004+ / Windows 11).";
            return false;
        }

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null)
            {
                status = $"Windows Offline OCR ready ({engine.RecognizerLanguage.DisplayName}).";
                return true;
            }

            var langs = OcrEngine.AvailableRecognizerLanguages;
            if (langs.Count > 0)
            {
                status = $"Windows Offline OCR ready ({langs[0].DisplayName}).";
                return true;
            }

            status = "No Windows OCR language pack installed. Install English (US) language pack in Windows Settings -> Time & Language.";
            return false;
        }
        catch (Exception ex)
        {
            status = $"Windows OCR initialization check failed: {ex.Message}";
            return false;
        }
    }

    public async Task<OcrRecognitionResult> RecognizePngBytesAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            return new OcrRecognitionResult(false, string.Empty, new(), new(), 0.0, "Empty image payload.");
        }

        if (!IsPlatformSupported)
        {
            return new OcrRecognitionResult(false, string.Empty, new(), new(), 0.0, "Windows OCR API is not present on this OS version.");
        }

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0
                    ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
                    : null);

            if (engine == null)
            {
                return new OcrRecognitionResult(
                    false,
                    string.Empty,
                    new(),
                    new(),
                    0.0,
                    "No Windows OCR language pack is installed. Add a language pack in Windows Settings to enable offline OCR.");
            }

            ct.ThrowIfCancellationRequested();

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            ct.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(softwareBitmap);

            var lines = new List<string>();
            var words = new List<OcrWordBox>();

            foreach (var line in result.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    lines.Add(line.Text.Trim());
                }
                foreach (var w in line.Words)
                {
                    words.Add(new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height));
                }
            }

            string full = string.Join(Environment.NewLine, lines);
            if (string.IsNullOrWhiteSpace(full))
            {
                return new OcrRecognitionResult(
                    false,
                    string.Empty,
                    lines,
                    words,
                    0.0,
                    "No readable text was detected in the selected region.");
            }

            // Heuristic confidence based on average recognized token length & alphanumeric ratio
            int totalChars = words.Sum(w => w.Text.Length);
            int validChars = words.Sum(w => w.Text.Count(c => char.IsLetterOrDigit(c) || "+-*/=().,^".Contains(c)));
            double ratio = totalChars > 0 ? validChars / (double)totalChars : 0.0;
            double confidence = Math.Clamp(0.45 + ratio * 0.50, 0.35, 0.96);

            return new OcrRecognitionResult(
                true,
                full,
                lines,
                words,
                confidence,
                $"Recognized {words.Count} word(s) using Windows Offline OCR ({engine.RecognizerLanguage.LanguageTag}).");
        }
        catch (OperationCanceledException)
        {
            return new OcrRecognitionResult(false, string.Empty, new(), new(), 0.0, "OCR operation was cancelled.");
        }
        catch (Exception ex)
        {
            return new OcrRecognitionResult(false, string.Empty, new(), new(), 0.0, $"OCR error: {ex.Message}");
        }
    }
}
