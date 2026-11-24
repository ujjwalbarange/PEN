using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TrackPadLens.Services;

/// <summary>
/// Manages the Windows system tray NotifyIcon and context menu with deterministic resource disposal.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly ToolStripMenuItem _enableDoodleItem;
    private readonly ToolStripMenuItem _disableDoodleItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private bool _disposed;

    public event Action? EnableDoodleRequested;
    public event Action? DisableDoodleRequested;
    public event Action? OpenWorkspaceRequested;
    public event Action? RegionCaptureRequested;
    public event Action? OpenCalibrationRequested;
    public event Action? OpenSettingsRequested;
    public event Action<bool>? StartWithWindowsToggled;
    public event Action? OpenMainWindowRequested;
    public event Action? ExitRequested;

    public TrayService(bool startWithWindows)
    {
        _trayIcon = CreateVectorLensIcon();

        var menu = new ContextMenuStrip();
        _enableDoodleItem = new ToolStripMenuItem("Enable Doodle Mode (Alt+D)", null, (_, _) => EnableDoodleRequested?.Invoke());
        _disableDoodleItem = new ToolStripMenuItem("Disable Doodle Mode", null, (_, _) => DisableDoodleRequested?.Invoke());
        var workspaceItem = new ToolStripMenuItem("Open Whiteboard & Smart Study (Alt+W)", null, (_, _) => OpenWorkspaceRequested?.Invoke());
        var regionItem = new ToolStripMenuItem("Region Screenshot Capture (Alt+Shift+S)", null, (_, _) => RegionCaptureRequested?.Invoke());
        var calibrationItem = new ToolStripMenuItem("Calibration Information", null, (_, _) => OpenCalibrationRequested?.Invoke());
        var settingsItem = new ToolStripMenuItem("Settings", null, (_, _) => OpenSettingsRequested?.Invoke());
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = startWithWindows
        };
        _startWithWindowsItem.Click += (_, _) => StartWithWindowsToggled?.Invoke(_startWithWindowsItem.Checked);
        var openItem = new ToolStripMenuItem("Open Hardware Diagnostics", null, (_, _) => OpenMainWindowRequested?.Invoke());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.Add(workspaceItem);
        menu.Items.Add(regionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enableDoodleItem);
        menu.Items.Add(_disableDoodleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(calibrationItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "TrackPadLens",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenMainWindowRequested?.Invoke();

        UpdateDoodleState(false);
    }

    public void UpdateDoodleState(bool doodleEnabled)
    {
        if (_disposed) return;
        _enableDoodleItem.Enabled = !doodleEnabled;
        _disableDoodleItem.Enabled = doodleEnabled;
        _notifyIcon.Text = doodleEnabled ? "TrackPadLens — Doodle Mode ON" : "TrackPadLens — Standby";
    }

    public void UpdateStartWithWindowsState(bool enabled)
    {
        if (_disposed) return;
        _startWithWindowsItem.Checked = enabled;
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_disposed) return;
        _notifyIcon.ShowBalloonTip(2200, title, message, icon);
    }

    private static Icon CreateVectorLensIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(255, 13, 18, 28));

        using var cyanPen = new Pen(Color.FromArgb(255, 0, 210, 255), 2.5f);
        g.DrawRectangle(cyanPen, 4, 6, 24, 20);

        using var accentBrush = new SolidBrush(Color.FromArgb(255, 0, 210, 255));
        g.FillEllipse(accentBrush, 13, 13, 6, 6);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }
}
