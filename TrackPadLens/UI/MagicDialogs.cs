using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrackPadLens.Core;
using TrackPadLens.Intelligence;

namespace TrackPadLens.UI;

/// <summary>
/// Clean native WPF modal dialogs for TrackPadLens Magic & Intelligent actions:
/// - Equation Editor & Offline Symbolic Math Solver (with confidence & verification)
/// - Handwriting Recognition Preview & Correction
/// - Diagram Reconstruction Preview (Clean Image vs Editable Vector Objects)
/// - Quick Text / Sticky Note Editor
/// - Image Crop Editor
/// </summary>
public static class MagicDialogs
{
    private static readonly SolidColorBrush BgBrush = new(Color.FromRgb(0x0F, 0x17, 0x2A));
    private static readonly SolidColorBrush CardBrush = new(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly SolidColorBrush BorderBrushCol = new(Color.FromRgb(0x33, 0x41, 0x55));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0x00, 0xD2, 0xFF));

    public sealed record EquationDialogOutput(string Expression, string Pretty, string? SolvedResult, List<string> Steps, bool Verified);

    public static EquationDialogOutput? ShowEquationSolverDialog(
        Window owner,
        string initialExpression,
        double confidence,
        MathSolverEngine solver)
    {
        var win = CreateDialogWindow(owner, "Mathematical Intelligence — Verify & Solve", 560, 440);
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = confidence < 0.99
                ? $"Verify recognized equation (confidence {confidence:P0}) before solving:"
                : "Enter or edit a mathematical expression (e.g., x^2 - 5x + 6 = 0, 3x + 4 = 19, 2x^3 - 4x):",
            Foreground = MutedBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var inputBox = new TextBox
        {
            Text = initialExpression,
            FontFamily = new FontFamily("Cambria Math, Consolas"),
            FontSize = 17,
            Padding = new Thickness(10, 7, 10, 7),
            Background = CardBrush,
            Foreground = TextBrush,
            BorderBrush = AccentBrush,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(inputBox, 1);
        root.Children.Add(inputBox);

        var opRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var resultBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            BorderBrush = BorderBrushCol,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(10)
        };

        MathSolveResult? lastSolve = null;

        void RunOp(MathOperation op)
        {
            lastSolve = solver.Process(inputBox.Text, op);
            if (lastSolve.Success)
            {
                resultBox.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                resultBox.Text = $"Expression:  {lastSolve.PrettyExpression}\n" +
                                 $"Result:      {lastSolve.ResultText}\n\n" +
                                 "Verified Steps:\n" +
                                 string.Join("\n", lastSolve.Steps.Select((s, i) => $"  {i + 1}. {s}"));
            }
            else
            {
                resultBox.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                resultBox.Text = $"Verification Notice:\n{lastSolve.ErrorMessage}\n\nEdit the expression above and retry.";
            }
        }

        foreach (var (label, op) in new[]
        {
            ("Solve", MathOperation.Solve),
            ("Simplify", MathOperation.Simplify),
            ("Factorize", MathOperation.Factorize),
            ("Differentiate", MathOperation.Differentiate),
            ("Integrate", MathOperation.Integrate)
        })
        {
            var btn = CreateActionButton(label, isPrimary: op == MathOperation.Solve);
            btn.Margin = new Thickness(0, 0, 6, 6);
            btn.Click += (_, _) => RunOp(op);
            opRow.Children.Add(btn);
        }

        Grid.SetRow(opRow, 2);
        root.Children.Add(opRow);

        Grid.SetRow(resultBox, 3);
        root.Children.Add(resultBox);

        EquationDialogOutput? output = null;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancelBtn = CreateActionButton("Cancel", false);
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };

        var insertBtn = CreateActionButton("Insert Equation on Canvas", true);
        insertBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inputBox.Text)) return;
            output = new EquationDialogOutput(
                inputBox.Text.Trim(),
                lastSolve?.PrettyExpression ?? MathSolverEngine.FormatPrettyMath(inputBox.Text.Trim()),
                lastSolve is { Success: true } ? lastSolve.ResultText : null,
                lastSolve is { Success: true } ? lastSolve.Steps : new List<string>(),
                lastSolve?.Success ?? false);
            win.DialogResult = true;
            win.Close();
        };

        footer.Children.Add(cancelBtn);
        footer.Children.Add(insertBtn);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        win.Content = root;
        if (!string.IsNullOrWhiteSpace(initialExpression))
        {
            RunOp(initialExpression.Contains('=') ? MathOperation.Solve : MathOperation.Simplify);
        }

        return win.ShowDialog() == true ? output : null;
    }

    public sealed record HandwritingCommitResult(string Text, bool ReplaceOriginal, bool Typeset);

    public static HandwritingCommitResult? ShowHandwritingPreviewDialog(
        Window owner,
        string recognizedText,
        double confidence,
        string diagnosticNote,
        bool defaultTypeset)
    {
        var win = CreateDialogWindow(owner, "Smart Ink — Verify Recognized Text", 520, 340);
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var info = new TextBlock
        {
            Text = $"Confidence: {confidence:P0}  ·  {diagnosticNote}\nEdit recognized text below before inserting:",
            Foreground = MutedBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(info, 0);
        root.Children.Add(info);

        var textBox = new TextBox
        {
            Text = recognizedText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = CardBrush,
            Foreground = TextBrush,
            BorderBrush = BorderBrushCol,
            FontSize = 15,
            Padding = new Thickness(10)
        };
        Grid.SetRow(textBox, 1);
        root.Children.Add(textBox);

        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var replaceCheck = new CheckBox { Content = "Replace original handwriting (undoable)", Foreground = TextBrush, Margin = new Thickness(0, 0, 18, 0), IsChecked = false };
        var typesetCheck = new CheckBox { Content = "Apply Typeset formatting", Foreground = TextBrush, IsChecked = defaultTypeset };
        opts.Children.Add(replaceCheck);
        opts.Children.Add(typesetCheck);
        Grid.SetRow(opts, 2);
        root.Children.Add(opts);

        HandwritingCommitResult? res = null;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = CreateActionButton("Cancel", false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };

        var ok = CreateActionButton("Commit Text Object", true);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text)) return;
            res = new HandwritingCommitResult(textBox.Text.Trim(), replaceCheck.IsChecked == true, typesetCheck.IsChecked == true);
            win.DialogResult = true;
            win.Close();
        };
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        win.Content = root;
        return win.ShowDialog() == true ? res : null;
    }

    public enum DiagramChoice { Cancel, InsertCleanImage, InsertEditableVectors }

    public static DiagramChoice ShowDiagramReconstructionDialog(Window owner, DiagramReconstructionResult result)
    {
        var win = CreateDialogWindow(owner, "Smart Diagram Reconstruction — Review Output", 560, 360);
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = result.Summary,
            Foreground = AccentBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        string notesText = result.AmbiguousNotes.Count > 0
            ? "\nNotes & Limitations:\n• " + string.Join("\n• ", result.AmbiguousNotes)
            : "\nAll detected primitives were mapped into independent vector shapes, connectors, and OCR text labels.";

        var body = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = CardBrush,
            Foreground = TextBrush,
            BorderBrush = BorderBrushCol,
            Padding = new Thickness(10),
            Text = "Your original image remains completely unchanged.\n\n" +
                   "Choose how to insert the reconstructed output side-by-side:\n" +
                   "  A) Clean Image — inserts a denoised, high-contrast cleaned image.\n" +
                   $"  B) Editable Vector Reconstruction — inserts {result.ReconstructedObjects.Count} independent movable/resizable shapes, connectors, and text objects.\n" +
                   notesText
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var choice = DiagramChoice.Cancel;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };

        var cancel = CreateActionButton("Cancel", false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };

        var cleanBtn = CreateActionButton("A. Insert Clean Image", false);
        cleanBtn.Margin = new Thickness(0, 0, 8, 0);
        cleanBtn.Click += (_, _) => { choice = DiagramChoice.InsertCleanImage; win.DialogResult = true; win.Close(); };

        var vecBtn = CreateActionButton($"B. Insert Editable Vectors ({result.ReconstructedObjects.Count})", true);
        vecBtn.IsEnabled = result.ReconstructedObjects.Count > 0;
        vecBtn.Click += (_, _) => { choice = DiagramChoice.InsertEditableVectors; win.DialogResult = true; win.Close(); };

        footer.Children.Add(cancel);
        footer.Children.Add(cleanBtn);
        footer.Children.Add(vecBtn);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        win.Content = root;
        win.ShowDialog();
        return choice;
    }

    public static string? ShowTextEditorDialog(Window owner, string title, string initialText)
    {
        var win = CreateDialogWindow(owner, title, 460, 260);
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tb = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = CardBrush,
            Foreground = TextBrush,
            BorderBrush = BorderBrushCol,
            FontSize = 14,
            Padding = new Thickness(10)
        };
        Grid.SetRow(tb, 0);
        root.Children.Add(tb);

        string? result = null;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = CreateActionButton("Cancel", false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        var ok = CreateActionButton("Apply", true);
        ok.Click += (_, _) => { result = tb.Text; win.DialogResult = true; win.Close(); };
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        win.Content = root;
        tb.Focus();
        tb.SelectAll();
        return win.ShowDialog() == true ? result : null;
    }

    public static (double Left, double Top, double Right, double Bottom)? ShowCropDialog(
        Window owner, double left, double top, double right, double bottom)
    {
        var win = CreateDialogWindow(owner, "Crop Image (Non-Destructive)", 400, 260);
        var stack = new StackPanel { Margin = new Thickness(18) };

        Slider MakeSlider(string label, double min, double max, double val)
        {
            stack.Children.Add(new TextBlock { Text = label, Foreground = TextBrush, FontSize = 12, Margin = new Thickness(0, 4, 0, 2) });
            var s = new Slider { Minimum = min, Maximum = max, Value = val, SmallChange = 0.02, LargeChange = 0.05 };
            stack.Children.Add(s);
            return s;
        }

        var sL = MakeSlider("Crop Left Edge (0% – 45%)", 0, 0.45, left);
        var sT = MakeSlider("Crop Top Edge (0% – 45%)", 0, 0.45, top);
        var sR = MakeSlider("Crop Right Edge (55% – 100%)", 0.55, 1.0, right);
        var sB = MakeSlider("Crop Bottom Edge (55% – 100%)", 0.55, 1.0, bottom);

        (double, double, double, double)? result = null;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = CreateActionButton("Reset", false);
        reset.Margin = new Thickness(0, 0, 8, 0);
        reset.Click += (_, _) => { sL.Value = 0; sT.Value = 0; sR.Value = 1; sB.Value = 1; };
        var apply = CreateActionButton("Apply Crop", true);
        apply.Click += (_, _) => { result = (sL.Value, sT.Value, sR.Value, sB.Value); win.DialogResult = true; win.Close(); };
        footer.Children.Add(reset);
        footer.Children.Add(apply);
        stack.Children.Add(footer);

        win.Content = stack;
        return win.ShowDialog() == true ? result : null;
    }

    private static Window CreateDialogWindow(Window owner, string title, double w, double h)
    {
        return new Window
        {
            Owner = owner,
            Title = title,
            Width = w,
            Height = h,
            Background = BgBrush,
            Foreground = TextBrush,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
    }

    private static Button CreateActionButton(string text, bool isPrimary)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
            Background = isPrimary ? AccentBrush : CardBrush,
            Foreground = isPrimary ? new SolidColorBrush(Color.FromRgb(0x0B, 0x11, 0x20)) : TextBrush,
            BorderBrush = isPrimary ? AccentBrush : BorderBrushCol,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
