using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Вложение одного элемента в другой без пересечения поверхностей. Участвуют только замкнутые сетки обоих элементов
///     (<see cref="MeshTopology"/>). Внутренний элемент - внутри, если его габарит строго внутри габарита внешнего
///     и луч из центра его первого невырожденного треугольника пересекает внешнюю сетку нечётное число раз.
/// </summary>
/// <remarks>
///     Известное ограничение: внешний элемент из нескольких перекрывающихся замкнутых оболочек в области их перекрытия
///     даёт чётное число пересечений, и вложенный туда элемент не находится.
/// </remarks>
internal sealed class Containment(IReadOnlyList<SceneElement> elements, Func<int, MeshBvh> bvhOf)
{
    /// <summary>Насколько габарит внутреннего элемента должен отступать от габарита внешнего с каждой стороны, мм.</summary>
    private const float BoundsMargin = 1e-3f;

    /// <summary>Порог близости луча к ребру или началу в барицентрических координатах и мм.</summary>
    private const double RayEpsilon = 1e-6;

    // Фиксированные направления не вдоль осей, чтобы луч не шёл по рёбрам ортогональных сеток.
    private static readonly Vec3d[] Directions =
    [
        new Vec3d(0.5773, 0.5774, 0.5775).Normalized(),
        new Vec3d(-0.3122, 0.8467, 0.4309).Normalized()
    ];

    /// <summary>Лежит ли элемент <paramref name="inner"/> внутри <paramref name="outer"/>; <paramref name="scratch"/> - буфер потока.</summary>
    public bool IsInside(int inner, int outer, List<int> scratch)
    {
        var a = elements[inner];
        var b = elements[outer];
        if (!StrictlyInside(a.Bounds, b.Bounds)) return false;

        var bvh = bvhOf(outer);
        if (!bvh.IsClosed || !bvhOf(inner).IsClosed) return false;
        if (!FirstCentroid(a, out var point)) return false;

        foreach (var direction in Directions)
        {
            var parity = Parity(bvh, point, direction, scratch);
            if (parity != RayResult.Ambiguous) return parity == RayResult.Inside;
        }

        return false;
    }

    private static bool StrictlyInside(in Aabb inner, in Aabb outer) =>
        inner.Min.X > outer.Min.X + BoundsMargin && inner.Min.Y > outer.Min.Y + BoundsMargin &&
        inner.Min.Z > outer.Min.Z + BoundsMargin && inner.Max.X < outer.Max.X - BoundsMargin &&
        inner.Max.Y < outer.Max.Y - BoundsMargin && inner.Max.Z < outer.Max.Z - BoundsMargin;

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
