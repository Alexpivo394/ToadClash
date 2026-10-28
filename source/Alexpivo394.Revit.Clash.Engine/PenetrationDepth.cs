using System.Numerics;
using System.Runtime.InteropServices;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Локальная оценка глубины проникновения по разделяющим осям. Берутся вершины треугольников обоих элементов,
///     габарит которых пересекает зону, и проекции на оси: 3 координатные, 4 диагонали куба и до 64 нормалей
///     треугольников зоны каждого элемента, отобранных равномерно. Глубина - минимум по осям величины
///     min(maxA − minB, maxB − minA).
/// </summary>
/// <remarks>
///     Если у элемента нет треугольников в зоне (вложение: внешняя оболочка далеко от внутреннего элемента),
///     вместо его вершин берутся восемь углов его габарита.
///     Известное ограничение: оси вида «ребро × ребро» не берутся, поэтому для пар, разделимых только такой осью
///     (например, повёрнутые рёбрами друг к другу брусья), глубина может быть завышена.
/// </remarks>
internal static class PenetrationDepth
{
    /// <summary>Наибольшее число нормалей, берущихся с одного элемента.</summary>
    public const int MaxNormalsPerElement = 64;

    private static readonly Vec3d[] FixedAxes =
    [
        new(1, 0, 0),
        new(0, 1, 0),
        new(0, 0, 1),
        new Vec3d(1, 1, 1).Normalized(),
        new Vec3d(1, 1, -1).Normalized(),
        new Vec3d(1, -1, 1).Normalized(),
        new Vec3d(-1, 1, 1).Normalized()
    ];

    /// <summary>
    ///     Оценивает глубину проникновения элементов в зоне, мм. <paramref name="shared"/> - вершины одного элемента,
    ///     лежащие внутри другого: они принадлежат обоим телам и добавляются к вершинам обоих, иначе тело, от которого в
    ///     зону попала одна грань, проецируется в точку.
    /// </summary>
    public static double Estimate(SceneElement a, MeshBvh bvhA, SceneElement b, MeshBvh bvhB, in Aabb zone, IReadOnlyList<Vector3> shared,
        Scratch scratch)
    {
        scratch.Reset();
        Collect(a, bvhA, zone, scratch, scratch.VerticesA);
        Collect(b, bvhB, zone, scratch, scratch.VerticesB);
        scratch.VerticesA.AddRange(shared);
        scratch.VerticesB.AddRange(shared);

        var depth = double.PositiveInfinity;
        foreach (var axis in FixedAxes) depth = Math.Min(depth, Overlap(axis, scratch.VerticesA, scratch.VerticesB));
        foreach (var axis in scratch.Normals) depth = Math.Min(depth, Overlap(axis, scratch.VerticesA, scratch.VerticesB));
        return depth;
    }

    // Вершины треугольников зоны и равномерная выборка их нормалей.
    private static void Collect(SceneElement element, MeshBvh bvh, in Aabb zone, Scratch scratch, List<Vector3> vertices)
    {
        vertices.Clear();
        var triangles = scratch.Triangles;
        triangles.Clear();
        bvh.Query(zone, triangles);
        if (triangles.Count == 0)
        {
            AddCorners(element.Bounds, vertices);
            return;
        }

        foreach (var t in triangles)
        {
            for (var corner = 0; corner < 3; corner++) vertices.Add(bvh.Vertex(bvh.VertexOf(t, corner)));
        }

        var take = Math.Min(MaxNormalsPerElement, triangles.Count);
        for (var i = 0; i < take; i++)
        {
            bvh.Vertices(triangles[(int)((long)i * triangles.Count / take)], out var v0, out var v1, out var v2);
            scratch.Normals.Add(Vec3d.Cross(v1 - v0, v2 - v0).Normalized());
        }
    }

    private static void AddCorners(in Aabb box, List<Vector3> vertices)
    {
        for (var i = 0; i < 8; i++)
        {
            vertices.Add(new Vector3(
                (i & 1) == 0 ? box.Min.X : box.Max.X,
                (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                (i & 4) == 0 ? box.Min.Z : box.Max.Z));
        }
    }

    private static double Overlap(in Vec3d axis, List<Vector3> a, List<Vector3> b)
    {
        Project(axis, a, out var minA, out var maxA);
        Project(axis, b, out var minB, out var maxB);
        return Math.Min(maxA - minB, maxB - minA);
    }

    private static void Project(in Vec3d axis, List<Vector3> points, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        foreach (var p in CollectionsMarshal.AsSpan(points))
        {
            var d = axis.X * p.X + axis.Y * p.Y + axis.Z * p.Z;
            if (d < min) min = d;
            if (d > max) max = d;
        }
    }

    /// <summary>Буферы одного потока, переиспользуемые между парами.</summary>
    public sealed class Scratch
    {
        public List<int> Triangles { get; } = [];
        public List<Vector3> VerticesA { get; } = [];
        public List<Vector3> VerticesB { get; } = [];
        public List<Vec3d> Normals { get; } = [];

        /// <summary>Готовит буферы к новой паре.</summary>
        public void Reset()
        {
            Triangles.Clear();
            VerticesA.Clear();
            VerticesB.Clear();
            Normals.Clear();
        }
    }
}
