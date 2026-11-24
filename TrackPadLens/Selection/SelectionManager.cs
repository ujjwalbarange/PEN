using TrackPadLens.Objects;

namespace TrackPadLens.Selection;

/// <summary>
/// Handles single/multi selection, Shift/Ctrl toggle selection, rectangular marquee,
/// freeform polygon lasso selection, and combined selection bounding-box calculation.
/// </summary>
public sealed class SelectionManager
{
    private readonly ObjectManager _objects;
    private readonly HashSet<string> _selectedIds = new();

    public IReadOnlyCollection<string> SelectedIds => _selectedIds;
    public bool HasSelection => _selectedIds.Count > 0;
    public event Action? SelectionChanged;

    public SelectionManager(ObjectManager objects)
    {
        _objects = objects;
    }

    public List<DocumentObject> GetSelectedObjects()
    {
        var list = new List<DocumentObject>(_selectedIds.Count);
        foreach (string id in _selectedIds)
        {
            var obj = _objects.GetById(id);
            if (obj != null) list.Add(obj);
        }
        return list;
    }

    public ObjectRect? GetSelectionBounds()
    {
        var selected = GetSelectedObjects();
        if (selected.Count == 0) return null;
        var bounds = selected[0].Bounds;
        for (int i = 1; i < selected.Count; i++)
        {
            bounds = ObjectRect.Union(in bounds, selected[i].Bounds);
        }
        return bounds;
    }

    public void ClearSelection()
    {
        if (_selectedIds.Count == 0) return;
        foreach (string id in _selectedIds)
        {
            var obj = _objects.GetById(id);
            if (obj != null) obj.IsSelected = false;
        }
        _selectedIds.Clear();
        SelectionChanged?.Invoke();
    }

    public void SelectSingle(DocumentObject? obj, bool additive = false)
    {
        if (!additive)
        {
            ClearSelectionSilent();
        }

        if (obj != null)
        {
            var group = _objects.ExpandWithGroupMembers(new[] { obj });
            bool allAlreadySelected = additive && group.All(m => _selectedIds.Contains(m.Id));
            foreach (var member in group)
            {
                if (allAlreadySelected)
                {
                    _selectedIds.Remove(member.Id);
                    member.IsSelected = false;
                }
                else
                {
                    _selectedIds.Add(member.Id);
                    member.IsSelected = true;
                }
            }
        }

        SelectionChanged?.Invoke();
    }

    public void SelectMany(IEnumerable<DocumentObject> items, bool additive = false)
    {
        if (!additive) ClearSelectionSilent();
        var expanded = _objects.ExpandWithGroupMembers(items);
        foreach (var obj in expanded)
        {
            _selectedIds.Add(obj.Id);
            obj.IsSelected = true;
        }
        SelectionChanged?.Invoke();
    }

    public void SelectInRect(in ObjectRect rect, bool additive = false)
    {
        var hits = _objects.HitTestRect(in rect);
        SelectMany(hits, additive);
    }

    public void SelectInLasso(IReadOnlyList<(double X, double Y)> polygon, bool additive = false)
    {
        if (polygon.Count < 3)
        {
            if (!additive) ClearSelection();
            return;
        }

        double minX = polygon.Min(p => p.X);
        double maxX = polygon.Max(p => p.X);
        double minY = polygon.Min(p => p.Y);
        double maxY = polygon.Max(p => p.Y);
        var bbox = new ObjectRect(minX, minY, Math.Max(1.0, maxX - minX), Math.Max(1.0, maxY - minY));

        var candidates = _objects.HitTestRect(in bbox);
        var matched = new List<DocumentObject>();
        foreach (var obj in candidates)
        {
            if (IsObjectInsideLasso(obj, polygon))
            {
                matched.Add(obj);
            }
        }
        SelectMany(matched, additive);
    }

    public static bool IsPointInPolygon(double px, double py, IReadOnlyList<(double X, double Y)> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y;
            double xj = poly[j].X, yj = poly[j].Y;
            bool intersect = ((yi > py) != (yj > py)) &&
                             (px < (xj - xi) * (py - yi) / ((yj - yi) == 0 ? 1e-9 : (yj - yi)) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private static bool IsObjectInsideLasso(DocumentObject obj, IReadOnlyList<(double X, double Y)> poly)
    {
        if (obj is InkStrokeObject stroke && stroke.Points.Count > 0)
        {
            int insideCount = 0;
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                if (IsPointInPolygon(stroke.Points[i].X, stroke.Points[i].Y, poly))
                {
                    insideCount++;
                }
            }
            return insideCount > 0 && insideCount >= Math.Max(1, stroke.Points.Count / 3);
        }

        var b = obj.Bounds;
        return IsPointInPolygon(b.CenterX, b.CenterY, poly) ||
               IsPointInPolygon(b.Left, b.Top, poly) ||
               IsPointInPolygon(b.Right, b.Bottom, poly);
    }

    private void ClearSelectionSilent()
    {
        foreach (string id in _selectedIds)
        {
            var obj = _objects.GetById(id);
            if (obj != null) obj.IsSelected = false;
        }
        _selectedIds.Clear();
    }
}
