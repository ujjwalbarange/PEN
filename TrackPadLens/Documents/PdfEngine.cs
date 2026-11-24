using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TrackPadLens.Documents;

public sealed record PdfPageMetadata(int PageIndex, double WidthDip, double HeightDip, int RotationDegrees);

/// <summary>
/// Native Windows 10/11 PDF document engine built on Windows.Data.Pdf.PdfDocument.
/// Supports lazy page rendering, bounded LRU caching, thumbnails, and rotation without
/// shipping heavyweight native third-party binaries. Original PDF files are opened read-only.
/// </summary>
public sealed class PdfEngine : IDisposable
{
    private const int MaxCachedPages = 16;

    private PdfDocument? _pdfDoc;
    private string? _filePath;
    private readonly Dictionary<int, int> _pageRotations = new();
    private readonly LinkedList<(string Key, BitmapSource Image)> _lruCache = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _lruLookup = new();
    private bool _disposed;

    public bool IsLoaded => _pdfDoc != null;
    public string? FilePath => _filePath;
    public int PageCount => _pdfDoc != null ? (int)_pdfDoc.PageCount : 0;

    public static bool IsPlatformSupported =>
        ApiInformation.IsTypePresent("Windows.Data.Pdf.PdfDocument");

    public async Task<bool> OpenAsync(string pdfPath, string? password = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPlatformSupported)
        {
            throw new PlatformNotSupportedException("Windows.Data.Pdf.PdfDocument requires Windows 10 (19041+) or Windows 11.");
        }
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            throw new FileNotFoundException("PDF file was not found.", pdfPath);
        }

        Close();

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        _pdfDoc = string.IsNullOrEmpty(password)
            ? await PdfDocument.LoadFromFileAsync(file)
            : await PdfDocument.LoadFromFileAsync(file, password);
        _filePath = Path.GetFullPath(pdfPath);
        return _pdfDoc != null;
    }

    public PdfPageMetadata GetPageMetadata(int pageIndex)
    {
        if (_pdfDoc == null || pageIndex < 0 || pageIndex >= PageCount)
        {
            return new PdfPageMetadata(pageIndex, 816, 1056, 0);
        }

        using var page = _pdfDoc.GetPage((uint)pageIndex);
        int rot = GetPageRotation(pageIndex);
        double w = page.Size.Width;
        double h = page.Size.Height;
        if (rot == 90 || rot == 270) (w, h) = (h, w);
        return new PdfPageMetadata(pageIndex, w, h, rot);
    }

    public int GetPageRotation(int pageIndex) => _pageRotations.TryGetValue(pageIndex, out int r) ? r : 0;

    public void RotatePageClockwise(int pageIndex)
    {
        int next = (GetPageRotation(pageIndex) + 90) % 360;
        _pageRotations[pageIndex] = next;
        ClearCache();
    }

    public async Task<BitmapSource?> RenderPageAsync(int pageIndex, double zoomScale = 1.5, bool isThumbnail = false)
    {
        if (_pdfDoc == null || pageIndex < 0 || pageIndex >= PageCount) return null;

        int rot = GetPageRotation(pageIndex);
        double clampedZoom = isThumbnail ? 0.25 : Math.Clamp(zoomScale, 0.5, 3.0);
        string key = $"{pageIndex}:{clampedZoom:F2}:{rot}";

        if (_lruLookup.TryGetValue(key, out var existing))
        {
            _lruCache.Remove(existing);
            _lruCache.AddFirst(existing);
            return existing.Value.Image;
        }

        using var page = _pdfDoc.GetPage((uint)pageIndex);
        uint targetW = (uint)Math.Clamp(Math.Round(page.Size.Width * clampedZoom), 64, 3840);
        uint targetH = (uint)Math.Clamp(Math.Round(page.Size.Height * clampedZoom), 64, 3840);

        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = targetW,
            DestinationHeight = targetH,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };
        await page.RenderToStreamAsync(stream, options);

        stream.Seek(0);
        using var netStream = stream.AsStreamForRead();
        using var copy = new MemoryStream();
        await netStream.CopyToAsync(copy);
        copy.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.Rotation = rot switch
        {
            90 => Rotation.Rotate90,
            180 => Rotation.Rotate180,
            270 => Rotation.Rotate270,
            _ => Rotation.Rotate0
        };
        bitmap.StreamSource = copy;
        bitmap.EndInit();
        bitmap.Freeze();

        var node = new LinkedListNode<(string, BitmapSource)>((key, bitmap));
        _lruCache.AddFirst(node);
        _lruLookup[key] = node;

        while (_lruCache.Count > MaxCachedPages)
        {
            var last = _lruCache.Last!;
            _lruCache.RemoveLast();
            _lruLookup.Remove(last.Value.Key);
        }

        return bitmap;
    }

    public void ClearCache()
    {
        _lruCache.Clear();
        _lruLookup.Clear();
    }

    public void Close()
    {
        ClearCache();
        _pageRotations.Clear();
        _pdfDoc = null;
        _filePath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
