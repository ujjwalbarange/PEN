using System.Globalization;
using System.Text;

namespace TrackPadLens.Documents;

public sealed record PdfExportPage(byte[] JpegBytes, int PixelWidth, int PixelHeight, double PageWidthPoints, double PageHeightPoints);

/// <summary>
/// Zero-dependency standards-compliant PDF 1.4 writer for exporting flattened annotated PDFs
/// and multi-page Whiteboards (embeds DCTDecode JPEG page streams with exact byte-offset xref table).
/// </summary>
public static class SimplePdfWriter
{
    public static void WriteFlattenedPdf(string outputPath, IReadOnlyList<PdfExportPage> pages)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("At least one page is required to export a PDF.");
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Object numbering:
        // 1: Catalog
        // 2: Pages
        // For page i (0-based):
        //   pageObj = 3 + i*3
        //   contentObj = 4 + i*3
        //   imageObj = 5 + i*3
        int totalObjects = 2 + pages.Count * 3;
        var offsets = new long[totalObjects + 1];

        using var ms = new MemoryStream();
        void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.4\n");
        byte[] binaryMarker = { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A };
        ms.Write(binaryMarker, 0, binaryMarker.Length);

        // 1: Catalog
        offsets[1] = ms.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        offsets[2] = ms.Position;
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            kids.Append($"{3 + i * 3} 0 R ");
        }
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [ {kids}] /Count {pages.Count} >>\nendobj\n");

        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            int pageId = 3 + i * 3;
            int contentId = 4 + i * 3;
            int imageId = 5 + i * 3;

            string wStr = p.PageWidthPoints.ToString("F2", CultureInfo.InvariantCulture);
            string hStr = p.PageHeightPoints.ToString("F2", CultureInfo.InvariantCulture);

            // Page dictionary
            offsets[pageId] = ms.Position;
            WriteAscii(
                $"{pageId} 0 obj\n" +
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wStr} {hStr}] " +
                $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\n" +
                $"endobj\n");

            // Content stream drawing the JPEG across the entire MediaBox
            string contentStream = $"q\n{wStr} 0 0 {hStr} 0 0 cm\n/Im0 Do\nQ\n";
            byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);
            offsets[contentId] = ms.Position;
            WriteAscii($"{contentId} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            ms.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii("endstream\nendobj\n");

            // JPEG Image XObject
            offsets[imageId] = ms.Position;
            WriteAscii(
                $"{imageId} 0 obj\n" +
                $"<< /Type /XObject /Subtype /Image /Width {p.PixelWidth} /Height {p.PixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.JpegBytes.Length} >>\n" +
                $"stream\n");
            ms.Write(p.JpegBytes, 0, p.JpegBytes.Length);
            WriteAscii("\nendstream\nendobj\n");
        }

        long xrefStart = ms.Position;
        WriteAscii($"xref\n0 {totalObjects + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int id = 1; id <= totalObjects; id++)
        {
            WriteAscii($"{offsets[id]:D10} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");

        File.WriteAllBytes(outputPath, ms.ToArray());
    }
}
