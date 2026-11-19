using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TrackPadLens.Models;
using TrackPadLens.Native;

namespace TrackPadLens.Services;

public sealed record ScreenshotResult(bool Success, string? FilePath, string? ErrorMessage);

/// <summary>
/// Captures the target display (or virtual multi-monitor desktop) including the visible
/// transparent ink overlay in a single desktop composite pass — avoiding double-capturing
/// the overlay — and saves PNG files to Pictures\TrackPadLens\TrackPadLens_yyyyMMdd_HHmmss.png.
/// </summary>
public sealed class ScreenshotService
{
    public static string BuildSnapshotPath(DateTime timestamp, string? basePicturesDirectory = null)
    {
        string picturesDir = string.IsNullOrWhiteSpace(basePicturesDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : basePicturesDirectory;

        string targetDir = Path.Combine(picturesDir, "TrackPadLens");
        string fileName = $"TrackPadLens_{timestamp:yyyyMMdd_HHmmss}.png";
        return Path.Combine(targetDir, fileName);
    }

    public static Rectangle ResolveCaptureBounds(MonitorScope scope)
    {
        if (scope == MonitorScope.AllMonitors)
        {
            int left = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
            int top = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
            int width = Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN);
            int height = Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN);
            if (width > 0 && height > 0)
            {
                return new Rectangle(left, top, width, height);
            }
        }

        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    public ScreenshotResult Capture(MonitorScope scope, Action? beforeCapture = null, Action? afterCapture = null)
    {
        string outputPath = BuildSnapshotPath(DateTime.Now);
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Rectangle bounds = ResolveCaptureBounds(scope);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return new ScreenshotResult(false, null, "No valid screen capture bounds resolved.");
            }

            beforeCapture?.Invoke();
            try
            {
                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                // CopyPixelOperation.CaptureBlt includes topmost WS_EX_LAYERED ink windows in one pass
                graphics.CopyFromScreen(
                    bounds.Left,
                    bounds.Top,
                    0,
                    0,
                    bounds.Size,
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

                bitmap.Save(outputPath, ImageFormat.Png);
            }
            finally
            {
                afterCapture?.Invoke();
            }

            return new ScreenshotResult(true, outputPath, null);
        }
        catch (Exception ex)
        {
            return new ScreenshotResult(
                false,
                null,
                $"Screen capture could not complete (DRM/protected surface or GDI error): {ex.Message}");
        }
    }
}
