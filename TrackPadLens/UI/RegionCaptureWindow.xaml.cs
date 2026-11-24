using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrackPadLens.Native;

namespace TrackPadLens.UI;

/// <summary>
/// Multi-monitor region screenshot capture overlay (Alt+Shift+S).
/// Captures the virtual desktop cleanly, lets the user drag a selection region,
/// and returns cropped PNG bytes without modifying the user's clipboard.
/// </summary>
public partial class RegionCaptureWindow : Window
{
    private readonly Bitmap _virtualDesktopBitmap;
    private readonly int _virtualLeft;
    private readonly int _virtualTop;
    private readonly int _virtualWidth;
    private readonly int _virtualHeight;

    private bool _isDragging;
    private System.Windows.Point _startDip;
    private System.Windows.Point _currentDip;

    public byte[]? CapturedPngBytes { get; private set; }

    public RegionCaptureWindow()
    {
        InitializeComponent();

        _virtualLeft = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
        _virtualTop = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
        _virtualWidth = Math.Max(320, Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN));
        _virtualHeight = Math.Max(240, Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN));

        _virtualDesktopBitmap = new Bitmap(_virtualWidth, _virtualHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_virtualDesktopBitmap))
        {
            g.CopyFromScreen(_virtualLeft, _virtualTop, 0, 0, new System.Drawing.Size(_virtualWidth, _virtualHeight), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        _virtualDesktopBitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = ms;
        preview.EndInit();
        preview.Freeze();
        DesktopSnapshotImage.Source = preview;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Closed += (_, _) => _virtualDesktopBitmap.Dispose();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CapturedPngBytes = null;
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            CapturedPngBytes = CropToPng(0, 0, _virtualWidth, _virtualHeight);
            DialogResult = true;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startDip = e.GetPosition(SelectionCanvas);
        _currentDip = _startDip;
        SelectionRect.Visibility = Visibility.Visible;
        DimensionsBadge.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelectionVisual();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        _currentDip = e.GetPosition(SelectionCanvas);
        UpdateSelectionVisual();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();
        _currentDip = e.GetPosition(SelectionCanvas);

        double dipLeft = Math.Min(_startDip.X, _currentDip.X);
        double dipTop = Math.Min(_startDip.Y, _currentDip.Y);
        double dipW = Math.Abs(_currentDip.X - _startDip.X);
        double dipH = Math.Abs(_currentDip.Y - _startDip.Y);

        if (dipW < 8 || dipH < 8)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            DimensionsBadge.Visibility = Visibility.Collapsed;
            return;
        }

        double scaleX = _virtualWidth / Math.Max(1.0, ActualWidth);
        double scaleY = _virtualHeight / Math.Max(1.0, ActualHeight);

        int pxX = Math.Clamp((int)Math.Round(dipLeft * scaleX), 0, _virtualWidth - 2);
        int pxY = Math.Clamp((int)Math.Round(dipTop * scaleY), 0, _virtualHeight - 2);
        int pxW = Math.Clamp((int)Math.Round(dipW * scaleX), 2, _virtualWidth - pxX);
        int pxH = Math.Clamp((int)Math.Round(dipH * scaleY), 2, _virtualHeight - pxY);

        CapturedPngBytes = CropToPng(pxX, pxY, pxW, pxH);
        DialogResult = true;
        Close();
    }

    private void UpdateSelectionVisual()
    {
        double left = Math.Min(_startDip.X, _currentDip.X);
        double top = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        double scaleX = _virtualWidth / Math.Max(1.0, ActualWidth);
        double scaleY = _virtualHeight / Math.Max(1.0, ActualHeight);
        DimensionsText.Text = $"{(int)Math.Round(w * scaleX)} × {(int)Math.Round(h * scaleY)} px";
        Canvas.SetLeft(DimensionsBadge, left);
        Canvas.SetTop(DimensionsBadge, Math.Max(8, top - 30));
    }

    private byte[] CropToPng(int x, int y, int width, int height)
    {
        using var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(_virtualDesktopBitmap, new Rectangle(0, 0, width, height), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
        }
        using var ms = new MemoryStream();
        cropped.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
