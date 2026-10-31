using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Зона пересечения двух тел по их сеткам, как в точной фазе движка: габарит всех отрезков пересечения треугольников.
///     Для длинных элементов под углом к осям она много меньше пересечения габаритов.
/// </summary>
public static class IntersectionZone
{
    /// <summary>
    ///     Габарит отрезков пересечения поверхностей A и B. Если поверхности не пересекаются (одно тело внутри другого) —
    ///     габарит меньшего тела в пределах габарита большего; габариты не пересекаются — <see cref="Aabb.Empty"/>.
    /// </summary>
    /// <param name="positionsA">Вершины A, тройки xyz.</param>
    /// <param name="indicesA">Треугольники A, тройки индексов.</param>
    /// <param name="positionsB">Вершины B в той же системе координат.</param>
    /// <param name="indicesB">Треугольники B.</param>
    public static Aabb Measure(float[] positionsA, int[] indicesA, float[] positionsB, int[] indicesB)
    {
        ArgumentNullException.ThrowIfNull(positionsA);
        ArgumentNullException.ThrowIfNull(indicesA);
        ArgumentNullException.ThrowIfNull(positionsB);
        ArgumentNullException.ThrowIfNull(indicesB);

        var boundsA = Aabb.FromPositions(positionsA);
        var boundsB = Aabb.FromPositions(positionsB);
        var common = new Aabb(Vector3.Max(boundsA.Min, boundsB.Min), Vector3.Min(boundsA.Max, boundsB.Max));
        if (!(common.Min.X <= common.Max.X && common.Min.Y <= common.Max.Y && common.Min.Z <= common.Max.Z)) return Aabb.Empty;

        var (small, large) = indicesA.Length <= indicesB.Length
            ? (MeshBvh.Build(positionsA, indicesA), MeshBvh.Build(positionsB, indicesB))
            : (MeshBvh.Build(positionsB, indicesB), MeshBvh.Build(positionsA, indicesA));
        var eps = (float)TriangleIntersection.PlaneEpsilon;
        var smallTriangles = new List<int>();
        var candidates = new List<int>();
        small.Query(common.Expand(eps), smallTriangles);
        var zone = Aabb.Empty;
        var found = false;
        foreach (var t in smallTriangles)
        {
            small.Vertices(t, out var a0, out var a1, out var a2);
            candidates.Clear();
            large.Query(small.BoundsOf(t).Expand(eps), candidates);
            foreach (var u in candidates)
            {
                large.Vertices(u, out var b0, out var b1, out var b2);
                if (!TriangleIntersection.Intersect(a0, a1, a2, b0, b1, b2, out var p, out var q)) continue;
                zone = zone.Union(new Aabb(
                    new Vector3((float)Math.Min(p.X, q.X), (float)Math.Min(p.Y, q.Y), (float)Math.Min(p.Z, q.Z)),
                    new Vector3((float)Math.Max(p.X, q.X), (float)Math.Max(p.Y, q.Y), (float)Math.Max(p.Z, q.Z))));
                found = true;
            }
        }

        if (found) return zone;
        var inner = Volume(boundsA) <= Volume(boundsB) ? boundsA : boundsB;
        return new Aabb(Vector3.Max(inner.Min, common.Min), Vector3.Min(inner.Max, common.Max));
    }

    private static double Volume(in Aabb box)
    {
        var size = box.Max - box.Min;
        return (double)size.X * size.Y * size.Z;
    }
}
