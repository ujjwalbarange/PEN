using System.IO.Compression;
using System.Text.Json;
using TrackPadLens.Objects;

namespace TrackPadLens.Storage;

public sealed record ProjectManifest(
    int FormatVersion,
    string Application,
    DateTime SavedUtc,
    string WorkspaceId,
    string Title,
    int AssetCount);

/// <summary>
/// Versioned .tlproject archive serializer storing clean JSON workspace metadata
/// alongside binary assets/ entries in a standard ZIP package (never giant base64 strings in JSON).
/// Uses atomic temp-file swap + pre-overwrite backup for crash safety.
/// </summary>
public sealed class ProjectSerializer
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public void Save(string filePath, WorkspaceDocument document, AssetStore assets, bool createBackupIfExisting = true)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        document.ModifiedUtc = DateTime.UtcNow;
        document.FilePath = filePath;

        var referencedAssets = CollectReferencedAssetIds(document);
        string tempPath = filePath + ".tmp";

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new ProjectManifest(
                CurrentFormatVersion,
                "TrackPadLens",
                DateTime.UtcNow,
                document.Id,
                document.Title,
                referencedAssets.Count);

            WriteJsonEntry(zip, "manifest.json", manifest);
            WriteJsonEntry(zip, "workspace.json", document);

            foreach (string assetId in referencedAssets)
            {
                var data = assets.GetBytes(assetId);
                if (data == null || data.Length == 0) continue;

                var entry = zip.CreateEntry($"assets/{assetId}", CompressionLevel.NoCompression);
                using var es = entry.Open();
                es.Write(data, 0, data.Length);
            }
        }

        if (createBackupIfExisting && File.Exists(filePath))
        {
            try
            {
                File.Copy(filePath, filePath + ".bak", overwrite: true);
            }
            catch
            {
                // Best-effort backup
            }
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    public (WorkspaceDocument Document, List<string> Warnings) Load(string filePath, AssetStore assets)
    {
        var warnings = new List<string>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var wsEntry = zip.GetEntry("workspace.json")
            ?? throw new InvalidDataException("Invalid .tlproject archive: missing workspace.json.");

        WorkspaceDocument doc;
        using (var s = wsEntry.Open())
        {
            doc = JsonSerializer.Deserialize<WorkspaceDocument>(s, JsonOptions)
                  ?? throw new InvalidDataException("Corrupt workspace.json inside .tlproject archive.");
        }

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || entry.Length == 0)
            {
                continue;
            }
            string assetId = entry.FullName["assets/".Length..];
            if (string.IsNullOrWhiteSpace(assetId)) continue;
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            assets.PutWithId(assetId, ms.ToArray());
        }

        // Verify referenced assets and attempt recovery from original file path if missing
        foreach (var nb in doc.Notebooks)
        {
            foreach (var page in nb.Pages)
            {
                foreach (var obj in page.Objects)
                {
                    if (obj is ImageObject img && !string.IsNullOrWhiteSpace(img.AssetId) && !assets.Contains(img.AssetId))
                    {
                        if (!string.IsNullOrWhiteSpace(img.OriginalFilePath) && File.Exists(img.OriginalFilePath))
                        {
                            byte[] recovered = File.ReadAllBytes(img.OriginalFilePath);
                            assets.PutWithId(img.AssetId, recovered);
                            warnings.Add($"Recovered missing image asset from '{img.OriginalFilePath}'.");
                        }
                        else
                        {
                            warnings.Add($"Missing image asset '{img.AssetId}' (original source unavailable).");
                        }
                    }
                }
            }
        }

        doc.FilePath = filePath;
        return (doc, warnings);
    }

    public static HashSet<string> CollectReferencedAssetIds(WorkspaceDocument doc)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nb in doc.Notebooks)
        {
            foreach (var page in nb.Pages)
            {
                foreach (var obj in page.Objects)
                {
                    if (obj is ImageObject img && !string.IsNullOrWhiteSpace(img.AssetId))
                    {
                        ids.Add(img.AssetId);
                    }
                    else if (obj is PdfPageObject pdf && !string.IsNullOrWhiteSpace(pdf.CachedAssetId))
                    {
                        ids.Add(pdf.CachedAssetId);
                    }
                }
            }
        }
        return ids;
    }

    private static void WriteJsonEntry<T>(ZipArchive zip, string entryName, T payload)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        JsonSerializer.Serialize(es, payload, JsonOptions);
    }
}
