using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     BVH треугольников одного элемента: разбиение по медиане центров вдоль самой длинной оси, листья не более 4 треугольников.
///     Вырожденные треугольники в дерево не попадают. Замкнутая сетка хранится с внешней ориентацией
///     (<see cref="MeshTopology"/>), исходные массивы элемента не меняются.
/// </summary>
internal sealed class MeshBvh
{
    private const int LeafSize = 4;
    private const int StackSize = 128;
    private const float RayMargin = 1e-3f;

    private readonly float[] _positions;
    private readonly int[] _indices;
    private readonly Node[] _nodes;
    private readonly int[] _triangles;
    private int _nodeCount;

    private MeshBvh(float[] positions, int[] indices, int[] triangles, bool closed)
    {
        IsClosed = closed;
        _positions = positions;
        _indices = indices;
        _triangles = triangles;
        // Листья не меньше двух треугольников, поэтому узлов не больше числа треугольников.
        _nodes = new Node[Math.Max(1, triangles.Length)];
    }

    /// <summary>Число невырожденных треугольников в дереве.</summary>
    public int TriangleCount => _triangles.Length;

    /// <summary>Замкнута ли сетка элемента.</summary>
    public bool IsClosed { get; }

    /// <summary>Строит дерево по сетке элемента сцены.</summary>
    public static MeshBvh Build(SceneElement element) => Build(element.Positions, element.Indices);

    /// <summary>Строит дерево по сетке: позиции — тройки xyz, индексы — тройки треугольников.</summary>
    public static MeshBvh Build(float[] positions, int[] elementIndices)
    {
        var indices = MeshTopology.Orient(positions, elementIndices, out var closed);
        var triangleCount = indices.Length / 3;
        var triangles = new List<int>(triangleCount);
        for (var t = 0; t < triangleCount; t++)
        {
            if (!IsDegenerate(positions, indices, t)) triangles.Add(t);
        }

        var bvh = new MeshBvh(positions, indices, [.. triangles], closed);
        if (bvh._triangles.Length == 0) return bvh;

        var centers = new Vector3[triangleCount];
        foreach (var t in bvh._triangles) centers[t] = bvh.BoundsOf(t).Center;
        bvh.Build(centers, 0, bvh._triangles.Length);
        return bvh;
    }

    /// <summary>Вырожден ли треугольник: квадрат длины векторного произведения сторон не больше порога.</summary>
    public static bool IsDegenerate(float[] positions, int[] indices, int triangle)
    {
        var a = Vec3d.At(positions, indices[triangle * 3]);
        var n = Vec3d.Cross(Vec3d.At(positions, indices[triangle * 3 + 1]) - a, Vec3d.At(positions, indices[triangle * 3 + 2]) - a);
        return n.LengthSquared <= TriangleIntersection.DegenerateAreaSquared;
    }

    /// <summary>Вершины треугольника в double.</summary>
    public void Vertices(int triangle, out Vec3d a, out Vec3d b, out Vec3d c)
    {
        a = Vec3d.At(_positions, _indices[triangle * 3]);
        b = Vec3d.At(_positions, _indices[triangle * 3 + 1]);
        c = Vec3d.At(_positions, _indices[triangle * 3 + 2]);
    }

    /// <summary>Габарит треугольника.</summary>
    public Aabb BoundsOf(int triangle)
    {
        var a = Vertex(_indices[triangle * 3]);
        var b = Vertex(_indices[triangle * 3 + 1]);
        var c = Vertex(_indices[triangle * 3 + 2]);
        return new Aabb(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
    }

    /// <summary>Позиция вершины.</summary>
    public Vector3 Vertex(int vertex) => new(_positions[vertex * 3], _positions[vertex * 3 + 1], _positions[vertex * 3 + 2]);

    /// <summary>Индекс вершины треугольника.</summary>
    public int VertexOf(int triangle, int corner) => _indices[triangle * 3 + corner];

    /// <summary>Добавляет в <paramref name="output"/> треугольники, габарит которых пересекается с <paramref name="box"/>.</summary>
    public void Query(in Aabb box, List<int> output)
    {
        if (_triangles.Length == 0) return;

        Span<int> stack = stackalloc int[StackSize];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var index = stack[--top];
            ref readonly var node = ref _nodes[index];
            if (!node.Bounds.Overlaps(box)) continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                {
                    var t = _triangles[i];
                    if (BoundsOf(t).Overlaps(box)) output.Add(t);
                }

                continue;
            }

            stack[top++] = index + 1;
            stack[top++] = node.First;
        }
    }

    /// <summary>Добавляет в <paramref name="output"/> треугольники листьев, габарит которых пересекает луч.</summary>
    public void QueryRay(in Vec3d origin, in Vec3d direction, List<int> output)
    {
        if (_triangles.Length == 0) return;

        var inv = new Vec3d(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        Span<int> stack = stackalloc int[StackSize];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var index = stack[--top];
            ref readonly var node = ref _nodes[index];
            if (!RayHits(node.Bounds, origin, inv)) continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++) output.Add(_triangles[i]);
                continue;
            }

            stack[top++] = index + 1;
            stack[top++] = node.First;
        }
    }

    private static bool RayHits(in Aabb box, in Vec3d o, in Vec3d inv)
    {
        double near = 0, far = double.PositiveInfinity;
        Slab(box.Min.X - RayMargin, box.Max.X + RayMargin, o.X, inv.X, ref near, ref far);
        Slab(box.Min.Y - RayMargin, box.Max.Y + RayMargin, o.Y, inv.Y, ref near, ref far);
        Slab(box.Min.Z - RayMargin, box.Max.Z + RayMargin, o.Z, inv.Z, ref near, ref far);
        return near <= far;
    }

    private static void Slab(double min, double max, double o, double inv, ref double near, ref double far)
    {
        var t0 = (min - o) * inv;
        var t1 = (max - o) * inv;
        if (t0 > t1) (t0, t1) = (t1, t0);
        if (t0 > near) near = t0;
        if (t1 < far) far = t1;
    }

    // Узел пишется до детей: левый ребёнок идёт сразу за ним, индекс правого хранится в First.
    private void Build(Vector3[] centers, int start, int count)
    {
        var index = _nodeCount++;
        var bounds = Aabb.Empty;
        var centerBounds = Aabb.Empty;
        for (var i = start; i < start + count; i++)
        {
            var t = _triangles[i];
            bounds = bounds.Union(BoundsOf(t));
            centerBounds = new Aabb(Vector3.Min(centerBounds.Min, centers[t]), Vector3.Max(centerBounds.Max, centers[t]));
        }

        if (count <= LeafSize)
        {
            _nodes[index] = new Node(bounds, start, count);
            return;
        }

        var extent = centerBounds.Max - centerBounds.Min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var half = count / 2;
        SelectMedian(centers, axis, start, start + count - 1, start + half);

        Build(centers, start, half);
        var right = _nodeCount;
        Build(centers, start + half, count - half);
        _nodes[index] = new Node(bounds, right, 0);
    }

    // Quickselect: после вызова на позиции nth стоит треугольник с nth-м по величине центром вдоль оси.
    private void SelectMedian(Vector3[] centers, int axis, int lo, int hi, int nth)
    {
        while (hi > lo)
        {
            var pivot = Key(centers, _triangles[(lo + hi) >>> 1], axis);
            int i = lo, j = hi;
            while (i <= j)
            {
                while (Key(centers, _triangles[i], axis) < pivot) i++;
                while (Key(centers, _triangles[j], axis) > pivot) j--;
                if (i > j) break;
                (_triangles[i], _triangles[j]) = (_triangles[j], _triangles[i]);
                i++;
                j--;
            }

            if (nth <= j) hi = j;
            else if (nth >= i) lo = i;
            else return;
        }
    }

    private static float Key(Vector3[] centers, int triangle, int axis) =>
        axis == 0 ? centers[triangle].X : axis == 1 ? centers[triangle].Y : centers[triangle].Z;

    /// <summary>Узел: лист, если <see cref="Count"/> больше нуля; у внутреннего узла <see cref="First"/> - индекс правого ребёнка.</summary>
    private readonly struct Node(Aabb bounds, int first, int count)
    {
        public Aabb Bounds { get; } = bounds;
        public int First { get; } = first;
        public int Count { get; } = count;
    }
}
