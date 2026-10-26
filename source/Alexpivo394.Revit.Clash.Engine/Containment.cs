using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Вложение одного элемента в другой без пересечения поверхностей. Участвуют только замкнутые сетки обоих элементов:
///     каждое неориентированное ребро (вершины сварены по точному совпадению координат) принадлежит ровно двум треугольникам.
/// </summary>
internal sealed class Containment
{
    /// <summary>Насколько габарит внутреннего элемента должен отступать от габарита внешнего с каждой стороны, мм.</summary>
    private const float BoundsMargin = 1e-3f;

    /// <summary>Порог близости луча к ребру или началу в барицентрических координатах и мм.</summary>
    private const double RayEpsilon = 1e-6;

    private const sbyte Unknown = 0;
    private const sbyte Closed = 1;
    private const sbyte Open = 2;

    // Фиксированные направления не вдоль осей, чтобы луч не шёл по рёбрам ортогональных сеток.
    private static readonly Vec3d[] Directions =
    [
        new Vec3d(0.5773, 0.5774, 0.5775).Normalized(),
        new Vec3d(-0.3122, 0.8467, 0.4309).Normalized()
    ];

    private readonly IReadOnlyList<SceneElement> _elements;
    private readonly Func<int, MeshBvh> _bvhOf;
    private readonly sbyte[] _closed;

    public Containment(IReadOnlyList<SceneElement> elements, Func<int, MeshBvh> bvhOf)
    {
        _elements = elements;
        _bvhOf = bvhOf;
        _closed = new sbyte[elements.Count];
    }

    /// <summary>Лежит ли элемент <paramref name="inner"/> внутри <paramref name="outer"/>; <paramref name="scratch"/> - буфер потока.</summary>
    public bool IsInside(int inner, int outer, List<int> scratch)
    {
        var a = _elements[inner];
        var b = _elements[outer];
        if (!StrictlyInside(a.Bounds, b.Bounds)) return false;
        if (!IsClosed(outer) || !IsClosed(inner)) return false;
        if (!FirstCentroid(a, out var point)) return false;

        var bvh = _bvhOf(outer);
        foreach (var direction in Directions)
        {
            var parity = Parity(bvh, point, direction, scratch);
            if (parity != RayResult.Ambiguous) return parity == RayResult.Inside;
        }

        return false;
    }

    /// <summary>Замкнута ли сетка элемента; результат вычисляется один раз за прогон.</summary>
    public bool IsClosed(int element)
    {
        var state = Volatile.Read(ref _closed[element]);
        if (state == Unknown)
        {
            // Гонка безвредна: оба потока получат одинаковый результат.
            state = ComputeClosed(_elements[element]) ? Closed : Open;
            Volatile.Write(ref _closed[element], state);
        }

        return state == Closed;
    }

    private static bool StrictlyInside(in Aabb inner, in Aabb outer) =>
        inner.Min.X > outer.Min.X + BoundsMargin && inner.Min.Y > outer.Min.Y + BoundsMargin &&
        inner.Min.Z > outer.Min.Z + BoundsMargin && inner.Max.X < outer.Max.X - BoundsMargin &&
        inner.Max.Y < outer.Max.Y - BoundsMargin && inner.Max.Z < outer.Max.Z - BoundsMargin;

    private static bool ComputeClosed(SceneElement element)
    {
        var positions = element.Positions;
        var indices = element.Indices;
        if (indices.Length == 0) return false;

        var welded = new Dictionary<(float, float, float), int>();
        var ids = new int[positions.Length / 3];
        for (var v = 0; v < ids.Length; v++)
        {
            var key = (positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            if (!welded.TryGetValue(key, out var id))
            {
                id = welded.Count;
                welded[key] = id;
            }

            ids[v] = id;
        }

        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int x = ids[indices[t]], y = ids[indices[t + 1]], z = ids[indices[t + 2]];
            if (x == y || y == z || z == x) continue;
            Count(edges, x, y);
            Count(edges, y, z);
            Count(edges, z, x);
        }

        if (edges.Count == 0) return false;
        foreach (var count in edges.Values)
        {
            if (count != 2) return false;
        }

        return true;
    }

    private static void Count(Dictionary<(int, int), int> edges, int x, int y)
    {
        var key = x < y ? (x, y) : (y, x);
        edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    private static bool FirstCentroid(SceneElement element, out Vec3d point)
    {
        for (var t = 0; t < element.Indices.Length / 3; t++)
        {
            if (MeshBvh.IsDegenerate(element.Positions, element.Indices, t)) continue;
            var a = Vec3d.At(element.Positions, element.Indices[t * 3]);
            var b = Vec3d.At(element.Positions, element.Indices[t * 3 + 1]);
            var c = Vec3d.At(element.Positions, element.Indices[t * 3 + 2]);
            point = (a + b + c) * (1.0 / 3);
            return true;
        }

        point = default;
        return false;
    }

    // Чётность пересечений луча с треугольниками; близость к ребру, вершине или началу луча - неоднозначно.
    private static RayResult Parity(MeshBvh bvh, in Vec3d origin, in Vec3d direction, List<int> scratch)
    {
        scratch.Clear();
        bvh.QueryRay(origin, direction, scratch);
        var hits = 0;
        foreach (var t in scratch)
        {
            bvh.Vertices(t, out var v0, out var v1, out var v2);
            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var pv = Vec3d.Cross(direction, e2);
            var det = Vec3d.Dot(e1, pv);
            if (Math.Abs(det) <= 1e-12 * Math.Sqrt(e1.LengthSquared * e2.LengthSquared)) continue;

            var inv = 1 / det;
            var tv = origin - v0;
            var u = Vec3d.Dot(tv, pv) * inv;
            if (u < -RayEpsilon || u > 1 + RayEpsilon) continue;
            var qv = Vec3d.Cross(tv, e1);
            var v = Vec3d.Dot(direction, qv) * inv;
            if (v < -RayEpsilon || u + v > 1 + RayEpsilon) continue;
            var distance = Vec3d.Dot(e2, qv) * inv;
            if (distance < -RayEpsilon) continue;

            if (u < RayEpsilon || v < RayEpsilon || u + v > 1 - RayEpsilon || distance < RayEpsilon)
                return RayResult.Ambiguous;
            hits++;
        }

        return hits % 2 == 1 ? RayResult.Inside : RayResult.Outside;
    }

    private enum RayResult
    {
        Outside,
        Inside,
        Ambiguous
    }
}
