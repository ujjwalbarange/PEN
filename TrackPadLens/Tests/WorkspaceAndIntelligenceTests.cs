using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using TrackPadLens.Commands;
using TrackPadLens.Core;
using TrackPadLens.Documents;
using TrackPadLens.Intelligence;
using TrackPadLens.Objects;
using TrackPadLens.Selection;
using TrackPadLens.Storage;
using Xunit;

namespace TrackPadLens.Tests;

public sealed class WorkspaceAndIntelligenceTests
{
    [Fact]
    public void SpatialIndex_ReturnsOnlyObjectsIntersectingViewport()
    {
        var manager = new ObjectManager();
        var inside = new VectorShapeObject { X = 100, Y = 100, Width = 80, Height = 60 };
        var farAway = new VectorShapeObject { X = 4000, Y = 4000, Width = 80, Height = 60 };

        manager.Add(inside);
        manager.Add(farAway);

        var viewport = new ObjectRect(0, 0, 500, 500);
        var visible = manager.QueryViewport(in viewport);

        Assert.Single(visible);
        Assert.Equal(inside.Id, visible[0].Id);
    }

    [Fact]
    public void SelectionManager_LassoAndMarqueeSelectExpectedObjects()
    {
        var manager = new ObjectManager();
        var a = new TextObject { X = 50, Y = 50, Width = 40, Height = 30, Text = "Inside" };
        var b = new TextObject { X = 400, Y = 400, Width = 40, Height = 30, Text = "Outside" };
        manager.Add(a);
        manager.Add(b);

        var selection = new SelectionManager(manager);
        var polygon = new (double X, double Y)[]
        {
            (0, 0), (150, 0), (150, 150), (0, 150)
        };
        selection.SelectInLasso(polygon);

        Assert.Single(selection.SelectedIds);
        Assert.Contains(a.Id, selection.SelectedIds);
        Assert.DoesNotContain(b.Id, selection.SelectedIds);
    }

    [Fact]
    public void ObjectManager_GroupingAligningAndZOrderWorkPredictably()
    {
        var manager = new ObjectManager();
        var s1 = new VectorShapeObject { X = 120, Y = 40, Width = 60, Height = 40 };
        var s2 = new VectorShapeObject { X = 260, Y = 120, Width = 60, Height = 40 };
        manager.Add(s1);
        manager.Add(s2);

        string gid = manager.GroupObjects(new DocumentObject[] { s1, s2 });
        Assert.Equal(gid, s1.GroupId);
        Assert.Equal(gid, s2.GroupId);

        manager.AlignObjects(new DocumentObject[] { s1, s2 }, AlignmentKind.Left);
        Assert.Equal(120.0, s2.X, precision: 2);

        manager.BringForward(new[] { s1 });
        Assert.True(s1.ZIndex > s2.ZIndex);
    }

    [Fact]
    public void CommandManager_SupportsMultiStepUndoAndRedo()
    {
        var manager = new ObjectManager();
        var commands = new CommandManager();
        var txt = new TextObject { X = 20, Y = 20, Width = 140, Height = 40, Text = "Physics Notes" };

        commands.Execute(new AddObjectsCommand(manager, new[] { txt }, "Add Text"));
        Assert.Equal(1, manager.Count);

        var before = new List<ObjectGeometrySnapshot> { ObjectGeometrySnapshot.Capture(txt) };
        txt.Translate(100, 50);
        manager.NotifyObjectGeometryChanged(txt);
        var after = new List<ObjectGeometrySnapshot> { ObjectGeometrySnapshot.Capture(txt) };
        commands.Execute(new TransformObjectsCommand(manager, before, after, "Move Text"));

        Assert.Equal(120.0, txt.X, precision: 2);
        Assert.True(commands.Undo());
        Assert.Equal(20.0, txt.X, precision: 2);
        Assert.True(commands.Undo());
        Assert.Equal(0, manager.Count);
        Assert.True(commands.Redo());
        Assert.Equal(1, manager.Count);
        Assert.True(commands.Redo());
        Assert.Equal(120.0, manager.GetById(txt.Id)!.X, precision: 2);
    }

    [Fact]
    public void ShortcutManager_DetectsScopeConflictsAndRoundTripsProfile()
    {
        var sm = new ShortcutManager();
        var (accepted, err) = sm.TryUpdateGesture("Mode.Whiteboard", "Alt+D");
        Assert.False(accepted);
        Assert.False(string.IsNullOrWhiteSpace(err));

        // Same key 'E' is allowed in WorkspaceWindow and DoodleModeOnly because scopes do not overlap
        Assert.Equal("E", sm.GetGesture("Tool.Eraser"));
        Assert.Equal("E", sm.GetGesture("Doodle.Eraser"));

        string tempFile = Path.Combine(Path.GetTempPath(), $"tpl_shortcuts_{Guid.NewGuid():N}.json");
        try
        {
            sm.ExportProfileToFile(tempFile);
            var sm2 = new ShortcutManager();
            var warnings = sm2.ImportProfileFromFile(tempFile);
            Assert.Empty(warnings);
            Assert.Equal("Alt+W", sm2.GetGesture("Mode.Whiteboard"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProjectSerializer_RoundTripsWorkspaceAndBinaryAssetsInZipArchive()
    {
        var serializer = new ProjectSerializer();
        var storeA = new AssetStore();
        byte[] samplePng = CreateTestDiagramPng();
        string assetId = storeA.PutBytes(samplePng, ".png");

        var doc = new WorkspaceDocument { Title = "Calculus Lecture 04" };
        var page = doc.GetActivePage();
        page.Title = "Derivatives";
        page.PaperStyle = CanvasPaperStyle.Grid;
        page.Objects.Add(new TextObject { Text = "Chain Rule", X = 40, Y = 30, Width = 200, Height = 40 });
        page.Objects.Add(new EquationObject { RawExpression = "x^2 - 5x + 6 = 0", SolvedResult = "x = 2, x = 3", X = 40, Y = 100 });
        page.Objects.Add(new ImageObject { AssetId = assetId, PixelWidth = 120, PixelHeight = 80, X = 40, Y = 200, Width = 120, Height = 80 });

        string path = Path.Combine(Path.GetTempPath(), $"tpl_proj_{Guid.NewGuid():N}.tlproject");
        try
        {
            serializer.Save(path, doc, storeA);
            Assert.True(File.Exists(path));

            // Confirm ZIP contains manifest.json, workspace.json, and binary assets/* entry
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.NotNull(zip.GetEntry("manifest.json"));
                Assert.NotNull(zip.GetEntry("workspace.json"));
                Assert.NotNull(zip.GetEntry($"assets/{assetId}"));
            }

            var storeB = new AssetStore();
            var (loaded, warnings) = serializer.Load(path, storeB);
            Assert.Empty(warnings);
            Assert.Equal("Calculus Lecture 04", loaded.Title);
            Assert.Equal(3, loaded.GetActivePage().Objects.Count);
            Assert.Equal(samplePng.Length, storeB.GetBytes(assetId)!.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void SimplePdfWriter_GeneratesValidPdfHeaderAndXrefTable()
    {
        byte[] jpeg = CreateTestJpeg();
        string pdfPath = Path.Combine(Path.GetTempPath(), $"tpl_export_{Guid.NewGuid():N}.pdf");
        try
        {
            SimplePdfWriter.WriteFlattenedPdf(pdfPath, new[] { new PdfExportPage(jpeg, 120, 80, 612, 792) });
            byte[] bytes = File.ReadAllBytes(pdfPath);
            string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
            string tail = Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 64), Math.Min(64, bytes.Length));

            Assert.StartsWith("%PDF-1.4", head);
            Assert.Contains("%%EOF", tail);
            Assert.Contains("startxref", tail);
        }
        finally
        {
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
        }
    }

    [Fact]
    public void DocxEngine_ParsesSyntheticOpenXmlDocumentAccurately()
    {
        string docxPath = Path.Combine(Path.GetTempPath(), $"tpl_doc_{Guid.NewGuid():N}.docx");
        try
        {
            using (var fs = new FileStream(docxPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("word/document.xml");
                using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
                sw.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p>
                      <w:pPr><w:pStyle w:val="Heading1"/></w:pPr>
                      <w:r><w:t>Electromagnetism Notes</w:t></w:r>
                    </w:p>
                    <w:p>
                      <w:r><w:t>Maxwell equations describe electric and magnetic fields.</w:t></w:r>
                    </w:p>
                    <w:tbl>
                      <w:tr>
                        <w:tc><w:p><w:r><w:t>Law</w:t></w:r></w:p></w:tc>
                        <w:tc><w:p><w:r><w:t>Symbol</w:t></w:r></w:p></w:tc>
                      </w:tr>
                    </w:tbl>
                  </w:body>
                </w:document>
                """);
            }

            var blocks = DocxEngine.ParseOpenXmlNative(docxPath);
            Assert.Equal(3, blocks.Count);
            Assert.Equal(DocxBlockKind.Heading1, blocks[0].Kind);
            Assert.Equal("Electromagnetism Notes", blocks[0].Text);
            Assert.Equal(DocxBlockKind.Paragraph, blocks[1].Kind);
            Assert.Equal(DocxBlockKind.Table, blocks[2].Kind);
            Assert.Single(blocks[2].TableRows!);
            Assert.Equal(new[] { "Law", "Symbol" }, blocks[2].TableRows![0]);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void MathSolverEngine_SolvesLinearQuadraticFactorizationCalculusAndRejectsInvalidInput()
    {
        var solver = new MathSolverEngine();

        var lin = solver.Process("3x + 4 = 19", MathOperation.Solve);
        Assert.True(lin.Success);
        Assert.Contains("5", lin.ResultText);

        var quad = solver.Process("x^2 - 5x + 6 = 0", MathOperation.Solve);
        Assert.True(quad.Success);
        Assert.Contains("2", quad.ResultText);
        Assert.Contains("3", quad.ResultText);

        var complex = solver.Process("x^2 + 4 = 0", MathOperation.Solve);
        Assert.True(complex.Success);
        Assert.Contains("i", complex.ResultText);

        var fact = solver.Process("x^2 - 5x + 6", MathOperation.Factorize);
        Assert.True(fact.Success);
        Assert.Contains("(x", fact.ResultText);

        var deriv = solver.Process("3x^2 + 2x + 5", MathOperation.Differentiate);
        Assert.True(deriv.Success);
        Assert.Contains("6x", deriv.ResultText);

        var integ = solver.Process("2x + 3", MathOperation.Integrate);
        Assert.True(integ.Success);
        Assert.Contains("+ C", integ.ResultText);

        var bad = solver.Process("2x + = ** )", MathOperation.Solve);
        Assert.False(bad.Success);
        Assert.False(string.IsNullOrWhiteSpace(bad.ErrorMessage));
    }

    [Fact]
    public void SmartInkEngine_CleanHandwritingAlignsBaselineAndPreservesStrokeCount()
    {
        var s1 = new InkStrokeObject
        {
            Points = new() { new InkPointData(40, 100, 0), new InkPointData(70, 118, 0.1) }
        };
        s1.RecalculateBounds();
        var s2 = new InkStrokeObject
        {
            Points = new() { new InkPointData(95, 110, 0.2), new InkPointData(125, 130, 0.3) }
        };
        s2.RecalculateBounds();

        var cleaned = SmartInkEngine.CleanHandwritingStrokes(new[] { s1, s2 });
        Assert.Equal(2, cleaned.Count);
        double originalBottomDiff = Math.Abs(s1.Bounds.Bottom - s2.Bounds.Bottom);
        double cleanedBottomDiff = Math.Abs(cleaned[0].Bounds.Bottom - cleaned[1].Bounds.Bottom);
        Assert.True(cleanedBottomDiff < originalBottomDiff);
    }

    [Fact]
    public void DiagramRecognitionEngine_CleanImageProducesValidPngWithoutMutatingInput()
    {
        byte[] raw = CreateTestDiagramPng();
        byte[] snapshot = (byte[])raw.Clone();

        byte[] cleaned = DiagramRecognitionEngine.BuildCleanedImagePng(raw);
        Assert.Equal(snapshot, raw); // original input byte buffer untouched
        Assert.True(cleaned.Length > 32);
        // PNG magic bytes: 89 50 4E 47
        Assert.Equal(0x89, cleaned[0]);
        Assert.Equal(0x50, cleaned[1]);
        Assert.Equal(0x4E, cleaned[2]);
        Assert.Equal(0x47, cleaned[3]);
    }

    private static byte[] CreateTestDiagramPng()
    {
        using var bmp = new Bitmap(120, 80, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(245, 245, 242));
            using var pen = new Pen(Color.Black, 3f);
            g.DrawRectangle(pen, 15, 15, 40, 40);
            g.DrawLine(pen, 55, 35, 95, 35);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] CreateTestJpeg()
    {
        using var bmp = new Bitmap(120, 80, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }
}
