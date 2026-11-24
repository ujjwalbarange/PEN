using System.Windows.Threading;
using TrackPadLens.Objects;

namespace TrackPadLens.Storage;

/// <summary>
/// Event-driven incremental autosave and crash-recovery service.
/// Only writes to disk when the workspace has actually been marked dirty (zero idle I/O).
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private readonly ProjectSerializer _serializer;
    private readonly string _recoveryDirectory;
    private readonly DispatcherTimer _timer;

    private Func<WorkspaceDocument?>? _documentProvider;
    private Func<AssetStore?>? _assetProvider;
    private bool _isDirty;
    private bool _disposed;

    public string RecoveryFilePath => Path.Combine(_recoveryDirectory, "autosave_recovery.tlproject");
    public DateTime? LastSavedUtc { get; private set; }
    public event Action<DateTime>? Autosaved;

    public AutosaveService(ProjectSerializer serializer, string? recoveryDir = null, int intervalSeconds = 45)
    {
        _serializer = serializer;
        _recoveryDirectory = string.IsNullOrWhiteSpace(recoveryDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackPadLens", "Recovery")
            : recoveryDir;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 10, 600))
        };
        _timer.Tick += (_, _) => SaveIfDirty();
    }

    public void Start(Func<WorkspaceDocument?> docProvider, Func<AssetStore?> assetProvider, int intervalSeconds)
    {
        _documentProvider = docProvider;
        _assetProvider = assetProvider;
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 10, 600));
        _timer.Start();
    }

    public void UpdateInterval(int intervalSeconds)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 10, 600));
    }

    public void MarkDirty() => _isDirty = true;

    public bool HasRecoverySnapshot() => File.Exists(RecoveryFilePath);

    public bool SaveIfDirty()
    {
        if (!_isDirty || _disposed) return false;
        var doc = _documentProvider?.Invoke();
        var assets = _assetProvider?.Invoke();
        if (doc == null || assets == null) return false;

        try
        {
            Directory.CreateDirectory(_recoveryDirectory);
            _serializer.Save(RecoveryFilePath, doc, assets, createBackupIfExisting: false);
            _isDirty = false;
            LastSavedUtc = DateTime.UtcNow;
            Autosaved?.Invoke(LastSavedUtc.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ClearRecoverySnapshot()
    {
        try
        {
            if (File.Exists(RecoveryFilePath)) File.Delete(RecoveryFilePath);
        }
        catch
        {
            // Ignore file lock on shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
