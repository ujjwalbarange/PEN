using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace TrackPadLens.Storage;

/// <summary>
/// Content-addressed binary asset store (SHA-256 keyed) that keeps image bytes
/// outside JSON metadata files and avoids duplicate image decoding in memory.
/// </summary>
public sealed class AssetStore
{
    private readonly Dictionary<string, byte[]> _bytesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<BitmapSource>> _decodedCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> AssetIds => _bytesById.Keys;
    public int Count => _bytesById.Count;

    public string PutBytes(byte[] data, string extension = ".png")
    {
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..24];
        string ext = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        string id = $"{hash}{ext}";
        _bytesById[id] = data;
        return id;
    }

    public void PutWithId(string assetId, byte[] data)
    {
        _bytesById[assetId] = data;
    }

    public byte[]? GetBytes(string assetId)
    {
        return _bytesById.TryGetValue(assetId, out var bytes) ? bytes : null;
    }

    public bool Contains(string assetId) => _bytesById.ContainsKey(assetId);

    public BitmapSource? GetDecodedBitmap(string assetId, int maxDecodeWidth = 0)
    {
        string cacheKey = maxDecodeWidth > 0 ? $"{assetId}@{maxDecodeWidth}" : assetId;
        if (_decodedCache.TryGetValue(cacheKey, out var weak) && weak.TryGetTarget(out var cached))
        {
            return cached;
        }

        if (!_bytesById.TryGetValue(assetId, out var bytes) || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (maxDecodeWidth > 0)
            {
                bmp.DecodePixelWidth = maxDecodeWidth;
            }
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            _decodedCache[cacheKey] = new WeakReference<BitmapSource>(bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void PruneUnused(IEnumerable<string> referencedIds)
    {
        var keep = new HashSet<string>(referencedIds, StringComparer.OrdinalIgnoreCase);
        foreach (string id in _bytesById.Keys.ToList())
        {
            if (!keep.Contains(id))
            {
                _bytesById.Remove(id);
            }
        }
    }

    public void Clear()
    {
        _bytesById.Clear();
        _decodedCache.Clear();
    }

    public static byte[] EncodeBitmapToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
