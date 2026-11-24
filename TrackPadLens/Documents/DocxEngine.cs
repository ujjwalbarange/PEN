using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TrackPadLens.Documents;

public enum DocxBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    ListItem,
    Table,
    Image
}

public sealed record DocxBlock(
    DocxBlockKind Kind,
    string Text,
    bool IsBold = false,
    bool IsItalic = false,
    List<List<string>>? TableRows = null,
    byte[]? ImageBytes = null);

public sealed record DocxLoadResult(
    string SourcePath,
    bool UsedHighFidelityPdfConversion,
    string? ConvertedPdfPath,
    string RendererNotice,
    List<DocxBlock> Blocks);

/// <summary>
/// Honest DOCX document engine:
/// 1. Prefers optional conversion to PDF via an already-installed LibreOffice (soffice.exe) for
///    high-fidelity Word layout pagination (never silently installs software or modifies the original DOCX).
/// 2. When LibreOffice is unavailable, parses Open XML (.docx ZIP + word/document.xml) natively
///    for paragraphs, headings, lists, tables, formatting, and embedded images with an explicit notice.
/// </summary>
public sealed class DocxEngine
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static string? FindInstalledLibreOffice()
    {
        string[] candidates =
        {
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\LibreOffice\program\soffice.exe")
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async Task<DocxLoadResult> LoadAsync(string docxPath, bool preferLibreOfficePdf = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
        {
            throw new FileNotFoundException("DOCX file was not found.", docxPath);
        }

        string fullPath = Path.GetFullPath(docxPath);
        var parsedBlocks = ParseOpenXmlNative(fullPath);

        if (preferLibreOfficePdf)
        {
            string? soffice = FindInstalledLibreOffice();
            if (!string.IsNullOrEmpty(soffice))
            {
                string? pdfPath = await TryConvertWithLibreOfficeAsync(soffice, fullPath, ct);
                if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
                {
                    return new DocxLoadResult(
                        fullPath,
                        UsedHighFidelityPdfConversion: true,
                        ConvertedPdfPath: pdfPath,
                        RendererNotice: "Rendered via installed LibreOffice high-fidelity PDF conversion (original DOCX preserved untouched).",
                        Blocks: parsedBlocks);
                }
            }
        }

        return new DocxLoadResult(
            fullPath,
            UsedHighFidelityPdfConversion: false,
            ConvertedPdfPath: null,
            RendererNotice: "Native Open XML fallback view (paragraphs, headings, lists, tables, and embedded images). Install LibreOffice for full Word page-layout fidelity.",
            Blocks: parsedBlocks);
    }

    public static List<DocxBlock> ParseOpenXmlNative(string docxPath)
    {
        var blocks = new List<DocxBlock>();
        using var fs = new FileStream(docxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var rels = ReadRelationships(zip);
        var docEntry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Invalid DOCX archive: missing word/document.xml.");

        XDocument xdoc;
        using (var s = docEntry.Open())
        {
            xdoc = XDocument.Load(s);
        }

        var body = xdoc.Root?.Element(W + "body");
        if (body == null) return blocks;

        foreach (var child in body.Elements())
        {
            if (child.Name == W + "p")
            {
                // Extract embedded images inside drawings if present
                foreach (var blip in child.Descendants(A + "blip"))
                {
                    string? embedId = blip.Attribute(R + "embed")?.Value;
                    if (!string.IsNullOrEmpty(embedId) && rels.TryGetValue(embedId, out string? target))
                    {
                        string entryPath = target.StartsWith('/') ? target.TrimStart('/') : $"word/{target.TrimStart('/')}";
                        var mediaEntry = zip.GetEntry(entryPath);
                        if (mediaEntry != null)
                        {
                            using var ms = new MemoryStream();
                            using var es = mediaEntry.Open();
                            es.CopyTo(ms);
                            blocks.Add(new DocxBlock(DocxBlockKind.Image, Path.GetFileName(entryPath), ImageBytes: ms.ToArray()));
                        }
                    }
                }

                string text = ExtractParagraphText(child);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var pPr = child.Element(W + "pPr");
                string? style = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
                bool isList = pPr?.Element(W + "numPr") != null;

                var kind = DocxBlockKind.Paragraph;
                if (isList) kind = DocxBlockKind.ListItem;
                else if (style != null)
                {
                    if (style.Contains("Heading1", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase))
                        kind = DocxBlockKind.Heading1;
                    else if (style.Contains("Heading2", StringComparison.OrdinalIgnoreCase))
                        kind = DocxBlockKind.Heading2;
                    else if (style.Contains("Heading3", StringComparison.OrdinalIgnoreCase))
                        kind = DocxBlockKind.Heading3;
                }

                bool hasBold = child.Descendants(W + "b").Any(b => b.Attribute(W + "val")?.Value != "0");
                bool hasItalic = child.Descendants(W + "i").Any(i => i.Attribute(W + "val")?.Value != "0");
                blocks.Add(new DocxBlock(kind, text, hasBold, hasItalic));
            }
            else if (child.Name == W + "tbl")
            {
                var rows = new List<List<string>>();
                foreach (var tr in child.Elements(W + "tr"))
                {
                    var row = new List<string>();
                    foreach (var tc in tr.Elements(W + "tc"))
                    {
                        string cellText = string.Join(" ", tc.Elements(W + "p").Select(ExtractParagraphText).Where(t => !string.IsNullOrWhiteSpace(t)));
                        row.Add(cellText);
                    }
                    if (row.Count > 0) rows.Add(row);
                }
                if (rows.Count > 0)
                {
                    blocks.Add(new DocxBlock(DocxBlockKind.Table, $"Table ({rows.Count} rows)", TableRows: rows));
                }
            }
        }

        return blocks;
    }

    private static string ExtractParagraphText(XElement paragraph)
    {
        var sb = new StringBuilder();
        foreach (var run in paragraph.Descendants(W + "r"))
        {
            foreach (var node in run.Elements())
            {
                if (node.Name == W + "t") sb.Append(node.Value);
                else if (node.Name == W + "tab") sb.Append("    ");
                else if (node.Name == W + "br") sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relsEntry = zip.GetEntry("word/_rels/document.xml.rels");
        if (relsEntry == null) return map;

        using var s = relsEntry.Open();
        var doc = XDocument.Load(s);
        foreach (var rel in doc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            string? id = rel.Attribute("Id")?.Value;
            string? target = rel.Attribute("Target")?.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
            {
                map[id] = target;
            }
        }
        return map;
    }

    private static async Task<string?> TryConvertWithLibreOfficeAsync(string sofficePath, string docxPath, CancellationToken ct)
    {
        try
        {
            string outDir = Path.Combine(Path.GetTempPath(), "TrackPadLens", "DocxCache", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo
            {
                FileName = sofficePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(docxPath);

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            await proc.WaitForExitAsync(timeoutCts.Token);

            string expectedPdf = Path.Combine(outDir, Path.GetFileNameWithoutExtension(docxPath) + ".pdf");
            return File.Exists(expectedPdf) ? expectedPdf : null;
        }
        catch
        {
            return null;
        }
    }
}
