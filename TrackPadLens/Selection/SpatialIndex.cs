using TrackPadLens.Objects;

namespace TrackPadLens.Selection;

/// <summary>
/// Uniform-grid spatial hash index enabling fast O(k) viewport culling, click hit-testing,
/// marquee selection, and lasso candidate queries on large whiteboards without scanning all strokes.
/// </summary>
public sealed class SpatialIndex
{
    private readonly double _cellSize;
    private readonly Dictionary<(int Cx, int Cy), HashSet<string>> _cells = new();
    private readonly Dictionary<string, (ObjectRect Bounds, List<(int Cx, int Cy)> Cells)> _entries = new();

    public int Count => _entries.Count;

    public SpatialIndex(double cellSize = 160.0)
    {
        _cellSize = Math.Max(32.0, cellSize);
    }

    public void Clear()
    {
        _cells.Clear();
        _entries.Clear();
    }

    public void Rebuild(IEnumerable<DocumentObject> objects)
    {
        Clear();
        foreach (var obj in objects)
        {
            Insert(obj);
        }
    }

    public void Insert(DocumentObject obj)
    {
        Remove(obj.Id);
        var bounds = obj.Bounds;
        var covered = GetCoveredCells(in bounds);
        foreach (var cell in covered)
        {
            if (!_cells.TryGetValue(cell, out var bucket))
            {
                bucket = new HashSet<string>();
                _cells[cell] = bucket;
            }
            bucket.Add(obj.Id);
        }
        _entries[obj.Id] = (bounds, covered);
    }

    public void Update(DocumentObject obj) => Insert(obj);

    public void Remove(string objectId)
    {
        if (!_entries.TryGetValue(objectId, out var existing)) return;
        foreach (var cell in existing.Cells)
        {
            if (_cells.TryGetValue(cell, out var bucket))
            {
                bucket.Remove(objectId);
                if (bucket.Count == 0)
                {
                    _cells.Remove(cell);
                }
            }
        }
        _entries.Remove(objectId);
    }

    public HashSet<string> QueryRect(in ObjectRect query)
    {
        var result = new HashSet<string>();
        int minX = CellCoord(query.Left);
        int maxX = CellCoord(query.Right);
        int minY = CellCoord(query.Top);
        int maxY = CellCoord(query.Bottom);

        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                if (_cells.TryGetValue((cx, cy), out var bucket))
                {
                    foreach (string id in bucket)
                    {
                        if (_entries.TryGetValue(id, out var entry) && entry.Bounds.Intersects(in query))
                        {
                            result.Add(id);
                        }
                    }
                }
            }
        }
        return result;
    }

    public HashSet<string> QueryPoint(double x, double y, double tolerance = 8.0)
    {
        var rect = new ObjectRect(x - tolerance, y - tolerance, tolerance * 2.0, tolerance * 2.0);
        return QueryRect(in rect);
    }

    private int CellCoord(double value) => (int)Math.Floor(value / _cellSize);

    private List<(int Cx, int Cy)> GetCoveredCells(in ObjectRect bounds)
    {
        int minX = CellCoord(bounds.Left);
        int maxX = CellCoord(bounds.Right);
        int minY = CellCoord(bounds.Top);
        int maxY = CellCoord(bounds.Bottom);

        // Guard pathological ultra-huge objects from exploding cell count
        int spanX = Math.Min(maxX - minX, 64);
        int spanY = Math.Min(maxY - minY, 64);
        var list = new List<(int, int)>((spanX + 1) * (spanY + 1));
        for (int cx = minX; cx <= minX + spanX; cx++)
        {
            for (int cy = minY; cy <= minY + spanY; cy++)
            {
                list.Add((cx, cy));
            }
        }
        return list;
    }
}
