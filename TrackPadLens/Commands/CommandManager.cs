using TrackPadLens.Objects;

namespace TrackPadLens.Commands;

public interface IWorkspaceCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

/// <summary>
/// Bounded command-based Undo/Redo history manager (never clones full documents per action).
/// </summary>
public sealed class CommandManager
{
    public const int MaxHistoryDepth = 200;

    private readonly LinkedList<IWorkspaceCommand> _undoStack = new();
    private readonly Stack<IWorkspaceCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public event Action? HistoryChanged;

    public void Execute(IWorkspaceCommand command)
    {
        command.Execute();
        _undoStack.AddLast(command);
        while (_undoStack.Count > MaxHistoryDepth)
        {
            _undoStack.RemoveFirst();
        }
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        var cmd = _undoStack.Last!.Value;
        _undoStack.RemoveLast();
        cmd.Undo();
        _redoStack.Push(cmd);
        HistoryChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.AddLast(cmd);
        HistoryChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }
}

public sealed class AddObjectsCommand : IWorkspaceCommand
{
    private readonly ObjectManager _manager;
    private readonly List<DocumentObject> _items;
    public string Description { get; }

    public AddObjectsCommand(ObjectManager manager, IEnumerable<DocumentObject> items, string description = "Add Object")
    {
        _manager = manager;
        _items = items.ToList();
        Description = description;
    }

    public void Execute() => _manager.AddRange(_items);
    public void Undo() => _manager.RemoveRange(_items.Select(i => i.Id));
}

public sealed class RemoveObjectsCommand : IWorkspaceCommand
{
    private readonly ObjectManager _manager;
    private readonly List<DocumentObject> _items;
    public string Description { get; }

    public RemoveObjectsCommand(ObjectManager manager, IEnumerable<DocumentObject> items, string description = "Delete Object")
    {
        _manager = manager;
        _items = items.ToList();
        Description = description;
    }

    public void Execute() => _manager.RemoveRange(_items.Select(i => i.Id));
    public void Undo() => _manager.AddRange(_items);
}

public sealed class ReplaceObjectsCommand : IWorkspaceCommand
{
    private readonly ObjectManager _manager;
    private readonly List<DocumentObject> _removed;
    private readonly List<DocumentObject> _added;
    public string Description { get; }

    public ReplaceObjectsCommand(
        ObjectManager manager,
        IEnumerable<DocumentObject> toRemove,
        IEnumerable<DocumentObject> toAdd,
        string description = "Transform Objects")
    {
        _manager = manager;
        _removed = toRemove.ToList();
        _added = toAdd.ToList();
        Description = description;
    }

    public void Execute()
    {
        if (_removed.Count > 0) _manager.RemoveRange(_removed.Select(o => o.Id));
        if (_added.Count > 0) _manager.AddRange(_added);
    }

    public void Undo()
    {
        if (_added.Count > 0) _manager.RemoveRange(_added.Select(o => o.Id));
        if (_removed.Count > 0) _manager.AddRange(_removed);
    }
}

public sealed record ObjectGeometrySnapshot(
    string Id,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    List<InkPointData>? InkPoints = null,
    double StartX = 0,
    double StartY = 0,
    double EndX = 0,
    double EndY = 0)
{
    public static ObjectGeometrySnapshot Capture(DocumentObject obj)
    {
        if (obj is InkStrokeObject stroke)
        {
            return new ObjectGeometrySnapshot(obj.Id, obj.X, obj.Y, obj.Width, obj.Height, obj.RotationDegrees, new List<InkPointData>(stroke.Points));
        }
        if (obj is ConnectorObject conn)
        {
            return new ObjectGeometrySnapshot(obj.Id, obj.X, obj.Y, obj.Width, obj.Height, obj.RotationDegrees, null, conn.StartX, conn.StartY, conn.EndX, conn.EndY);
        }
        return new ObjectGeometrySnapshot(obj.Id, obj.X, obj.Y, obj.Width, obj.Height, obj.RotationDegrees);
    }

    public void ApplyTo(DocumentObject obj)
    {
        obj.X = X;
        obj.Y = Y;
        obj.Width = Width;
        obj.Height = Height;
        obj.RotationDegrees = RotationDegrees;
        if (obj is InkStrokeObject stroke && InkPoints != null)
        {
            stroke.Points = new List<InkPointData>(InkPoints);
        }
        else if (obj is ConnectorObject conn)
        {
            conn.StartX = StartX;
            conn.StartY = StartY;
            conn.EndX = EndX;
            conn.EndY = EndY;
        }
    }
}

public sealed class TransformObjectsCommand : IWorkspaceCommand
{
    private readonly ObjectManager _manager;
    private readonly List<ObjectGeometrySnapshot> _before;
    private readonly List<ObjectGeometrySnapshot> _after;
    public string Description { get; }

    public TransformObjectsCommand(
        ObjectManager manager,
        List<ObjectGeometrySnapshot> before,
        List<ObjectGeometrySnapshot> after,
        string description = "Transform")
    {
        _manager = manager;
        _before = before;
        _after = after;
        Description = description;
    }

    public void Execute() => ApplySnapshots(_after);
    public void Undo() => ApplySnapshots(_before);

    private void ApplySnapshots(List<ObjectGeometrySnapshot> snapshots)
    {
        var touched = new List<DocumentObject>(snapshots.Count);
        foreach (var snap in snapshots)
        {
            var obj = _manager.GetById(snap.Id);
            if (obj != null)
            {
                snap.ApplyTo(obj);
                touched.Add(obj);
            }
        }
        _manager.NotifyBatchChanged(touched);
    }
}
