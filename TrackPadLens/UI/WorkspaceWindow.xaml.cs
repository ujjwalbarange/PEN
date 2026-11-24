using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrackPadLens.Commands;
using TrackPadLens.Core;
using TrackPadLens.Documents;
using TrackPadLens.Intelligence;
using TrackPadLens.Models;
using TrackPadLens.Objects;
using TrackPadLens.Selection;
using TrackPadLens.Storage;

namespace TrackPadLens.UI;

/// <summary>
/// Unified TrackPadLens Whiteboard & Smart Study Workspace window.
/// Coordinates notebooks/pages, retained high-DPI canvas drawing, PDF/DOCX reading &
/// non-destructive annotation, Send to Whiteboard, Universal Clipboard paste,
/// Region Screenshot insertion, offline OCR / Handwriting / Symbolic Math / Diagram
/// reconstruction, .tlproject persistence, and Flattened PDF / PNG export.
/// </summary>
public partial class WorkspaceWindow : Window
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Action _saveSettings;
    private readonly Action _openSettings;
    private readonly Action _openDiagnostics;
    private readonly Action _switchToLensMode;
    private readonly Action _requestRegionCapture;
    private readonly ModeManager _modeManager;
    private readonly AutosaveService _autosave;

    private readonly ProjectSerializer _serializer = new();
    private readonly AssetStore _assets = new();
    private readonly ObjectManager _objects = new();
    private readonly SelectionManager _selection;
    private readonly TrackPadLens.Commands.CommandManager _commands = new();
    private readonly PdfEngine _pdfEngine = new();
    private readonly DocxEngine _docxEngine = new();
    private readonly TrackPadLens.Clipboard.ClipboardService _clipboard = new();
    private readonly OcrService _ocr = new();
    private readonly MathSolverEngine _mathSolver = new();
    private readonly SmartInkEngine _smartInk;
    private readonly DiagramRecognitionEngine _diagramEngine;

    private WorkspaceDocument _document = new();
    private StudyLayoutMode _studyLayout = StudyLayoutMode.FreeWorkspace;
    private DocxLoadResult? _activeDocx;
    private int _currentDocPageIndex;
    private bool _darkTheme = true;
    private bool _allowClose;
    private bool _suppressUiEvents;

    public WorkspaceDocument Document => _document;
    public AssetStore Assets => _assets;

    public WorkspaceWindow(
        Func<AppSettings> getSettings,
        Action saveSettings,
        Action openSettings,
        Action openDiagnostics,
        Action switchToLensMode,
        Action requestRegionCapture,
        ModeManager modeManager,
        AutosaveService autosave)
    {
        InitializeComponent();

        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _openSettings = openSettings;
        _openDiagnostics = openDiagnostics;
        _switchToLensMode = switchToLensMode;
        _requestRegionCapture = requestRegionCapture;
        _modeManager = modeManager;
        _autosave = autosave;

        _selection = new SelectionManager(_objects);
        _smartInk = new SmartInkEngine(new WindowsOcrHandwritingProvider(_ocr));
        _diagramEngine = new DiagramRecognitionEngine(_ocr);

        CanvasHost.BindWorkspace(_objects, _selection, _commands, _assets);
        CanvasHost.ZoomChanged += z => ZoomLabel.Text = $"{(int)Math.Round(z * 100)}%";
        CanvasHost.SelectionContextUpdated += UpdateMagicMenuVisibility;
        CanvasHost.ContentModified += OnCanvasContentModified;
        CanvasHost.ObjectDoubleClicked += OnObjectDoubleClicked;
        CanvasHost.TextPlacementRequested += OnTextPlacementRequested;
        CanvasHost.EquationPlacementRequested += OnEquationPlacementRequested;
        CanvasHost.StickyNotePlacementRequested += OnStickyNotePlacementRequested;

        _autosave.Autosaved += utc => Dispatcher.BeginInvoke(() =>
            AutosaveStatusText.Text = $"Autosaved {utc.ToLocalTime():HH:mm:ss}");

        ApplyThemeFromSettings();
        BindDocument(_document);
        SelectTool(WorkspaceTool.Pen);
        ShowCanvasView();
    }

    public void AllowFullClose() => _allowClose = true;

    public void ApplyThemeFromSettings()
    {
        var mode = _getSettings().ThemeMode;
        bool dark = mode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => true
        };
        ApplyTheme(dark);
    }

    private void ApplyTheme(bool dark)
    {
        _darkTheme = dark;
        var bg = new SolidColorBrush(dark ? Color.FromRgb(0x0B, 0x11, 0x20) : Color.FromRgb(0xF8, 0xFA, 0xFC));
        var panel = new SolidColorBrush(dark ? Color.FromRgb(0x0F, 0x17, 0x2A) : Color.FromRgb(0xF1, 0xF5, 0xF9));
        var border = new SolidColorBrush(dark ? Color.FromRgb(0x1E, 0x29, 0x3B) : Color.FromRgb(0xCB, 0xD5, 0xE1));
        var fg = new SolidColorBrush(dark ? Color.FromRgb(0xE2, 0xE8, 0xF0) : Color.FromRgb(0x0F, 0x17, 0x2A));

        Background = bg;
        Foreground = fg;
        SidebarPanel.Background = panel;
        SidebarPanel.BorderBrush = border;
        TopToolbarPanel.Background = panel;
        TopToolbarPanel.BorderBrush = border;
        StatusBarPanel.Background = panel;
        StatusBarPanel.BorderBrush = border;
        DocumentPaneBorder.Background = panel;
        DocumentPaneBorder.BorderBrush = border;

        CanvasHost.SetTheme(dark);
    }

    public void BindDocument(WorkspaceDocument doc)
    {
        _document = doc;
        var activePage = _document.GetActivePage();
        _document.ActiveNotebookId = _document.GetActiveNotebook().Id;
        _document.ActivePageId = activePage.Id;

        _objects.BindPage(activePage);
        _selection.ClearSelection();
        _commands.Clear();
        CanvasHost.SetDocumentBackground(null);

        RefreshSidebarLists();
        SyncPageControls();
        UpdateStatusBar($"Loaded '{_document.Title}' — {activePage.Title}");
    }

    public bool TryLoadProjectFile(string filePath)
    {
        try
        {
            var (doc, warnings) = _serializer.Load(filePath, _assets);
            BindDocument(doc);
            _getSettings().AddRecentProject(filePath);
            _saveSettings();
            RefreshRecentLists();
            ShowCanvasView();

            if (warnings.Count > 0)
            {
                UpdateStatusBar($"Loaded with {warnings.Count} notice(s): {warnings[0]}");
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open project:\n{ex.Message}", "Open Project Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public void InsertCapturedRegionPng(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0) return;
        ShowCanvasView();
        var center = CanvasHost.GetViewportCenterWorld();
        var img = TrackPadLens.Clipboard.ClipboardService.CreateImageObjectFromBytes(pngBytes, ".png", _assets, center.X, center.Y, null);
        _commands.Execute(new AddObjectsCommand(_objects, new[] { img }, "Insert Region Screenshot"));
        _selection.SelectSingle(img);
        UpdateStatusBar($"Inserted region capture ({img.PixelWidth}×{img.PixelHeight} px).");
    }

    public async Task OpenExternalFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".tlproject")
        {
            TryLoadProjectFile(path);
            return;
        }

        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
        {
            ShowCanvasView();
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var center = CanvasHost.GetViewportCenterWorld();
            var img = TrackPadLens.Clipboard.ClipboardService.CreateImageObjectFromBytes(bytes, ext, _assets, center.X, center.Y, path);
            _commands.Execute(new AddObjectsCommand(_objects, new[] { img }, "Insert Image"));
            _selection.SelectSingle(img);
            _getSettings().AddRecentDocument(path);
            _saveSettings();
            RefreshRecentLists();
            UpdateStatusBar($"Inserted image '{Path.GetFileName(path)}'.");
            return;
        }

        if (ext == ".txt")
        {
            ShowCanvasView();
            string text = await File.ReadAllTextAsync(path);
            var center = CanvasHost.GetViewportCenterWorld();
            var txtObj = TrackPadLens.Clipboard.ClipboardService.CreateTextObject(text, center.X, center.Y, _darkTheme);
            txtObj.SourceDocumentPath = path;
            _commands.Execute(new AddObjectsCommand(_objects, new[] { txtObj }, "Import Text"));
            _selection.SelectSingle(txtObj);
            _getSettings().AddRecentDocument(path);
            _saveSettings();
            RefreshRecentLists();
            UpdateStatusBar($"Imported text file '{Path.GetFileName(path)}'.");
            return;
        }

        if (ext == ".pdf")
        {
            await OpenPdfDocumentAsync(path);
            return;
        }

        if (ext == ".docx")
        {
            await OpenDocxDocumentAsync(path);
            return;
        }

        UpdateStatusBar($"Unsupported file format: {ext}");
    }

    private async Task OpenPdfDocumentAsync(string path)
    {
        try
        {
            UpdateStatusBar($"Opening PDF '{Path.GetFileName(path)}'...");
            _activeDocx = null;
            await _pdfEngine.OpenAsync(path);
            _currentDocPageIndex = 0;

            DocTitleText.Text = Path.GetFileName(path);
            DocNoticeText.Text = $"Native Windows PDF Engine · {_pdfEngine.PageCount} page(s)";
            PdfScrollViewer.Visibility = Visibility.Visible;
            PdfThumbStripScroller.Visibility = Visibility.Visible;
            DocxFallbackViewer.Visibility = Visibility.Collapsed;

            ShowCanvasView();
            ApplyStudyLayout(StudyLayoutMode.SplitWorkspace);
            _modeManager.SwitchTo(WorkspaceEnvironment.SmartStudy);

            await RenderCurrentPdfPageAsync();
            _ = BuildPdfThumbnailsAsync();

            _getSettings().AddRecentDocument(path);
            _saveSettings();
            RefreshRecentLists();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open PDF:\n{ex.Message}", "PDF Engine Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatusBar($"PDF open failed: {ex.Message}");
        }
    }

    private async Task OpenDocxDocumentAsync(string path)
    {
        try
        {
            UpdateStatusBar($"Opening Word document '{Path.GetFileName(path)}'...");
            var res = await _docxEngine.LoadAsync(path, _getSettings().PreferLibreOfficeForDocx);
            _activeDocx = res;

            if (res.UsedHighFidelityPdfConversion && !string.IsNullOrEmpty(res.ConvertedPdfPath))
            {
                await _pdfEngine.OpenAsync(res.ConvertedPdfPath);
                _currentDocPageIndex = 0;
                DocTitleText.Text = Path.GetFileName(path);
                DocNoticeText.Text = res.RendererNotice;
                PdfScrollViewer.Visibility = Visibility.Visible;
                PdfThumbStripScroller.Visibility = Visibility.Visible;
                DocxFallbackViewer.Visibility = Visibility.Collapsed;

                ShowCanvasView();
                ApplyStudyLayout(StudyLayoutMode.SplitWorkspace);
                await RenderCurrentPdfPageAsync();
                _ = BuildPdfThumbnailsAsync();
            }
            else
            {
                _pdfEngine.Close();
                DocTitleText.Text = Path.GetFileName(path);
                DocNoticeText.Text = res.RendererNotice;
                DocPageCounterText.Text = $"{res.Blocks.Count} blocks";
                PdfScrollViewer.Visibility = Visibility.Collapsed;
                PdfThumbStripScroller.Visibility = Visibility.Collapsed;
                DocxFallbackViewer.Visibility = Visibility.Visible;

                PopulateDocxFallbackView(res.Blocks);
                ShowCanvasView();
                ApplyStudyLayout(StudyLayoutMode.SplitWorkspace);
            }

            _modeManager.SwitchTo(WorkspaceEnvironment.SmartStudy);
            _getSettings().AddRecentDocument(path);
            _saveSettings();
            RefreshRecentLists();
            UpdateStatusBar($"Opened '{Path.GetFileName(path)}'.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open DOCX:\n{ex.Message}", "DOCX Engine Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatusBar($"DOCX open failed: {ex.Message}");
        }
    }

    private void PopulateDocxFallbackView(List<DocxBlock> blocks)
    {
        DocxBlocksStack.Children.Clear();
        var textBrush = new SolidColorBrush(_darkTheme ? Color.FromRgb(0xE2, 0xE8, 0xF0) : Color.FromRgb(0x0F, 0x17, 0x2A));
        var headingBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));

        foreach (var block in blocks)
        {
            if (block.Kind == DocxBlockKind.Image && block.ImageBytes != null)
            {
                try
                {
                    using var ms = new MemoryStream(block.ImageBytes, writable: false);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    DocxBlocksStack.Children.Add(new Image { Source = bmp, MaxHeight = 280, Margin = new Thickness(0, 6, 0, 10), Stretch = Stretch.Uniform });
                }
                catch { }
                continue;
            }

            if (block.Kind == DocxBlockKind.Table && block.TableRows != null)
            {
                var tableBorder = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 6, 0, 10)
                };
                var tblStack = new StackPanel();
                foreach (var row in block.TableRows)
                {
                    tblStack.Children.Add(new TextBlock
                    {
                        Text = string.Join("  |  ", row),
                        Foreground = textBrush,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                }
                tableBorder.Child = tblStack;
                DocxBlocksStack.Children.Add(tableBorder);
                continue;
            }

            string prefix = block.Kind == DocxBlockKind.ListItem ? "•  " : string.Empty;
            var tb = new TextBlock
            {
                Text = prefix + block.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = block.Kind is DocxBlockKind.Heading1 or DocxBlockKind.Heading2 ? headingBrush : textBrush,
                FontSize = block.Kind switch
                {
                    DocxBlockKind.Heading1 => 20,
                    DocxBlockKind.Heading2 => 16,
                    DocxBlockKind.Heading3 => 14,
                    _ => 13
                },
                FontWeight = block.IsBold || block.Kind is DocxBlockKind.Heading1 or DocxBlockKind.Heading2 or DocxBlockKind.Heading3
                    ? FontWeights.SemiBold
                    : FontWeights.Normal,
                FontStyle = block.IsItalic ? FontStyles.Italic : FontStyles.Normal,
                Margin = new Thickness(block.Kind == DocxBlockKind.ListItem ? 14 : 0, 4, 0, 6)
            };
            DocxBlocksStack.Children.Add(tb);
        }
    }

    private async Task RenderCurrentPdfPageAsync()
    {
        if (!_pdfEngine.IsLoaded || _pdfEngine.PageCount == 0) return;
        _currentDocPageIndex = Math.Clamp(_currentDocPageIndex, 0, _pdfEngine.PageCount - 1);
        DocPageCounterText.Text = $"{_currentDocPageIndex + 1} / {_pdfEngine.PageCount}";
        var bmp = await _pdfEngine.RenderPageAsync(_currentDocPageIndex, zoomScale: 1.5);
        PdfPageImage.Source = bmp;
    }

    private async Task BuildPdfThumbnailsAsync()
    {
        PdfThumbStrip.Children.Clear();
        int count = Math.Min(_pdfEngine.PageCount, 30);
        for (int i = 0; i < count; i++)
        {
            int pageIdx = i;
            var thumb = await _pdfEngine.RenderPageAsync(pageIdx, isThumbnail: true);
            if (thumb == null) continue;

            var btn = new Button
            {
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(2),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                ToolTip = $"Page {pageIdx + 1}",
                Content = new StackPanel
                {
                    Children =
                    {
                        new Image { Source = thumb, Height = 44, Stretch = Stretch.Uniform },
                        new TextBlock { Text = $"{pageIdx + 1}", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.LightGray }
                    }
                }
            };
            btn.Click += async (_, _) =>
            {
                _currentDocPageIndex = pageIdx;
                await RenderCurrentPdfPageAsync();
            };
            PdfThumbStrip.Children.Add(btn);
        }
    }

    private void ApplyStudyLayout(StudyLayoutMode mode)
    {
        _studyLayout = mode;
        _suppressUiEvents = true;
        StudyLayoutCombo.SelectedIndex = mode switch
        {
            StudyLayoutMode.DocumentFocus => 0,
            StudyLayoutMode.SplitWorkspace => 1,
            _ => 2
        };
        _suppressUiEvents = false;

        bool hasDoc = _pdfEngine.IsLoaded || _activeDocx != null;
        if (!hasDoc && mode != StudyLayoutMode.FreeWorkspace)
        {
            mode = StudyLayoutMode.FreeWorkspace;
        }

        switch (mode)
        {
            case StudyLayoutMode.DocumentFocus:
                DocumentPaneBorder.Visibility = Visibility.Visible;
                StudySplitter.Visibility = Visibility.Collapsed;
                DocumentPaneColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitterColumn.Width = new GridLength(0);
                CanvasPaneColumn.Width = new GridLength(0);
                break;

            case StudyLayoutMode.SplitWorkspace:
                DocumentPaneBorder.Visibility = Visibility.Visible;
                StudySplitter.Visibility = Visibility.Visible;
                DocumentPaneColumn.Width = new GridLength(0.46, GridUnitType.Star);
                SplitterColumn.Width = new GridLength(5);
                CanvasPaneColumn.Width = new GridLength(0.54, GridUnitType.Star);
                break;

            default:
                DocumentPaneBorder.Visibility = Visibility.Collapsed;
                StudySplitter.Visibility = Visibility.Collapsed;
                DocumentPaneColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);
                CanvasPaneColumn.Width = new GridLength(1, GridUnitType.Star);
                break;
        }
    }

    private void ShowHomeView()
    {
        RefreshRecentLists();
        HomeDashboardView.Visibility = Visibility.Visible;
        WorkspaceSplitView.Visibility = Visibility.Collapsed;
        TopToolbarPanel.Visibility = Visibility.Collapsed;
        MagicMenuBar.Visibility = Visibility.Collapsed;
        UpdateStatusBar("Home Dashboard");
    }

    private void ShowCanvasView()
    {
        HomeDashboardView.Visibility = Visibility.Collapsed;
        WorkspaceSplitView.Visibility = Visibility.Visible;
        TopToolbarPanel.Visibility = Visibility.Visible;
        UpdateMagicMenuVisibility();
    }

    private void RefreshSidebarLists()
    {
        _suppressUiEvents = true;
        try
        {
            string query = SearchBox.Text.Trim();
            NotebooksList.Items.Clear();
            var activeNb = _document.GetActiveNotebook();
            int selectedNbIdx = 0;

            for (int i = 0; i < _document.Notebooks.Count; i++)
            {
                var nb = _document.Notebooks[i];
                NotebooksList.Items.Add(new ListBoxItem { Content = $"▤  {nb.Name}", Tag = nb.Id, Foreground = Foreground });
                if (nb.Id == activeNb.Id) selectedNbIdx = i;
            }
            if (NotebooksList.Items.Count > 0) NotebooksList.SelectedIndex = selectedNbIdx;

            PagesList.Items.Clear();
            var activePage = _document.GetActivePage();
            int selectedPageIdx = -1;
            int visibleIdx = 0;

            foreach (var p in activeNb.Pages)
            {
                if (!string.IsNullOrEmpty(query) && !PageMatchesSearch(p, query)) continue;
                string star = p.IsBookmarked ? "★ " : string.Empty;
                PagesList.Items.Add(new ListBoxItem
                {
                    Content = $"{star}{p.Title} ({p.Objects.Count})",
                    Tag = p.Id,
                    Foreground = Foreground
                });
                if (p.Id == activePage.Id) selectedPageIdx = visibleIdx;
                visibleIdx++;
            }
            if (selectedPageIdx >= 0) PagesList.SelectedIndex = selectedPageIdx;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private static bool PageMatchesSearch(WorkspacePage page, string query)
    {
        if (page.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var obj in page.Objects)
        {
            if (obj is TextObject t && t.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            if (obj is EquationObject e && (e.RawExpression.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                            (e.SolvedResult?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))) return true;
            if (obj is StickyNoteObject s && s.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void RefreshRecentLists()
    {
        var settings = _getSettings();
        RecentProjectsList.Items.Clear();
        foreach (string p in settings.RecentProjects)
        {
            RecentProjectsList.Items.Add(new ListBoxItem { Content = Path.GetFileName(p), ToolTip = p, Tag = p, Foreground = Foreground });
        }
        if (RecentProjectsList.Items.Count == 0)
        {
            RecentProjectsList.Items.Add(new ListBoxItem { Content = "No saved projects yet — press Ctrl+S to save.", IsEnabled = false, Foreground = Brushes.Gray });
        }

        RecentDocsList.Items.Clear();
        foreach (string d in settings.RecentDocuments)
        {
            RecentDocsList.Items.Add(new ListBoxItem { Content = Path.GetFileName(d), ToolTip = d, Tag = d, Foreground = Foreground });
        }
        if (RecentDocsList.Items.Count == 0)
        {
            RecentDocsList.Items.Add(new ListBoxItem { Content = "No documents opened yet.", IsEnabled = false, Foreground = Brushes.Gray });
        }
    }

    private void SyncPageControls()
    {
        _suppressUiEvents = true;
        var page = _objects.ActivePage;
        PaperStyleCombo.SelectedIndex = (int)page.PaperStyle;
        CanvasModeCombo.SelectedIndex = page.LayoutMode == CanvasLayoutMode.PagedNotebook ? 1 : 0;
        ObjectCountText.Text = $"{page.Objects.Count} object(s)";
        _suppressUiEvents = false;
    }

    private void SelectTool(WorkspaceTool tool)
    {
        CanvasHost.ActiveTool = tool;
        var activeBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF));
        var activeBg = new SolidColorBrush(Color.FromArgb(40, 0, 210, 255));

        void StyleBtn(Button btn, WorkspaceTool target)
        {
            btn.BorderBrush = tool == target ? activeBrush : Brushes.Transparent;
            btn.Background = tool == target ? activeBg : Brushes.Transparent;
        }

        StyleBtn(ToolSelectBtn, WorkspaceTool.Select);
        StyleBtn(ToolLassoBtn, WorkspaceTool.Lasso);
        StyleBtn(ToolPenBtn, WorkspaceTool.Pen);
        StyleBtn(ToolPencilBtn, WorkspaceTool.Pencil);
        StyleBtn(ToolMarkerBtn, WorkspaceTool.Marker);
        StyleBtn(ToolHighlighterBtn, WorkspaceTool.Highlighter);
        StyleBtn(ToolEraserBtn, WorkspaceTool.Eraser);
        StyleBtn(ToolShapeBtn, WorkspaceTool.Shape);
        StyleBtn(ToolConnectorBtn, WorkspaceTool.Connector);
        StyleBtn(ToolTextBtn, WorkspaceTool.Text);
        StyleBtn(ToolEquationBtn, WorkspaceTool.Equation);
        StyleBtn(ToolStickyBtn, WorkspaceTool.StickyNote);

        UpdateStatusBar($"Active tool: {tool}");
    }

    private void UpdateMagicMenuVisibility()
    {
        var selected = _selection.GetSelectedObjects();
        if (selected.Count == 0 || HomeDashboardView.Visibility == Visibility.Visible)
        {
            MagicMenuBar.Visibility = Visibility.Collapsed;
            return;
        }

        MagicMenuBar.Visibility = Visibility.Visible;
        MagicSelectionBadge.Text = $"✦ Magic ({selected.Count} selected)";
        MagicInkActions.Visibility = selected.Any(o => o is InkStrokeObject) ? Visibility.Visible : Visibility.Collapsed;
        MagicEquationActions.Visibility = selected.Count == 1 && selected[0] is EquationObject ? Visibility.Visible : Visibility.Collapsed;
        MagicImageActions.Visibility = selected.Count == 1 && selected[0] is ImageObject ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCanvasContentModified()
    {
        _autosave.MarkDirty();
        ObjectCountText.Text = $"{_objects.Count} object(s)";
    }

    private void UpdateStatusBar(string message) => StatusLeftText.Text = message;

    // ─── Keyboard Shortcuts (Workspace-Local Only — Never Steals From External Apps) ───
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape) Keyboard.ClearFocus();
            return;
        }

        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);

        if (ctrl && e.Key == Key.Z) { UndoBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { RedoBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { HandleClipboardPaste(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D) { MagicDuplicate_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.G) { MagicGroup_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.G) { MagicUngroup_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { SaveProjectBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.N) { NewPageBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Space)
        {
            MagicMenuBar.Visibility = MagicMenuBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.T) { MagicConvertToText_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Enter) { MagicSolveInkEquation_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { CanvasHost.SetZoom(CanvasHost.Zoom * 1.15); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { CanvasHost.SetZoom(CanvasHost.Zoom / 1.15); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { CanvasHost.FitToViewport(); e.Handled = true; return; }

        if (!ctrl && !mods.HasFlag(ModifierKeys.Alt))
        {
            switch (e.Key)
            {
                case Key.V: SelectTool(WorkspaceTool.Select); e.Handled = true; break;
                case Key.P: SelectTool(WorkspaceTool.Pen); e.Handled = true; break;
                case Key.E: SelectTool(WorkspaceTool.Eraser); e.Handled = true; break;
                case Key.H: SelectTool(WorkspaceTool.Highlighter); e.Handled = true; break;
                case Key.L: SelectTool(WorkspaceTool.Lasso); e.Handled = true; break;
                case Key.Delete:
                case Key.Back:
                    if (_selection.HasSelection) { MagicDelete_Click(this, new RoutedEventArgs()); e.Handled = true; }
                    break;
                case Key.Tab:
                    TopToolbarPanel.Visibility = TopToolbarPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true;
                    break;
            }
        }
    }

    private async void HandleClipboardPaste()
    {
        ShowCanvasView();
        var center = CanvasHost.GetViewportCenterWorld();
        var res = _clipboard.TryReadClipboardForWorkspace(_assets, center.X, center.Y, _darkTheme);

        if (res.Kind == TrackPadLens.Clipboard.ClipboardPayloadKind.FileDrop && res.DroppedFilePaths != null)
        {
            foreach (string path in res.DroppedFilePaths)
            {
                await OpenExternalFileAsync(path);
            }
            return;
        }

        if (res.CreatedObject != null)
        {
            _commands.Execute(new AddObjectsCommand(_objects, new[] { res.CreatedObject }, "Paste from Clipboard"));
            _selection.SelectSingle(res.CreatedObject);
        }

        UpdateStatusBar(res.StatusMessage);
    }

    // ─── Canvas Object Placement & Double-Click Edit Handlers ───
    private void OnTextPlacementRequested(Point world)
    {
        string? text = MagicDialogs.ShowTextEditorDialog(this, "Insert Text Object", string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return;

        var obj = TrackPadLens.Clipboard.ClipboardService.CreateTextObject(text, world.X + 100, world.Y + 24, _darkTheme);
        obj.X = world.X;
        obj.Y = world.Y;
        _commands.Execute(new AddObjectsCommand(_objects, new[] { obj }, "Insert Text"));
        _selection.SelectSingle(obj);
        SelectTool(WorkspaceTool.Select);
    }

    private void OnEquationPlacementRequested(Point world)
    {
        var output = MagicDialogs.ShowEquationSolverDialog(this, string.Empty, 1.0, _mathSolver);
        if (output == null) return;

        var eq = new EquationObject
        {
            X = world.X,
            Y = world.Y,
            Width = Math.Max(260, output.Pretty.Length * 13 + 60),
            Height = string.IsNullOrWhiteSpace(output.SolvedResult) ? 54 : 84,
            RawExpression = output.Expression,
            LaTeX = output.Pretty,
            SolvedResult = output.SolvedResult,
            SolutionSteps = output.Steps,
            IsVerified = output.Verified
        };
        _commands.Execute(new AddObjectsCommand(_objects, new[] { eq }, "Insert Equation"));
        _selection.SelectSingle(eq);
        SelectTool(WorkspaceTool.Select);
    }

    private void OnStickyNotePlacementRequested(Point world)
    {
        string? text = MagicDialogs.ShowTextEditorDialog(this, "New Sticky Note", "Study note...");
        if (string.IsNullOrWhiteSpace(text)) return;

        var note = new StickyNoteObject
        {
            X = world.X,
            Y = world.Y,
            Width = 220,
            Height = 140,
            Text = text
        };
        _commands.Execute(new AddObjectsCommand(_objects, new[] { note }, "Insert Sticky Note"));
        _selection.SelectSingle(note);
        SelectTool(WorkspaceTool.Select);
    }

    private void OnObjectDoubleClicked(DocumentObject obj)
    {
        if (obj is TextObject txt)
        {
            string? edited = MagicDialogs.ShowTextEditorDialog(this, "Edit Text", txt.Text);
            if (edited != null)
            {
                txt.Text = edited;
                _objects.NotifyObjectGeometryChanged(txt);
            }
        }
        else if (obj is StickyNoteObject note)
        {
            string? edited = MagicDialogs.ShowTextEditorDialog(this, "Edit Sticky Note", note.Text);
            if (edited != null)
            {
                note.Text = edited;
                _objects.NotifyObjectGeometryChanged(note);
            }
        }
        else if (obj is EquationObject eq)
        {
            EditAndSolveEquation(eq);
        }
    }

    private void EditAndSolveEquation(EquationObject eq)
    {
        var output = MagicDialogs.ShowEquationSolverDialog(this, eq.RawExpression, eq.RecognitionConfidence, _mathSolver);
        if (output == null) return;

        eq.RawExpression = output.Expression;
        eq.LaTeX = output.Pretty;
        eq.SolvedResult = output.SolvedResult;
        eq.SolutionSteps = output.Steps;
        eq.IsVerified = output.Verified;
        eq.Height = string.IsNullOrWhiteSpace(output.SolvedResult) ? 54 : 84;
        _objects.NotifyObjectGeometryChanged(eq);
    }

    // ─── Magic Menu Actions ───
    private async void MagicConvertToText_Click(object sender, RoutedEventArgs e) => await RunHandwritingToTextAsync(defaultTypeset: false);
    private async void MagicTypeset_Click(object sender, RoutedEventArgs e) => await RunHandwritingToTextAsync(defaultTypeset: true);

    private async Task RunHandwritingToTextAsync(bool defaultTypeset)
    {
        var strokes = _selection.GetSelectedObjects().OfType<InkStrokeObject>().ToList();
        if (strokes.Count == 0)
        {
            UpdateStatusBar("Select handwritten strokes first.");
            return;
        }

        UpdateStatusBar($"Running offline handwriting recognition ({_smartInk.ActiveProviderName})...");
        var rec = await _smartInk.RecognizeHandwritingAsync(strokes);
        var commit = MagicDialogs.ShowHandwritingPreviewDialog(this, rec.FullText, rec.EstimatedConfidence, rec.DiagnosticMessage, defaultTypeset);
        if (commit == null)
        {
            UpdateStatusBar("Handwriting conversion cancelled.");
            return;
        }

        var bounds = _selection.GetSelectionBounds() ?? new ObjectRect(80, 80, 260, 60);
        var txt = SmartInkEngine.CreateTypesetTextObject(commit.Text, in bounds, _darkTheme, commit.Typeset);

        if (commit.ReplaceOriginal)
        {
            _commands.Execute(new ReplaceObjectsCommand(_objects, strokes.Cast<DocumentObject>().ToList(), new DocumentObject[] { txt }, "Convert Handwriting to Text"));
        }
        else
        {
            txt.Y = bounds.Bottom + 14;
            _commands.Execute(new AddObjectsCommand(_objects, new[] { txt }, "Insert Recognized Text"));
        }
        _selection.SelectSingle(txt);
        UpdateStatusBar("Created editable text object from handwriting.");
    }

    private void MagicCleanHandwriting_Click(object sender, RoutedEventArgs e)
    {
        var strokes = _selection.GetSelectedObjects().OfType<InkStrokeObject>().ToList();
        if (strokes.Count == 0) return;

        var cleaned = SmartInkEngine.CleanHandwritingStrokes(strokes);
        _commands.Execute(new ReplaceObjectsCommand(
            _objects,
            strokes.Cast<DocumentObject>().ToList(),
            cleaned.Cast<DocumentObject>().ToList(),
            "Clean Handwriting"));
        _selection.SelectMany(cleaned);
        UpdateStatusBar($"Aligned and cleaned {cleaned.Count} handwritten stroke(s) (Ctrl+Z to undo).");
    }

    private async void MagicSolveInkEquation_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selection.GetSelectedObjects();
        if (selected.Count == 1 && selected[0] is EquationObject existingEq)
        {
            EditAndSolveEquation(existingEq);
            return;
        }

        var strokes = selected.OfType<InkStrokeObject>().ToList();
        if (strokes.Count == 0)
        {
            OnEquationPlacementRequested(CanvasHost.GetViewportCenterWorld());
            return;
        }

        UpdateStatusBar("Recognizing handwritten equation offline...");
        var rec = await _smartInk.RecognizeHandwritingAsync(strokes);
        var output = MagicDialogs.ShowEquationSolverDialog(this, rec.FullText.Replace("\r", " ").Replace("\n", " "), rec.EstimatedConfidence, _mathSolver);
        if (output == null) return;

        var bounds = _selection.GetSelectionBounds() ?? new ObjectRect(100, 100, 280, 60);
        var eq = new EquationObject
        {
            X = bounds.Left,
            Y = bounds.Bottom + 14,
            Width = Math.Max(280, bounds.Width + 60),
            Height = string.IsNullOrWhiteSpace(output.SolvedResult) ? 54 : 84,
            RawExpression = output.Expression,
            LaTeX = output.Pretty,
            SolvedResult = output.SolvedResult,
            SolutionSteps = output.Steps,
            RecognitionConfidence = rec.EstimatedConfidence,
            IsVerified = output.Verified
        };
        _commands.Execute(new AddObjectsCommand(_objects, new[] { eq }, "Solve Handwritten Equation"));
        _selection.SelectSingle(eq);
        UpdateStatusBar("Inserted verified symbolic equation solution.");
    }

    private void MagicEditSolveEquation_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is EquationObject eq)
        {
            EditAndSolveEquation(eq);
        }
    }

    private async void MagicExtractImageText_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is not ImageObject img) return;
        byte[]? bytes = _assets.GetBytes(img.AssetId);
        if (bytes == null) return;

        UpdateStatusBar("Running offline Windows OCR on selected image...");
        var rec = await _ocr.RecognizePngBytesAsync(bytes);
        var commit = MagicDialogs.ShowHandwritingPreviewDialog(this, rec.FullText, rec.EstimatedConfidence, rec.DiagnosticMessage, defaultTypeset: false);
        if (commit == null) return;

        var txt = TrackPadLens.Clipboard.ClipboardService.CreateTextObject(commit.Text, img.X + img.Width * 0.5, img.Y + img.Height + 60, _darkTheme);
        txt.X = img.X;
        txt.Y = img.Y + img.Height + 16;
        _commands.Execute(new AddObjectsCommand(_objects, new[] { txt }, "Extract OCR Text"));
        _selection.SelectSingle(txt);
        UpdateStatusBar("Inserted OCR extracted text next to original image.");
    }

    private async void MagicRecreateDiagram_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is not ImageObject img) return;
        byte[]? bytes = _assets.GetBytes(img.AssetId);
        if (bytes == null) return;

        var profile = (DiagramProfile)Math.Clamp(DiagramProfileCombo.SelectedIndex, 0, 3);
        UpdateStatusBar($"Analyzing diagram ({profile}) offline...");

        var result = await _diagramEngine.AnalyzeAndReconstructAsync(bytes, img, profile, _darkTheme);
        var choice = MagicDialogs.ShowDiagramReconstructionDialog(this, result);

        if (choice == MagicDialogs.DiagramChoice.InsertCleanImage && result.CleanedImagePng.Length > 0)
        {
            var cleanObj = TrackPadLens.Clipboard.ClipboardService.CreateImageObjectFromBytes(
                result.CleanedImagePng, ".png", _assets, img.X + img.Width + 24 + img.Width * 0.5, img.Y + img.Height * 0.5, null);
            cleanObj.X = img.X + img.Width + 24;
            cleanObj.Y = img.Y;
            cleanObj.Width = img.Width;
            cleanObj.Height = img.Height;
            _commands.Execute(new AddObjectsCommand(_objects, new[] { cleanObj }, "Insert Clean Diagram Image"));
            _selection.SelectSingle(cleanObj);
            UpdateStatusBar("Inserted cleaned diagram image alongside original.");
        }
        else if (choice == MagicDialogs.DiagramChoice.InsertEditableVectors && result.ReconstructedObjects.Count > 0)
        {
            _commands.Execute(new AddObjectsCommand(_objects, result.ReconstructedObjects, "Insert Reconstructed Vector Diagram"));
            _selection.SelectMany(result.ReconstructedObjects);
            UpdateStatusBar(result.Summary);
        }
    }

    private void MagicCleanImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is not ImageObject img) return;
        byte[]? bytes = _assets.GetBytes(img.AssetId);
        if (bytes == null) return;

        byte[] cleanPng = DiagramRecognitionEngine.BuildCleanedImagePng(bytes);
        var cleanObj = TrackPadLens.Clipboard.ClipboardService.CreateImageObjectFromBytes(
            cleanPng, ".png", _assets, img.X + img.Width + 24 + img.Width * 0.5, img.Y + img.Height * 0.5, null);
        cleanObj.X = img.X + img.Width + 24;
        cleanObj.Y = img.Y;
        cleanObj.Width = img.Width;
        cleanObj.Height = img.Height;
        _commands.Execute(new AddObjectsCommand(_objects, new[] { cleanObj }, "Clean Image"));
        _selection.SelectSingle(cleanObj);
        UpdateStatusBar("Created cleaned high-contrast copy (original preserved untouched).");
    }

    private void MagicCropImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is not ImageObject img) return;
        var crop = MagicDialogs.ShowCropDialog(this, img.CropLeft, img.CropTop, img.CropRight, img.CropBottom);
        if (!crop.HasValue) return;

        img.CropLeft = crop.Value.Left;
        img.CropTop = crop.Value.Top;
        img.CropRight = crop.Value.Right;
        img.CropBottom = crop.Value.Bottom;
        _objects.NotifyObjectGeometryChanged(img);
        UpdateStatusBar("Applied non-destructive crop.");
    }

    private void MagicFlipH_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is ImageObject img)
        {
            img.FlipHorizontal = !img.FlipHorizontal;
            _objects.NotifyObjectGeometryChanged(img);
        }
    }

    private void MagicFlipV_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.GetSelectedObjects().FirstOrDefault() is ImageObject img)
        {
            img.FlipVertical = !img.FlipVertical;
            _objects.NotifyObjectGeometryChanged(img);
        }
    }

    private void MagicDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selection.GetSelectedObjects();
        if (selected.Count == 0) return;

        var clones = selected.Select(o =>
        {
            var c = o.Clone(newId: true);
            c.Translate(24, 24);
            return c;
        }).ToList();

        _commands.Execute(new AddObjectsCommand(_objects, clones, "Duplicate"));
        _selection.SelectMany(clones);
    }

    private void MagicGroup_Click(object sender, RoutedEventArgs e)
    {
        var sel = _selection.GetSelectedObjects();
        if (sel.Count < 2) return;
        _objects.GroupObjects(sel);
        UpdateStatusBar($"Grouped {sel.Count} objects.");
    }

    private void MagicUngroup_Click(object sender, RoutedEventArgs e)
    {
        var sel = _selection.GetSelectedObjects();
        if (sel.Count == 0) return;
        _objects.UngroupObjects(sel);
        UpdateStatusBar("Ungrouped selection.");
    }

    private void MagicAlignLeft_Click(object sender, RoutedEventArgs e)
    {
        var sel = _selection.GetSelectedObjects();
        if (sel.Count < 2) return;
        var before = sel.Select(ObjectGeometrySnapshot.Capture).ToList();
        _objects.AlignObjects(sel, AlignmentKind.Left);
        var after = sel.Select(ObjectGeometrySnapshot.Capture).ToList();
        _commands.Execute(new TransformObjectsCommand(_objects, before, after, "Align Left"));
    }

    private void MagicBringForward_Click(object sender, RoutedEventArgs e) => _objects.BringForward(_selection.GetSelectedObjects());
    private void MagicSendBackward_Click(object sender, RoutedEventArgs e) => _objects.SendBackward(_selection.GetSelectedObjects());

    private void MagicToggleLock_Click(object sender, RoutedEventArgs e)
    {
        var sel = _selection.GetSelectedObjects();
        if (sel.Count == 0) return;
        bool nextLock = !sel.All(o => o.IsLocked);
        foreach (var o in sel) o.IsLocked = nextLock;
        _objects.NotifyBatchChanged(sel);
        UpdateStatusBar(nextLock ? "Locked selected object(s)." : "Unlocked selected object(s).");
    }

    private void MagicDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = _selection.GetSelectedObjects().Where(o => !o.IsLocked).ToList();
        if (sel.Count == 0) return;
        _selection.ClearSelection();
        _commands.Execute(new RemoveObjectsCommand(_objects, sel, "Delete Selection"));
    }

    // ─── Document Panel Actions (Send to Whiteboard & Annotate) ───
    private async void DocPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_pdfEngine.IsLoaded || _currentDocPageIndex <= 0) return;
        _currentDocPageIndex--;
        await RenderCurrentPdfPageAsync();
    }

    private async void DocNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (!_pdfEngine.IsLoaded || _currentDocPageIndex >= _pdfEngine.PageCount - 1) return;
        _currentDocPageIndex++;
        await RenderCurrentPdfPageAsync();
    }

    private async void DocRotatePage_Click(object sender, RoutedEventArgs e)
    {
        if (!_pdfEngine.IsLoaded) return;
        _pdfEngine.RotatePageClockwise(_currentDocPageIndex);
        await RenderCurrentPdfPageAsync();
    }

    private async void SendDocPageToWhiteboard_Click(object sender, RoutedEventArgs e)
    {
        var center = CanvasHost.GetViewportCenterWorld();
        if (_pdfEngine.IsLoaded)
        {
            var bmp = await _pdfEngine.RenderPageAsync(_currentDocPageIndex, zoomScale: 1.5);
            if (bmp == null) return;
            byte[] png = AssetStore.EncodeBitmapToPng(bmp);
            var img = TrackPadLens.Clipboard.ClipboardService.CreateImageObjectFromBytes(png, ".png", _assets, center.X, center.Y, _pdfEngine.FilePath);
            img.SourceDocumentPath = _activeDocx?.SourcePath ?? _pdfEngine.FilePath;
            img.SourcePageNumber = _currentDocPageIndex + 1;
            _commands.Execute(new AddObjectsCommand(_objects, new[] { img }, "Send PDF Page to Whiteboard"));
            _selection.SelectSingle(img);
            UpdateStatusBar($"Sent page {_currentDocPageIndex + 1} to Whiteboard (source reference preserved).");
        }
        else if (_activeDocx != null && _activeDocx.Blocks.Count > 0)
        {
            string summary = string.Join("\n\n", _activeDocx.Blocks.Where(b => b.Kind != DocxBlockKind.Image).Take(8).Select(b => b.Text));
            var txt = TrackPadLens.Clipboard.ClipboardService.CreateTextObject(summary, center.X, center.Y, _darkTheme);
            txt.SourceDocumentPath = _activeDocx.SourcePath;
            txt.SourcePageNumber = 1;
            _commands.Execute(new AddObjectsCommand(_objects, new[] { txt }, "Send DOCX Excerpt to Whiteboard"));
            _selection.SelectSingle(txt);
            UpdateStatusBar("Sent DOCX excerpt to Whiteboard.");
        }
    }

    private async void AnnotateDocPageDirectly_Click(object sender, RoutedEventArgs e)
    {
        if (!_pdfEngine.IsLoaded) return;
        var bmp = await _pdfEngine.RenderPageAsync(_currentDocPageIndex, zoomScale: 1.75);
        if (bmp == null) return;
        _objects.ActivePage.LinkedDocumentPath = _activeDocx?.SourcePath ?? _pdfEngine.FilePath;
        _objects.ActivePage.LinkedDocumentPageIndex = _currentDocPageIndex;
        CanvasHost.SetDocumentBackground(bmp);
        CanvasHost.FitToViewport();
        UpdateStatusBar($"Annotating page {_currentDocPageIndex + 1} non-destructively on canvas.");
    }

    // ─── Sidebar, Toolbar, File & Export Handlers ───
    private void NavHomeBtn_Click(object sender, RoutedEventArgs e) => ShowHomeView();
    private void NavWhiteboardBtn_Click(object sender, RoutedEventArgs e) { ShowCanvasView(); ApplyStudyLayout(StudyLayoutMode.FreeWorkspace); _modeManager.SwitchTo(WorkspaceEnvironment.Whiteboard); }
    private void NavStudyBtn_Click(object sender, RoutedEventArgs e) { ShowCanvasView(); ApplyStudyLayout(StudyLayoutMode.SplitWorkspace); _modeManager.SwitchTo(WorkspaceEnvironment.SmartStudy); }
    private void HomeNewWhiteboard_Click(object sender, RoutedEventArgs e) { BindDocument(new WorkspaceDocument()); ShowCanvasView(); ApplyStudyLayout(StudyLayoutMode.FreeWorkspace); }
    private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _getSettings().ThemeMode = _darkTheme ? AppThemeMode.Light : AppThemeMode.Dark;
        _saveSettings();
        ApplyTheme(!_darkTheme);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshSidebarLists();

    private void NewNotebookBtn_Click(object sender, RoutedEventArgs e)
    {
        string? name = MagicDialogs.ShowTextEditorDialog(this, "New Notebook", $"Notebook {_document.Notebooks.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var nb = new WorkspaceNotebook { Name = name.Trim() };
        _document.Notebooks.Add(nb);
        _document.ActiveNotebookId = nb.Id;
        _document.ActivePageId = nb.Pages[0].Id;
        _objects.BindPage(nb.Pages[0]);
        _selection.ClearSelection();
        CanvasHost.SetDocumentBackground(null);
        RefreshSidebarLists();
        SyncPageControls();
    }

    private void NewPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var nb = _document.GetActiveNotebook();
        var page = new WorkspacePage { Title = $"Page {nb.Pages.Count + 1}", PaperStyle = _objects.ActivePage.PaperStyle, LayoutMode = _objects.ActivePage.LayoutMode };
        nb.Pages.Add(page);
        _document.ActivePageId = page.Id;
        _objects.BindPage(page);
        _selection.ClearSelection();
        CanvasHost.SetDocumentBackground(null);
        RefreshSidebarLists();
        SyncPageControls();
    }

    private void ToggleBookmarkBtn_Click(object sender, RoutedEventArgs e)
    {
        _objects.ActivePage.IsBookmarked = !_objects.ActivePage.IsBookmarked;
        _autosave.MarkDirty();
        RefreshSidebarLists();
    }

    private void NotebooksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || NotebooksList.SelectedItem is not ListBoxItem { Tag: string nbId }) return;
        var nb = _document.Notebooks.FirstOrDefault(n => n.Id == nbId);
        if (nb == null) return;
        _document.ActiveNotebookId = nb.Id;
        if (nb.Pages.Count == 0) nb.Pages.Add(new WorkspacePage());
        _document.ActivePageId = nb.Pages[0].Id;
        _objects.BindPage(nb.Pages[0]);
        _selection.ClearSelection();
        CanvasHost.SetDocumentBackground(null);
        RefreshSidebarLists();
        SyncPageControls();
    }

    private void PagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || PagesList.SelectedItem is not ListBoxItem { Tag: string pageId }) return;
        var nb = _document.GetActiveNotebook();
        var page = nb.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page == null) return;
        _document.ActivePageId = page.Id;
        _objects.BindPage(page);
        _selection.ClearSelection();
        CanvasHost.SetDocumentBackground(null);
        SyncPageControls();
    }

    private void RecentProjectsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentProjectsList.SelectedItem is ListBoxItem { Tag: string path }) TryLoadProjectFile(path);
    }

    private async void RecentDocsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentDocsList.SelectedItem is ListBoxItem { Tag: string path }) await OpenExternalFileAsync(path);
    }

    private void ToolSelectBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Select);
    private void ToolLassoBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Lasso);
    private void ToolPenBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Pen);
    private void ToolPencilBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Pencil);
    private void ToolMarkerBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Marker);
    private void ToolHighlighterBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Highlighter);
    private void ToolEraserBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Eraser);
    private void ToolShapeBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Shape);
    private void ToolConnectorBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Connector);
    private void ToolTextBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Text);
    private void ToolEquationBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.Equation);
    private void ToolStickyBtn_Click(object sender, RoutedEventArgs e) => SelectTool(WorkspaceTool.StickyNote);

    private void ShapeKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CanvasHost.ActiveShapeKind = (ShapeKind)Math.Clamp(ShapeKindCombo.SelectedIndex, 0, 5);
        SelectTool(WorkspaceTool.Shape);
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) CanvasHost.ActiveColorHex = hex;
    }

    private void ThicknessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CanvasHost.ActiveThickness = ThicknessCombo.SelectedIndex switch { 0 => 2.0, 2 => 7.0, _ => 3.5 };
    }

    private void PaperStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        _objects.ActivePage.PaperStyle = (CanvasPaperStyle)Math.Clamp(PaperStyleCombo.SelectedIndex, 0, 3);
        CanvasHost.RedrawAll();
        _autosave.MarkDirty();
    }

    private void CanvasModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        _objects.ActivePage.LayoutMode = CanvasModeCombo.SelectedIndex == 1 ? CanvasLayoutMode.PagedNotebook : CanvasLayoutMode.InfiniteCanvas;
        CanvasHost.RedrawAll();
        _autosave.MarkDirty();
    }

    private void StudyLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        ApplyStudyLayout((StudyLayoutMode)Math.Clamp(StudyLayoutCombo.SelectedIndex, 0, 2));
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e) { if (_commands.Undo()) UpdateStatusBar("Undid last action."); }
    private void RedoBtn_Click(object sender, RoutedEventArgs e) { if (_commands.Redo()) UpdateStatusBar("Redid action."); }
    private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => CanvasHost.SetZoom(CanvasHost.Zoom * 1.15);
    private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => CanvasHost.SetZoom(CanvasHost.Zoom / 1.15);
    private void FitCanvasBtn_Click(object sender, RoutedEventArgs e) => CanvasHost.FitToViewport();

    private async void OpenDocumentBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Document, Image, or Workspace",
            Filter = "Supported Files|*.pdf;*.docx;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.txt;*.tlproject|PDF Documents (*.pdf)|*.pdf|Word Documents (*.docx)|*.docx|Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|TrackPadLens Projects (*.tlproject)|*.tlproject|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) await OpenExternalFileAsync(dlg.FileName);
    }

    private void OpenProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open TrackPadLens Workspace", Filter = "TrackPadLens Project (*.tlproject)|*.tlproject" };
        if (dlg.ShowDialog(this) == true) TryLoadProjectFile(dlg.FileName);
    }

    private async void InsertImageBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Insert Image", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dlg.ShowDialog(this) == true) await OpenExternalFileAsync(dlg.FileName);
    }

    private void SaveProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        string? target = _document.FilePath;
        if (string.IsNullOrWhiteSpace(target))
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save TrackPadLens Workspace",
                Filter = "TrackPadLens Project (*.tlproject)|*.tlproject",
                FileName = $"{_document.Title}.tlproject"
            };
            if (dlg.ShowDialog(this) != true) return;
            target = dlg.FileName;
        }

        try
        {
            _document.Title = Path.GetFileNameWithoutExtension(target);
            _serializer.Save(target, _document, _assets, createBackupIfExisting: true);
            _getSettings().AddRecentProject(target);
            _saveSettings();
            UpdateStatusBar($"Saved workspace to '{Path.GetFileName(target)}'.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save workspace:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportPngBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Export Active Page to PNG", Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg", FileName = $"{_objects.ActivePage.Title}.png" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            if (Path.GetExtension(dlg.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                DocumentExporter.ExportPageToJpeg(dlg.FileName, _objects.ActivePage, _assets, darkBg: _darkTheme);
            else
                DocumentExporter.ExportPageToPng(dlg.FileName, _objects.ActivePage, _assets, darkBg: _darkTheme);

            UpdateStatusBar($"Exported image to '{Path.GetFileName(dlg.FileName)}'.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportPdfBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Export Flattened PDF", Filter = "PDF Document (*.pdf)|*.pdf", FileName = $"{_document.Title}.pdf" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var nb = _document.GetActiveNotebook();
            var pages = nb.Pages.Select(p => (p, (BitmapSource?)null)).ToList();
            DocumentExporter.ExportPagesToFlattenedPdf(dlg.FileName, pages, _assets, darkBg: false);
            UpdateStatusBar($"Exported {pages.Count}-page flattened PDF to '{Path.GetFileName(dlg.FileName)}'.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Flattened PDF export failed:\n{ex.Message}", "PDF Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string path in files) await OpenExternalFileAsync(path);
        }
    }

    private void SwitchToLensBtn_Click(object sender, RoutedEventArgs e) => _switchToLensMode();
    private void RegionCaptureBtn_Click(object sender, RoutedEventArgs e) => _requestRegionCapture();
    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void DiagnosticsBtn_Click(object sender, RoutedEventArgs e) => _openDiagnostics();

    protected override void OnClosing(CancelEventArgs e)
    {
        _autosave.SaveIfDirty();
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _pdfEngine.Dispose();
        base.OnClosing(e);
    }
}
