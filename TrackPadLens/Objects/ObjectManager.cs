using TrackPadLens.Selection;

namespace TrackPadLens.Objects;

public enum AlignmentKind
{
    Left,
    CenterHorizontal,
    Right,
    Top,
    CenterVertical,
    Bottom
}

/// <summary>
/// Manages DocumentObjects on the active WorkspacePage, keeping the SpatialIndex in sync
/// and supporting O(k) viewport culling, accurate hit-testing, z-ordering, grouping, and alignment.
/// </summary>
public sealed class ObjectManager
{
    private WorkspacePage _activePage = new();
    private readonly Dictionary<string, DocumentObject> _byId = new();
    private readonly SpatialIndex _spatialIndex = new(160.0);

    public WorkspacePage ActivePage => _activePage;
    public IReadOnlyList<DocumentObject> Objects => _activePage.Objects;
    public int Count => _activePage.Objects.Count;

    public event Action? SceneChanged;

    public void BindPage(WorkspacePage page)
    {
        _activePage = page;
        RebuildIndex();
        SceneChanged?.Invoke();
    }

    public void RebuildIndex()
    {
        _byId.Clear();
        int nextZ = 0;
        foreach (var obj in _activePage.Objects)
        {
            if (obj.ZIndex <= 0) obj.ZIndex = ++nextZ;
            else nextZ = Math.Max(nextZ, obj.ZIndex);
            _byId[obj.Id] = obj;
        }
        _spatialIndex.Rebuild(_activePage.Objects);
    }

    public DocumentObject? GetById(string id) => _byId.TryGetValue(id, out var obj) ? obj : null;

    public void Add(DocumentObject obj)
    {
        if (_byId.ContainsKey(obj.Id)) return;
        obj.ZIndex = _activePage.Objects.Count == 0 ? 1 : _activePage.Objects.Max(o => o.ZIndex) + 1;
        _activePage.Objects.Add(obj);
        _byId[obj.Id] = obj;
        _spatialIndex.Insert(obj);
        SceneChanged?.Invoke();
    }

    public void AddRange(IEnumerable<DocumentObject> objects)
    {
        int maxZ = _activePage.Objects.Count == 0 ? 0 : _activePage.Objects.Max(o => o.ZIndex);
        bool addedAny = false;
        foreach (var obj in objects)
        {
            if (_byId.ContainsKey(obj.Id)) continue;
            if (obj.ZIndex <= 0) obj.ZIndex = ++maxZ;
            _activePage.Objects.Add(obj);
            _byId[obj.Id] = obj;
            _spatialIndex.Insert(obj);
            addedAny = true;
        }
        if (addedAny) SceneChanged?.Invoke();
    }

    public bool Remove(string objectId)
    {
        if (!_byId.Remove(objectId, out var obj)) return false;
        _activePage.Objects.Remove(obj);
        _spatialIndex.Remove(objectId);
        SceneChanged?.Invoke();
        return true;
    }

    public int RemoveRange(IEnumerable<string> ids)
    {
        int removed = 0;
        foreach (string id in ids.ToList())
        {
            if (_byId.Remove(id, out var obj))
            {
                _activePage.Objects.Remove(obj);
                _spatialIndex.Remove(id);
                removed++;
            }
        }
        if (removed > 0) SceneChanged?.Invoke();
        return removed;
    }

    public void NotifyObjectGeometryChanged(DocumentObject obj)
    {
        if (_byId.ContainsKey(obj.Id))
        {
            _spatialIndex.Update(obj);
            SceneChanged?.Invoke();
        }
    }

    public void NotifyBatchChanged(IEnumerable<DocumentObject> objects)
    {
        foreach (var obj in objects)
        {
            if (_byId.ContainsKey(obj.Id))
            {
                _spatialIndex.Update(obj);
            }
        }
        SceneChanged?.Invoke();
    }

    public IReadOnlyList<DocumentObject> QueryViewport(in ObjectRect viewport)
    {
        var ids = _spatialIndex.QueryRect(in viewport);
        if (ids.Count == 0) return Array.Empty<DocumentObject>();
        var visible = new List<DocumentObject>(ids.Count);
        foreach (string id in ids)
        {
            if (_byId.TryGetValue(id, out var obj) && obj.IsVisible)
            {
                visible.Add(obj);
            }
        }
        visible.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        return visible;
    }

    public DocumentObject? HitTestTopMost(double x, double y, double tolerance = 10.0)
    {
        var candidates = _spatialIndex.QueryPoint(x, y, tolerance);
        DocumentObject? best = null;
        int bestZ = int.MinValue;

        foreach (string id in candidates)
        {
            if (!_byId.TryGetValue(id, out var obj) || !obj.IsVisible) continue;
            if (!HitTestExact(obj, x, y, tolerance)) continue;
            if (obj.ZIndex >= bestZ)
            {
                bestZ = obj.ZIndex;
                best = obj;
            }
        }
        return best;
    }

    public List<DocumentObject> HitTestRect(in ObjectRect rect)
    {
        var ids = _spatialIndex.QueryRect(in rect);
        var list = new List<DocumentObject>();
        foreach (string id in ids)
        {
            if (_byId.TryGetValue(id, out var obj) && obj.IsVisible && obj.Bounds.Intersects(in rect))
            {
                list.Add(obj);
            }
        }
        list.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        return list;
    }

    public List<DocumentObject> HitTestEraserSegment(double x0, double y0, double x1, double y1, double radius = 12.0)
    {
        var query = new ObjectRect(
            Math.Min(x0, x1) - radius,
            Math.Min(y0, y1) - radius,
            Math.Abs(x1 - x0) + radius * 2.0,
            Math.Abs(y1 - y0) + radius * 2.0);
        var ids = _spatialIndex.QueryRect(in query);
        var hit = new List<DocumentObject>();

        foreach (string id in ids)
        {
            if (!_byId.TryGetValue(id, out var obj) || !obj.IsVisible || obj.IsLocked) continue;
            if (obj is InkStrokeObject stroke)
            {
                if (StrokeTouchesSegment(stroke, x0, y0, x1, y1, radius + stroke.Thickness * 0.5))
                {
                    hit.Add(stroke);
                }
            }
        }
        return hit;
    }

    public void BringForward(IEnumerable<DocumentObject> objects)
    {
        int maxZ = _activePage.Objects.Count == 0 ? 1 : _activePage.Objects.Max(o => o.ZIndex);
        foreach (var obj in objects.OrderBy(o => o.ZIndex))
        {
            obj.ZIndex = ++maxZ;
        }
        SceneChanged?.Invoke();
    }

    public void SendBackward(IEnumerable<DocumentObject> objects)
    {
        int minZ = _activePage.Objects.Count == 0 ? 1 : _activePage.Objects.Min(o => o.ZIndex);
        foreach (var obj in objects.OrderByDescending(o => o.ZIndex))
        {
            obj.ZIndex = --minZ;
        }
        SceneChanged?.Invoke();
    }

    public string GroupObjects(IReadOnlyList<DocumentObject> objects)
    {
        string groupId = Guid.NewGuid().ToString("N");
        foreach (var obj in objects)
        {
            obj.GroupId = groupId;
        }
        SceneChanged?.Invoke();
        return groupId;
    }

    public void UngroupObjects(IReadOnlyList<DocumentObject> objects)
    {
        foreach (var obj in objects)
        {
            obj.GroupId = null;
        }
        SceneChanged?.Invoke();
    }

    public List<DocumentObject> ExpandWithGroupMembers(IEnumerable<DocumentObject> seed)
    {
        var result = new Dictionary<string, DocumentObject>();
        var groupIds = new HashSet<string>();
        foreach (var obj in seed)
        {
            result[obj.Id] = obj;
            if (!string.IsNullOrEmpty(obj.GroupId))
            {
                groupIds.Add(obj.GroupId);
            }
        }

        if (groupIds.Count > 0)
        {
            foreach (var obj in _activePage.Objects)
            {
                if (!string.IsNullOrEmpty(obj.GroupId) && groupIds.Contains(obj.GroupId))
                {
                    result[obj.Id] = obj;
                }
            }
        }

        return result.Values.ToList();
    }

    public void AlignObjects(IReadOnlyList<DocumentObject> objects, AlignmentKind alignment)
    {
        if (objects.Count < 2) return;
        double minLeft = objects.Min(o => o.Bounds.Left);
        double maxRight = objects.Max(o => o.Bounds.Right);
        double minTop = objects.Min(o => o.Bounds.Top);
        double maxBottom = objects.Max(o => o.Bounds.Bottom);
        double midX = (minLeft + maxRight) * 0.5;
        double midY = (minTop + maxBottom) * 0.5;

        foreach (var obj in objects)
        {
            if (obj.IsLocked) continue;
            var b = obj.Bounds;
            double dx = 0, dy = 0;
            switch (alignment)
            {
                case AlignmentKind.Left: dx = minLeft - b.Left; break;
                case AlignmentKind.CenterHorizontal: dx = midX - b.CenterX; break;
                case AlignmentKind.Right: dx = maxRight - b.Right; break;
                case AlignmentKind.Top: dy = minTop - b.Top; break;
                case AlignmentKind.CenterVertical: dy = midY - b.CenterY; break;
                case AlignmentKind.Bottom: dy = maxBottom - b.Bottom; break;
            }
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                obj.Translate(dx, dy);
                _spatialIndex.Update(obj);
            }
        }
        SceneChanged?.Invoke();
    }

    public void DistributeObjects(IReadOnlyList<DocumentObject> objects, bool horizontally)
    {
        if (objects.Count < 3) return;
        var ordered = horizontally
            ? objects.OrderBy(o => o.Bounds.CenterX).ToList()
            : objects.OrderBy(o => o.Bounds.CenterY).ToList();

        double first = horizontally ? ordered[0].Bounds.CenterX : ordered[0].Bounds.CenterY;
        double last = horizontally ? ordered[^1].Bounds.CenterX : ordered[^1].Bounds.CenterY;
        double step = (last - first) / (ordered.Count - 1);

        for (int i = 1; i < ordered.Count - 1; i++)
        {
            var obj = ordered[i];
            if (obj.IsLocked) continue;
            double target = first + step * i;
            if (horizontally)
            {
                obj.Translate(target - obj.Bounds.CenterX, 0);
            }
            else
            {
                obj.Translate(0, target - obj.Bounds.CenterY);
            }
            _spatialIndex.Update(obj);
        }
        SceneChanged?.Invoke();
    }

    private static bool HitTestExact(DocumentObject obj, double x, double y, double tolerance)
    {
        if (obj is InkStrokeObject stroke)
        {
            return StrokeTouchesSegment(stroke, x, y, x, y, tolerance + stroke.Thickness * 0.5);
        }
        if (obj is ConnectorObject conn)
        {
            return PointToSegmentDistSq(x, y, conn.StartX, conn.StartY, conn.EndX, conn.EndY) <= (tolerance + conn.StrokeThickness) * (tolerance + conn.StrokeThickness);
        }
        return obj.Bounds.Contains(x, y, tolerance * 0.5);
    }

    private static bool StrokeTouchesSegment(InkStrokeObject stroke, double ax, double ay, double bx, double by, double radius)
    {
        var pts = stroke.Points;
        if (pts.Count == 0) return false;
        double rSq = radius * radius;
        if (pts.Count == 1)
        {
            return PointToSegmentDistSq(pts[0].X, pts[0].Y, ax, ay, bx, by) <= rSq;
        }
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (PointToSegmentDistSq(pts[i].X, pts[i].Y, ax, ay, bx, by) <= rSq ||
                PointToSegmentDistSq(ax, ay, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y) <= rSq ||
                PointToSegmentDistSq(bx, by, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y) <= rSq)
            {
                return true;
            }
        }
        return false;
    }

    private static double PointToSegmentDistSq(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax;
        double aby = by - ay;
        double lenSq = abx * abx + aby * aby;
        if (lenSq <= 1e-9)
        {
            double dx0 = px - ax, dy0 = py - ay;
            return dx0 * dx0 + dy0 * dy0;
        }
        double t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lenSq, 0.0, 1.0);
        double dx = px - (ax + t * abx);
        double dy = py - (ay + t * aby);
        return dx * dx + dy * dy;
    }
}
